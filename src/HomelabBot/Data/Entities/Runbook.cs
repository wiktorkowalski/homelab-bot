namespace HomelabBot.Data.Entities;

public sealed class Runbook
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public required string TriggerCondition { get; set; }

    public required string StepsJson { get; set; }

    public bool Enabled { get; set; } = true;

    public int Version { get; set; } = 1;

    public TrustLevel TrustLevel { get; set; } = TrustLevel.ReadOnly;

    public int ExecutionCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastExecutedAt { get; set; }

    public RunbookSourceType SourceType { get; set; } = RunbookSourceType.Manual;

    public int? SourceInvestigationId { get; set; }

    public int? ParentRunbookId { get; set; }

    // Pattern fields (merged from Pattern entity)
    public string? CommonCause { get; set; }

    public int OccurrenceCount { get; set; }

    public int SuccessCount { get; set; }

    public int FailureCount { get; set; }

    public DateTime? LastSeen { get; set; }

    public double SuccessRate => (SuccessCount + FailureCount) > 0
        ? (double)SuccessCount / (SuccessCount + FailureCount) * 100
        : 0;
}

public enum RunbookSourceType
{
    Manual = 0,
    AutoCompiled = 1,
    HealingChain = 2,
}

public enum TrustLevel
{
    ReadOnly,
    Conservative,
    Risky,
}
