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
}

public enum TrustLevel
{
    ReadOnly,
    Conservative,
    Risky,
}
