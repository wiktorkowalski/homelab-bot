using HomelabBot.Data.Entities;

namespace HomelabBot.Models;

public sealed class SimilarIncident
{
    public int InvestigationId { get; init; }

    public required string Trigger { get; init; }

    public string? Resolution { get; init; }

    public double SimilarityScore { get; init; }

    public required List<string> MatchReasons { get; init; }

    public DateTime OccurredAt { get; init; }

    public List<RemediationAction> SuccessfulRemediations { get; init; } = [];
}
