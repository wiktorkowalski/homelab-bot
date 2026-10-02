using HomelabBot.Models;

namespace HomelabBot.Services;

internal static class StoppedContainerGrace
{
    public static bool IsParked(TimeSpan? stoppedFor, int graceHours)
    {
        // Non-positive means "no grace at all" — every stopped container still counts.
        var grace = graceHours > 0 ? TimeSpan.FromHours(graceHours) : TimeSpan.MaxValue;

        // Unknown exit time is treated as "just stopped".
        return stoppedFor is not null && stoppedFor >= grace;
    }

    public static ParkedResolution Resolve(
        IReadOnlyList<StoppedContainerInfo> stopped,
        IReadOnlySet<string> confirmedLastCycle,
        int graceHours)
    {
        var confirmed = new HashSet<string>(StringComparer.Ordinal);
        var parked = new HashSet<string>(StringComparer.Ordinal);

        foreach (var container in stopped)
        {
            if (IsParked(container.StoppedFor, graceHours))
            {
                confirmed.Add(container.Name);
                parked.Add(container.Name);
            }
            else if (container.StoppedFor is null && graceHours > 0 && confirmedLastCycle.Contains(container.Name))
            {
                // A transient exit-time read failure must not flip a parked container to "down" —
                // that drops the score by a full weight and fires a false drop alert. Bridging is
                // not confirmed, so a second failure in a row counts it as down.
                parked.Add(container.Name);
            }
        }

        return new ParkedResolution { Parked = parked, Confirmed = confirmed };
    }
}

internal sealed record ParkedResolution
{
    // Treated as parked this cycle, including containers bridged over a failed exit-time read.
    public required IReadOnlySet<string> Parked { get; init; }

    // Parked on a successful exit-time read; only these may be bridged next cycle.
    public required IReadOnlySet<string> Confirmed { get; init; }
}
