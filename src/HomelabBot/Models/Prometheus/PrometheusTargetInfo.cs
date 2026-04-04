namespace HomelabBot.Models.Prometheus;

public sealed class PrometheusTargetInfo
{
    public required string Job { get; init; }

    public required string Instance { get; init; }

    public required string Health { get; init; }
}
