namespace HomelabBot.Configuration;

public sealed class WarRoomConfiguration
{
    public const string SectionName = "WarRoom";

    public bool Enabled { get; init; }

    public ulong ChannelId { get; init; }

    public string[] TriggerSeverities { get; init; } = ["critical"];
}
