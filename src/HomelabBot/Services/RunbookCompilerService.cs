using System.Text.Json;
using HomelabBot.Data;
using HomelabBot.Data.Entities;
using HomelabBot.Models;
using HomelabBot.Plugins;
using Microsoft.EntityFrameworkCore;

namespace HomelabBot.Services;

public sealed class RunbookCompilerService
{
    private readonly IDbContextFactory<HomelabDbContext> _dbFactory;
    private readonly ILogger<RunbookCompilerService> _logger;

    public RunbookCompilerService(
        IDbContextFactory<HomelabDbContext> dbFactory,
        ILogger<RunbookCompilerService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<Runbook?> CompileFromInvestigationAsync(Investigation investigation, CancellationToken ct = default)
    {
        if (investigation.Steps.Count == 0 || string.IsNullOrEmpty(investigation.Resolution))
        {
            return null;
        }

        var steps = ConvertInvestigationSteps(investigation.Steps);
        if (steps.Count == 0)
        {
            return null;
        }

        var name = $"Auto: {ConversationSearchResult.Truncate(investigation.Trigger, 80)}";
        var trigger = ExtractTriggerCondition(investigation.Trigger);

        return await FindOrCreateRunbookAsync(
            name, trigger, steps, RunbookSourceType.AutoCompiled, investigation.Id, ct);
    }

    public async Task<Runbook?> CompileFromRemediationAsync(
        RemediationAction action, Pattern? pattern, CancellationToken ct = default)
    {
        if (!action.Success)
        {
            return null;
        }

        var steps = new List<RunbookStep>
        {
            new()
            {
                Order = 1,
                Description = $"{action.ActionType} container {action.ContainerName}",
                PluginName = "Docker",
                FunctionName = action.ActionType == "start" ? "StartContainer" : "RestartContainer",
                Parameters = new Dictionary<string, string>
                {
                    ["containerName"] = action.ContainerName
                }
            },
            new()
            {
                Order = 2,
                Description = $"Verify {action.ContainerName} is running",
                PluginName = "Docker",
                FunctionName = "GetContainerStatus",
                Parameters = new Dictionary<string, string>
                {
                    ["containerName"] = action.ContainerName
                }
            }
        };

        var symptom = pattern?.Symptom ?? $"Container {action.ContainerName} {action.ActionType}";
        var name = $"Auto: {ConversationSearchResult.Truncate(symptom, 80)}";
        var trigger = pattern?.Symptom ?? $"container {action.ContainerName} down";

        return await FindOrCreateRunbookAsync(name, trigger, steps, RunbookSourceType.AutoCompiled, null, ct);
    }

    private async Task<Runbook> FindOrCreateRunbookAsync(
        string name,
        string trigger,
        List<RunbookStep> steps,
        RunbookSourceType sourceType,
        int? sourceInvestigationId,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var stepsJson = JsonSerializer.Serialize(steps);
        var trustLevel = RunbookPlugin.DetermineTrustLevel(steps);
        var existing = await FindSimilarRunbookAsync(db, trigger, ct);

        if (existing != null)
        {
            var versioned = new Runbook
            {
                Name = existing.Name,
                Description = existing.Description,
                TriggerCondition = existing.TriggerCondition,
                StepsJson = stepsJson,
                TrustLevel = trustLevel,
                Version = existing.Version + 1,
                SourceType = sourceType,
                SourceInvestigationId = sourceInvestigationId,
                ParentRunbookId = existing.Id
            };

            existing.Enabled = false;
            db.Runbooks.Add(versioned);
            await db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Compiled runbook '{Name}' v{Version} (supersedes ID {OldId})",
                versioned.Name, versioned.Version, existing.Id);

            return versioned;
        }

        var runbook = new Runbook
        {
            Name = name,
            TriggerCondition = trigger,
            StepsJson = stepsJson,
            TrustLevel = trustLevel,
            SourceType = sourceType,
            SourceInvestigationId = sourceInvestigationId
        };

        db.Runbooks.Add(runbook);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Compiled new runbook '{Name}' (ID {Id})", runbook.Name, runbook.Id);
        return runbook;
    }

    private static async Task<Runbook?> FindSimilarRunbookAsync(
        HomelabDbContext db, string trigger, CancellationToken ct)
    {
        var keywords = trigger.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(k => k.Length > 2)
            .ToArray();

        if (keywords.Length == 0)
        {
            return null;
        }

        var runbooks = await db.Runbooks
            .Where(r => r.Enabled)
            .ToListAsync(ct);

        return runbooks.FirstOrDefault(r =>
        {
            var triggerLower = r.TriggerCondition.ToLowerInvariant();
            var matchCount = keywords.Count(k => triggerLower.Contains(k));
            return matchCount >= Math.Max(2, (keywords.Length + 1) / 2);
        });
    }

    private static List<RunbookStep> ConvertInvestigationSteps(IEnumerable<InvestigationStep> steps)
    {
        var result = new List<RunbookStep>();
        var order = 1;

        foreach (var step in steps.OrderBy(s => s.Timestamp))
        {
            if (string.IsNullOrEmpty(step.Plugin))
            {
                continue;
            }

            var (functionName, parameters) = ParseAction(step.Action);
            if (functionName == null)
            {
                continue;
            }

            result.Add(new RunbookStep
            {
                Order = order++,
                Description = step.Action,
                PluginName = step.Plugin,
                FunctionName = functionName,
                Parameters = parameters
            });
        }

        return result;
    }

    // Parses "FunctionName(args)" or bare "FunctionName" into structured data
    private static (string? FunctionName, Dictionary<string, string> Parameters) ParseAction(string action)
    {
        var actionTrimmed = action.Trim();

        var parenIndex = actionTrimmed.IndexOf('(');
        if (parenIndex > 0)
        {
            var funcName = actionTrimmed[..parenIndex].Trim();
            var argsStr = actionTrimmed[(parenIndex + 1) ..].TrimEnd(')').Trim();
            var parameters = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(argsStr))
            {
                parameters["input"] = argsStr;
            }

            return (funcName, parameters);
        }

        if (!actionTrimmed.Contains(' ') && actionTrimmed.Length > 0 && char.IsUpper(actionTrimmed[0]))
        {
            return (actionTrimmed, new Dictionary<string, string>());
        }

        return (null, new Dictionary<string, string>());
    }

    private static string ExtractTriggerCondition(string investigationTrigger)
    {
        return investigationTrigger
            .Replace("Alert:", "", StringComparison.OrdinalIgnoreCase)
            .Trim()
            .ToLowerInvariant();
    }
}
