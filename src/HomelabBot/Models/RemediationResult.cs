namespace HomelabBot.Models;

public sealed class RemediationResult
{
    public bool WasAutoExecuted { get; init; }

    public bool NeedsConfirmation { get; init; }

    public int? ActionId { get; init; }

    public required string Message { get; init; }

    public string? ContainerName { get; init; }
}
