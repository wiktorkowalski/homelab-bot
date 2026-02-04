using DSharpPlus.Entities;
using HomelabBot.Configuration;
using HomelabBot.Models;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public sealed class AlertWebhookService
{
    private readonly DiscordBotService _discordService;
    private readonly KernelService _kernelService;
    private readonly AlertWebhookConfiguration _config;
    private readonly ILogger<AlertWebhookService> _logger;

    private static readonly DiscordColor ColorFiringCritical = new("#DC2626");
    private static readonly DiscordColor ColorFiringWarning = new("#F59E0B");
    private static readonly DiscordColor ColorResolved = new("#22C55E");

    public AlertWebhookService(
        DiscordBotService discordService,
        KernelService kernelService,
        IOptions<AlertWebhookConfiguration> config,
        ILogger<AlertWebhookService> logger)
    {
        _discordService = discordService;
        _kernelService = kernelService;
        _config = config.Value;
        _logger = logger;
    }

    public async Task ProcessAlertsAsync(AlertmanagerWebhookPayload payload, CancellationToken ct = default)
    {
        if (_config.DiscordUserId == 0)
        {
            _logger.LogWarning("AlertWebhook: DiscordUserId not configured, skipping");
            return;
        }

        _logger.LogInformation("Processing {Count} alerts from Alertmanager", payload.Alerts.Count);

        foreach (var alert in payload.Alerts)
        {
            try
            {
                await ProcessAlertAsync(alert, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process alert {AlertName}", alert.AlertName);
            }
        }
    }

    private async Task ProcessAlertAsync(AlertmanagerWebhookAlert alert, CancellationToken ct)
    {
        var conversationId = (ulong)$"alert-{alert.Fingerprint ?? Guid.NewGuid().ToString()}".GetHashCode();

        string analysis;
        if (alert.IsFiring)
        {
            var prompt = $"""
                ALERT FIRING - Investigate this:
                Alert: {alert.AlertName}
                Severity: {alert.Severity}
                Instance: {alert.Instance ?? "unknown"}
                Description: {alert.Description ?? alert.Summary ?? "none"}
                Started: {alert.StartsAt:u}

                Use your tools to investigate what's happening. Check relevant logs, metrics, container status, etc.
                Provide a brief summary of what you found and any recommended actions.
                """;

            analysis = await _kernelService.ProcessMessageAsync(
                conversationId,
                prompt,
                _config.DiscordUserId,
                TraceType.Scheduled,
                ct);
        }
        else
        {
            analysis = $"Alert resolved after {FormatDuration(alert.Duration ?? TimeSpan.Zero)}.";
        }

        var embed = BuildAlertEmbed(alert, analysis);
        await _discordService.SendDmAsync(_config.DiscordUserId, embed);
    }

    private DiscordEmbed BuildAlertEmbed(AlertmanagerWebhookAlert alert, string analysis)
    {
        var color = GetAlertColor(alert);
        var emoji = alert.IsFiring ? "🔥" : "✅";
        var title = $"{emoji} {alert.AlertName}";

        var builder = new DiscordEmbedBuilder()
            .WithTitle(title)
            .WithColor(color)
            .WithTimestamp(alert.StartsAt);

        // Basic info
        var severityEmoji = alert.Severity.ToLowerInvariant() switch
        {
            "critical" => "🔴",
            "warning" => "🟡",
            "info" => "🔵",
            _ => "⚪"
        };

        builder.AddField("Status", alert.Status.ToUpperInvariant(), true);
        builder.AddField("Severity", $"{severityEmoji} {alert.Severity}", true);

        if (!string.IsNullOrEmpty(alert.Instance))
            builder.AddField("Instance", alert.Instance, true);

        if (alert.IsResolved && alert.Duration.HasValue)
            builder.AddField("Duration", FormatDuration(alert.Duration.Value), true);

        // LLM analysis as description
        if (analysis.Length > 4096)
            analysis = analysis[..4093] + "...";

        builder.WithDescription(analysis);

        return builder.Build();
    }

    private static DiscordColor GetAlertColor(AlertmanagerWebhookAlert alert)
    {
        if (alert.IsResolved)
            return ColorResolved;

        return alert.Severity.ToLowerInvariant() switch
        {
            "critical" => ColorFiringCritical,
            "warning" => ColorFiringWarning,
            _ => ColorFiringWarning
        };
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
            return $"{(int)duration.TotalDays}d {duration.Hours}h";
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        return $"{(int)duration.TotalSeconds}s";
    }
}
