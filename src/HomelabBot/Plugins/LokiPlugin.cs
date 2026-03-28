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
    [Description("Lists available log labels in Loki. Use this to discover what labels are available for querying.")]
    public async Task<string> ListLabels()
    {
        _logger.LogDebug("Listing Loki labels...");

        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/loki/api/v1/labels");
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<LokiLabelsResponse>();

            if (result?.Data == null || result.Data.Count == 0)
            {
                return "No labels found.";
            }

            var sb = new StringBuilder();
            sb.AppendLine("**Available Loki Labels**\n");

            foreach (var label in result.Data.OrderBy(l => l))
            {
                sb.AppendLine($"- `{label}`");
            }

            sb.AppendLine("\nUse these labels in queries like: `{label_name=\"value\"}`");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing labels");
            return $"Error listing labels: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Lists values for a specific label. Useful to see what containers/services are available.")]
    public async Task<string> ListLabelValues([Description("Label name (e.g., 'container_name', 'compose_service', 'job')")] string labelName)
    {
        _logger.LogDebug("Listing values for label {Label}...", labelName);

        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/loki/api/v1/label/{labelName}/values");
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<LokiLabelsResponse>();

            if (result?.Data == null || result.Data.Count == 0)
            {
                return $"No values found for label '{labelName}'.";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"**Values for `{labelName}`** ({result.Data.Count})\n");

            foreach (var value in result.Data.OrderBy(v => v).Take(50))
            {
                sb.AppendLine($"- {value}");
            }

            if (result.Data.Count > 50)
            {
                sb.AppendLine($"\n... and {result.Data.Count - 50} more");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing label values");
            return $"Error listing label values: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Executes a LogQL query against Loki. Returns matching log entries. Use ListLabels first to discover available labels.")]
    public async Task<string> QueryLogs(
        [Description("LogQL query expression (e.g., '{compose_service=\"traefik\"}' or '{container_name=~\".*traefik.*\"}')")] string query,
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
    [Description("Gets recent logs for a specific Docker container. Tries multiple label patterns to find logs.")]
    public async Task<string> GetContainerLogs(
        [Description("Container name (will try multiple label patterns like compose_service, container_name)")] string containerName,
        [Description("Time range like '1h', '30m', '15m' (default 1h)")] string since = "1h")
    {
        _logger.LogDebug("Getting logs for container {Container} since {Since}", containerName, since);

        var duration = ParseDuration(since);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
        var start = DateTimeOffset.UtcNow.Subtract(duration).ToUnixTimeMilliseconds() * 1_000_000;

        // Try different label patterns that Docker/Loki might use
        var labelPatterns = new[]
        {
            $"{{compose_service=\"{containerName}\"}}",
            $"{{container_name=\"{containerName}\"}}",
            $"{{container_name=~\".*{containerName}.*\"}}",
            $"{{compose_service=~\".*{containerName}.*\"}}"
        };

        foreach (var query in labelPatterns)
        {
            try
            {
                var encodedQuery = Uri.EscapeDataString(query);
                var url = $"{_baseUrl}/loki/api/v1/query_range?query={encodedQuery}&start={start}&end={now}&limit=100";
                var response = await _httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var result = await response.Content.ReadFromJsonAsync<LokiQueryResponse>();

                if (result?.Data?.Result == null || result.Data.Result.Count == 0)
                {
                    continue;
                }

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

                if (allLogs.Count == 0)
                {
                    continue;
                }

                var sb = new StringBuilder();
                sb.AppendLine($"**Logs for {containerName}** (last {since}):\n```");

                var sortedLogs = allLogs
                    .OrderByDescending(l => l.Timestamp)
                    .Take(50)
                    .Reverse()
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
            catch
            {
                // Try next pattern
                continue;
            }
        }

        return $"No logs found for container '{containerName}' in the last {since}. Try using ListLabels and ListLabelValues to discover available labels.";
    }

    public async Task<Dictionary<string, long>> GetErrorCountsByContainerAsync(string since = "1h", string? containerName = null)
    {
        var normalizedSince = NormalizeDuration(since);
        var selector = string.IsNullOrWhiteSpace(containerName)
            ? "{compose_service=~\".+\"}"
            : $"{{compose_service=\"{containerName}\"}}";
        var query = $"sum by (compose_service) (count_over_time({selector} |~ \"(?i)(\\\\berror\\\\b|\\\\bexception\\\\b|\\\\bfailed\\\\b|\\\\bfailure\\\\b)\" [{normalizedSince}]))";

        var encodedQuery = Uri.EscapeDataString(query);
        var url = $"{_baseUrl}/loki/api/v1/query?query={encodedQuery}";
        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<LokiQueryResponse>();
        var entries = new Dictionary<string, long>();

        if (result?.Data?.Result == null)
            return entries;

        foreach (var stream in result.Data.Result)
        {
            var labels = stream.Stream ?? stream.Metric;
            var container = labels?.GetValueOrDefault("compose_service") ?? "unknown";
            long count = 0;

            if (stream.Values is { Count: > 0 } && stream.Values[0].Length >= 2)
            {
                long.TryParse(stream.Values[0][1].ToString(), out count);
            }
            else if (stream.Value is { Length: >= 2 })
            {
                long.TryParse(stream.Value[1].ToString(), out count);
            }

            if (count > 0)
                entries[container] = count;
        }

        return entries;
    }

    [KernelFunction]
    [Description("Counts error/exception log lines per container over a time window. Returns container name and error count.")]
    public async Task<string> CountErrorsByContainer(
        [Description("Time range like '1h', '6h', '24h' (default 1h)")] string since = "1h",
        string? containerName = null)
    {
        _logger.LogDebug("Counting errors by container since {Since}", since);

        try
        {
            var entries = await GetErrorCountsByContainerAsync(since, containerName);

            if (entries.Count == 0)
            {
                return $"No error logs found in the last {since}.";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"**Error counts by container** (last {since}):\n");

            foreach (var entry in entries.OrderByDescending(e => e.Value))
            {
                var emoji = entry.Value > 100 ? "🔴" : entry.Value > 10 ? "🟡" : "🟢";
                sb.AppendLine($"{emoji} **{entry.Key}**: {entry.Value} errors");
            }

            sb.AppendLine($"\nTotal: {entries.Values.Sum()} errors across {entries.Count} containers");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error counting errors by container");
            return $"Error counting errors: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Detects critical log patterns (fatal, panic, OOM, segfault, killed) across all containers.")]
    public async Task<string> DetectCriticalPatterns(
        [Description("Time range like '1h', '6h', '24h' (default 1h)")] string since = "1h")
    {
        _logger.LogDebug("Detecting critical patterns since {Since}", since);

        var duration = ParseDuration(since);
        var query = "{compose_service=~\".+\",compose_service!=\"loki\"} |~ \"(?i)(\\\\bfatal\\\\b|\\\\bpanic\\\\b|\\\\boom\\\\b|out of memory|killed process|segfault)\"";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
        var start = DateTimeOffset.UtcNow.Subtract(duration).ToUnixTimeMilliseconds() * 1_000_000;

        try
        {
            var encodedQuery = Uri.EscapeDataString(query);
            var url = $"{_baseUrl}/loki/api/v1/query_range?query={encodedQuery}&start={start}&end={now}&limit=50";
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<LokiQueryResponse>();

            if (result?.Data?.Result == null || result.Data.Result.Count == 0)
            {
                return $"No critical patterns (fatal/panic/OOM/segfault) found in the last {since}.";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"**Critical patterns detected** (last {since}):\n");

            var allEvents = new List<(DateTime Timestamp, string Container, string Message)>();

            foreach (var stream in result.Data.Result)
            {
                var container = stream.Stream?.GetValueOrDefault("compose_service")
                    ?? stream.Stream?.GetValueOrDefault("container_name")
                    ?? "unknown";

                if (stream.Values != null)
                {
                    foreach (var value in stream.Values)
                    {
                        if (value.Length >= 2)
                        {
                            var timestamp = ParseNanoseconds(value[0].ToString() ?? "0");
                            var message = value[1].ToString() ?? "";
                            allEvents.Add((timestamp, container, message));
                        }
                    }
                }
            }

            var grouped = allEvents
                .GroupBy(e => e.Container)
                .OrderByDescending(g => g.Count());

            foreach (var group in grouped)
            {
                sb.AppendLine($"🔴 **{group.Key}** ({group.Count()} events):");
                foreach (var evt in group.OrderByDescending(e => e.Timestamp).Take(3))
                {
                    var time = evt.Timestamp.ToString("HH:mm:ss");
                    var msg = evt.Message.Length > 120 ? evt.Message[..117] + "..." : evt.Message;
                    sb.AppendLine($"  `{time}` {msg}");
                }

                if (group.Count() > 3)
                {
                    sb.AppendLine($"  ... and {group.Count() - 3} more");
                }
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detecting critical patterns");
            return $"Error detecting critical patterns: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Searches all logs for specific text (grep-style search).")]
    public async Task<string> SearchLogs(
        [Description("Text to search for in logs")] string searchText,
        [Description("Time range like '1h', '30m', '15m' (default 1h)")] string since = "1h",
        string? containerName = null)
    {
        _logger.LogDebug("Searching logs for '{SearchText}' since {Since}", searchText, since);

        var escapedText = searchText.Replace("\"", "\\\"");
        var selector = string.IsNullOrWhiteSpace(containerName)
            ? "{compose_service=~\".+\"}"
            : $"{{compose_service=\"{containerName}\"}}";
        var query = $"{selector} |~ \"(?i){escapedText}\"";
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
                var container = stream.Stream?.GetValueOrDefault("compose_service")
                    ?? stream.Stream?.GetValueOrDefault("container_name") ?? "unknown";

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

    private static string NormalizeDuration(string input)
    {
        var duration = ParseDuration(input);
        var totalMinutes = (int)duration.TotalMinutes;
        return totalMinutes switch
        {
            < 60 => $"{totalMinutes}m",
            < 1440 => $"{totalMinutes / 60}h",
            _ => $"{totalMinutes / 1440}d"
        };
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
        public Dictionary<string, string>? Metric { get; set; }
        public List<JsonElement[]>? Values { get; set; }
        public JsonElement[]? Value { get; set; }
    }

    private sealed class LokiLabelsResponse
    {
        public string Status { get; set; } = "";
        public List<string> Data { get; set; } = [];
    }
}
