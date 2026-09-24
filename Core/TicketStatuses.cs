namespace QueueSystem.Core;

/// <summary>
/// حالات التذكرة (نفس المنطق الأصلي).
/// </summary>
public static class TicketStatuses
{
    public const string Waiting = "WAITING";
    public const string Called = "CALLED";
    public const string Skipped = "SKIPPED";
    public const string Closed = "CLOSED";
    public const string Transferred = "TRANSFERRED";
    public const string SkippedExpired = "SKIPPED_EXPIRED";

    public static readonly HashSet<string> Terminal = new(StringComparer.OrdinalIgnoreCase)
    {
        Closed, Transferred, SkippedExpired
    };

    public static bool IsTerminal(string status) => Terminal.Contains(status);
}
