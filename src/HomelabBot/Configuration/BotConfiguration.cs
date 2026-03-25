namespace HomelabBot.Configuration;

/// <summary>
/// Single-user homelab bot — the owner's Discord ID is hardcoded here
/// rather than duplicated across every feature config section.
/// </summary>
public static class HomelabOwner
{
    public const ulong DiscordUserId = 170921674840080384;
}

public sealed class BotConfiguration
{
    public const string SectionName = "Bot";

    public required string DiscordToken { get; init; }

    // Anthropic (primary) — set AnthropicApiKey + AnthropicBaseUrl to enable
    public string? AnthropicApiKey { get; init; }
    public string? AnthropicBaseUrl { get; init; }
    public string AnthropicModel { get; init; } = "claude-opus-4-6";

    // OpenRouter (fallback)
    public required string OpenRouterApiKey { get; init; }
    public string OpenRouterModel { get; init; } = "anthropic/claude-sonnet-4.6";
    public string OpenRouterEndpoint { get; init; } = "https://openrouter.ai/api/v1";

    public List<ulong> DedicatedChannels { get; init; } = [];
    public Dictionary<string, string> ExternalUrls { get; init; } = [];
}

public sealed class MikroTikConfiguration
{
    public const string SectionName = "MikroTik";

    public required string Host { get; init; }
    public int ApiPort { get; init; } = 8728;
    public required string Username { get; init; }
    public required string Password { get; init; }
}

public sealed class TrueNASConfiguration
{
    public const string SectionName = "TrueNAS";

    public required string Host { get; init; }
    public required string ApiKey { get; init; }
}

public sealed class HomeAssistantConfiguration
{
    public const string SectionName = "HomeAssistant";

    public required string Host { get; init; }
    public required string AccessToken { get; init; }
}

public sealed class PrometheusConfiguration
{
    public const string SectionName = "Prometheus";

    public string Host { get; init; } = "http://prometheus:9090";
}

public sealed class GrafanaConfiguration
{
    public const string SectionName = "Grafana";

    public string Host { get; init; } = "http://grafana:3000";
    public required string ApiKey { get; init; }
}

public sealed class AlertmanagerConfiguration
{
    public const string SectionName = "Alertmanager";

    public string Host { get; init; } = "http://alertmanager:9093";
}

public sealed class LokiConfiguration
{
    public const string SectionName = "Loki";

    public string Host { get; init; } = "http://loki:3100";
}

public sealed class NtfyConfiguration
{
    public const string SectionName = "Ntfy";

    public string Host { get; init; } = "http://ntfy:80";
    public string DefaultTopic { get; init; } = "alerts";
}

public sealed class LangfuseConfiguration
{
    public const string SectionName = "Langfuse";

    public required string Endpoint { get; init; }
    public required string PublicKey { get; init; }
    public required string SecretKey { get; init; }
}

public sealed class DailySummaryConfiguration
{
    public const string SectionName = "DailySummary";

    public bool Enabled { get; init; } = false;
    public string ScheduleTime { get; init; } = "08:00";
    public string TimeZone { get; init; } = "Europe/Warsaw";
}

public sealed class AlertWebhookConfiguration
{
    public const string SectionName = "AlertWebhook";
}

public sealed class AnomalyDetectionConfiguration
{
    public const string SectionName = "AnomalyDetection";

    public bool Enabled { get; init; } = true;
    public int HeuristicIntervalMinutes { get; init; } = 60;
    public int LlmIntervalTicks { get; init; } = 1;
}

public sealed class SecurityAuditConfiguration
{
    public const string SectionName = "SecurityAudit";

    public bool Enabled { get; init; } = false;
    public DayOfWeek ScheduleDay { get; init; } = DayOfWeek.Sunday;
    public string ScheduleTime { get; init; } = "04:00";
    public string TimeZone { get; init; } = "Europe/Warsaw";
}

public sealed class LogAnomalyConfiguration
{
    public const string SectionName = "LogAnomaly";

    public bool Enabled { get; init; } = true;
    public int IntervalMinutes { get; init; } = 30;
    public int ErrorThreshold { get; init; } = 50;
}

public sealed class HealthScoreConfiguration
{
    public const string SectionName = "HealthScore";

    public bool Enabled { get; init; } = true;
    public int IntervalMinutes { get; init; } = 15;
    public int AlertDropThreshold { get; init; } = 15;
    public int CriticalAlertWeight { get; init; } = 20;
    public int WarningAlertWeight { get; init; } = 5;
    public int StoppedContainerWeight { get; init; } = 10;
    public int UnhealthyPoolWeight { get; init; } = 25;
    public int DownTargetWeight { get; init; } = 15;
    public int MissingContainersWeight { get; init; } = 15;
    public int MissingPoolsWeight { get; init; } = 15;
    public int MissingRouterWeight { get; init; } = 10;
    public int MissingMonitoringWeight { get; init; } = 15;
}

public sealed class KnowledgeRefreshConfiguration
{
    public const string SectionName = "KnowledgeRefresh";

    public bool Enabled { get; init; } = false;
    public string ScheduleTime { get; init; } = "05:00";
    public string TimeZone { get; init; } = "Europe/Warsaw";
    public bool NotifyOnChanges { get; init; } = false;
}

public sealed class AutoRemediationConfiguration
{
    public const string SectionName = "AutoRemediation";

    public bool Enabled { get; init; } = true;
    public double MinSuccessRate { get; init; } = 80;
    public int MinFeedbackCount { get; init; } = 3;
    public int MaxRestartsPerHour { get; init; } = 3;
}

public sealed class McpServerConfiguration
{
    public const string SectionName = "McpServer";

    public bool Enabled { get; init; }
    public string ApiKey { get; init; } = "";
}
