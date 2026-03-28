namespace HomelabBot.Configuration;

public sealed class HealingChainConfiguration
{
    public const string SectionName = "HealingChain";

    public bool Enabled { get; init; } = true;

    public int MaxStepsPerChain { get; init; } = 10;

    public int MaxConcurrentChains { get; init; } = 2;

    public bool RequireConfirmationForRisky { get; init; } = true;
}
