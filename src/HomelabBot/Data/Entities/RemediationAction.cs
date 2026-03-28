namespace HomelabBot.Data.Entities;

public sealed class RemediationAction
{
    public int Id { get; set; }

    public required string ContainerName { get; set; }

    public required string ActionType { get; set; }

    public required string Trigger { get; set; }

    public int? PatternId { get; set; }

    public required string BeforeState { get; set; }

    public string? AfterState { get; set; }

    public bool Success { get; set; }

    public bool RollbackPerformed { get; set; }

    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;

    public bool ConfirmedByUser { get; set; }
}
