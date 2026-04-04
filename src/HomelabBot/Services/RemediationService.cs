using HomelabBot.Data.Entities;
using HomelabBot.Models;
using Microsoft.Extensions.Logging;

namespace HomelabBot.Services;

public sealed class RemediationService
{
    private readonly RunbookTriggerService _runbookTrigger;
    private readonly InvestigationService _memoryService;
    private readonly AutoRemediationService _autoRemediation;
    private readonly IncidentSimilarityService _similarityService;
    private readonly HealingChainService _healingChain;
    private readonly ContagionTrackerService _contagionTracker;
    private readonly ILogger<RemediationService> _logger;

    public RemediationService(
        RunbookTriggerService runbookTrigger,
        InvestigationService memoryService,
        AutoRemediationService autoRemediation,
        IncidentSimilarityService similarityService,
        HealingChainService healingChain,
        ContagionTrackerService contagionTracker,
        ILogger<RemediationService> logger)
    {
        _runbookTrigger = runbookTrigger;
        _memoryService = memoryService;
        _autoRemediation = autoRemediation;
        _similarityService = similarityService;
        _healingChain = healingChain;
        _contagionTracker = contagionTracker;
        _logger = logger;
    }

    public async Task<RemediationOutcome> TryRemediateAsync(
        AlertmanagerWebhookAlert alert, CancellationToken ct)
    {
        _logger.LogInformation("Attempting remediation for alert {AlertName}", alert.AlertName);

        // 1. Try runbook match — fastest, no LLM needed
        var runbookResult = await _runbookTrigger.TryMatchAndExecuteAsync(alert, ct);
        if (runbookResult != null)
        {
            _logger.LogInformation("Remediation for {AlertName}: runbook matched", alert.AlertName);
            return new RemediationOutcome
            {
                Message = runbookResult,
                Success = true,
                Handled = true,
                Method = RemediationMethod.Runbook,
            };
        }

        // 2. Gather context shared across strategies
        var searchTerms = $"{alert.AlertName} {alert.Description ?? alert.Summary ?? ""}";
        var matchedRunbooks = await _memoryService.GetRelevantRunbooksAsync(searchTerms);
        var containerName = AutoRemediationService.ExtractContainerName(alert);

        // 3. Try pattern-based auto-remediation (simple restart/start)
        var remResult = await _autoRemediation.TryAutoRemediateAsync(alert, matchedRunbooks, ct);
        if (remResult is { WasAutoExecuted: true } or { NeedsConfirmation: true })
        {
            _logger.LogInformation("Remediation for {AlertName}: auto-remediation {Outcome}",
                alert.AlertName, remResult.WasAutoExecuted ? "executed" : "needs confirmation");
            return new RemediationOutcome
            {
                Message = remResult.Message,
                Success = remResult.WasAutoExecuted,
                Handled = true,
                NeedsConfirmation = remResult.NeedsConfirmation,
                ActionId = remResult.ActionId,
                ContainerName = remResult.ContainerName,
                Method = RemediationMethod.AutoRemediation,
                MatchedRunbooks = matchedRunbooks,
            };
        }

        // 4. Find similar past incidents (shared with healing chain to avoid double-call)
        var similarIncidents = await _similarityService.FindSimilarAsync(
            searchTerms, containerName, alert.Labels, limit: 3, ct: ct);

        // 5. Try LLM-planned multi-step healing chain
        var chainResult = await _healingChain.PlanAndExecuteAsync(
            searchTerms, containerName, ct, similarIncidents);
        if (chainResult is { Success: true })
        {
            _logger.LogInformation("Remediation for {AlertName}: healing chain succeeded", alert.AlertName);
            return new RemediationOutcome
            {
                Message = chainResult.Message,
                Success = true,
                Handled = true,
                Method = RemediationMethod.HealingChain,
                GeneratedRunbookId = chainResult.GeneratedRunbookId,
                MatchedRunbooks = matchedRunbooks,
                SimilarIncidents = similarIncidents,
            };
        }

        // 6. Nothing handled — analyze blast radius and return context for LLM investigation fallback
        _logger.LogInformation("Remediation for {AlertName}: no strategy handled, falling back to LLM", alert.AlertName);

        BlastRadiusReport? blastRadius = null;
        if (containerName != null)
        {
            try
            {
                blastRadius = await _contagionTracker.AnalyzeBlastRadiusAsync(
                    containerName, alert.AlertName, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to analyze blast radius for {Container}", containerName);
            }
        }

        return new RemediationOutcome
        {
            Message = string.Empty,
            Success = false,
            Handled = false,
            Method = RemediationMethod.None,
            ContainerName = containerName,
            MatchedRunbooks = matchedRunbooks,
            SimilarIncidents = similarIncidents,
            BlastRadius = blastRadius,
        };
    }
}
