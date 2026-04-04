using System.Collections.Concurrent;
using System.Text.Json;
using HomelabBot.Configuration;
using HomelabBot.Data;
using HomelabBot.Data.Entities;
using HomelabBot.Models;
using HomelabBot.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace HomelabBot.Services;

public sealed class HealingChainService
{
    private readonly IDbContextFactory<HomelabDbContext> _dbFactory;
    private readonly KernelService _kernelService;
    private readonly RunbookCompilerService _runbookCompiler;
    private readonly IncidentSimilarityService _similarityService;
    private readonly HealingChainConfiguration _config;
    private readonly ILogger<HealingChainService> _logger;
    private readonly ConcurrentDictionary<int, bool> _activeChains = new();

    public HealingChainService(
        IDbContextFactory<HomelabDbContext> dbFactory,
        KernelService kernelService,
        RunbookCompilerService runbookCompiler,
        IncidentSimilarityService similarityService,
        IOptions<HealingChainConfiguration> config,
        ILogger<HealingChainService> logger)
    {
        _dbFactory = dbFactory;
        _kernelService = kernelService;
        _runbookCompiler = runbookCompiler;
        _similarityService = similarityService;
        _config = config.Value;
        _logger = logger;
    }

    public async Task<HealingChainResult?> PlanAndExecuteAsync(
        string symptom,
        string? containerName,
        CancellationToken ct)
    {
        if (!_config.Enabled)
        {
            return null;
        }

        if (_activeChains.Count >= _config.MaxConcurrentChains)
        {
            _logger.LogWarning("Max concurrent healing chains ({Max}) reached, skipping", _config.MaxConcurrentChains);
            return null;
        }

        var similarIncidents = await _similarityService.FindSimilarAsync(symptom, containerName, limit: 3, ct: ct);
        var pastContext = similarIncidents.Count > 0
            ? IncidentSimilarityService.FormatDejaVuContext(similarIncidents)
            : "";

        var steps = await PlanRecoveryAsync(symptom, containerName, pastContext, ct);
        if (steps == null || steps.Count == 0)
        {
            return new HealingChainResult
            {
                ChainId = 0,
                Success = false,
                Message = "Could not plan recovery steps.",
                StepsExecuted = 0
            };
        }

        if (steps.Count > _config.MaxStepsPerChain)
        {
            steps = steps.Take(_config.MaxStepsPerChain).ToList();
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var chain = new HealingChain
        {
            Trigger = symptom,
            StepsJson = JsonSerializer.Serialize(steps),
            Status = HealingChainStatus.Executing
        };

        db.HealingChains.Add(chain);
        await db.SaveChangesAsync(ct);

        _activeChains[chain.Id] = true;
        try
        {
            return await ExecuteChainAsync(chain, steps, ct);
        }
        finally
        {
            _activeChains.TryRemove(chain.Id, out _);
        }
    }

    private async Task<List<RunbookStep>?> PlanRecoveryAsync(
        string symptom,
        string? containerName,
        string pastContext,
        CancellationToken ct)
    {
        var containerContext = containerName != null
            ? $"\nAffected container: {containerName}"
            : "";

        var pastSection = !string.IsNullOrEmpty(pastContext)
            ? $"\n\nPast incident context:\n{pastContext}"
            : "";

        var jsonExample = """[{"Order":1,"Description":"...","PluginName":"Docker","FunctionName":"GetContainerStatus","Parameters":{"containerName":"nginx"}}]""";
        var prompt = $"""
            Plan a recovery for this issue. Return ONLY a JSON array of steps.

            Issue: {symptom}{containerContext}{pastSection}

            Available plugins and functions:
            - Docker: ListContainers, GetContainerStatus(containerName), GetContainerLogsFromDocker(containerName, tail), RestartContainer(containerName), StartContainer(containerName), StopContainer(containerName)
            - Prometheus: GetNodeStats, GetTargets
            - Loki: SearchLogs(query, since), CountErrorsByContainer(since)
            - TrueNAS: GetPoolStatus, GetDatasetUsage

            Format: {jsonExample}

            Rules:
            - Start with diagnostic steps (check status/logs)
            - Only include restart/start if diagnostics indicate it's needed
            - End with a verification step
            - Max {_config.MaxStepsPerChain} steps
            - Return ONLY the JSON array, no markdown or explanation
            """;

        try
        {
            var response = await _kernelService.ProcessMessageAsync(
                threadId: (ulong)Random.Shared.NextInt64(),
                userMessage: prompt,
                traceType: TraceType.Scheduled,
                maxTokens: 1024,
                systemPromptOverride: "You are a recovery planning assistant. Output only valid JSON arrays of recovery steps. No explanation, no markdown fences.",
                ct: ct);

            return ParseStepsFromResponse(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to plan recovery for '{Symptom}'", symptom);
            return null;
        }
    }

    private async Task<HealingChainResult> ExecuteChainAsync(
        HealingChain chain, List<RunbookStep> steps, CancellationToken ct)
    {
        var executionLog = new List<StepExecutionLog>();
        var kernel = _kernelService.GetKernel();
        var stepsExecuted = 0;
        var success = true;

        _logger.LogInformation("Executing healing chain {ChainId} with {StepCount} steps for '{Trigger}'",
            chain.Id, steps.Count, chain.Trigger);

        foreach (var step in steps.OrderBy(s => s.Order))
        {
            if (ct.IsCancellationRequested)
            {
                await UpdateChainStatusAsync(chain.Id, HealingChainStatus.Aborted, executionLog, null, ct);
                return BuildResult(chain, false, "Chain aborted due to cancellation.", stepsExecuted);
            }

            if (_config.RequireConfirmationForRisky && IsRiskyStep(step))
            {
                chain.RequiredConfirmation = true;
                executionLog.Add(new StepExecutionLog
                {
                    StepOrder = step.Order,
                    Description = step.Description,
                    Skipped = true,
                    Result = "Skipped: risky step requires manual confirmation"
                });
                continue;
            }

            var function = RunbookPlugin.FindFunction(kernel, step);

            if (function == null)
            {
                executionLog.Add(new StepExecutionLog
                {
                    StepOrder = step.Order,
                    Description = step.Description,
                    Skipped = true,
                    Result = $"Function {step.PluginName}.{step.FunctionName} not found"
                });
                continue;
            }

            try
            {
                var args = new KernelArguments();
                foreach (var param in step.Parameters)
                {
                    args[param.Key] = param.Value;
                }

                var result = await kernel.InvokeAsync(function, args, ct);
                stepsExecuted++;

                var resultStr = result.ToString();
                executionLog.Add(new StepExecutionLog
                {
                    StepOrder = step.Order,
                    Description = step.Description,
                    Result = resultStr?.Length > 500 ? resultStr[..497] + "..." : resultStr
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Healing chain step {Order} failed: {Description}",
                    step.Order, step.Description);

                executionLog.Add(new StepExecutionLog
                {
                    StepOrder = step.Order,
                    Description = step.Description,
                    Result = $"Error: {ex.Message}"
                });
                success = false;
                break;
            }
        }

        if (stepsExecuted == 0)
        {
            success = false;
        }

        var finalStatus = success
            ? HealingChainStatus.Completed
            : stepsExecuted == 0 ? HealingChainStatus.Aborted : HealingChainStatus.Failed;
        int? runbookId = null;
        if (success)
        {
            runbookId = await TryCompileRunbookAsync(chain, steps, ct);
        }

        await UpdateChainStatusAsync(chain.Id, finalStatus, executionLog, runbookId, ct,
            chain.RequiredConfirmation);

        var message = success
            ? $"Healing chain completed: {stepsExecuted}/{steps.Count} steps executed."
            : $"Healing chain failed at step {stepsExecuted + 1}/{steps.Count}.";

        if (chain.RequiredConfirmation)
        {
            message += " Some risky steps were skipped (require manual confirmation).";
        }

        return BuildResult(chain, success, message, stepsExecuted, runbookId);
    }

    private async Task UpdateChainStatusAsync(
        int chainId, HealingChainStatus status, List<StepExecutionLog> log,
        int? generatedRunbookId, CancellationToken ct,
        bool requiredConfirmation = false)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var chain = await db.HealingChains.FindAsync([chainId], ct);
            if (chain != null)
            {
                chain.Status = status;
                chain.ExecutionLogJson = JsonSerializer.Serialize(log);
                chain.GeneratedRunbookId = generatedRunbookId;
                chain.RequiredConfirmation = requiredConfirmation;
                if (status is HealingChainStatus.Completed or HealingChainStatus.Failed or HealingChainStatus.Aborted)
                {
                    chain.CompletedAt = DateTime.UtcNow;
                }

                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update chain {ChainId} status", chainId);
        }
    }

    private async Task<int?> TryCompileRunbookAsync(
        HealingChain chain, List<RunbookStep> steps, CancellationToken ct)
    {
        try
        {
            var investigation = new Investigation
            {
                ThreadId = 0,
                Trigger = chain.Trigger,
                Resolved = true,
                Resolution = $"Healed via {steps.Count}-step chain",
                Steps = steps.Select((s, i) => new InvestigationStep
                {
                    Action = s.FunctionName,
                    Plugin = s.PluginName,
                    ResultSummary = s.Description,
                    Timestamp = DateTime.UtcNow.AddSeconds(i)
                }).ToList()
            };

            var runbook = await _runbookCompiler.CompileFromInvestigationAsync(investigation, ct);
            return runbook?.Id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compile runbook from healing chain {ChainId}", chain.Id);
            return null;
        }
    }

    private static bool IsRiskyStep(RunbookStep step) =>
        RunbookPlugin.DetermineTrustLevel([step]) == TrustLevel.Risky;

    private static HealingChainResult BuildResult(
        HealingChain chain, bool success, string message, int stepsExecuted, int? runbookId = null) =>
        new()
        {
            ChainId = chain.Id,
            Success = success,
            Message = message,
            StepsExecuted = stepsExecuted,
            GeneratedRunbookId = runbookId
        };

    internal static List<RunbookStep>? ParseStepsFromResponse(string response)
    {
        // Strip markdown fences if present
        var json = response.Trim();
        if (json.StartsWith("```"))
        {
            var firstNewline = json.IndexOf('\n');
            if (firstNewline > 0)
            {
                json = json[(firstNewline + 1) ..];
            }

            var lastFence = json.LastIndexOf("```");
            if (lastFence > 0)
            {
                json = json[..lastFence];
            }
        }

        // Find the JSON array
        var start = json.IndexOf('[');
        var end = json.LastIndexOf(']');
        if (start < 0 || end < 0 || end <= start)
        {
            return null;
        }

        json = json[start..(end + 1)];

        try
        {
            return JsonSerializer.Deserialize<List<RunbookStep>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class StepExecutionLog
    {
        public int StepOrder { get; init; }

        public required string Description { get; init; }

        public string? Result { get; init; }

        public bool Skipped { get; init; }
    }
}
