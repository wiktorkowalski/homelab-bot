using System.ComponentModel;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HomelabBot.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace HomelabBot.Plugins;

public sealed class PrometheusPlugin
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<PrometheusPlugin> _logger;
    private readonly string _baseUrl;

    public PrometheusPlugin(
        IHttpClientFactory httpClientFactory,
        IOptions<PrometheusConfiguration> config,
        ILogger<PrometheusPlugin> logger)
    {
        _httpClient = httpClientFactory.CreateClient("Default");
        _logger = logger;
        _baseUrl = config.Value.Host.TrimEnd('/');
    }

    [KernelFunction]
    [Description("Lists available Prometheus metric names. Use this to discover what metrics are available before querying.")]
    public async Task<string> GetAvailableMetrics([Description("Optional filter prefix (e.g., 'node_', 'container_')")] string? prefix = null)
    {
        _logger.LogDebug("Getting available metrics with prefix: {Prefix}", prefix ?? "all");

        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/api/v1/label/__name__/values");
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<PrometheusLabelResponse>();

            if (result?.Data == null || result.Data.Count == 0)
            {
                return "No metrics found.";
            }

            var metrics = result.Data.AsEnumerable();

            if (!string.IsNullOrEmpty(prefix))
            {
                metrics = metrics.Where(m => m.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            }

            var metricList = metrics.OrderBy(m => m).ToList();

            if (metricList.Count == 0)
            {
                return $"No metrics found with prefix '{prefix}'.";
            }

            // Group by prefix for readability
            var grouped = metricList
                .GroupBy(m => m.Split('_')[0])
                .OrderByDescending(g => g.Count())
                .Take(20);

            var sb = new StringBuilder();
            sb.AppendLine($"Found {metricList.Count} metrics" + (prefix != null ? $" matching '{prefix}'" : "") + ":\n");

            foreach (var group in grouped)
            {
                var samples = group.Take(5).ToList();
                sb.AppendLine($"**{group.Key}_*** ({group.Count()} metrics)");
                foreach (var metric in samples)
                {
                    sb.AppendLine($"  - {metric}");
                }
                if (group.Count() > 5)
                {
                    sb.AppendLine($"  - ... and {group.Count() - 5} more");
                }
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting available metrics");
            return $"Error getting metrics: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Executes a PromQL query against Prometheus. Returns the current value of the expression.")]
    public async Task<string> QueryPrometheus([Description("PromQL query expression")] string query)
    {
        _logger.LogDebug("Executing PromQL query: {Query}", query);

        try
        {
            var encodedQuery = Uri.EscapeDataString(query);
            var response = await _httpClient.GetAsync($"{_baseUrl}/api/v1/query?query={encodedQuery}");
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<PrometheusQueryResponse>();

            if (result?.Data?.Result == null || result.Data.Result.Count == 0)
            {
                return "Query returned no results.";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Query: `{query}`\n");

            foreach (var item in result.Data.Result.Take(20))
            {
                var labels = FormatLabels(item.Metric);
                var value = item.Value?.Length > 1 ? item.Value[1].ToString() : "N/A";

                sb.AppendLine($"- {labels}: **{value}**");
            }

            if (result.Data.Result.Count > 20)
            {
                sb.AppendLine($"\n... and {result.Data.Result.Count - 20} more results");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing PromQL query: {Query}", query);
            return $"Error executing query: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Gets current CPU, memory, and disk usage for the node (Ubuntu VM).")]
    public async Task<string> GetNodeStats()
    {
        _logger.LogDebug("Getting node stats...");

        var sb = new StringBuilder();
        sb.AppendLine("**Node Statistics**\n");

        // CPU usage
        var cpuQuery = "100 - (avg(rate(node_cpu_seconds_total{mode=\"idle\"}[5m])) * 100)";
        var cpuResult = await QuerySingleValue(cpuQuery);
        sb.AppendLine($"CPU Usage: **{cpuResult:F1}%**");

        // Memory usage
        var memQuery = "(1 - (node_memory_MemAvailable_bytes / node_memory_MemTotal_bytes)) * 100";
        var memResult = await QuerySingleValue(memQuery);
        sb.AppendLine($"Memory Usage: **{memResult:F1}%**");

        // Disk usage (root filesystem)
        var diskQuery = "(1 - (node_filesystem_avail_bytes{mountpoint=\"/\"} / node_filesystem_size_bytes{mountpoint=\"/\"})) * 100";
        var diskResult = await QuerySingleValue(diskQuery);
        sb.AppendLine($"Disk Usage (/): **{diskResult:F1}%**");

        // Load average
        var loadQuery = "node_load1";
        var loadResult = await QuerySingleValue(loadQuery);
        sb.AppendLine($"Load (1m): **{loadResult:F2}**");

        // Uptime
        var uptimeQuery = "node_time_seconds - node_boot_time_seconds";
        var uptimeResult = await QuerySingleValue(uptimeQuery);
        var uptime = TimeSpan.FromSeconds(uptimeResult);
        sb.AppendLine($"Uptime: **{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m**");

        return sb.ToString();
    }

    [KernelFunction]
    [Description("Gets resource metrics for a specific Docker container (CPU, memory usage).")]
    public async Task<string> GetContainerMetrics([Description("Container name")] string containerName)
    {
        _logger.LogDebug("Getting metrics for container: {Container}", containerName);

        var sb = new StringBuilder();
        sb.AppendLine($"**Container Metrics: {containerName}**\n");

        // CPU usage rate
        var cpuQuery = $"rate(container_cpu_usage_seconds_total{{name=\"{containerName}\"}}[5m]) * 100";
        var cpuResult = await QuerySingleValue(cpuQuery);
        sb.AppendLine($"CPU Usage: **{cpuResult:F2}%**");

        // Memory usage
        var memQuery = $"container_memory_usage_bytes{{name=\"{containerName}\"}}";
        var memResult = await QuerySingleValue(memQuery);
        var memMB = memResult / (1024 * 1024);
        sb.AppendLine($"Memory: **{memMB:F0} MB**");

        // Memory limit
        var memLimitQuery = $"container_spec_memory_limit_bytes{{name=\"{containerName}\"}}";
        var memLimitResult = await QuerySingleValue(memLimitQuery);
        if (memLimitResult > 0)
        {
            var memLimitMB = memLimitResult / (1024 * 1024);
            var memPercent = (memResult / memLimitResult) * 100;
            sb.AppendLine($"Memory Limit: **{memLimitMB:F0} MB** ({memPercent:F1}% used)");
        }

        // Network I/O
        var netRxQuery = $"rate(container_network_receive_bytes_total{{name=\"{containerName}\"}}[5m])";
        var netRxResult = await QuerySingleValue(netRxQuery);
        var netTxQuery = $"rate(container_network_transmit_bytes_total{{name=\"{containerName}\"}}[5m])";
        var netTxResult = await QuerySingleValue(netTxQuery);

        sb.AppendLine($"Network: **{FormatBytes(netRxResult)}/s** in, **{FormatBytes(netTxResult)}/s** out");

        return sb.ToString();
    }

    private async Task<double> QuerySingleValue(string query)
    {
        try
        {
            var encodedQuery = Uri.EscapeDataString(query);
            var response = await _httpClient.GetAsync($"{_baseUrl}/api/v1/query?query={encodedQuery}");
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<PrometheusQueryResponse>();

            if (result?.Data?.Result?.FirstOrDefault()?.Value?.Length > 1)
            {
                if (double.TryParse(result.Data.Result[0].Value![1].ToString(), out var value))
                {
                    return value;
                }
            }

            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatLabels(Dictionary<string, string>? labels)
    {
        if (labels == null || labels.Count == 0)
        {
            return "{}";
        }

        var relevant = labels
            .Where(l => l.Key != "__name__")
            .Select(l => $"{l.Key}={l.Value}")
            .Take(3);

        return "{" + string.Join(", ", relevant) + "}";
    }

    private static string FormatBytes(double bytes)
    {
        if (bytes < 1024) return $"{bytes:F0} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024 * 1024):F1} MB";
        return $"{bytes / (1024 * 1024 * 1024):F2} GB";
    }

    private sealed class PrometheusLabelResponse
    {
        public string Status { get; set; } = "";
        public List<string> Data { get; set; } = [];
    }

    private sealed class PrometheusQueryResponse
    {
        public string Status { get; set; } = "";
        public PrometheusQueryData? Data { get; set; }
    }

    private sealed class PrometheusQueryData
    {
        public string ResultType { get; set; } = "";
        public List<PrometheusResult> Result { get; set; } = [];
    }

    private sealed class PrometheusResult
    {
        public Dictionary<string, string>? Metric { get; set; }
        public JsonElement[]? Value { get; set; }
    }
}
