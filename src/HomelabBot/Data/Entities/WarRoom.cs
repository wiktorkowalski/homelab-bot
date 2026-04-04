namespace HomelabBot.Data.Entities;

public sealed class WarRoom
{
    public int Id { get; set; }

    public ulong DiscordThreadId { get; set; }

    public ulong StatusMessageId { get; set; }

    public required string Trigger { get; set; }

    public required string Severity { get; set; }

    public WarRoomStatus Status { get; set; } = WarRoomStatus.Active;

    public string TimelineJson { get; set; } = "[]";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ResolvedAt { get; set; }

    public string? Resolution { get; set; }

    public string? PostMortemSummary { get; set; }

    public TimeSpan? Mttr { get; set; }
}

public enum WarRoomStatus
{
    Active = 0,
    Resolved = 1,
}
