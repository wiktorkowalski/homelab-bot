using Docker.DotNet;
using Docker.DotNet.Models;
using HomelabBot.Models;
using HomelabBot.Plugins;

namespace HomelabBot.Services;

public sealed class SummaryDataAggregator
{
    private readonly PrometheusQueryService _prometheus;
    private readonly MikroTikPlugin _mikrotikPlugin;
    private readonly TrueNASPlugin _truenasPlugin;
    private readonly ILogger<SummaryDataAggregator> _logger;
    private readonly HealthScoreService _healthScoreService;

    public SummaryDataAggregator(
        PrometheusQueryService prometheus,
        MikroTikPlugin mikrotikPlugin,
        TrueNASPlugin truenasPlugin,
        HealthScoreService healthScoreService,
        ILogger<SummaryDataAggregator> logger)
    {
        _prometheus = prometheus;
        _mikrotikPlugin = mikrotikPlugin;
        _truenasPlugin = truenasPlugin;
        _logger = logger;
        _healthScoreService = healthScoreService;
    }

    public async Task<DailySummaryData> AggregateAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("Starting data aggregation for daily summary");

        var alertsTask = GetAlertsAsync(ct);
        var containersTask = GetContainersAsync(ct);
        var poolsTask = GetPoolsAsync(ct);
        var routerTask = GetRouterStatusAsync(ct);
        var monitoringTask = GetMonitoringStatusAsync(ct);

        await Task.WhenAll(alertsTask, containersTask, poolsTask, routerTask, monitoringTask);

        var alerts = await alertsTask;
        var containers = await containersTask;
        var pools = await poolsTask;
        var router = await routerTask;
        var monitoring = await monitoringTask;

        var data = new DailySummaryData
        {
            Alerts = alerts,
            Containers = containers,
            Pools = pools,
            Router = router,
            Monitoring = monitoring,
            GeneratedAt = DateTime.UtcNow,
        };

        data.HealthScore = _healthScoreService.CalculateScore(data).Score;

        return data;
    }

    private async Task<List<AlertSummary>> GetAlertsAsync(CancellationToken ct)
    {
        try
        {
            var endTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var startTime = endTime - 86400; // 24 hours ago

            var result = await _prometheus.QueryRangeAsync(
                "ALERTS{alertstate=\"firing\"}", startTime, endTime, 300, ct);

            var alertResults = result?.Data?.Result ?? [];

            var alerts = alertResults
                .Select(r => new AlertSummary
                {
                    Name = r.Metric?.GetValueOrDefault("alertname") ?? "unknown",
                    Severity = r.Metric?.GetValueOrDefault("severity") ?? "unknown",
                    Instance = r.Metric?.GetValueOrDefault("instance")
                })
                .DistinctBy(a => (a.Name, a.Instance))
                .ToList();

            _logger.LogDebug("Found {Count} alerts in the last 24h", alerts.Count);
            return alerts;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch alerts from Prometheus");
            return [];
        }
    }

    private async Task<List<Models.ContainerStatus>> GetContainersAsync(CancellationToken ct)
    {
        try
        {
            using var dockerClient = new DockerClientConfiguration(
                new Uri("unix:///var/run/docker.sock")).CreateClient();

            var containers = await dockerClient.Containers.ListContainersAsync(
                new ContainersListParameters { All = true }, ct);

            return containers.Select(c => new Models.ContainerStatus
            {
                Name = c.Names.FirstOrDefault()?.TrimStart('/') ?? c.ID[..12],
                State = c.State,
                Health = c.Status
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch containers");
            return [];
        }
    }

    private async Task<List<PoolStatus>> GetPoolsAsync(CancellationToken ct)
    {
        try
        {
            var poolInfos = await _truenasPlugin.GetPoolInfoAsync(ct);

            return poolInfos.Select(p =>
            {
                var allocatedTB = p.AllocatedBytes / (1024.0 * 1024 * 1024 * 1024);
                var sizeTB = p.SizeBytes / (1024.0 * 1024 * 1024 * 1024);
                var usedPercent = sizeTB > 0 ? (allocatedTB / sizeTB) * 100 : 0;

                return new PoolStatus
                {
                    Name = p.Name,
                    Health = p.Status,
                    UsedPercent = usedPercent
                };
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch pool status");
            return [];
        }
    }

    private async Task<RouterStatus?> GetRouterStatusAsync(CancellationToken ct)
    {
        try
        {
            var metrics = await _mikrotikPlugin.GetRouterMetricsAsync(ct);

            return new RouterStatus
            {
                CpuPercent = metrics.CpuLoad,
                MemoryPercent = metrics.MemoryPercent,
                Uptime = metrics.Uptime
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch router status");
            return null;
        }
    }

    private async Task<MonitoringStatus?> GetMonitoringStatusAsync(CancellationToken ct)
    {
        try
        {
            var targets = await _prometheus.GetTargetStatusesAsync(ct);

            return new MonitoringStatus
            {
                TotalTargets = targets.Count,
                UpTargets = targets.Count(t => t.Health == "up"),
                DownTargets = targets.Count(t => t.Health == "down")
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch monitoring status");
            return null;
        }
    }
}
