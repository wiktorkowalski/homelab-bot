namespace HomelabBot.Models;

public sealed class ContainerNetworkInfo
{
    public required string Name { get; init; }

    public required string State { get; init; }

    public List<string> Networks { get; init; } = [];

    public List<string> Ports { get; init; } = [];

    public Dictionary<string, string> Labels { get; init; } = new();

    public required string Image { get; init; }
}

public sealed class BlastRadiusReport
{
    public required string SourceService { get; init; }

    public required string SourceAnomaly { get; init; }

    public List<AffectedService> AffectedServices { get; init; } = [];

    public string RiskLevel { get; init; } = "low";
}

public sealed class AffectedService
{
    public required string Name { get; init; }

    public required string Relationship { get; init; }

    public required string CurrentStatus { get; init; }
}
