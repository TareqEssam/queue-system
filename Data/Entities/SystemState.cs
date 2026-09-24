namespace QueueSystem.Data.Entities;

/// <summary>
/// حالة النظام العامة (مفتوحة / مغلقة + المؤشر التسلسلي + عدادات يومية).
/// </summary>
public class SystemState
{
    public int Id { get; set; } = 1;

    /// <summary>هل النظام مفتوح لاستقبال عملاء جدد؟</summary>
    public bool IsOpen { get; set; } = true;

    /// <summary>المؤشر التسلسلي للطابور (مصدر قرار NEXT).</summary>
    public long QueueSequence { get; set; }

    /// <summary>آخر رقم تم استدعاؤه فعلياً.</summary>
    public int LastCalledNumber { get; set; }

    /// <summary>مفتاح اليوم الحالي (yyyy-MM-dd) لإعادة التعيين اليومية.</summary>
    public string ActiveDayKey { get; set; } = string.Empty;

    public long LiveEventSeq { get; set; }

    public int WaitingCount { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>آخر عملية إغلاق وفتح لاستقبال العملاء، مع بيانات التدقيق.</summary>
    public DateTime? LastClosedAt { get; set; }
    public string? LastClosedBy { get; set; }
    public DateTime? LastOpenedAt { get; set; }
    public string? LastOpenedBy { get; set; }
}
