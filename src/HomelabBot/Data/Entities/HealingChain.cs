namespace HomelabBot.Data.Entities;

public sealed class HealingChain
{
    public int Id { get; set; }

    public required string Trigger { get; set; }

    public required string StepsJson { get; set; }

    public string ExecutionLogJson { get; set; } = "[]";

    public HealingChainStatus Status { get; set; } = HealingChainStatus.Planned;

    public bool RequiredConfirmation { get; set; }

    public int? GeneratedRunbookId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAt { get; set; }
}

public enum HealingChainStatus
{
    Planned = 0,
    Executing = 1,
    Completed = 2,
    Failed = 3,
    Aborted = 4,
}
