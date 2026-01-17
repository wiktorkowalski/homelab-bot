namespace HomelabBot.Configuration;

public sealed class BotConfiguration
{
    public const string SectionName = "Bot";

    public required string DiscordToken { get; init; }
    public required string OpenRouterApiKey { get; init; }
    public string OpenRouterModel { get; init; } = "google/gemini-3-flash-preview";
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
