using HomelabBot.Configuration;
using HomelabBot.Data;
using HomelabBot.Data.Entities;
using HomelabBot.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public sealed class HealthScoreService
{
    private readonly IDbContextFactory<HomelabDbContext> _dbFactory;
    private readonly IOptionsMonitor<HealthScoreConfiguration> _config;
    private readonly ILogger<HealthScoreService> _logger;

    public HealthScoreService(
        IDbContextFactory<HomelabDbContext> dbFactory,
        IOptionsMonitor<HealthScoreConfiguration> config,
        ILogger<HealthScoreService> logger)
    {
        _dbFactory = dbFactory;
        _config = config;
        _logger = logger;
    }

    public HealthScoreResult CalculateScore(DailySummaryData data)
    {
        var cfg = _config.CurrentValue;
        var score = 100;

        // Connectivity deductions (missing data sources)
        var connectivityDeductions = 0;
        if (data.Containers.Count == 0)
        {
            connectivityDeductions += cfg.MissingContainersWeight;
        }

        if (data.Pools.Count == 0)
        {
            connectivityDeductions += cfg.MissingPoolsWeight;
        }

        if (data.Router == null)
        {
            connectivityDeductions += cfg.MissingRouterWeight;
        }

        if (data.Monitoring == null)
        {
            connectivityDeductions += cfg.MissingMonitoringWeight;
        }

        // Alert deductions
        var alertDeductions = 0;
        alertDeductions += data.Alerts.Count(a => a.Severity == "critical") * cfg.CriticalAlertWeight;
        alertDeductions += data.Alerts.Count(a => a.Severity == "warning") * cfg.WarningAlertWeight;

        // Container deductions
        var containerDeductions = data.Containers.Count(c => c.State != "running") * cfg.StoppedContainerWeight;

        // Pool deductions
        var poolDeductions = data.Pools.Count(p => p.Health != "ONLINE") * cfg.UnhealthyPoolWeight;

        // Monitoring deductions
        var monitoringDeductions = data.Monitoring != null
            ? data.Monitoring.DownTargets * cfg.DownTargetWeight
            : 0;

        score -= connectivityDeductions + alertDeductions + containerDeductions + poolDeductions + monitoringDeductions;
        score = Math.Max(0, Math.Min(100, score));

        return new HealthScoreResult
        {
            Score = score,
            AlertDeductions = alertDeductions,
            ContainerDeductions = containerDeductions,
            PoolDeductions = poolDeductions,
            MonitoringDeductions = monitoringDeductions,
            ConnectivityDeductions = connectivityDeductions,
        };
    }

    public async Task RecordScoreAsync(HealthScoreResult result, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.HealthScoreHistory.Add(new HealthScoreHistory
        {
            Score = result.Score,
            AlertDeductions = result.AlertDeductions,
            ContainerDeductions = result.ContainerDeductions,
            PoolDeductions = result.PoolDeductions,
            MonitoringDeductions = result.MonitoringDeductions,
            ConnectivityDeductions = result.ConnectivityDeductions,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<string> GetTrendAsync(TimeSpan window, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var since = DateTime.UtcNow - window;

        var records = await db.HealthScoreHistory
            .Where(h => h.RecordedAt >= since)
            .OrderBy(h => h.RecordedAt)
            .ToListAsync(ct);

        if (records.Count < 2)
        {
            return "Not enough data for trend analysis";
        }

        var oldest = records[0].Score;
        var newest = records[^1].Score;
        var diff = newest - oldest;

        return diff switch
        {
            > 10 => $"Improving (+{diff} points in last {FormatWindow(window)})",
            < -10 => $"Dropping ({diff} points in last {FormatWindow(window)})",
            _ => $"Stable (±{Math.Abs(diff)} in last {FormatWindow(window)})"
        };
    }

    public async Task<int?> GetScoreAtWindowStartAsync(TimeSpan window, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var since = DateTime.UtcNow - window;

        return await db.HealthScoreHistory
            .Where(h => h.RecordedAt >= since)
            .OrderBy(h => h.RecordedAt)
            .Select(h => (int?)h.Score)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<int?> GetLatestScoreAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        return await db.HealthScoreHistory
            .OrderByDescending(h => h.RecordedAt)
            .Select(h => (int?)h.Score)
            .FirstOrDefaultAsync(ct);
    }

    public async Task PruneOldRecordsAsync(TimeSpan retention, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var cutoff = DateTime.UtcNow - retention;

        var deleted = await db.HealthScoreHistory
            .Where(h => h.RecordedAt < cutoff)
            .ExecuteDeleteAsync(ct);

        if (deleted > 0)
        {
            _logger.LogDebug("Pruned {Count} old health score records", deleted);
        }
    }

    private static string FormatWindow(TimeSpan window) => window.TotalHours switch
    {
        < 1 => $"{window.TotalMinutes:F0}m",
        < 24 => $"{window.TotalHours:F0}h",
        _ => $"{window.TotalDays:F0}d"
    };
}
