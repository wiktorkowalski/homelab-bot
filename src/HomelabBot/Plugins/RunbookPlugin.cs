using System.ComponentModel;
using System.Text;
using System.Text.Json;
using HomelabBot.Data;
using HomelabBot.Data.Entities;
using HomelabBot.Helpers;
using HomelabBot.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;
using ModelContextProtocol.Server;

namespace HomelabBot.Plugins;

[McpServerToolType]
public sealed class RunbookPlugin
{
    private readonly IDbContextFactory<HomelabDbContext> _dbFactory;
    private readonly ILogger<RunbookPlugin> _logger;

    public RunbookPlugin(
        IDbContextFactory<HomelabDbContext> dbFactory,
        ILogger<RunbookPlugin> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    [KernelFunction]
    [ChangesState]
    [Description("Create a new runbook from a name, trigger condition, and structured steps as JSON array. " +
        "Steps format: [{\"Order\":1,\"Description\":\"...\",\"PluginName\":\"Docker\",\"FunctionName\":\"ListContainers\",\"Parameters\":{}}]. " +
        "Trust level is auto-determined based on functions used.")]
    public async Task<string> CreateRunbook(
        [Description("Name for the runbook")] string name,
        [Description("Trigger condition, e.g. 'alertname=HighCPU' for label match, or descriptive text")] string triggerCondition,
        [Description("JSON array of steps with Order, Description, PluginName, FunctionName, Parameters")] string stepsJson)
    {
        if (!TryParseSteps(stepsJson, out var steps, out var error))
        {
            return $"Error: Invalid steps: {error}";
        }

        var trustLevel = DetermineTrustLevel(steps);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var runbook = new Runbook
        {
            Name = name,
            TriggerCondition = triggerCondition,
            StepsJson = JsonSerializer.Serialize(steps),
            TrustLevel = trustLevel,
        };

        db.Runbooks.Add(runbook);
        await db.SaveChangesAsync();

        _logger.LogInformation("Created runbook '{Name}' (ID {Id}, TrustLevel: {Trust})", name, runbook.Id, trustLevel);
        return $"Created runbook **{name}** (ID: {runbook.Id}, Trust: {trustLevel}, Steps: {steps.Count})";
    }

    [KernelFunction]
    [McpServerTool(ReadOnly = true)]
    [Description("List all runbooks with their status, trigger conditions, and execution stats.")]
    public async Task<string> ListRunbooks()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var runbooks = await db.Runbooks.OrderBy(r => r.Name).ToListAsync();

        if (runbooks.Count == 0)
        {
            return "No runbooks configured.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"**Runbooks** ({runbooks.Count}):\n");

        foreach (var r in runbooks)
        {
            var status = r.Enabled ? "✅" : "❌";
            var lastRun = r.LastExecutedAt.HasValue
                ? r.LastExecutedAt.Value.ToString("yyyy-MM-dd HH:mm")
                : "never";
            sb.AppendLine($"{status} **{r.Name}** (ID: {r.Id})");
            sb.AppendLine($"  Trigger: `{r.TriggerCondition}` | Trust: {EffectiveTrustLevel(r)} | Runs: {r.ExecutionCount} | Last: {lastRun}");
        }

        return sb.ToString();
    }

    [KernelFunction]
    [ChangesState]
    [Description("Execute a runbook by ID. ReadOnly/Conservative steps auto-execute. Risky runbooks require manual confirmation.")]
    public async Task<string> ExecuteRunbook(
        [Description("Runbook ID to execute")] int runbookId,
        Kernel kernel)
    {
        // Load runbook in a short-lived context
        string runbookName;
        string stepsJson;
        TrustLevel storedTrustLevel;

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var runbook = await db.Runbooks.FindAsync(runbookId);
            if (runbook == null)
            {
                return $"Error: Runbook {runbookId} not found.";
            }

            if (!runbook.Enabled)
            {
                return $"Runbook **{runbook.Name}** is disabled.";
            }

            runbookName = runbook.Name;
            stepsJson = runbook.StepsJson;
            storedTrustLevel = runbook.TrustLevel;
        }

        if (!TryParseSteps(stepsJson, out var steps, out var error))
        {
            return $"Error: runbook **{runbookName}** has invalid steps — {error}";
        }

        var trustLevel = EffectiveTrustLevel(storedTrustLevel, steps);
        if (trustLevel == TrustLevel.Risky)
        {
            _logger.LogInformation(
                "Runbook {RunbookId} waits for confirmation (stored trust {StoredTrust}, effective {EffectiveTrust})",
                runbookId, storedTrustLevel, trustLevel);
            return $"⚠️ Runbook **{runbookName}** has TrustLevel=Risky and requires manual confirmation. " +
                   "Please review the steps and confirm execution.";
        }

        // Execute steps (may take minutes — no DB context held)
        var results = new List<RunbookStepResult>();
        var success = true;

        foreach (var step in steps.OrderBy(s => s.Order))
        {
            try
            {
                var function = FindFunction(kernel, step);
                if (function == null)
                {
                    results.Add(new RunbookStepResult
                    {
                        StepOrder = step.Order,
                        Description = step.Description,
                        Executed = false,
                        Skipped = true,
                        SkipReason = $"Function {step.PluginName}.{step.FunctionName} not found",
                    });
                    continue;
                }

                var args = new KernelArguments();
                foreach (var param in step.Parameters)
                {
                    args[param.Key] = param.Value;
                }

                var result = (await kernel.InvokeAsync(function, args)).ToString();

                var failed = ToolResult.IsFailure(result);
                if (failed)
                {
                    _logger.LogWarning("Runbook step {Order} answered with an error: {Description}", step.Order, step.Description);
                    success = false;
                }

                results.Add(new RunbookStepResult
                {
                    StepOrder = step.Order,
                    Description = step.Description,
                    Executed = !failed,
                    Result = result,
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Runbook step {Order} failed: {Description}", step.Order, step.Description);
                results.Add(new RunbookStepResult
                {
                    StepOrder = step.Order,
                    Description = step.Description,
                    Executed = false,
                    Result = $"Error: {ex.Message}",
                });
                success = false;
            }
        }

        // Update execution stats in a new context
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var runbook = await db.Runbooks.FindAsync(runbookId);
            if (runbook != null)
            {
                runbook.ExecutionCount++;
                runbook.LastExecutedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
        }

        return FormatExecutionResult(runbookName, success, results);
    }

    [KernelFunction]
    [Description("Validate a runbook without executing it. Checks that all referenced plugins and functions exist.")]
    public async Task<string> TestRunbook(
        [Description("Runbook ID to test")] int runbookId,
        Kernel kernel)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var runbook = await db.Runbooks.FindAsync(runbookId);

        if (runbook == null)
        {
            return $"Error: Runbook {runbookId} not found.";
        }

        if (!TryParseSteps(runbook.StepsJson, out var steps, out var error))
        {
            return $"Error: Invalid steps: {error}";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"**Dry-run: {runbook.Name}** (Trust: {EffectiveTrustLevel(runbook.TrustLevel, steps)})\n");

        var allValid = true;
        foreach (var step in steps.OrderBy(s => s.Order))
        {
            var function = FindFunction(kernel, step);
            if (function != null)
            {
                sb.AppendLine($"✅ Step {step.Order}: {step.Description} → {step.PluginName}.{step.FunctionName}");
            }
            else
            {
                sb.AppendLine($"❌ Step {step.Order}: {step.Description} → {step.PluginName}.{step.FunctionName} NOT FOUND");
                allValid = false;
            }
        }

        sb.AppendLine(allValid ? "\n✅ All steps valid." : "\n❌ Some steps reference missing functions.");
        return sb.ToString();
    }

    internal static KernelFunction? FindFunction(Kernel kernel, RunbookStep step)
    {
        return kernel.Plugins
            .SelectMany(p => p)
            .FirstOrDefault(f =>
                string.Equals(f.PluginName, step.PluginName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(f.Name, step.FunctionName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryParseSteps(string json, out List<RunbookStep> steps, out string error)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<List<RunbookStep>>(json);
            if (parsed == null || parsed.Count == 0)
            {
                steps = [];
                error = "Steps must be a non-empty JSON array.";
                return false;
            }

            steps = parsed;
            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            steps = [];
            error = $"Invalid JSON: {ex.Message}";
            return false;
        }
    }

    internal static TrustLevel DetermineTrustLevel(List<RunbookStep> steps)
    {
        if (steps.Any(s => ChangesStateAttribute.FunctionNames.Contains(s.FunctionName)))
        {
            return TrustLevel.Risky;
        }

        return TrustLevel.ReadOnly;
    }

    // Every trust decision and display goes through here, never the stored column alone: a runbook
    // stored before a tool got its [ChangesState] mark keeps its old, lower level.
    internal static TrustLevel EffectiveTrustLevel(Runbook runbook)
    {
        List<RunbookStep>? steps;
        try
        {
            steps = JsonSerializer.Deserialize<List<RunbookStep>>(runbook.StepsJson);
        }
        catch (JsonException)
        {
            steps = null;
        }

        // A gate that cannot read the steps stays closed.
        return steps is null ? TrustLevel.Risky : EffectiveTrustLevel(runbook.TrustLevel, steps);
    }

    // The enum runs from least to most restrictive, so the larger level wins: a level the owner
    // stored by hand (e.g. a seeded Risky runbook with read steps) is never lowered by the steps.
    internal static TrustLevel EffectiveTrustLevel(TrustLevel stored, List<RunbookStep> steps)
    {
        var computed = DetermineTrustLevel(steps);
        return computed > stored ? computed : stored;
    }

    private static string FormatExecutionResult(string name, bool success, List<RunbookStepResult> results)
    {
        var sb = new StringBuilder();
        var emoji = success ? "✅" : "⚠️";
        sb.AppendLine($"{emoji} **Runbook: {name}**\n");

        foreach (var r in results)
        {
            if (r.Skipped)
            {
                sb.AppendLine($"⏭️ Step {r.StepOrder}: {r.Description} — Skipped: {r.SkipReason}");
            }
            else if (r.Executed)
            {
                var preview = r.Result is { Length: > 100 } ? r.Result[..97] + "..." : r.Result;
                sb.AppendLine($"✅ Step {r.StepOrder}: {r.Description}");
                if (!string.IsNullOrEmpty(preview))
                {
                    sb.AppendLine($"   → {preview}");
                }
            }
            else
            {
                sb.AppendLine($"❌ Step {r.StepOrder}: {r.Description} — {r.Result}");
            }
        }

        return sb.ToString();
    }
}
