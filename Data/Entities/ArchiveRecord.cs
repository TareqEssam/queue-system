namespace QueueSystem.Data.Entities;

/// <summary>
/// أرشيف دائم يمكن للمدير البحث فيه باسم الشركة أو رقم السجل التجاري.
/// </summary>
public class ArchiveRecord
{
    public long Id { get; set; }

    public int ClientNumber { get; set; }

    public string CompanyName { get; set; } = string.Empty;

    public string CommercialRegister { get; set; } = string.Empty;

    public int? Desk { get; set; }

    /// <summary>اسم العرض للباحث الذي استقبل الرقم.</summary>
    public string? EmployeeDisplayName { get; set; }

    public string Action { get; set; } = string.Empty;

    public string StatusFinal { get; set; } = string.Empty;

    public string? Notes { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
