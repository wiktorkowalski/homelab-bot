namespace HomelabBot.Data.Entities;

public sealed class Investigation
{
    public int Id { get; set; }
    public ulong ThreadId { get; set; }
    public required string Trigger { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public bool Resolved { get; set; }
    public string? Resolution { get; set; }
    public List<InvestigationStep> Steps { get; set; } = [];
}

public sealed class InvestigationStep
{
    public int Id { get; set; }
    public int InvestigationId { get; set; }
    public Investigation Investigation { get; set; } = null!;
    public required string Action { get; set; }
    public string? Plugin { get; set; }
    public string? ResultSummary { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
