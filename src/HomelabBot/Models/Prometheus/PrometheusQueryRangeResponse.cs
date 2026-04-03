using System.Text.Json;

namespace HomelabBot.Models.Prometheus;

public sealed class PrometheusQueryRangeResponse
{
    public PrometheusQueryRangeData? Data { get; set; }
}

public sealed class PrometheusQueryRangeData
{
    public List<PrometheusRangeResult>? Result { get; set; }
}

public sealed class PrometheusRangeResult
{
    public Dictionary<string, string>? Metric { get; set; }

    public JsonElement[][]? Values { get; set; }
}
