namespace HomelabBot.Data.Entities;

public sealed class AnomalyEvent
{
    public int Id { get; set; }
    public required string Summary { get; set; }
    public string? Analysis { get; set; }
    public required string Severity { get; set; }
    public int AnomalyCount { get; set; }
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
}
