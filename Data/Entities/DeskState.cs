namespace QueueSystem.Data.Entities;

/// <summary>
/// الحالة الحالية لكل شباك (مقابل QueueState).
/// </summary>
public class DeskState
{
    public int Id { get; set; }

    public int Desk { get; set; }

    public int CurrentNumber { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public string? UpdatedBy { get; set; }
}
