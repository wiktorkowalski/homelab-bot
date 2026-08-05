namespace HomelabBot.Models;

internal sealed class StoppedContainerInfo
{
    public required string Name { get; init; }

    // Null when Docker reports no usable exit time — treated as "just stopped" by callers.
    public TimeSpan? StoppedFor { get; init; }
}
