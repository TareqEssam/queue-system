namespace QueueSystem.Data.Entities;

/// <summary>
/// سجل تدقيق لكل حركة في الطابور.
/// </summary>
public class QueueLog
{
    public long Id { get; set; }

    /// <summary>NEXT | MANUAL | SKIP | TRANSFER | CLOSE | MISS | DAILY_RESET ...</summary>
    public string Action { get; set; } = string.Empty;

    public int? ClientNumber { get; set; }

    public int? Desk { get; set; }

    /// <summary>اسم العرض للباحث (وليس Username).</summary>
    public string? EmployeeDisplayName { get; set; }

    public string? Details { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
