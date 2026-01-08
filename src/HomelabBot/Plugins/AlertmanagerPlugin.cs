using System.ComponentModel;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using HomelabBot.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace HomelabBot.Plugins;

public sealed class AlertmanagerPlugin
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AlertmanagerPlugin> _logger;
    private readonly string _baseUrl;

    public AlertmanagerPlugin(
        IHttpClientFactory httpClientFactory,
        IOptions<AlertmanagerConfiguration> config,
        ILogger<AlertmanagerPlugin> logger)
    {
        _httpClient = httpClientFactory.CreateClient("Default");
        _logger = logger;
        _baseUrl = config.Value.Host.TrimEnd('/');
    }

    [KernelFunction]
    [Description("Gets all currently firing alerts from Alertmanager.")]
    public async Task<string> GetActiveAlerts()
    {
        _logger.LogDebug("Getting active alerts...");

        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/api/v2/alerts?active=true&silenced=false&inhibited=false");
            response.EnsureSuccessStatusCode();

            var alerts = await response.Content.ReadFromJsonAsync<List<AlertmanagerAlert>>();

            if (alerts == null || alerts.Count == 0)
            {
                return "No active alerts.";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"**{alerts.Count} Active Alert(s)**\n");

            var grouped = alerts
                .GroupBy(a => a.Labels?.GetValueOrDefault("alertname") ?? "unknown")
                .OrderByDescending(g => g.Count());

            foreach (var group in grouped)
            {
                var severity = group.First().Labels?.GetValueOrDefault("severity") ?? "unknown";
                var emoji = severity switch
                {
                    "critical" => "🔴",
                    "warning" => "🟡",
                    "info" => "🔵",
                    _ => "⚪"
                };

                sb.AppendLine($"{emoji} **{group.Key}** ({severity}) - {group.Count()} instance(s)");

                foreach (var alert in group.Take(3))
                {
                    var instance = alert.Labels?.GetValueOrDefault("instance") ?? "N/A";
                    var summary = alert.Annotations?.GetValueOrDefault("summary") ?? alert.Annotations?.GetValueOrDefault("description") ?? "";

                    if (!string.IsNullOrEmpty(summary))
                    {
                        sb.AppendLine($"   {instance}: {summary}");
                    }
                    else
                    {
                        sb.AppendLine($"   Instance: {instance}");
                    }
                }

                if (group.Count() > 3)
                {
                    sb.AppendLine($"   ... and {group.Count() - 3} more");
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting active alerts");
            return $"Error getting alerts: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Gets alerts grouped by labels (shows how alerts are organized).")]
    public async Task<string> GetAlertGroups()
    {
        _logger.LogDebug("Getting alert groups...");

        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/api/v2/alerts/groups");
            response.EnsureSuccessStatusCode();

            var groups = await response.Content.ReadFromJsonAsync<List<AlertGroup>>();

            if (groups == null || groups.Count == 0)
            {
                return "No alert groups found.";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"**Alert Groups ({groups.Count})**\n");

            foreach (var group in groups)
            {
                var labels = group.Labels != null
                    ? string.Join(", ", group.Labels.Select(l => $"{l.Key}={l.Value}"))
                    : "no labels";

                var alertCount = group.Alerts?.Count ?? 0;
                sb.AppendLine($"- **{labels}**: {alertCount} alert(s)");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting alert groups");
            return $"Error getting alert groups: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Creates a silence for a specific alert to suppress notifications for a duration.")]
    public async Task<string> SilenceAlert(
        [Description("Alert name to silence")] string alertName,
        [Description("Duration in format like '2h', '30m', '1d'")] string duration)
    {
        _logger.LogInformation("Creating silence for alert {AlertName} for {Duration}", alertName, duration);

        try
        {
            var durationSpan = ParseDuration(duration);
            if (durationSpan == TimeSpan.Zero)
            {
                return "Invalid duration format. Use formats like '2h', '30m', '1d'.";
            }

            var now = DateTime.UtcNow;
            var silence = new
            {
                matchers = new[]
                {
                    new
                    {
                        name = "alertname",
                        value = alertName,
                        isRegex = false,
                        isEqual = true
                    }
                },
                startsAt = now.ToString("o"),
                endsAt = now.Add(durationSpan).ToString("o"),
                createdBy = "HomeLabBot",
                comment = $"Silenced by HomeLabBot for {duration}"
            };

            var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/api/v2/silences", silence);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<SilenceResponse>();

            _logger.LogInformation("Created silence {SilenceId} for alert {AlertName}", result?.SilenceID, alertName);
            return $"Created silence for **{alertName}** until {now.Add(durationSpan):g} UTC (ID: {result?.SilenceID})";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating silence for {AlertName}", alertName);
            return $"Error creating silence: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Lists all active silences.")]
    public async Task<string> ListSilences()
    {
        _logger.LogDebug("Listing silences...");

        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/api/v2/silences");
            response.EnsureSuccessStatusCode();

            var silences = await response.Content.ReadFromJsonAsync<List<Silence>>();

            var activeSilences = silences?
                .Where(s => s.Status?.State == "active")
                .ToList();

            if (activeSilences == null || activeSilences.Count == 0)
            {
                return "No active silences.";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"**Active Silences ({activeSilences.Count})**\n");

            foreach (var silence in activeSilences)
            {
                var matchers = silence.Matchers != null
                    ? string.Join(", ", silence.Matchers.Select(m => $"{m.Name}={m.Value}"))
                    : "no matchers";

                sb.AppendLine($"- **{silence.Id?[..8]}**: {matchers}");
                sb.AppendLine($"  Ends: {silence.EndsAt:g} UTC");
                sb.AppendLine($"  By: {silence.CreatedBy} - {silence.Comment}");
                sb.AppendLine();
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing silences");
            return $"Error listing silences: {ex.Message}";
        }
    }

    private static TimeSpan ParseDuration(string duration)
    {
        if (string.IsNullOrWhiteSpace(duration))
        {
            return TimeSpan.Zero;
        }

        var unit = duration[^1];
        if (!int.TryParse(duration[..^1], out var value))
        {
            return TimeSpan.Zero;
        }

        return unit switch
        {
            'm' => TimeSpan.FromMinutes(value),
            'h' => TimeSpan.FromHours(value),
            'd' => TimeSpan.FromDays(value),
            _ => TimeSpan.Zero
        };
    }

    private sealed class AlertmanagerAlert
    {
        public Dictionary<string, string>? Labels { get; set; }
        public Dictionary<string, string>? Annotations { get; set; }
        public string? Status { get; set; }
    }

    private sealed class AlertGroup
    {
        public Dictionary<string, string>? Labels { get; set; }
        public List<AlertmanagerAlert>? Alerts { get; set; }
    }

    private sealed class SilenceResponse
    {
        [JsonPropertyName("silenceID")]
        public string? SilenceID { get; set; }
    }

    private sealed class Silence
    {
        public string? Id { get; set; }
        public List<SilenceMatcher>? Matchers { get; set; }
        public DateTime StartsAt { get; set; }
        public DateTime EndsAt { get; set; }
        public string? CreatedBy { get; set; }
        public string? Comment { get; set; }
        public SilenceStatus? Status { get; set; }
    }

    private sealed class SilenceMatcher
    {
        public string? Name { get; set; }
        public string? Value { get; set; }
        public bool IsRegex { get; set; }
        public bool IsEqual { get; set; }
    }

    private sealed class SilenceStatus
    {
        public string? State { get; set; }
    }
}
