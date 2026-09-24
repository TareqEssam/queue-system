namespace QueueSystem.Data.Entities;

/// <summary>
/// حساب الموظف / المدير.
/// Username: يستخدم لتسجيل الدخول فقط (شروط صارمة).
/// DisplayName: الاسم الذي يظهر للمدير والعملاء وفي الأرشيف.
/// </summary>
public class Employee
{
    public int Id { get; set; }

    /// <summary>اسم المستخدم لتسجيل الدخول (حروف إنجليزية وأرقام فقط، طول محدد).</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>اسم العرض الظاهر في الواجهات والأرشيف.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>كلمة المرور مشفرة بـ BCrypt.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Employee أو Manager</summary>
    public string Role { get; set; } = "Employee";

    /// <summary>الشباك المخصص للحساب. المدير لا يحتاج شباكًا.</summary>
    public int? Desk { get; set; }

    public bool IsActive { get; set; } = true;

    public int FailedLoginCount { get; set; }

    public DateTime? LockUntil { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastLoginAt { get; set; }
}
