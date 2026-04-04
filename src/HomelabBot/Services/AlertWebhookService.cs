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
    private readonly MemoryService _memoryService;
    private readonly RunbookTriggerService _runbookTriggerService;
    private readonly AutoRemediationService _autoRemediationService;
    private readonly IncidentSimilarityService _similarityService;
    private readonly HealingChainService _healingChainService;
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
        MemoryService memoryService,
        RunbookTriggerService runbookTriggerService,
        AutoRemediationService autoRemediationService,
        IncidentSimilarityService similarityService,
        HealingChainService healingChainService,
        ContagionTrackerService contagionTracker,
        WarRoomService warRoomService,
        IOptions<AlertWebhookConfiguration> config,
        ILogger<AlertWebhookService> logger)
    {
        _discordService = discordService;
        _kernelService = kernelService;
        _memoryService = memoryService;
        _runbookTriggerService = runbookTriggerService;
        _autoRemediationService = autoRemediationService;
        _similarityService = similarityService;
        _healingChainService = healingChainService;
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
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"alert-{alert.Fingerprint ?? Guid.NewGuid().ToString()}"));
        var conversationId = BitConverter.ToUInt64(hashBytes, 0);

        string analysis;
        List<Data.Entities.Pattern> matchedPatterns = [];

        if (alert.IsFiring)
        {
            // Open War Room for critical alerts
            Data.Entities.WarRoom? warRoom = null;
            if (_warRoomService.ShouldOpenWarRoom(alert.Severity))
            {
                var trigger = $"{alert.AlertName}: {alert.Description ?? alert.Summary ?? "unknown"}";
                warRoom = await _warRoomService.OpenWarRoomAsync(trigger, alert.Severity, ct);
            }

            // Try runbook first — if matched, use its result instead of LLM investigation
            var runbookResult = await _runbookTriggerService.TryMatchAndExecuteAsync(alert, ct);
            if (runbookResult != null)
            {
                if (warRoom != null)
                {
                    await _warRoomService.LogEventAsync(warRoom.Id, $"Runbook executed: {runbookResult}", ct);
                    await _warRoomService.ResolveAsync(warRoom.Id, "Resolved by runbook", ct);
                }

                var runbookEmbed = BuildAlertEmbed(alert, runbookResult);
                await _discordService.SendDmAsync(HomelabOwner.DiscordUserId, runbookEmbed);
                return;
            }

            // Check for known patterns before LLM investigation
            var searchTerms = $"{alert.AlertName} {alert.Description ?? alert.Summary ?? ""}";
            matchedPatterns = await _memoryService.GetRelevantPatternsAsync(searchTerms);

            // Try auto-remediation before LLM investigation
            var containerName = AutoRemediationService.ExtractContainerName(alert);
            var remediationResult = await _autoRemediationService.TryAutoRemediateAsync(alert, matchedPatterns, ct);
            if (remediationResult != null)
            {
                if (remediationResult.WasAutoExecuted)
                {
                    if (warRoom != null)
                    {
                        await _warRoomService.LogEventAsync(warRoom.Id, $"Auto-remediation: {remediationResult.Message}", ct);
                    }

                    var remEmbed = BuildAlertEmbed(alert, remediationResult.Message);
                    var remButtons = BuildRemediationFeedbackButtons(remediationResult.ActionId!.Value);
                    await _discordService.SendDmWithComponentsAsync(HomelabOwner.DiscordUserId, remEmbed, remButtons);
                    return;
                }

                if (remediationResult.NeedsConfirmation)
                {
                    var remEmbed = BuildAlertEmbed(alert, remediationResult.Message);
                    var remButtons = BuildRemediationConfirmButtons(remediationResult.ActionId!.Value);
                    await _discordService.SendDmWithComponentsAsync(HomelabOwner.DiscordUserId, remEmbed, remButtons);
                    return;
                }
            }

            // Check for similar past incidents (Deja Vu) — hoisted to share with healing chain
            var similarIncidents = await _similarityService.FindSimilarAsync(
                searchTerms, containerName, alert.Labels, limit: 3, ct: ct);
            var dejaVuContext = IncidentSimilarityService.FormatDejaVuContext(similarIncidents);

            // Escalate to healing chain if simple remediation didn't handle it
            var chainResult = await _healingChainService.PlanAndExecuteAsync(
                searchTerms, containerName, ct, similarIncidents);
            if (chainResult is { Success: true })
            {
                if (warRoom != null)
                {
                    await _warRoomService.LogEventAsync(warRoom.Id, $"Healing chain: {chainResult.Message}", ct);
                    await _warRoomService.ResolveAsync(warRoom.Id, "Resolved by healing chain", ct);
                }

                var chainEmbed = BuildAlertEmbed(alert, chainResult.Message);
                await _discordService.SendDmAsync(HomelabOwner.DiscordUserId, chainEmbed);
                return;
            }

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

            var dejaVuPrompt = !string.IsNullOrEmpty(dejaVuContext)
                ? $"\n\nPAST INCIDENT MATCH:\n{dejaVuContext}\n"
                : "";

            var blastRadiusPrompt = "";
            if (containerName != null)
            {
                var blastRadius = await _contagionTracker.AnalyzeBlastRadiusAsync(
                    containerName, alert.AlertName, ct);
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

            analysis = await _kernelService.ProcessMessageAsync(
                conversationId,
                prompt,
                HomelabOwner.DiscordUserId,
                TraceType.Scheduled,
                ct: ct);
        }
        else
        {
            analysis = $"Alert resolved after {FormattingHelpers.FormatDuration(alert.Duration ?? TimeSpan.Zero)}.";
        }

        var embed = BuildAlertEmbed(alert, analysis);

        if (matchedPatterns.Count > 0)
        {
            await _discordService.SendDmWithComponentsAsync(
                HomelabOwner.DiscordUserId,
                embed,
                BuildPatternFeedbackButtons(matchedPatterns));
        }
        else
        {
            await _discordService.SendDmAsync(HomelabOwner.DiscordUserId, embed);
        }
    }

    private static List<DiscordComponent> BuildPatternFeedbackButtons(List<Data.Entities.Pattern> patterns)
    {
        var components = new List<DiscordComponent>();

        // Encode all pattern IDs so one button press feeds back on all matched patterns
        var ids = string.Join(",", patterns.Select(p => p.Id));

        components.Add(new DiscordButtonComponent(
            ButtonStyle.Success,
            $"pattern_helpful_{ids}",
            "Analysis was helpful",
            false,
            new DiscordComponentEmoji("👍")));

        components.Add(new DiscordButtonComponent(
            ButtonStyle.Secondary,
            $"pattern_notrelevant_{ids}",
            "Not relevant",
            false,
            new DiscordComponentEmoji("👎")));

        return components;
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

    private static string Truncate(string text, int maxLength)
    {
        return text.Length > maxLength ? text[..(maxLength - 3)] + "..." : text;
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
        {
            builder.AddField("Instance", alert.Instance, true);
        }

        if (alert.IsResolved && alert.Duration.HasValue)
        {
            builder.AddField("Duration", FormattingHelpers.FormatDuration(alert.Duration.Value), true);
        }

        // LLM analysis as description
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
