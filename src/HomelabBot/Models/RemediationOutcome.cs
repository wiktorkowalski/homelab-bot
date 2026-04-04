using HomelabBot.Data.Entities;

namespace HomelabBot.Models;

public sealed class RemediationOutcome
{
    public required string Message { get; init; }

    public bool Success { get; init; }

    public bool Handled { get; init; }

    public bool NeedsConfirmation { get; init; }

    public int? ActionId { get; init; }

    public string? ContainerName { get; init; }

    public RemediationMethod Method { get; init; }

    public int? GeneratedRunbookId { get; init; }

    // Context for downstream use (LLM investigation, feedback buttons)
    public List<Runbook> MatchedRunbooks { get; init; } = [];

    public List<SimilarIncident> SimilarIncidents { get; init; } = [];

    public BlastRadiusReport? BlastRadius { get; init; }
}

public enum RemediationMethod
{
    None,
    Runbook,
    AutoRemediation,
    HealingChain,
}
