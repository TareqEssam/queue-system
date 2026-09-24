namespace QueueSystem.Data.Entities;

/// <summary>
/// تذكرة المتابعة (مقابل ClientTickets).
/// الحالات النهائية: CLOSED / TRANSFERRED / SKIPPED_EXPIRED
/// </summary>
public class Ticket
{
    public int Id { get; set; }

    public int ClientNumber { get; set; }

    /// <summary>SHA-256 للرمز السري (لا نخزن الرمز الخام).</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// WAITING | CALLED | SKIPPED | CLOSED | TRANSFERRED | SKIPPED_EXPIRED
    /// </summary>
    public string Status { get; set; } = "WAITING";

    public int? Desk { get; set; }

    public DateTime? CalledAt { get; set; }

    public DateTime? SkippedAt { get; set; }

    /// <summary>المصدر الحقيقي لانتهاء مهلة التخطي (Sequence + 5).</summary>
    public long? SkipExpireAtSequence { get; set; }

    /// <summary>ترتيب الحدث الحي (للمزامنة ومنع الرجوع للخلف).</summary>
    public long LiveEventSeq { get; set; }

    /// <summary>آخر عدد متبقٍ تم إرسال تنبيه الاقتراب عنده (1 أو 2) لمنع تكرار التنبيه.</summary>
    public int? LastNearRemainingNotified { get; set; }

    public int? RegistrationId { get; set; }
    public Registration? Registration { get; set; }

    public string? TransferNote { get; set; }
    public DateTime? TransferredAt { get; set; }
    public string? TransferredBy { get; set; }
    public string? TransferMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
