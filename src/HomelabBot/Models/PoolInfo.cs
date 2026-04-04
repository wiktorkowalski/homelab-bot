namespace HomelabBot.Models;

public sealed class PoolInfo
{
    public required string Name { get; init; }

    public required string Status { get; init; }

    public bool Healthy { get; init; }

    public long AllocatedBytes { get; init; }

    public long SizeBytes { get; init; }
}
