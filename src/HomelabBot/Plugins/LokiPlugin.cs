using System.ComponentModel;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HomelabBot.Configuration;
using HomelabBot.Helpers;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using ModelContextProtocol.Server;

namespace HomelabBot.Plugins;

[McpServerToolType]
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
        _logger.LogInformation("Listing Loki labels...");

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
        _logger.LogInformation("Listing values for label {Label}...", labelName);

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
    [McpServerTool(Name = "QueryLoki")]
    [Description("Executes a LogQL query against Loki over a time range (default last hour, max 7d). Returns matching log entries. Use ListLabels first to discover available labels.")]
    public async Task<string> QueryLogs(
        [Description("LogQL query expression (e.g., '{compose_service=\"traefik\"}' or '{container_name=~\".*traefik.*\"}')")] string query,
        [Description("Maximum number of log entries to return (default 100, max 5000)")] int limit = 100,
        [Description("Time range to look back, like '30m', '1h', '24h', '7d' (default 1h, max 7d)")] string since = "1h")
    {
        if (!TryParseSince(since, out var lookback, out var sinceError))
        {
            return sinceError;
        }

        limit = LogQl.ClampLimit(limit);
        var range = FormattingHelpers.FormatCompactDuration(lookback);

        // Query text stays at Debug: at Information it lands in Loki and matches later log searches.
        _logger.LogInformation("Executing LogQL query over {Range} with limit {Limit}", range, limit);
        _logger.LogDebug("LogQL query text: {Query}", query);

        try
        {
            var encodedQuery = Uri.EscapeDataString(query);
            var nowUtc = DateTimeOffset.UtcNow;
            var now = nowUtc.ToUnixTimeMilliseconds() * 1_000_000;
            var start = nowUtc.Subtract(lookback).ToUnixTimeMilliseconds() * 1_000_000;

            var url = $"{_baseUrl}/loki/api/v1/query_range?query={encodedQuery}&start={start}&end={now}&limit={limit}";
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<LokiQueryResponse>();

            if (result?.Data?.Result == null || result.Data.Result.Count == 0)
            {
                return "Query returned no results.";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Query: `{query}` (last {range}, limit {limit})\n");

            var allLogs = new List<(DateTime Timestamp, string Stream, string Message)>();

            foreach (var stream in result.Data.Result)
            {
                var streamLabels = FormatLabels(stream.Labels);

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
            // Query text only at Debug, also on failure, so the bot's own logs never match it.
            _logger.LogError(ex, "Error executing LogQL query over {Range}", range);
            _logger.LogDebug("Failed LogQL query text: {Query}", query);
            return $"Error executing query: {ex.Message}";
        }
    }

    [KernelFunction]
    [McpServerTool(Name = "GetContainerLogs")]
    [Description("Gets historical logs for a Docker container from Loki with time-range filtering. Best for searching logs over a specific period (e.g. last 1h, 6h). For real-time tail of the latest output, use Docker's GetContainerLogsFromDocker instead.")]
    public async Task<string> GetContainerLogsFromLoki(
        [Description("Container name (will try multiple label patterns like compose_service, container_name)")] string containerName,
        [Description("Time range like '1h', '30m', '15m' (default 1h, max 7d)")] string since = "1h")
    {
        if (!TryParseSince(since, out var duration, out var sinceError))
        {
            return sinceError;
        }

        var range = FormattingHelpers.FormatCompactDuration(duration);
        _logger.LogInformation("Getting logs for container {Container} over {Range}", containerName, range);

        var nowUtc = DateTimeOffset.UtcNow;
        var now = nowUtc.ToUnixTimeMilliseconds() * 1_000_000;
        var start = nowUtc.Subtract(duration).ToUnixTimeMilliseconds() * 1_000_000;

        foreach (var query in BuildContainerSelectors(containerName))
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
                sb.AppendLine($"**Logs for {containerName}** (last {range}):\n```");

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
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Loki query failed for container {Container}", containerName);
                continue;
            }
        }

        return $"No logs found for container '{containerName}' in the last {range}. Try using ListLabels and ListLabelValues to discover available labels.";
    }

    // Keyword match kept for AnomalyDetectionService: switching it to levels would shift its thresholds.
    public Task<Dictionary<string, long>> GetErrorCountsByContainerAsync(string since = "1h", string? containerName = null) =>
        QueryCountsByContainerAsync(BuildCountQuery(
            "|~ \"(?i)(\\\\berror\\\\b|\\\\bexception\\\\b|\\\\bfailed\\\\b|\\\\bfailure\\\\b)\"",
            NormalizeDuration(since),
            containerName));

    internal static string BuildServiceSelector(string? containerName) =>
        string.IsNullOrWhiteSpace(containerName)
            ? "{compose_service=~\".+\"}"
            : $"{{compose_service={LogQl.QuoteString(containerName)}}}";

    // Exact label matches first, then substring regex matches, as Docker/Loki label names vary.
    internal static string[] BuildContainerSelectors(string containerName)
    {
        var exact = LogQl.QuoteString(containerName);
        var contains = LogQl.QuoteString($".*{LogQl.EscapeRegex(containerName)}.*");
        return
        [
            $"{{compose_service={exact}}}",
            $"{{container_name={exact}}}",
            $"{{container_name=~{contains}}}",
            $"{{compose_service=~{contains}}}",
        ];
    }

    internal static string BuildErrorLevelCountQuery(string logQlRange, string? containerName) =>
        BuildCountQuery($"|~ {LogQl.QuoteString(LogQl.ErrorLevelRegex)}", logQlRange, containerName);

    internal static string BuildSearchQuery(string searchText, string? containerName) =>
        $"{BuildServiceSelector(containerName)} {LogQl.ContainsIgnoreCaseFilter(searchText)}";

    private static string BuildCountQuery(string lineFilter, string logQlRange, string? containerName) =>
        $"sum by (compose_service) (count_over_time({BuildServiceSelector(containerName)} {lineFilter} [{logQlRange}]))";

    // Rejects invalid or too-long ranges up front; the caller returns the error text to the tool user.
    private bool TryParseSince(string since, out TimeSpan lookback, out string error)
    {
        if (FormattingHelpers.TryParseDuration(since, LogQl.MaxLookback, out lookback, out error))
        {
            return true;
        }

        _logger.LogWarning("Rejected Loki tool call with invalid time range {Since}", since);
        return false;
    }

    private async Task<Dictionary<string, long>> QueryCountsByContainerAsync(string query)
    {
        var encodedQuery = Uri.EscapeDataString(query);
        var url = $"{_baseUrl}/loki/api/v1/query?query={encodedQuery}";
        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<LokiQueryResponse>();
        var entries = new Dictionary<string, long>();

        if (result?.Data?.Result == null)
        {
            return entries;
        }

        foreach (var stream in result.Data.Result)
        {
            var container = stream.Labels?.GetValueOrDefault("compose_service") ?? "unknown";
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
            {
                entries[container] = count;
            }
        }

        return entries;
    }

    [KernelFunction]
    [McpServerTool(Name = "CountErrors")]
    [Description("Counts level-tagged error lines (log level error/fatal/critical/panic, e.g. [ERR], ERROR, level=error, \"level\":\"error\") per container over a time window. Lines that only mention error words in their text are not counted. Returns container name and error count.")]
    public async Task<string> CountErrorsByContainer(
        [Description("Time range like '1h', '6h', '24h' (default 1h, max 7d)")] string since = "1h",
        string? containerName = null)
    {
        if (!TryParseSince(since, out var lookback, out var sinceError))
        {
            return sinceError;
        }

        var range = FormattingHelpers.FormatCompactDuration(lookback);
        _logger.LogInformation("Counting error-level lines by container over {Range}", range);

        try
        {
            var entries = await QueryCountsByContainerAsync(BuildErrorLevelCountQuery(range, containerName));

            if (entries.Count == 0)
            {
                return $"No error logs found in the last {range}.";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"**Error counts by container** (last {range}):\n");

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
        _logger.LogInformation("Detecting critical patterns since {Since}", since);

        var duration = FormattingHelpers.ParseDuration(since);
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
                var container = stream.Labels?.GetValueOrDefault("compose_service")
                    ?? stream.Labels?.GetValueOrDefault("container_name")
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

    internal async Task<bool> HasCriticalPatternsAsync(string since = "1h", CancellationToken ct = default)
    {
        var duration = FormattingHelpers.ParseDuration(since);
        var query = "{compose_service=~\".+\",compose_service!=\"loki\"} |~ \"(?i)(\\\\bfatal\\\\b|\\\\bpanic\\\\b|\\\\boom\\\\b|out of memory|killed process|segfault)\"";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
        var start = DateTimeOffset.UtcNow.Subtract(duration).ToUnixTimeMilliseconds() * 1_000_000;

        var encodedQuery = Uri.EscapeDataString(query);
        var url = $"{_baseUrl}/loki/api/v1/query_range?query={encodedQuery}&start={start}&end={now}&limit=1";
        var response = await _httpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<LokiQueryResponse>(ct);
        return result?.Data?.Result is { Count: > 0 };
    }

    [KernelFunction]
    [McpServerTool]
    [Description("Searches all logs for specific text (grep-style search).")]
    public async Task<string> SearchLogs(
        [Description("Text to search for in logs")] string searchText,
        [Description("Time range like '1h', '30m', '15m' (default 1h, max 7d)")] string since = "1h",
        string? containerName = null)
    {
        if (!TryParseSince(since, out var duration, out var sinceError))
        {
            return sinceError;
        }

        var range = FormattingHelpers.FormatCompactDuration(duration);

        // Search text stays at Debug: at Information it lands in Loki and matches the next search.
        _logger.LogInformation("Searching logs over {Range}", range);
        _logger.LogDebug("Log search text: {SearchText}", searchText);

        var query = BuildSearchQuery(searchText, containerName);

        try
        {
            var encodedQuery = Uri.EscapeDataString(query);
            var nowUtc = DateTimeOffset.UtcNow;
            var now = nowUtc.ToUnixTimeMilliseconds() * 1_000_000;
            var start = nowUtc.Subtract(duration).ToUnixTimeMilliseconds() * 1_000_000;

            var url = $"{_baseUrl}/loki/api/v1/query_range?query={encodedQuery}&start={start}&end={now}&limit=50";
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<LokiQueryResponse>();

            if (result?.Data?.Result == null || result.Data.Result.Count == 0)
            {
                return $"No logs found containing '{searchText}' in the last {range}.";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"**Search results for '{searchText}'** (last {range}):\n");

            var allLogs = new List<(DateTime Timestamp, string Container, string Message)>();

            foreach (var stream in result.Data.Result)
            {
                var container = stream.Labels?.GetValueOrDefault("compose_service")
                    ?? stream.Labels?.GetValueOrDefault("container_name") ?? "unknown";

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
            // Search text only at Debug, also on failure, so the bot's own logs never match it.
            _logger.LogError(ex, "Error searching logs over {Range}", range);
            _logger.LogDebug("Failed log search text: {SearchText}", searchText);
            return $"Error searching logs: {ex.Message}";
        }
    }

    internal static string NormalizeDuration(string input)
    {
        var duration = FormattingHelpers.ParseDuration(input);
        var totalMinutes = (int)duration.TotalMinutes;
        return totalMinutes switch
        {
            < 60 => $"{totalMinutes}m",
            < 1440 => $"{totalMinutes / 60}h",
            _ => $"{totalMinutes / 1440}d"
        };
    }

    internal static string FormatLabels(Dictionary<string, string>? labels)
    {
        if (labels == null || labels.Count == 0)
        {
            return "{}";
        }

        var container = labels.GetValueOrDefault("container_name") ?? labels.GetValueOrDefault("job") ?? "unknown";
        return container;
    }

    internal static DateTime ParseNanoseconds(string nanoseconds)
    {
        if (long.TryParse(nanoseconds, out var ns))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ns / 1_000_000).DateTime;
        }

        return DateTime.UtcNow;
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

        public Dictionary<string, string>? Labels => Stream ?? Metric;

        public List<JsonElement[]>? Values { get; set; }

        public JsonElement[]? Value { get; set; }
    }

    private sealed class LokiLabelsResponse
    {
        public string Status { get; set; } = "";

        public List<string> Data { get; set; } = [];
    }
}
