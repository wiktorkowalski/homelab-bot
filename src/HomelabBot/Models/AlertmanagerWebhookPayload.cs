using System.Text.Json.Serialization;

namespace HomelabBot.Models;

public sealed class AlertmanagerWebhookPayload
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("alerts")]
    public List<AlertmanagerWebhookAlert> Alerts { get; init; } = [];

    [JsonPropertyName("groupLabels")]
    public Dictionary<string, string> GroupLabels { get; init; } = [];

    [JsonPropertyName("commonLabels")]
    public Dictionary<string, string> CommonLabels { get; init; } = [];

    [JsonPropertyName("commonAnnotations")]
    public Dictionary<string, string> CommonAnnotations { get; init; } = [];

    [JsonPropertyName("externalURL")]
    public string? ExternalUrl { get; init; }

    [JsonPropertyName("groupKey")]
    public string? GroupKey { get; init; }
}

public sealed class AlertmanagerWebhookAlert
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("labels")]
    public Dictionary<string, string> Labels { get; init; } = [];

    [JsonPropertyName("annotations")]
    public Dictionary<string, string> Annotations { get; init; } = [];

    [JsonPropertyName("startsAt")]
    public DateTime StartsAt { get; init; }

    [JsonPropertyName("endsAt")]
    public DateTime? EndsAt { get; init; }

    [JsonPropertyName("generatorURL")]
    public string? GeneratorUrl { get; init; }

    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; init; }

    public string AlertName => Labels.GetValueOrDefault("alertname", "Unknown Alert");
    public string Severity => Labels.GetValueOrDefault("severity", "unknown");
    public string? Instance => Labels.GetValueOrDefault("instance");
    public string? Summary => Annotations.GetValueOrDefault("summary");
    public string? Description => Annotations.GetValueOrDefault("description");

    public bool IsFiring => Status.Equals("firing", StringComparison.OrdinalIgnoreCase);
    public bool IsResolved => Status.Equals("resolved", StringComparison.OrdinalIgnoreCase);

    public TimeSpan? Duration
    {
        get
        {
            if (EndsAt.HasValue && EndsAt.Value > StartsAt)
                return EndsAt.Value - StartsAt;
            if (IsFiring)
                return DateTime.UtcNow - StartsAt;
            return null;
        }
    }
}
