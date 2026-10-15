namespace HomelabBot.Data.Entities;

// One claude -p run on the host, started by /claude, a follow-up of the owner, or ask_claude.
public sealed class ClaudeRun
{
    public int Id { get; set; }

    public ClaudeRunOrigin Origin { get; set; }

    public required string Task { get; set; }

    public Guid SessionId { get; set; }

    public bool Resume { get; set; }

    // The chat that gets the result: a guild channel or thread. Null is the owner DM.
    public ulong? ChannelId { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? EndedAt { get; set; }

    public long? DurationMs { get; set; }

    public int? NumTurns { get; set; }

    public double? TotalCostUsd { get; set; }

    public bool? IsError { get; set; }

    public string? PermissionDenialsJson { get; set; }

    public ClaudeRunState State { get; set; } = ClaudeRunState.Running;
}

// A Discord id that continues a Claude session: a /claude thread, or a result message in the owner DM.
// Snowflakes are unique across channels and messages, so one column holds both.
public sealed class ClaudeSessionLink
{
    public int Id { get; set; }

    public ulong DiscordId { get; set; }

    public Guid SessionId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum ClaudeRunOrigin
{
    Owner = 0,
    Llm = 1,
}

public enum ClaudeRunState
{
    Running = 0,
    Completed = 1,
    Failed = 2,
    TimedOut = 3,
    UsageLimit = 4,
    Interrupted = 5,
}
