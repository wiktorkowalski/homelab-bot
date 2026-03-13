using HomelabBot.Data;
using HomelabBot.Data.Entities;
using HomelabBot.Models;
using HomelabBot.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;

namespace HomelabBot.Services;

public sealed class RunbookTriggerService
{
    private readonly IDbContextFactory<HomelabDbContext> _dbFactory;
    private readonly RunbookPlugin _runbookPlugin;
    private readonly KernelService _kernelService;
    private readonly ILogger<RunbookTriggerService> _logger;

    public RunbookTriggerService(
        IDbContextFactory<HomelabDbContext> dbFactory,
        RunbookPlugin runbookPlugin,
        KernelService kernelService,
        ILogger<RunbookTriggerService> logger)
    {
        _dbFactory = dbFactory;
        _runbookPlugin = runbookPlugin;
        _kernelService = kernelService;
        _logger = logger;
    }

    public async Task<string?> TryMatchAndExecuteAsync(AlertmanagerWebhookAlert alert, CancellationToken ct = default)
    {
        if (!alert.IsFiring)
            return null;

        Runbook? matched;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var runbooks = await db.Runbooks
                .Where(r => r.Enabled)
                .ToListAsync(ct);

            matched = runbooks.FirstOrDefault(r => MatchesTrigger(r, alert));
        }

        if (matched == null)
            return null;

        _logger.LogInformation("Alert {AlertName} matched runbook '{Runbook}' (ID {Id})",
            alert.AlertName, matched.Name, matched.Id);

        if (matched.TrustLevel == TrustLevel.Risky)
        {
            return $"📋 Runbook **{matched.Name}** matched this alert but requires manual confirmation (TrustLevel=Risky). " +
                   $"Use the bot to run: `execute runbook {matched.Id}`";
        }

        // Execute directly — no LLM roundtrip needed
        try
        {
            var result = await _runbookPlugin.ExecuteRunbook(matched.Id, _kernelService.GetKernel());
            return $"📋 **Runbook: {matched.Name}** auto-triggered\n\n{result}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute runbook {Id} for alert {Alert}", matched.Id, alert.AlertName);
            return $"📋 Runbook **{matched.Name}** matched but execution failed: {ex.Message}";
        }
    }

    private static bool MatchesTrigger(Runbook runbook, AlertmanagerWebhookAlert alert)
    {
        var trigger = runbook.TriggerCondition;

        // Label match: "alertname=HighCPU" or "severity=critical"
        if (trigger.Contains('='))
        {
            var parts = trigger.Split('=', 2);
            if (parts.Length == 2)
            {
                var key = parts[0].Trim();
                var value = parts[1].Trim();
                return alert.Labels.TryGetValue(key, out var labelValue) &&
                       labelValue.Equals(value, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Keyword match against alert name and description
        var keywords = trigger.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var alertText = $"{alert.AlertName} {alert.Description ?? ""} {alert.Summary ?? ""}".ToLowerInvariant();

        return keywords.Length > 0 && keywords.All(k => alertText.Contains(k));
    }

    public async Task SeedDefaultRunbooksAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        if (await db.Runbooks.AnyAsync(ct))
            return;

        var defaults = new List<Runbook>
        {
            new()
            {
                Name = "High CPU Investigation",
                Description = "Investigate high CPU usage by checking metrics and top containers",
                TriggerCondition = "alertname=HighCPU",
                TrustLevel = TrustLevel.ReadOnly,
                StepsJson = """
                [
                    {"Order":1,"Description":"Check node CPU metrics","PluginName":"Prometheus","FunctionName":"GetNodeStats","Parameters":{}},
                    {"Order":2,"Description":"Check container metrics","PluginName":"Prometheus","FunctionName":"GetContainerMetrics","Parameters":{}},
                    {"Order":3,"Description":"List containers","PluginName":"Docker","FunctionName":"ListContainers","Parameters":{}}
                ]
                """,
            },
            new()
            {
                Name = "Container Down Recovery",
                Description = "Check and restart a failed container",
                TriggerCondition = "alertname=ContainerDown",
                TrustLevel = TrustLevel.Risky,
                StepsJson = """
                [
                    {"Order":1,"Description":"List all containers","PluginName":"Docker","FunctionName":"ListContainers","Parameters":{}},
                    {"Order":2,"Description":"Check container logs","PluginName":"Loki","FunctionName":"CountErrorsByContainer","Parameters":{"since":"1h"}}
                ]
                """,
            },
            new()
            {
                Name = "Disk Space Alert",
                Description = "Investigate low disk space",
                TriggerCondition = "alertname=DiskSpaceLow",
                TrustLevel = TrustLevel.ReadOnly,
                StepsJson = """
                [
                    {"Order":1,"Description":"Check node disk stats","PluginName":"Prometheus","FunctionName":"GetNodeStats","Parameters":{}},
                    {"Order":2,"Description":"Check TrueNAS pool status","PluginName":"TrueNAS","FunctionName":"GetPoolStatus","Parameters":{}},
                    {"Order":3,"Description":"Check dataset usage","PluginName":"TrueNAS","FunctionName":"GetDatasetUsage","Parameters":{}}
                ]
                """,
            },
        };

        db.Runbooks.AddRange(defaults);
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Seeded {Count} default runbooks", defaults.Count);
    }
}
