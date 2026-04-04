using System.Text.Json;

namespace HomelabBot.Models.Prometheus;

public sealed class PrometheusQueryResponse
{
    public string Status { get; set; } = "";

    public PrometheusQueryData? Data { get; set; }
}

public sealed class PrometheusQueryData
{
    public string ResultType { get; set; } = "";

    public List<PrometheusResult> Result { get; set; } = [];
}

public sealed class PrometheusResult
{
    public Dictionary<string, string>? Metric { get; set; }

    public JsonElement[]? Value { get; set; }
}
