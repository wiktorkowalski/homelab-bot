namespace HomelabBot.Configuration;

public sealed class HealingChainConfiguration
{
    public const string SectionName = "HealingChain";

    // Off by owner decision (2026-10-14): plans never parsed in practice, and a chain that runs
    // replaces the alert investigation and resolves the war room on its own.
    public bool Enabled { get; init; } = false;

    public int MaxStepsPerChain { get; init; } = 10;

    public int MaxConcurrentChains { get; init; } = 2;

    public bool RequireConfirmationForRisky { get; init; } = true;
}
