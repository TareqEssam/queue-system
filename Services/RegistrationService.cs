using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using QueueSystem.Core;
using QueueSystem.Data;
using QueueSystem.Data.Entities;

namespace QueueSystem.Services;

public class RegistrationService
{
    private readonly AppDbContext _db;
    private readonly ILogger<RegistrationService> _logger;
    private readonly QueueLock _queueLock;
    private readonly IConfiguration _config;
    private static readonly ConcurrentDictionaryCleaner GuardTokens = new();

    public RegistrationService(AppDbContext db, ILogger<RegistrationService> logger, IConfiguration config, QueueLock queueLock)
    {
        _db = db;
        _logger = logger;
        _config = config;
        _queueLock = queueLock;
    }

    /// <summary>
    /// إصدار رمز حماية للفورم (يُستخدم ضد الإرسال الآلي السريع).
    /// </summary>
    public string IssueGuardToken()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        GuardTokens.Add(token, DateTime.UtcNow);
        return token;
    }

    /// <summary>
    /// تسجيل عميل جديد بنفس منطق المشروع الأصلي.
    /// </summary>
    public async Task<RegistrationResult> SubmitAsync(RegistrationRequest req, CancellationToken ct = default)
    {
        // 1) Honeypot
        if (!string.IsNullOrWhiteSpace(req.Website))
        {
            _logger.LogWarning("تم رفض تسجيل بسبب honeypot");
            return RegistrationResult.Fail("طلب غير صالح.");
        }

        // 2) Guard token + الحد الأدنى للوقت
        var minMs = _config.GetValue("Queue:FormMinSubmitMs", 5000);
        if (string.IsNullOrWhiteSpace(req.GuardToken) || !GuardTokens.TryGet(req.GuardToken, out var issuedAt))
            return RegistrationResult.Fail("انتهت صلاحية صفحة التسجيل. أعد تحميل الصفحة.");

        if ((DateTime.UtcNow - issuedAt).TotalMilliseconds < minMs)
            return RegistrationResult.Fail("يرجى الانتظار لحظات ثم إعادة المحاولة.");

        GuardTokens.Remove(req.GuardToken);

        // 3) تنظيف وتحقق من المدخلات
        var regDigits = NormalizeDigits(req.RegNumber ?? "");
        if (regDigits.Length != 15)
            return RegistrationResult.Fail("رقم السجل التجاري الموحد يجب أن يكون 15 رقماً فقط.");

        var company = (req.CompanyName ?? "").Trim();
        if (company.Length < 2 || company.Length > 300)
            return RegistrationResult.Fail("اسم الشركة غير صالح.");

        if (!int.TryParse(Regex.Replace(req.ClientNumber ?? "", @"\D", ""), out var clientNumber) ||
            clientNumber < 1 || clientNumber > 500)
            return RegistrationResult.Fail("رقم العميل يجب أن يكون بين 1 و 500.");

        using (await _queueLock.AcquireAsync(ct))
        {
            // 4) هل النظام مفتوح؟
            var state = await _db.SystemStates.FirstOrDefaultAsync(ct);
            if (state == null)
            {
                state = new SystemState { Id = 1, IsOpen = true, ActiveDayKey = DateTime.UtcNow.ToString("yyyy-MM-dd") };
                _db.SystemStates.Add(state);
                await _db.SaveChangesAsync(ct);
            }

            // إعادة تعيين يومية خفيفة
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            if (state.ActiveDayKey != today)
            {
                state.ActiveDayKey = today;
                state.QueueSequence = 0;
                state.LastCalledNumber = 0;
                state.WaitingCount = 0;
            }

            if (!state.IsOpen)
                return RegistrationResult.Fail("التسجيل مغلق حالياً. يرجى المحاولة لاحقاً.");

            // 5) مسموح بتكرار رقم التذكرة (إدخال يدوي) — الباحث يرى كل التسجيلات لنفس الرقم

// 6) إنشاء التسجيل + التذكرة
            var now = DateTime.UtcNow;
            var rawToken = GenerateSecureToken();
            var tokenHash = Sha256Hex(rawToken);

            var registration = new Registration
            {
                CommercialRegister = regDigits,
                CompanyName = company,
                ClientNumber = clientNumber,
                Source = "Form",
                CreatedAt = now
            };
            _db.Registrations.Add(registration);
            await _db.SaveChangesAsync(ct); // للحصول على Id

            var ticket = new Ticket
            {
                ClientNumber = clientNumber,
                TokenHash = tokenHash,
                Status = TicketStatuses.Waiting,
                RegistrationId = registration.Id,
                LiveEventSeq = ++state.LiveEventSeq,
                CreatedAt = now
            };
            _db.Tickets.Add(ticket);

            state.WaitingCount = await _db.Tickets
                .CountAsync(t => t.Status == TicketStatuses.Waiting || t.Status == TicketStatuses.Skipped, ct) + 1;
            state.UpdatedAt = now;

            _db.QueueLogs.Add(new QueueLog
            {
                Action = "REGISTER",
                ClientNumber = clientNumber,
                Details = $"سجل: {regDigits} | شركة: {company}",
                Timestamp = now
            });

            // أرشيف أولي
            _db.ArchiveRecords.Add(new ArchiveRecord
            {
                ClientNumber = clientNumber,
                CompanyName = company,
                CommercialRegister = regDigits,
                Action = "REGISTER",
                StatusFinal = TicketStatuses.Waiting,
                Timestamp = now
            });

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("تم تسجيل العميل {Number} — {Company}", clientNumber, company);

            return new RegistrationResult
            {
                Success = true,
                Message = "تم التسجيل بنجاح.",
                Ticket = clientNumber,
                TicketId = ticket.Id,
                RegistrationId = registration.Id,
                CompanyName = registration.CompanyName,
                RegNumber = registration.CommercialRegister,
                WaitingCount = state.WaitingCount,
                LiveEventSeq = ticket.LiveEventSeq,
                TrackToken = rawToken,
                TrackUrl = ""
            };
        }
    }

    public async Task<(Ticket? Ticket, Registration? Reg)> FindByTokenAsync(string rawToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return (null, null);
        var hash = Sha256Hex(rawToken.Trim());
        var ticket = await _db.Tickets
            .Include(t => t.Registration)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        return (ticket, ticket?.Registration);
    }

    // ── Helpers ──────────────────────────────────────────

    private static string NormalizeDigits(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c >= '0' && c <= '9') sb.Append(c);
            else if (c >= '٠' && c <= '٩') sb.Append((char)('0' + (c - '٠'))); // عربية
            else if (c >= '۰' && c <= '۹') sb.Append((char)('0' + (c - '۰'))); // فارسية/هندية
        }
        return sb.ToString();
    }

    private static string GenerateSecureToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    private static string Sha256Hex(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public class RegistrationRequest
{
    public string? RegNumber { get; set; }
    public string? CompanyName { get; set; }
    public string? ClientNumber { get; set; }
    public string? Website { get; set; }      // honeypot
    public string? GuardToken { get; set; }
}

public class RegistrationResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public int Ticket { get; set; }
    public int TicketId { get; set; }
    public int RegistrationId { get; set; }
    public string CompanyName { get; set; } = "";
    public string RegNumber { get; set; } = "";
    public int WaitingCount { get; set; }
    public long LiveEventSeq { get; set; }
    public string TrackToken { get; set; } = "";
    public string TrackUrl { get; set; } = "";

    public static RegistrationResult Fail(string msg) => new() { Success = false, Message = msg };
}

/// <summary>
/// قاموس بسيط مع تنظيف تلقائي للرموز القديمة.
/// </summary>
internal class ConcurrentDictionaryCleaner
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _map = new();
    private DateTime _lastClean = DateTime.UtcNow;

    public void Add(string key, DateTime value)
    {
        MaybeClean();
        _map[key] = value;
    }

    public bool TryGet(string key, out DateTime value) => _map.TryGetValue(key, out value);

    public void Remove(string key) => _map.TryRemove(key, out _);

    private void MaybeClean()
    {
        if ((DateTime.UtcNow - _lastClean).TotalMinutes < 10) return;
        _lastClean = DateTime.UtcNow;
        var cutoff = DateTime.UtcNow.AddMinutes(-30);
        foreach (var kv in _map)
            if (kv.Value < cutoff)
                _map.TryRemove(kv.Key, out _);
    }
}
