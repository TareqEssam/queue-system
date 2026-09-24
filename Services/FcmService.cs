using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Google.Apis.Auth.OAuth2;
using Microsoft.EntityFrameworkCore;
using QueueSystem.Data;
using QueueSystem.Data.Entities;
using WebPush;

namespace QueueSystem.Services;

/// <summary>
/// Unified push dispatcher:
/// - FCM/FID for Firebase Web Messaging-compatible browsers.
/// - Standard Web Push/VAPID for Safari iOS and other Push API browsers.
/// Network delivery is always asynchronous so queue actions remain fast.
/// </summary>
public sealed class FcmService : IHostedService
{
    private sealed record Job(
        int RegistrationId,
        string Target,
        string Language,
        NotificationCopy Copy,
        object? Data,
        string PushType,
        string? Endpoint,
        string? P256dh,
        string? Auth);

    private readonly IConfiguration _config;
    private readonly ILogger<FcmService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Channel<Job> _queue = Channel.CreateBounded<Job>(new BoundedChannelOptions(4096)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });

    private GoogleCredential? _credential;
    private string? _projectId;
    private bool _fcmEnabled;
    private bool _webPushEnabled;
    private string? _webPushSubject;
    private string? _webPushPublicKey;
    private string? _webPushPrivateKey;
    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;

    public FcmService(
        IConfiguration config,
        ILogger<FcmService> logger,
        IHttpClientFactory httpClientFactory,
        IServiceScopeFactory scopeFactory)
    {
        _config = config;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _scopeFactory = scopeFactory;
        Initialize();
    }

    public bool IsEnabled => _fcmEnabled || _webPushEnabled;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled)
            return Task.CompletedTask;

        _workerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _workerTask = Task.Run(() => WorkerAsync(_workerCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        if (_workerTask == null)
            return;

        try
        {
            await _workerTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _workerCts?.Cancel();
        }
        finally
        {
            _workerCts?.Dispose();
            _workerCts = null;
        }
    }

    /// <summary>
    /// يأخذ snapshot من جميع أجهزة التذكرة النشطة ثم يضعها في طابور الإرسال.
    /// </summary>
    public Task SendToTicketAsync(
        int ticketId,
        NotificationCopy copy,
        object? data = null,
        CancellationToken ct = default)
        => SendToTicketsAsync(new[] { (ticketId, copy, data) }, ct);

    public async Task SendToTicketsAsync(
        IEnumerable<(int TicketId, NotificationCopy Copy, object? Data)> notifications,
        CancellationToken ct = default)
    {
        if (!IsEnabled)
            return;

        var items = notifications
            .Where(x => x.TicketId > 0)
            .GroupBy(x => x.TicketId)
            .Select(g => g.Last())
            .ToList();
        if (items.Count == 0) return;

        var ids = items.Select(x => x.TicketId).Distinct().ToList();
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var targets = await db.PushRegistrations
            .AsNoTracking()
            .Where(p => p.IsActive && ids.Contains(p.TicketId))
            .Select(p => new
            {
                p.Id,
                p.TicketId,
                p.FcmToken,
                p.Language,
                p.PushType,
                p.Endpoint,
                p.P256dh,
                p.Auth
            })
            .ToListAsync(ct);

        var byTicket = items.ToDictionary(x => x.TicketId);
        foreach (var target in targets)
        {
            if (!byTicket.TryGetValue(target.TicketId, out var item))
                continue;

            var language = string.Equals(target.Language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ar";
            var pushType = string.Equals(target.PushType, "webpush", StringComparison.OrdinalIgnoreCase) ? "webpush" : "fcm";

            if (pushType == "webpush")
            {
                if (string.IsNullOrWhiteSpace(target.Endpoint) ||
                    string.IsNullOrWhiteSpace(target.P256dh) ||
                    string.IsNullOrWhiteSpace(target.Auth))
                    continue;

                await _queue.Writer.WriteAsync(new Job(
                    target.Id,
                    target.FcmToken,
                    language,
                    item.Copy,
                    item.Data,
                    "webpush",
                    target.Endpoint,
                    target.P256dh,
                    target.Auth), ct);
            }
            else
            {
                if (!_fcmEnabled || !IsUsableToken(target.FcmToken))
                    continue;

                await _queue.Writer.WriteAsync(new Job(
                    target.Id,
                    target.FcmToken,
                    language,
                    item.Copy,
                    item.Data,
                    "fcm",
                    null,
                    null,
                    null), ct);
            }
        }
    }

    private async Task WorkerAsync(CancellationToken ct)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                var title = job.Language == "en" ? job.Copy.TitleEn : job.Copy.TitleAr;
                var body = job.Language == "en" ? job.Copy.BodyEn : job.Copy.BodyAr;

                if (job.PushType == "webpush")
                {
                    await SendWebPushAsync(job, title, body, ct);
                }
                else
                {
                    await SendFcmAsync(job, title, body, job.Language, job.Data, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "فشل إرسال إشعار الدفع للخلفية للتسجيل {RegistrationId}", job.RegistrationId);
            }
        }
    }

    private async Task SendFcmAsync(
        Job job,
        string title,
        string body,
        string language,
        object? data,
        CancellationToken ct)
    {
        var accessToken = await _credential!.UnderlyingCredential.GetAccessTokenForRequestAsync(cancellationToken: ct);
        var client = _httpClientFactory.CreateClient("fcm");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var dataMap = BuildDataMap(title, body, language, data);
        var payload = new
        {
            message = new
            {
                // Firebase Installation ID target.
                fid = job.Target,
                data = dataMap,
                webpush = new
                {
                    headers = new { Urgency = "high" }
                }
            }
        };

        var url = $"https://fcm.googleapis.com/v1/projects/{_projectId}/messages:send";
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var resp = await client.PostAsync(url, content, ct);
        var respBody = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            _logger.LogWarning("FCM HTTP {Code}: {Body}", (int)resp.StatusCode, respBody);
    }

    private async Task SendWebPushAsync(Job job, string title, string body, CancellationToken ct)
    {
        if (!_webPushEnabled ||
            string.IsNullOrWhiteSpace(job.Endpoint) ||
            string.IsNullOrWhiteSpace(job.P256dh) ||
            string.IsNullOrWhiteSpace(job.Auth))
            return;

        var subscription = new PushSubscription(job.Endpoint, job.P256dh, job.Auth);
        var vapid = new VapidDetails(_webPushSubject!, _webPushPublicKey!, _webPushPrivateKey!);
        var payload = JsonSerializer.Serialize(new
        {
            title,
            body,
            lang = job.Language,
            link = "/track/",
            registrationId = job.RegistrationId,
            data = job.Data
        });

        var client = new WebPushClient();
        try
        {
            await client.SendNotificationAsync(subscription, payload, vapid);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Web Push delivery failed for registration {RegistrationId}", job.RegistrationId);
        }
    }

    private static Dictionary<string, string> BuildDataMap(string title, string body, string language, object? data)
    {
        var dataMap = new Dictionary<string, string>
        {
            ["title"] = title,
            ["body"] = body,
            ["lang"] = language
        };

        if (data == null)
            return dataMap;

        var json = JsonSerializer.Serialize(data);
        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
            dataMap[prop.Name] = prop.Value.ToString();

        return dataMap;
    }

    private void Initialize()
    {
        _fcmEnabled = _config.GetValue("Firebase:Enabled", false);
        _projectId = _config["Firebase:ProjectId"];
        var path = _config["Firebase:ServiceAccountJsonPath"];

        if (!_fcmEnabled || string.IsNullOrWhiteSpace(_projectId) || string.IsNullOrWhiteSpace(path))
        {
            _fcmEnabled = false;
            _logger.LogInformation("FCM معطّل أو غير مكتمل الإعداد؛ سيتم استخدام Web Push إن كان مفعّلاً.");
        }
        else
        {
            if (!Path.IsPathRooted(path))
                path = Path.Combine(AppContext.BaseDirectory, path);

            if (!File.Exists(path))
            {
                _fcmEnabled = false;
                _logger.LogWarning("ملف Service Account غير موجود: {Path}", path);
            }
            else
            {
                try
                {
                    _credential = GoogleCredential.FromFile(path)
                        .CreateScoped("https://www.googleapis.com/auth/firebase.messaging");
                    _logger.LogInformation("FCM جاهز للمشروع {Project}", _projectId);
                }
                catch (Exception ex)
                {
                    _fcmEnabled = false;
                    _logger.LogError(ex, "فشل تحميل بيانات FCM");
                }
            }
        }

        _webPushEnabled = _config.GetValue("WebPush:Enabled", false);
        _webPushSubject = _config["WebPush:Subject"]?.Trim();
        _webPushPublicKey = _config["WebPush:PublicKey"]?.Trim();
        _webPushPrivateKey = _config["WebPush:PrivateKey"]?.Trim();

        if (_webPushEnabled &&
            (string.IsNullOrWhiteSpace(_webPushSubject) ||
             string.IsNullOrWhiteSpace(_webPushPublicKey) ||
             string.IsNullOrWhiteSpace(_webPushPrivateKey)))
        {
            _webPushEnabled = false;
            _logger.LogWarning("Web Push مفعّل لكن مفاتيح VAPID غير مكتملة.");
        }
    }

    private static bool IsUsableToken(string? token)
        => !string.IsNullOrWhiteSpace(token) && token != "browser-local" && token.Length > 20 && token.Length <= 512;
}

public sealed record NotificationCopy(
    string TitleAr,
    string BodyAr,
    string TitleEn,
    string BodyEn);
