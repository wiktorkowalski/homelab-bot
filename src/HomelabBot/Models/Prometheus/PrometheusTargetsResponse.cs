namespace HomelabBot.Models.Prometheus;

public sealed class PrometheusTargetsResponse
{
    public string Status { get; set; } = "";

    public PrometheusTargetsData? Data { get; set; }
}

public sealed class PrometheusTargetsData
{
    public List<PrometheusTarget> ActiveTargets { get; set; } = [];
}

public sealed class PrometheusTarget
{
    public Dictionary<string, string>? Labels { get; set; }

    public string? ScrapeUrl { get; set; }

    public string Health { get; set; } = "";
}
