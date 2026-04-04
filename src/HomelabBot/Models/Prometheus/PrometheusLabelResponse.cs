namespace HomelabBot.Models.Prometheus;

public sealed class PrometheusLabelResponse
{
    public string Status { get; set; } = "";

    public List<string> Data { get; set; } = [];
}
