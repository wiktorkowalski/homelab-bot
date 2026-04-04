using System.ComponentModel;
using System.Text;
using HomelabBot.Helpers;
using HomelabBot.Models.Prometheus;
using HomelabBot.Services;
using Microsoft.SemanticKernel;
using ModelContextProtocol.Server;

namespace HomelabBot.Plugins;

[McpServerToolType]
public sealed class PrometheusPlugin
{
    private readonly PrometheusQueryService _prometheus;
    private readonly ILogger<PrometheusPlugin> _logger;

    public PrometheusPlugin(
        PrometheusQueryService prometheus,
        ILogger<PrometheusPlugin> logger)
    {
        _prometheus = prometheus;
        _logger = logger;
    }

    [KernelFunction]
    [Description("Lists available Prometheus metric names. Use this to discover what metrics are available before querying.")]
    public async Task<string> GetAvailableMetrics([Description("Optional filter prefix (e.g., 'node_', 'container_')")] string? prefix = null)
    {
        _logger.LogDebug("Getting available metrics with prefix: {Prefix}", prefix ?? "all");

        try
        {
            var result = await _prometheus.GetLabelValuesAsync("__name__");

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
    [McpServerTool]
    [Description("Executes a PromQL query against Prometheus. Returns the current value of the expression.")]
    public async Task<string> QueryPrometheus([Description("PromQL query expression")] string query)
    {
        _logger.LogDebug("Executing PromQL query: {Query}", query);

        try
        {
            var result = await _prometheus.QueryAsync(query);

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
    [McpServerTool]
    [Description("Gets current CPU, memory, and disk usage for the node (Ubuntu VM).")]
    public async Task<string> GetNodeStats()
    {
        _logger.LogDebug("Getting node stats...");

        var sb = new StringBuilder();
        sb.AppendLine("**Node Statistics**\n");

        // CPU usage
        var cpuResult = await _prometheus.QueryScalarAsync(
            "100 - (avg(rate(node_cpu_seconds_total{mode=\"idle\"}[5m])) * 100)");
        sb.AppendLine($"CPU Usage: **{cpuResult ?? 0:F1}%**");

        // Memory usage
        var memResult = await _prometheus.QueryScalarAsync(
            "(1 - (node_memory_MemAvailable_bytes / node_memory_MemTotal_bytes)) * 100");
        sb.AppendLine($"Memory Usage: **{memResult ?? 0:F1}%**");

        // Disk usage (root filesystem)
        var diskResult = await _prometheus.QueryScalarAsync(
            "(1 - (node_filesystem_avail_bytes{mountpoint=\"/\"} / node_filesystem_size_bytes{mountpoint=\"/\"})) * 100");
        sb.AppendLine($"Disk Usage (/): **{diskResult ?? 0:F1}%**");

        // Load average
        var loadResult = await _prometheus.QueryScalarAsync("node_load1");
        sb.AppendLine($"Load (1m): **{loadResult ?? 0:F2}**");

        // Uptime
        var uptimeResult = await _prometheus.QueryScalarAsync("node_time_seconds - node_boot_time_seconds");
        var uptime = TimeSpan.FromSeconds(uptimeResult ?? 0);
        sb.AppendLine($"Uptime: **{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m**");

        return sb.ToString();
    }

    [KernelFunction]
    [Description("Gets the status of all Prometheus scrape targets. Shows which services are being monitored and their health.")]
    public async Task<string> GetTargets()
    {
        _logger.LogDebug("Getting Prometheus targets...");

        try
        {
            var targets = await _prometheus.GetTargetStatusesAsync();

            if (targets.Count == 0)
            {
                return "No active targets found.";
            }

            var sb = new StringBuilder();
            var upCount = targets.Count(t => t.Health == "up");
            var downCount = targets.Count(t => t.Health == "down");

            sb.AppendLine($"**Prometheus Targets ({targets.Count} total)**");
            sb.AppendLine($"✅ Up: {upCount} | ❌ Down: {downCount}\n");

            var grouped = targets
                .GroupBy(t => t.Job)
                .OrderBy(g => g.Key);

            foreach (var group in grouped)
            {
                var allUp = group.All(t => t.Health == "up");
                var icon = allUp ? "✅" : "⚠️";
                sb.AppendLine($"{icon} **{group.Key}** ({group.Count()} targets)");

                foreach (var target in group)
                {
                    var status = target.Health == "up" ? "✅" : "❌";
                    sb.AppendLine($"   {status} {target.Instance}");
                }
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Prometheus targets");
            return $"Error getting targets: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Gets resource metrics for a specific Docker container (CPU, memory usage).")]
    public async Task<string> GetContainerMetrics([Description("Container name")] string containerName)
    {
        _logger.LogDebug("Getting metrics for container: {Container}", containerName);

        var sb = new StringBuilder();
        sb.AppendLine($"**Container Metrics: {containerName}**\n");

        // CPU usage rate
        var cpuResult = await _prometheus.QueryScalarAsync(
            $"rate(container_cpu_usage_seconds_total{{name=\"{containerName}\"}}[5m]) * 100");
        sb.AppendLine($"CPU Usage: **{cpuResult ?? 0:F2}%**");

        // Memory usage
        var memResult = await _prometheus.QueryScalarAsync(
            $"container_memory_usage_bytes{{name=\"{containerName}\"}}");
        var memMB = (memResult ?? 0) / (1024 * 1024);
        sb.AppendLine($"Memory: **{memMB:F0} MB**");

        // Memory limit
        var memLimitResult = await _prometheus.QueryScalarAsync(
            $"container_spec_memory_limit_bytes{{name=\"{containerName}\"}}");
        if (memLimitResult is > 0)
        {
            var memLimitMB = memLimitResult.Value / (1024 * 1024);
            var memPercent = ((memResult ?? 0) / memLimitResult.Value) * 100;
            sb.AppendLine($"Memory Limit: **{memLimitMB:F0} MB** ({memPercent:F1}% used)");
        }

        // Network I/O
        var netRxResult = await _prometheus.QueryScalarAsync(
            $"rate(container_network_receive_bytes_total{{name=\"{containerName}\"}}[5m])");
        var netTxResult = await _prometheus.QueryScalarAsync(
            $"rate(container_network_transmit_bytes_total{{name=\"{containerName}\"}}[5m])");

        sb.AppendLine($"Network: **{FormattingHelpers.FormatBytes(netRxResult ?? 0)}/s** in, **{FormattingHelpers.FormatBytes(netTxResult ?? 0)}/s** out");

        return sb.ToString();
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
}
