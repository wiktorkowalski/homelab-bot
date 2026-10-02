namespace HomelabBot.Services;

internal static class StoppedContainerGrace
{
    // Duplicates AnomalyDetectionService.CheckContainerHealthAsync's rule; keep both in sync.
    public static bool IsParked(TimeSpan? stoppedFor, int graceHours)
    {
        // Non-positive means "no grace at all" — every stopped container still counts.
        var grace = graceHours > 0 ? TimeSpan.FromHours(graceHours) : TimeSpan.MaxValue;

        // Unknown exit time is treated as "just stopped", same as the anomaly check.
        return stoppedFor is not null && stoppedFor >= grace;
    }
}
