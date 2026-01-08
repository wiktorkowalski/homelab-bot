using System.ComponentModel;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HomelabBot.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace HomelabBot.Plugins;

public sealed class LokiPlugin
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<LokiPlugin> _logger;
    private readonly string _baseUrl;

    public LokiPlugin(
        IHttpClientFactory httpClientFactory,
        IOptions<LokiConfiguration> config,
        ILogger<LokiPlugin> logger)
    {
        _httpClient = httpClientFactory.CreateClient("Default");
        _logger = logger;
        _baseUrl = config.Value.Host.TrimEnd('/');
    }

    [KernelFunction]
    [Description("Executes a LogQL query against Loki. Returns matching log entries.")]
    public async Task<string> QueryLogs(
        [Description("LogQL query expression (e.g., '{container_name=\"traefik\"}')")] string query,
        [Description("Maximum number of log entries to return (default 100)")] int limit = 100)
    {
        _logger.LogDebug("Executing LogQL query: {Query}", query);

        try
        {
            var encodedQuery = Uri.EscapeDataString(query);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
            var oneHourAgo = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds() * 1_000_000;

            var url = $"{_baseUrl}/loki/api/v1/query_range?query={encodedQuery}&start={oneHourAgo}&end={now}&limit={limit}";
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<LokiQueryResponse>();

            if (result?.Data?.Result == null || result.Data.Result.Count == 0)
            {
                return "Query returned no results.";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Query: `{query}` (last hour, limit {limit})\n");

            var allLogs = new List<(DateTime Timestamp, string Stream, string Message)>();

            foreach (var stream in result.Data.Result)
            {
                var streamLabels = FormatLabels(stream.Stream);

                if (stream.Values != null)
                {
                    foreach (var value in stream.Values)
                    {
                        if (value.Length >= 2)
                        {
                            var timestamp = ParseNanoseconds(value[0].ToString() ?? "0");
                            var message = value[1].ToString() ?? "";
                            allLogs.Add((timestamp, streamLabels, message));
                        }
                    }
                }
            }

            // Sort by timestamp descending
            var sortedLogs = allLogs
                .OrderByDescending(l => l.Timestamp)
                .Take(limit)
                .ToList();

            foreach (var log in sortedLogs)
            {
                var time = log.Timestamp.ToString("HH:mm:ss");
                var message = log.Message.Length > 200
                    ? log.Message[..197] + "..."
                    : log.Message;
                sb.AppendLine($"`{time}` {message}");
            }

            if (allLogs.Count > limit)
            {
                sb.AppendLine($"\n... {allLogs.Count - limit} more entries not shown");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing LogQL query: {Query}", query);
            return $"Error executing query: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Gets recent logs for a specific Docker container.")]
    public async Task<string> GetContainerLogs(
        [Description("Container name")] string containerName,
        [Description("Time range like '1h', '30m', '15m' (default 1h)")] string since = "1h")
    {
        _logger.LogDebug("Getting logs for container {Container} since {Since}", containerName, since);

        var query = $"{{container_name=\"{containerName}\"}}";
        var duration = ParseDuration(since);

        try
        {
            var encodedQuery = Uri.EscapeDataString(query);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
            var start = DateTimeOffset.UtcNow.Subtract(duration).ToUnixTimeMilliseconds() * 1_000_000;

            var url = $"{_baseUrl}/loki/api/v1/query_range?query={encodedQuery}&start={start}&end={now}&limit=100";
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<LokiQueryResponse>();

            if (result?.Data?.Result == null || result.Data.Result.Count == 0)
            {
                return $"No logs found for container '{containerName}' in the last {since}.";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"**Logs for {containerName}** (last {since}):\n```");

            var allLogs = new List<(DateTime Timestamp, string Message)>();

            foreach (var stream in result.Data.Result)
            {
                if (stream.Values != null)
                {
                    foreach (var value in stream.Values)
                    {
                        if (value.Length >= 2)
                        {
                            var timestamp = ParseNanoseconds(value[0].ToString() ?? "0");
                            var message = value[1].ToString() ?? "";
                            allLogs.Add((timestamp, message));
                        }
                    }
                }
            }

            var sortedLogs = allLogs
                .OrderByDescending(l => l.Timestamp)
                .Take(50)
                .Reverse() // Show oldest first within the 50
                .ToList();

            foreach (var log in sortedLogs)
            {
                var time = log.Timestamp.ToString("HH:mm:ss");
                sb.AppendLine($"[{time}] {log.Message}");
            }

            sb.AppendLine("```");

            if (allLogs.Count > 50)
            {
                sb.AppendLine($"Showing 50 of {allLogs.Count} log entries.");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting container logs for {Container}", containerName);
            return $"Error getting logs: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Searches all logs for specific text (grep-style search).")]
    public async Task<string> SearchLogs(
        [Description("Text to search for in logs")] string searchText,
        [Description("Time range like '1h', '30m', '15m' (default 1h)")] string since = "1h")
    {
        _logger.LogDebug("Searching logs for '{SearchText}' since {Since}", searchText, since);

        // Use line_format to filter
        var escapedText = searchText.Replace("\"", "\\\"");
        var query = $"{{job=~\".+\"}} |~ \"(?i){escapedText}\"";
        var duration = ParseDuration(since);

        try
        {
            var encodedQuery = Uri.EscapeDataString(query);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
            var start = DateTimeOffset.UtcNow.Subtract(duration).ToUnixTimeMilliseconds() * 1_000_000;

            var url = $"{_baseUrl}/loki/api/v1/query_range?query={encodedQuery}&start={start}&end={now}&limit=50";
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<LokiQueryResponse>();

            if (result?.Data?.Result == null || result.Data.Result.Count == 0)
            {
                return $"No logs found containing '{searchText}' in the last {since}.";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"**Search results for '{searchText}'** (last {since}):\n");

            var allLogs = new List<(DateTime Timestamp, string Container, string Message)>();

            foreach (var stream in result.Data.Result)
            {
                var container = stream.Stream?.GetValueOrDefault("container_name") ?? "unknown";

                if (stream.Values != null)
                {
                    foreach (var value in stream.Values)
                    {
                        if (value.Length >= 2)
                        {
                            var timestamp = ParseNanoseconds(value[0].ToString() ?? "0");
                            var message = value[1].ToString() ?? "";
                            allLogs.Add((timestamp, container, message));
                        }
                    }
                }
            }

            var sortedLogs = allLogs
                .OrderByDescending(l => l.Timestamp)
                .Take(20)
                .ToList();

            foreach (var log in sortedLogs)
            {
                var time = log.Timestamp.ToString("HH:mm:ss");
                var message = log.Message.Length > 150
                    ? log.Message[..147] + "..."
                    : log.Message;
                sb.AppendLine($"`{time}` **{log.Container}**: {message}");
            }

            if (allLogs.Count > 20)
            {
                sb.AppendLine($"\n... {allLogs.Count - 20} more matches not shown");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching logs for '{SearchText}'", searchText);
            return $"Error searching logs: {ex.Message}";
        }
    }

    private static string FormatLabels(Dictionary<string, string>? labels)
    {
        if (labels == null || labels.Count == 0)
        {
            return "{}";
        }

        var container = labels.GetValueOrDefault("container_name") ?? labels.GetValueOrDefault("job") ?? "unknown";
        return container;
    }

    private static DateTime ParseNanoseconds(string nanoseconds)
    {
        if (long.TryParse(nanoseconds, out var ns))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ns / 1_000_000).DateTime;
        }

        return DateTime.UtcNow;
    }

    private static TimeSpan ParseDuration(string duration)
    {
        if (string.IsNullOrWhiteSpace(duration))
        {
            return TimeSpan.FromHours(1);
        }

        var unit = duration[^1];
        if (!int.TryParse(duration[..^1], out var value))
        {
            return TimeSpan.FromHours(1);
        }

        return unit switch
        {
            'm' => TimeSpan.FromMinutes(value),
            'h' => TimeSpan.FromHours(value),
            'd' => TimeSpan.FromDays(value),
            _ => TimeSpan.FromHours(1)
        };
    }

    private sealed class LokiQueryResponse
    {
        public string Status { get; set; } = "";
        public LokiData? Data { get; set; }
    }

    private sealed class LokiData
    {
        public string ResultType { get; set; } = "";
        public List<LokiStream> Result { get; set; } = [];
    }

    private sealed class LokiStream
    {
        public Dictionary<string, string>? Stream { get; set; }
        public List<JsonElement[]>? Values { get; set; }
    }
}
