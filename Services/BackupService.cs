using System.Security.Cryptography;
using System.Text;

namespace QueueSystem.Services;

/// <summary>
/// نسخ احتياطي دوري لملف SQLite مع إمكانية تشفير AES (كلمة سر اختيارية).
/// </summary>
public sealed class BackupService : IHostedService, IDisposable
{
    private readonly IConfiguration _config;
    private readonly ILogger<BackupService> _logger;
    private readonly IHostEnvironment _env;
    private Timer? _timer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BackupService(IConfiguration config, ILogger<BackupService> logger, IHostEnvironment env)
    {
        _config = config;
        _logger = logger;
        _env = env;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.GetValue("Backup:Enabled", false))
        {
            _logger.LogInformation("النسخ الاحتياطي معطّل (Backup:Enabled=false).");
            return Task.CompletedTask;
        }

        var minutes = Math.Max(5, _config.GetValue("Backup:IntervalMinutes", 30));
        _timer = new Timer(async _ => await SafeRunAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(minutes));
        _logger.LogInformation("النسخ الاحتياطي مفعّل كل {Min} دقيقة.", minutes);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        try { _ = RunBackupAsync(); } catch { /* best effort on shutdown */ }
        return Task.CompletedTask;
    }

    private async Task SafeRunAsync()
    {
        try { await RunBackupAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "فشل النسخ الاحتياطي"); }
    }

    public async Task RunBackupAsync()
    {
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            var dbRelative = _config.GetConnectionString("Default") ?? "Data Source=queue.db";
            var dbPath = ExtractDataSource(dbRelative);
            if (!Path.IsPathRooted(dbPath))
                dbPath = Path.Combine(_env.ContentRootPath, dbPath);

            if (!File.Exists(dbPath))
            {
                _logger.LogWarning("ملف قاعدة البيانات غير موجود: {Path}", dbPath);
                return;
            }

            var backupDir = _config["Backup:Folder"] ?? "backups";
            if (!Path.IsPathRooted(backupDir))
                backupDir = Path.Combine(_env.ContentRootPath, backupDir);
            Directory.CreateDirectory(backupDir);

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var plainName = $"queue_{stamp}.db";
            var plainPath = Path.Combine(backupDir, plainName);

            // نسخ مع مشاركة قراءة (SQLite قد يكون مفتوحاً)
            await using (var src = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            await using (var dst = new FileStream(plainPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await src.CopyToAsync(dst);
            }

            var password = _config["Backup:EncryptionPassword"];
            if (!string.IsNullOrWhiteSpace(password))
            {
                var encPath = plainPath + ".enc";
                EncryptFile(plainPath, encPath, password);
                File.Delete(plainPath);
                _logger.LogInformation("نسخة مشفّرة: {Path}", encPath);
            }
            else
            {
                _logger.LogInformation("نسخة احتياطية: {Path}", plainPath);
            }

            // الاحتفاظ بآخر N نسخ
            var keep = Math.Max(1, _config.GetValue("Backup:KeepCount", 14));
            var files = Directory.GetFiles(backupDir, "queue_*.db*")
                .OrderByDescending(f => f)
                .Skip(keep)
                .ToList();
            foreach (var f in files)
            {
                try { File.Delete(f); } catch { /* ignore */ }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string ExtractDataSource(string connectionString)
    {
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
                return part["Data Source=".Length..].Trim();
            if (part.StartsWith("Filename=", StringComparison.OrdinalIgnoreCase))
                return part["Filename=".Length..].Trim();
        }
        return "queue.db";
    }

    private static void EncryptFile(string inputPath, string outputPath, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256).GetBytes(32);
        var iv = RandomNumberGenerator.GetBytes(16);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        outFs.Write(salt);
        outFs.Write(iv);
        using var crypto = new CryptoStream(outFs, aes.CreateEncryptor(), CryptoStreamMode.Write);
        using var inFs = new FileStream(inputPath, FileMode.Open, FileAccess.Read);
        inFs.CopyTo(crypto);
    }

    /// <summary>استعادة ملف .enc إلى .db (أداة مساعدة).</summary>
    public static void DecryptFile(string encPath, string outputPath, string password)
    {
        using var inFs = new FileStream(encPath, FileMode.Open, FileAccess.Read);
        var salt = new byte[16];
        var iv = new byte[16];
        if (inFs.Read(salt) != 16 || inFs.Read(iv) != 16)
            throw new InvalidDataException("ملف مشفّر تالف.");

        var key = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256).GetBytes(32);
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var crypto = new CryptoStream(inFs, aes.CreateDecryptor(), CryptoStreamMode.Read);
        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        crypto.CopyTo(outFs);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _gate.Dispose();
    }
}
