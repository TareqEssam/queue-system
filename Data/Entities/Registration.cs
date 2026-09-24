namespace QueueSystem.Data.Entities;

/// <summary>
/// سجل التسجيل الأساسي (مقابل ورقة Registrations في المشروع الأصلي).
/// </summary>
public class Registration
{
    public int Id { get; set; }

    /// <summary>رقم السجل التجاري الموحد.</summary>
    public string CommercialRegister { get; set; } = string.Empty;

    /// <summary>اسم الشركة.</summary>
    public string CompanyName { get; set; } = string.Empty;

    /// <summary>رقم العميل (رقم الدور).</summary>
    public int ClientNumber { get; set; }

    /// <summary>المصدر (ماكينة / فورم / يدوي...).</summary>
    public string Source { get; set; } = "Form";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
