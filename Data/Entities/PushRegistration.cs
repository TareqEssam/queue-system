namespace QueueSystem.Data.Entities;

/// <summary>
/// سجل أجهزة FCM المرتبطة بتذكرة معينة (حد أقصى 4 لكل تذكرة كما في الأصل).
/// </summary>
public class PushRegistration
{
    public int Id { get; set; }

    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    /// <summary>
    /// FID for Firebase Web Push, or a stable hash of Endpoint for standard Web Push.
    /// </summary>
    public string FcmToken { get; set; } = string.Empty;

    /// <summary>fcm / webpush</summary>
    public string PushType { get; set; } = "fcm";

    /// <summary>Standard Web Push subscription endpoint.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Web Push p256dh client key.</summary>
    public string? P256dh { get; set; }

    /// <summary>Web Push auth secret.</summary>
    public string? Auth { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastSeenAt { get; set; }

    /// <summary>لغة العميل المستخدمة عند إرسال إشعارات الخلفية.</summary>
    public string Language { get; set; } = "ar";
}
