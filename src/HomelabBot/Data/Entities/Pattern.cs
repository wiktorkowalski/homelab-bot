namespace HomelabBot.Data.Entities;

public sealed class Pattern
{
    public int Id { get; set; }
    public required string Symptom { get; set; }
    public string? CommonCause { get; set; }
    public string? Resolution { get; set; }
    public int OccurrenceCount { get; set; } = 1;
    public DateTime? LastSeen { get; set; }
}
