using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using QueueSystem.Data;
using QueueSystem.Data.Entities;

namespace QueueSystem.Services;

public class AuthService
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<AuthService> _logger;

    // شروط اسم المستخدم: حروف إنجليزية وأرقام فقط، 4-20 حرف
    private static readonly Regex UsernameRegex = new(@"^[a-zA-Z][a-zA-Z0-9]{3,19}$", RegexOptions.Compiled);

    public AuthService(AppDbContext db, IConfiguration config, ILogger<AuthService> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;
    }

    public async Task<(bool Success, string Message, string? Token, Employee? Employee)> LoginAsync(
        string username,
        string password,
        CancellationToken ct = default)
    {
        username = (username ?? "").Trim();

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return (false, "اسم المستخدم وكلمة المرور مطلوبان.", null, null);

        var employee = await _db.Employees
            .FirstOrDefaultAsync(e => e.Username == username, ct);

        if (employee == null || !employee.IsActive)
        {
            await Task.Delay(300, ct); // تأخير بسيط ضد التخمين
            return (false, "بيانات الدخول غير صحيحة.", null, null);
        }

        // فحص القفل
        if (employee.LockUntil.HasValue && employee.LockUntil > DateTime.UtcNow)
        {
            var remaining = (employee.LockUntil.Value - DateTime.UtcNow).TotalMinutes;
            return (false, $"الحساب مقفل مؤقتاً. حاول بعد {Math.Ceiling(remaining)} دقيقة.", null, null);
        }

        if (!BCrypt.Net.BCrypt.Verify(password, employee.PasswordHash))
        {
            employee.FailedLoginCount++;
            var maxFailures = _config.GetValue("Queue:LoginMaxFailures", 5);
            var lockMinutes = _config.GetValue("Queue:LoginLockMinutes", 15);

            if (employee.FailedLoginCount >= maxFailures)
            {
                employee.LockUntil = DateTime.UtcNow.AddMinutes(lockMinutes);
                employee.FailedLoginCount = 0;
                _logger.LogWarning("تم قفل الحساب {Username} بعد محاولات فاشلة", username);
            }

            await _db.SaveChangesAsync(ct);
            return (false, "بيانات الدخول غير صحيحة.", null, null);
        }

        // نجاح
        employee.FailedLoginCount = 0;
        employee.LockUntil = null;
        employee.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var token = GenerateJwt(employee);
        return (true, "تم تسجيل الدخول بنجاح.", token, employee);
    }

    public async Task<(bool Success, string Message)> CreateEmployeeAsync(
        string username,
        string password,
        string displayName,
        string role,
        int? desk = null,
        CancellationToken ct = default)
    {
        username = (username ?? "").Trim();
        displayName = (displayName ?? "").Trim();
        role = (role ?? "Employee").Trim();

        if (!UsernameRegex.IsMatch(username))
            return (false, "اسم المستخدم يجب أن يبدأ بحرف إنجليزي ويحتوي على 4-20 حرف/رقم فقط.");

        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length < 2 || displayName.Length > 100)
            return (false, "اسم العرض يجب أن يكون بين 2 و 100 حرف.");

        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            return (false, "كلمة المرور يجب أن تكون 8 أحرف على الأقل.");

        if (role is not ("Employee" or "Manager"))
            return (false, "الدور يجب أن يكون Employee أو Manager.");

        var allowedDesks = _config.GetSection("Queue:Desks").Get<int[]>() ?? new[] { 11, 12 };
        if (role == "Employee")
        {
            if (!desk.HasValue || !allowedDesks.Contains(desk.Value))
                return (false, $"يجب تحديد شباك صحيح للباحث: {string.Join(" أو ", allowedDesks)}.");
        }
        else
        {
            desk = null;
        }

        if (await _db.Employees.AnyAsync(e => e.Username == username, ct))
            return (false, "اسم المستخدم مستخدم بالفعل.");

        var employee = new Employee
        {
            Username = username,
            DisplayName = displayName,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12),
            Role = role,
            Desk = desk,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _db.Employees.Add(employee);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("تم إنشاء حساب {Username} باسم عرض {DisplayName}", username, displayName);
        return (true, "تم إنشاء الحساب بنجاح.");
    }

    public async Task<Employee?> ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        try
        {
            var principal = ValidateJwt(token);
            if (principal == null) return null;

            var idClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!int.TryParse(idClaim, out var id)) return null;

            var employee = await _db.Employees.FindAsync(new object[] { id }, ct);
            if (employee == null || !employee.IsActive) return null;

            // فحص انتهاء الجلسة حسب الإعدادات
            var sessionHours = _config.GetValue("Queue:EmployeeSessionHours", 8);
            if (employee.LastLoginAt.HasValue &&
                employee.LastLoginAt.Value.AddHours(sessionHours) < DateTime.UtcNow)
            {
                return null;
            }

            return employee;
        }
        catch
        {
            return null;
        }
    }

    private string GenerateJwt(Employee employee)
    {
        var secret = _config["Security:JwtSecret"]
                     ?? throw new InvalidOperationException("JwtSecret غير مضبوط");
        var expireHours = _config.GetValue("Security:TokenExpireHours", 8);

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, employee.Id.ToString()),
            new Claim(ClaimTypes.Name, employee.Username),
            new Claim("display_name", employee.DisplayName),
            new Claim(ClaimTypes.Role, employee.Role)
        };

        var token = new JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.AddHours(expireHours),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private ClaimsPrincipal? ValidateJwt(string token)
    {
        var secret = _config["Security:JwtSecret"]
                     ?? throw new InvalidOperationException("JwtSecret غير مضبوط");

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var handler = new JwtSecurityTokenHandler();

        var principal = handler.ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            IssuerSigningKey = key,
            ClockSkew = TimeSpan.FromMinutes(2)
        }, out _);

        return principal;
    }

    public bool VerifyPassword(string password, string passwordHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(passwordHash)) return false;
        try { return BCrypt.Net.BCrypt.Verify(password, passwordHash); }
        catch { return false; }
    }
}
