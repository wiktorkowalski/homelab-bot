using System.Collections.Concurrent;
using HomelabBot.Configuration;
using HomelabBot.Data;
using HomelabBot.Data.Entities;
using HomelabBot.Models;
using HomelabBot.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public sealed class AutoRemediationService
{
    private readonly DockerPlugin _dockerPlugin;
    private readonly IDbContextFactory<HomelabDbContext> _dbFactory;
    private readonly AutoRemediationConfiguration _config;
    private readonly ILogger<AutoRemediationService> _logger;
    private readonly ConcurrentDictionary<string, List<DateTime>> _cooldowns = new();
    private bool _enabled;

    public AutoRemediationService(
        DockerPlugin dockerPlugin,
        IDbContextFactory<HomelabDbContext> dbFactory,
        IOptions<AutoRemediationConfiguration> config,
        ILogger<AutoRemediationService> logger)
    {
        _dockerPlugin = dockerPlugin;
        _dbFactory = dbFactory;
        _config = config.Value;
        _logger = logger;
        _enabled = _config.Enabled;
    }

    public bool IsEnabled => _enabled;

    public void SetEnabled(bool enabled) => _enabled = enabled;

    public async Task<RemediationResult?> TryAutoRemediateAsync(
        AlertmanagerWebhookAlert alert,
        List<Pattern> matchedPatterns,
        CancellationToken ct)
    {
        if (!_enabled)
            return null;

        // Filter patterns by success rate and feedback count
        var qualifiedPatterns = matchedPatterns
            .Where(p =>
                p.SuccessRate >= _config.MinSuccessRate &&
                (p.SuccessCount + p.FailureCount) >= _config.MinFeedbackCount)
            .ToList();

        if (qualifiedPatterns.Count == 0)
            return null;

        // Extract container name from alert labels
        var containerName = ExtractContainerName(alert);
        if (containerName == null)
            return null;

        // Check cooldown
        if (IsCooldownExceeded(containerName))
        {
            _logger.LogWarning("Cooldown exceeded for container {Container}, skipping auto-remediation", containerName);
            return new RemediationResult
            {
                WasAutoExecuted = false,
                NeedsConfirmation = false,
                Message = $"Auto-remediation skipped for **{containerName}**: cooldown limit reached ({_config.MaxRestartsPerHour} restarts/hour). Manual intervention required.",
                ContainerName = containerName
            };
        }

        // Check criticality
        var isCritical = await IsContainerCriticalAsync(containerName, ct);
        var bestPattern = qualifiedPatterns.First();

        if (isCritical)
        {
            return await SuggestRemediationAsync(containerName, bestPattern, ct);
        }

        return await ExecuteRemediationAsync(containerName, bestPattern, ct);
    }

    public async Task RecordUserConfirmationAsync(int actionId, bool approved, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var action = await db.RemediationActions.FindAsync([actionId], ct);
        if (action == null)
        {
            _logger.LogWarning("Remediation action {ActionId} not found", actionId);
            return;
        }

        action.ConfirmedByUser = true;

        if (approved)
        {
            try
            {
                var beforeState = await _dockerPlugin.GetContainerStatus(action.ContainerName);
                action.BeforeState = beforeState;

                var result = action.ActionType == "start"
                    ? await _dockerPlugin.StartContainer(action.ContainerName)
                    : await _dockerPlugin.RestartContainer(action.ContainerName);

                _logger.LogInformation("User-approved remediation executed for {Container}: {Result}",
                    action.ContainerName, result);

                RecordCooldown(action.ContainerName);

                // Wait and check state
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                var afterState = await _dockerPlugin.GetContainerStatus(action.ContainerName);
                action.AfterState = afterState;
                action.Success = !afterState.Contains("not running", StringComparison.OrdinalIgnoreCase)
                    && !afterState.Contains("exited", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute approved remediation for {Container}", action.ContainerName);
                action.Success = false;
                action.AfterState = $"Error: {ex.Message}";
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task RecordFeedbackAsync(int actionId, bool success, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var action = await db.RemediationActions.FindAsync([actionId], ct);
        if (action == null)
            return;

        action.Success = success;

        // Update the associated pattern's feedback if present
        if (action.PatternId.HasValue)
        {
            var pattern = await db.Patterns.FindAsync([action.PatternId.Value], ct);
            if (pattern != null)
            {
                if (success)
                    pattern.SuccessCount++;
                else
                    pattern.FailureCount++;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<string> GetStatusAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var recentCount = await db.RemediationActions
            .CountAsync(a => a.ExecutedAt > DateTime.UtcNow.AddHours(-24), ct);
        var successCount = await db.RemediationActions
            .CountAsync(a => a.ExecutedAt > DateTime.UtcNow.AddHours(-24) && a.Success, ct);

        var activeCooldowns = _cooldowns
            .Where(kv => kv.Value.Count(d => d > DateTime.UtcNow.AddHours(-1)) > 0)
            .Select(kv => $"{kv.Key}: {kv.Value.Count(d => d > DateTime.UtcNow.AddHours(-1))}/{_config.MaxRestartsPerHour}")
            .ToList();

        var lines = new List<string>
        {
            $"**Auto-Remediation Status**",
            $"Enabled: {(_enabled ? "Yes" : "No")}",
            $"Actions (24h): {recentCount} ({successCount} successful)",
            $"Min success rate: {_config.MinSuccessRate}%",
            $"Min feedback count: {_config.MinFeedbackCount}",
            $"Max restarts/hour: {_config.MaxRestartsPerHour}"
        };

        if (activeCooldowns.Count > 0)
        {
            lines.Add($"Active cooldowns: {string.Join(", ", activeCooldowns)}");
        }

        return string.Join("\n", lines);
    }

    public async Task ToggleCriticalityAsync(string containerName, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.ContainerCriticalities
            .FirstOrDefaultAsync(c => c.ContainerName == containerName, ct);

        if (existing != null)
        {
            existing.IsCritical = !existing.IsCritical;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            db.ContainerCriticalities.Add(new ContainerCriticality
            {
                ContainerName = containerName,
                IsCritical = true,
                UpdatedAt = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<List<ContainerCriticality>> ListCriticalitiesAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.ContainerCriticalities.OrderBy(c => c.ContainerName).ToListAsync(ct);
    }

    private static string? ExtractContainerName(AlertmanagerWebhookAlert alert)
    {
        if (alert.Labels.TryGetValue("container", out var container))
            return container;

        if (alert.Labels.TryGetValue("container_name", out var containerName))
            return containerName;

        if (alert.Labels.TryGetValue("instance", out var instance))
        {
            // Strip port from instance (e.g., "nginx:80" -> "nginx")
            var colonIndex = instance.LastIndexOf(':');
            return colonIndex > 0 ? instance[..colonIndex] : instance;
        }

        return null;
    }

    private async Task<bool> IsContainerCriticalAsync(string containerName, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entry = await db.ContainerCriticalities
            .FirstOrDefaultAsync(c => c.ContainerName == containerName, ct);
        return entry?.IsCritical ?? false;
    }

    private bool IsCooldownExceeded(string containerName)
    {
        var timestamps = _cooldowns.GetOrAdd(containerName, _ => []);
        lock (timestamps)
        {
            // Clean up old entries
            timestamps.RemoveAll(t => t < DateTime.UtcNow.AddHours(-1));
            return timestamps.Count >= _config.MaxRestartsPerHour;
        }
    }

    private void RecordCooldown(string containerName)
    {
        var timestamps = _cooldowns.GetOrAdd(containerName, _ => []);
        lock (timestamps)
        {
            timestamps.Add(DateTime.UtcNow);
        }
    }

    private async Task<RemediationResult> ExecuteRemediationAsync(
        string containerName, Pattern pattern, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        string beforeState;
        try
        {
            beforeState = await _dockerPlugin.GetContainerStatus(containerName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get container status for {Container}", containerName);
            beforeState = "unknown";
        }

        var actionType = beforeState.Contains("exited", StringComparison.OrdinalIgnoreCase) ? "start" : "restart";

        var action = new RemediationAction
        {
            ContainerName = containerName,
            ActionType = actionType,
            Trigger = "pattern",
            PatternId = pattern.Id,
            BeforeState = beforeState,
            ConfirmedByUser = false
        };

        try
        {
            var result = actionType == "start"
                ? await _dockerPlugin.StartContainer(containerName)
                : await _dockerPlugin.RestartContainer(containerName);

            _logger.LogInformation("Auto-remediation executed for {Container}: {ActionType} - {Result}",
                containerName, actionType, result);

            RecordCooldown(containerName);

            // Wait and verify
            await Task.Delay(TimeSpan.FromSeconds(30), ct);

            string afterState;
            try
            {
                afterState = await _dockerPlugin.GetContainerStatus(containerName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get post-remediation status for {Container}", containerName);
                afterState = "unknown";
            }

            action.AfterState = afterState;
            action.Success = !afterState.Contains("not running", StringComparison.OrdinalIgnoreCase)
                && !afterState.Contains("exited", StringComparison.OrdinalIgnoreCase);

            db.RemediationActions.Add(action);
            await db.SaveChangesAsync(ct);

            var statusEmoji = action.Success ? "OK" : "FAILED";
            return new RemediationResult
            {
                WasAutoExecuted = true,
                NeedsConfirmation = false,
                ActionId = action.Id,
                Message = $"Auto-remediation [{statusEmoji}]: **{actionType}** on **{containerName}** "
                    + $"(pattern: {pattern.Symptom}, success rate: {pattern.SuccessRate:F0}%)",
                ContainerName = containerName
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-remediation failed for {Container}", containerName);

            action.Success = false;
            action.AfterState = $"Error: {ex.Message}";
            db.RemediationActions.Add(action);
            await db.SaveChangesAsync(ct);

            return new RemediationResult
            {
                WasAutoExecuted = true,
                NeedsConfirmation = false,
                ActionId = action.Id,
                Message = $"Auto-remediation FAILED for **{containerName}**: {ex.Message}",
                ContainerName = containerName
            };
        }
    }

    private async Task<RemediationResult> SuggestRemediationAsync(
        string containerName, Pattern pattern, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        string beforeState;
        try
        {
            beforeState = await _dockerPlugin.GetContainerStatus(containerName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get container status for {Container}", containerName);
            beforeState = "unknown";
        }

        var actionType = beforeState.Contains("exited", StringComparison.OrdinalIgnoreCase) ? "start" : "restart";

        var action = new RemediationAction
        {
            ContainerName = containerName,
            ActionType = actionType,
            Trigger = "pattern",
            PatternId = pattern.Id,
            BeforeState = beforeState,
            ConfirmedByUser = false
        };

        db.RemediationActions.Add(action);
        await db.SaveChangesAsync(ct);

        return new RemediationResult
        {
            WasAutoExecuted = false,
            NeedsConfirmation = true,
            ActionId = action.Id,
            Message = $"Container **{containerName}** is marked critical. "
                + $"Suggested action: **{actionType}** (pattern: {pattern.Symptom}, success rate: {pattern.SuccessRate:F0}%). "
                + "Approve or reject below.",
            ContainerName = containerName
        };
    }
}
