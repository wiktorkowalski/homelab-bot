namespace HomelabBot.Models;

public sealed class DiscoveredFact
{
    public required string Topic { get; init; }
    public required string Fact { get; init; }
    public string? Context { get; init; }
    public double Confidence { get; init; } = 0.8;
}

public sealed class RefreshResult
{
    public int AddedFacts { get; set; }
    public int VerifiedFacts { get; set; }
    public int StaleFacts { get; set; }
    public List<string> Errors { get; init; } = [];
}
