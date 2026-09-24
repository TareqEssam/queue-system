using System.Diagnostics;

namespace QueueSystem.Services;

/// <summary>
/// Cloudflare Tunnel بدون فتح بورت (Outbound فقط).
/// Mode=quick → رابط مؤقت trycloudflare.com
/// Mode=named → نفق ثابت (cloudflared tunnel run)
/// </summary>
public class TunnelService : IHostedService, IDisposable
{
    private readonly IConfiguration _config;
    private readonly ILogger<TunnelService> _logger;
    private Process? _process;
    private readonly string _localUrl = "http://127.0.0.1:5000";

    public string? PublicUrl { get; private set; }

    public TunnelService(IConfiguration config, ILogger<TunnelService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.GetValue("Tunnel:Enabled", true))
        {
            _logger.LogInformation("النفق معطل في الإعدادات.");
            return Task.CompletedTask;
        }

        var cloudflared = _config["Tunnel:CloudflaredPath"] ?? "cloudflared.exe";
        var fullPath = File.Exists(cloudflared)
            ? cloudflared
            : Path.Combine(AppContext.BaseDirectory, cloudflared);

        if (!File.Exists(fullPath))
        {
            _logger.LogWarning("لم يتم العثور على {Path}. ضع cloudflared بجانب البرنامج.", cloudflared);
            return Task.CompletedTask;
        }

        var mode = (_config["Tunnel:Mode"] ?? "quick").Trim().ToLowerInvariant();
        string args;

        if (mode == "named")
        {
            var tunnelName = _config["Tunnel:NamedTunnel"];
            var configPath = _config["Tunnel:ConfigPath"] ?? "cloudflared-config.yml";
            if (!Path.IsPathRooted(configPath))
                configPath = Path.Combine(AppContext.BaseDirectory, configPath);

            if (!string.IsNullOrWhiteSpace(tunnelName) && File.Exists(configPath))
                args = $"tunnel --config \"{configPath}\" run \"{tunnelName}\"";
            else if (!string.IsNullOrWhiteSpace(tunnelName))
                args = $"tunnel run \"{tunnelName}\"";
            else if (File.Exists(configPath))
                args = $"tunnel --config \"{configPath}\" run";
            else
            {
                _logger.LogWarning("Mode=named يتطلب NamedTunnel أو ConfigPath. التراجع إلى quick.");
                args = $"tunnel --url {_localUrl} --protocol http2";
            }
        }
        else
        {
            args = $"tunnel --url {_localUrl} --protocol http2";
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fullPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            };

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            void HandleLine(string? line)
            {
                if (string.IsNullOrEmpty(line)) return;
                _logger.LogInformation("[cloudflared] {Line}", line);

                var match = System.Text.RegularExpressions.Regex.Match(line, @"https://[a-zA-Z0-9\.\-]+");
                if (match.Success && (line.Contains("trycloudflare.com") || line.Contains("registered") || line.Contains("cloudflare")))
                {
                    PublicUrl = match.Value;
                    Console.WriteLine();
                    Console.WriteLine("=================================================");
                    Console.WriteLine($"  الرابط العام: {PublicUrl}");
                    Console.WriteLine("=================================================");
                    Console.WriteLine();
                }
            }

            _process.OutputDataReceived += (_, e) => HandleLine(e.Data);
            _process.ErrorDataReceived += (_, e) => HandleLine(e.Data);
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _logger.LogInformation("تم تشغيل Cloudflare Tunnel ({Mode})", mode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "فشل تشغيل Cloudflare Tunnel");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _logger.LogInformation("تم إيقاف Cloudflare Tunnel.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "خطأ أثناء إيقاف النفق");
        }
        return Task.CompletedTask;
    }

    public void Dispose() => _process?.Dispose();
}
