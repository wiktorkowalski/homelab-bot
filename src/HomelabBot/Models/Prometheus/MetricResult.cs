namespace HomelabBot.Models.Prometheus;

public sealed class MetricResult
{
    public Dictionary<string, string> Labels { get; set; } = [];

    public double Value { get; set; }
}
