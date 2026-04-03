namespace HomelabBot.Models;

public sealed class NotificationCandidate
{
    public required string Source { get; init; }

    public required string Summary { get; init; }

    public required string RawData { get; init; }

    public string? IssueType { get; init; }

    /// <summary>
    /// If true, RawData already contains a fully investigated report.
    /// The LLM will only decide whether to notify, not re-investigate.
    /// </summary>
    public bool AlreadyInvestigated { get; init; }

    /// <summary>
    /// If true, this candidate cannot be hard-suppressed by notification preferences.
    /// It will always reach the LLM for evaluation (preferences are still in the prompt).
    /// Use for critical issues that should never be silently dropped.
    /// </summary>
    public bool NeverSuppress { get; init; }
}
