using System.Security.Cryptography;
using System.Text;
using DSharpPlus;
using DSharpPlus.Entities;
using HomelabBot.Configuration;
using HomelabBot.Models;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public sealed class AlertWebhookService
{
    private readonly DiscordBotService _discordService;
    private readonly KernelService _kernelService;
    private readonly MemoryService _memoryService;
    private readonly AlertWebhookConfiguration _config;
    private readonly ILogger<AlertWebhookService> _logger;

    private static readonly DiscordColor ColorFiringCritical = new("#DC2626");
    private static readonly DiscordColor ColorFiringWarning = new("#F59E0B");
    private static readonly DiscordColor ColorResolved = new("#22C55E");

    public AlertWebhookService(
        DiscordBotService discordService,
        KernelService kernelService,
        MemoryService memoryService,
        IOptions<AlertWebhookConfiguration> config,
        ILogger<AlertWebhookService> logger)
    {
        _discordService = discordService;
        _kernelService = kernelService;
        _memoryService = memoryService;
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
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"alert-{alert.Fingerprint ?? Guid.NewGuid().ToString()}"));
        var conversationId = BitConverter.ToUInt64(hashBytes, 0);

        string analysis;
        List<Data.Entities.Pattern> matchedPatterns = [];

        if (alert.IsFiring)
        {
            // Check for known patterns before LLM investigation
            var searchTerms = $"{alert.AlertName} {alert.Description ?? alert.Summary ?? ""}";
            matchedPatterns = await _memoryService.GetRelevantPatternsAsync(searchTerms);

            var patternContext = "";
            if (matchedPatterns.Count > 0)
            {
                var patternLines = matchedPatterns.Select(p =>
                {
                    var successInfo = (p.SuccessCount + p.FailureCount) > 0
                        ? $" (resolved {p.SuccessRate:F0}% of cases)"
                        : "";
                    return $"- {p.Symptom}: {p.CommonCause} → Fix: {p.Resolution}{successInfo}";
                });
                patternContext = $"\n\nKNOWN PATTERNS for this type of issue (consider these first):\n{string.Join("\n", patternLines)}\n";
            }

            var prompt = $"""
                ALERT FIRING - Investigate this:
                Alert: {alert.AlertName}
                Severity: {alert.Severity}
                Instance: {alert.Instance ?? "unknown"}
                Description: {alert.Description ?? alert.Summary ?? "none"}
                Started: {alert.StartsAt:u}
                {patternContext}
                Use your tools to investigate what's happening. Check relevant logs, metrics, container status, etc.
                Provide a brief summary of what you found and any recommended actions.
                """;

            analysis = await _kernelService.ProcessMessageAsync(
                conversationId,
                prompt,
                _config.DiscordUserId,
                TraceType.Scheduled,
                ct: ct);
        }
        else
        {
            analysis = $"Alert resolved after {FormatDuration(alert.Duration ?? TimeSpan.Zero)}.";
        }

        var embed = BuildAlertEmbed(alert, analysis);

        if (matchedPatterns.Count > 0)
        {
            await _discordService.SendDmWithComponentsAsync(
                _config.DiscordUserId,
                embed,
                BuildPatternFeedbackButtons(matchedPatterns));
        }
        else
        {
            await _discordService.SendDmAsync(_config.DiscordUserId, embed);
        }
    }

    private static List<DiscordComponent> BuildPatternFeedbackButtons(List<Data.Entities.Pattern> patterns)
    {
        var components = new List<DiscordComponent>();

        foreach (var pattern in patterns.Take(2))
        {
            components.Add(new DiscordButtonComponent(
                ButtonStyle.Success,
                $"pattern_helpful_{pattern.Id}",
                $"Helpful: {Truncate(pattern.Symptom, 30)}",
                false,
                new DiscordComponentEmoji("👍")));

            components.Add(new DiscordButtonComponent(
                ButtonStyle.Secondary,
                $"pattern_notrelevant_{pattern.Id}",
                "Not relevant",
                false,
                new DiscordComponentEmoji("👎")));
        }

        return components;
    }

    private static string Truncate(string text, int maxLength)
    {
        return text.Length > maxLength ? text[.. (maxLength - 3)] + "..." : text;
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
