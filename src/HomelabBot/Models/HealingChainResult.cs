namespace HomelabBot.Models;

public sealed class HealingChainResult
{
    public int ChainId { get; init; }

    public bool Success { get; init; }

    public required string Message { get; init; }

    public int StepsExecuted { get; init; }

    public int? GeneratedRunbookId { get; init; }
}
