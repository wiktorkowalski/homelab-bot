using System.Security.Cryptography;
using System.Text;
using DSharpPlus;
using DSharpPlus.Entities;
using HomelabBot.Configuration;
using HomelabBot.Helpers;
using HomelabBot.Models;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public sealed class AlertWebhookService
{
    private readonly DiscordBotService _discordService;
    private readonly KernelService _kernelService;
    private readonly RemediationService _remediationService;
    private readonly ContagionTrackerService _contagionTracker;
    private readonly WarRoomService _warRoomService;
    private readonly AlertWebhookConfiguration _config;
    private readonly ILogger<AlertWebhookService> _logger;

    private static readonly DiscordColor ColorFiringCritical = new("#DC2626");
    private static readonly DiscordColor ColorFiringWarning = new("#F59E0B");
    private static readonly DiscordColor ColorResolved = new("#22C55E");

    public AlertWebhookService(
        DiscordBotService discordService,
        KernelService kernelService,
        RemediationService remediationService,
        ContagionTrackerService contagionTracker,
        WarRoomService warRoomService,
        IOptions<AlertWebhookConfiguration> config,
        ILogger<AlertWebhookService> logger)
    {
        _discordService = discordService;
        _kernelService = kernelService;
        _remediationService = remediationService;
        _contagionTracker = contagionTracker;
        _warRoomService = warRoomService;
        _config = config.Value;
        _logger = logger;
    }

    public async Task ProcessAlertsAsync(AlertmanagerWebhookPayload payload, CancellationToken ct = default)
    {
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
        if (!alert.IsFiring)
        {
            _logger.LogInformation("Alert {AlertName} resolved", alert.AlertName);
            var resolvedAnalysis = $"Alert resolved after {FormattingHelpers.FormatDuration(alert.Duration ?? TimeSpan.Zero)}.";
            var resolvedEmbed = BuildAlertEmbed(alert, resolvedAnalysis);
            await _discordService.SendDmAsync(HomelabOwner.DiscordUserId, resolvedEmbed);
            return;
        }

        // Open War Room for critical alerts
        Data.Entities.WarRoom? warRoom = null;
        if (_warRoomService.ShouldOpenWarRoom(alert.Severity))
        {
            _logger.LogInformation("Opening war room for {Severity} alert {AlertName}", alert.Severity, alert.AlertName);
            var trigger = $"{alert.AlertName}: {alert.Description ?? alert.Summary ?? "unknown"}";
            warRoom = await _warRoomService.OpenWarRoomAsync(trigger, alert.Severity, ct);
        }

        // Try automated remediation strategies (runbook → auto-remediation → healing chain)
        var outcome = await _remediationService.TryRemediateAsync(alert, ct);

        if (outcome.Handled)
        {
            await HandleRemediationOutcomeAsync(alert, outcome, warRoom, ct);
            return;
        }

        _logger.LogInformation("No automated remediation for {AlertName}, running LLM investigation", alert.AlertName);

        // Fall through to LLM investigation
        var analysis = await RunLlmInvestigationAsync(alert, outcome, ct);
        var embed = BuildAlertEmbed(alert, analysis);

        if (outcome.MatchedRunbooks.Count > 0)
        {
            await _discordService.SendDmWithComponentsAsync(
                HomelabOwner.DiscordUserId,
                embed,
                BuildPatternFeedbackButtons(outcome.MatchedRunbooks));
        }
        else
        {
            await _discordService.SendDmAsync(HomelabOwner.DiscordUserId, embed);
        }
    }

    private async Task HandleRemediationOutcomeAsync(
        AlertmanagerWebhookAlert alert,
        RemediationOutcome outcome,
        Data.Entities.WarRoom? warRoom,
        CancellationToken ct)
    {
        if (warRoom != null && !outcome.NeedsConfirmation)
        {
            var methodLabel = outcome.Method switch
            {
                RemediationMethod.Runbook => "Runbook executed",
                RemediationMethod.AutoRemediation => "Auto-remediation",
                RemediationMethod.HealingChain => "Healing chain",
                _ => outcome.Method.ToString(),
            };
            await _warRoomService.LogEventAsync(warRoom.Id, $"{methodLabel}: {outcome.Message}", ct);
            if (outcome.Success && outcome.Method != RemediationMethod.AutoRemediation)
            {
                await _warRoomService.ResolveAsync(warRoom.Id, $"Resolved by {methodLabel.ToLowerInvariant()}", ct);
            }
        }

        var embed = BuildAlertEmbed(alert, outcome.Message);

        if (outcome.Method == RemediationMethod.AutoRemediation && outcome.ActionId.HasValue)
        {
            var buttons = outcome.NeedsConfirmation
                ? BuildRemediationConfirmButtons(outcome.ActionId.Value)
                : BuildRemediationFeedbackButtons(outcome.ActionId.Value);
            await _discordService.SendDmWithComponentsAsync(HomelabOwner.DiscordUserId, embed, buttons);
        }
        else
        {
            await _discordService.SendDmAsync(HomelabOwner.DiscordUserId, embed);
        }
    }

    private async Task<string> RunLlmInvestigationAsync(
        AlertmanagerWebhookAlert alert,
        RemediationOutcome outcome,
        CancellationToken ct)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"alert-{alert.Fingerprint ?? Guid.NewGuid().ToString()}"));
        var conversationId = BitConverter.ToUInt64(hashBytes, 0);

        var patternContext = "";
        if (outcome.MatchedRunbooks.Count > 0)
        {
            var patternLines = outcome.MatchedRunbooks.Select(p =>
            {
                var successInfo = (p.SuccessCount + p.FailureCount) > 0
                    ? $" (resolved {p.SuccessRate:F0}% of cases)"
                    : "";
                return $"- {p.TriggerCondition}: {p.CommonCause} → Fix: {p.Description}{successInfo}";
            });
            patternContext = $"\n\nKNOWN PATTERNS for this type of issue (consider these first):\n{string.Join("\n", patternLines)}\n";
        }

        var dejaVuContext = IncidentSimilarityService.FormatDejaVuContext(outcome.SimilarIncidents);
        var dejaVuPrompt = !string.IsNullOrEmpty(dejaVuContext)
            ? $"\n\nPAST INCIDENT MATCH:\n{dejaVuContext}\n"
            : "";

        var blastRadiusPrompt = "";
        if (outcome.ContainerName != null)
        {
            var blastRadius = await _contagionTracker.AnalyzeBlastRadiusAsync(
                outcome.ContainerName, alert.AlertName, ct);
            var blastRadiusText = ContagionTrackerService.FormatBlastRadius(blastRadius);
            if (!string.IsNullOrEmpty(blastRadiusText))
            {
                blastRadiusPrompt = $"\n\nBLAST RADIUS:\n{blastRadiusText}\n";
            }
        }

        var prompt = $"""
            ALERT FIRING - Investigate this:
            Alert: {alert.AlertName}
            Severity: {alert.Severity}
            Instance: {alert.Instance ?? "unknown"}
            Description: {alert.Description ?? alert.Summary ?? "none"}
            Started: {alert.StartsAt:u}
            {patternContext}{dejaVuPrompt}{blastRadiusPrompt}
            Use your tools to investigate what's happening. Check relevant logs, metrics, container status, etc.
            Provide a brief summary of what you found and any recommended actions.
            """;

        return await _kernelService.ProcessMessageAsync(
            conversationId,
            prompt,
            HomelabOwner.DiscordUserId,
            TraceType.Scheduled,
            ct: ct);
    }

    private static List<DiscordComponent> BuildPatternFeedbackButtons(List<Data.Entities.Runbook> patterns)
    {
        var ids = string.Join(",", patterns.Select(p => p.Id));

        return
        [
            new DiscordButtonComponent(
                ButtonStyle.Success,
                $"pattern_helpful_{ids}",
                "Analysis was helpful",
                false,
                new DiscordComponentEmoji("👍")),
            new DiscordButtonComponent(
                ButtonStyle.Secondary,
                $"pattern_notrelevant_{ids}",
                "Not relevant",
                false,
                new DiscordComponentEmoji("👎"))
        ];
    }

    private static List<DiscordComponent> BuildRemediationFeedbackButtons(int actionId)
    {
        return
        [
            new DiscordButtonComponent(
                ButtonStyle.Success,
                $"remediation_ok_{actionId}",
                "Remediation Worked",
                false,
                new DiscordComponentEmoji("✅")),
            new DiscordButtonComponent(
                ButtonStyle.Danger,
                $"remediation_fail_{actionId}",
                "Still Broken",
                false,
                new DiscordComponentEmoji("❌"))
        ];
    }

    private static List<DiscordComponent> BuildRemediationConfirmButtons(int actionId)
    {
        return
        [
            new DiscordButtonComponent(
                ButtonStyle.Success,
                $"remediation_approve_{actionId}",
                "Approve Restart",
                false,
                new DiscordComponentEmoji("✅")),
            new DiscordButtonComponent(
                ButtonStyle.Danger,
                $"remediation_reject_{actionId}",
                "Reject",
                false,
                new DiscordComponentEmoji("❌"))
        ];
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
        {
            builder.AddField("Instance", alert.Instance, true);
        }

        if (alert.IsResolved && alert.Duration.HasValue)
        {
            builder.AddField("Duration", FormattingHelpers.FormatDuration(alert.Duration.Value), true);
        }

        if (analysis.Length > 4096)
        {
            analysis = analysis[..4093] + "...";
        }

        builder.WithDescription(analysis);

        return builder.Build();
    }

    private static DiscordColor GetAlertColor(AlertmanagerWebhookAlert alert)
    {
        if (alert.IsResolved)
        {
            return ColorResolved;
        }

        return alert.Severity.ToLowerInvariant() switch
        {
            "critical" => ColorFiringCritical,
            "warning" => ColorFiringWarning,
            _ => ColorFiringWarning
        };
    }
}
