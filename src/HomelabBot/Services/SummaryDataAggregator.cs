using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using HomelabBot.Configuration;
using HomelabBot.Models;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public sealed class SummaryDataAggregator
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SummaryDataAggregator> _logger;
    private readonly string _alertmanagerUrl;
    private readonly string _prometheusUrl;
    private readonly string _truenasUrl;
    private readonly string? _truenasApiKey;

    public SummaryDataAggregator(
        IHttpClientFactory httpClientFactory,
        IOptions<AlertmanagerConfiguration> alertmanagerConfig,
        IOptions<PrometheusConfiguration> prometheusConfig,
        IOptions<TrueNASConfiguration> truenasConfig,
        ILogger<SummaryDataAggregator> logger)
    {
        _httpClient = httpClientFactory.CreateClient("Default");
        _logger = logger;
        _alertmanagerUrl = alertmanagerConfig.Value.Host.TrimEnd('/');
        _prometheusUrl = prometheusConfig.Value.Host.TrimEnd('/');
        _truenasUrl = truenasConfig.Value.Host.TrimEnd('/');
        _truenasApiKey = truenasConfig.Value.ApiKey;
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

        var healthScore = CalculateHealthScore(alerts, containers, pools, monitoring);

        return new DailySummaryData
        {
            Alerts = alerts,
            Containers = containers,
            Pools = pools,
            Router = router,
            Monitoring = monitoring,
            HealthScore = healthScore,
            GeneratedAt = DateTime.UtcNow
        };
    }

    private async Task<List<AlertSummary>> GetAlertsAsync(CancellationToken ct)
    {
        try
        {
            var response = await _httpClient.GetAsync(
                $"{_alertmanagerUrl}/api/v2/alerts?active=true&silenced=false&inhibited=false", ct);
            response.EnsureSuccessStatusCode();

            var alerts = await response.Content.ReadFromJsonAsync<List<AlertmanagerAlert>>(ct);
            return alerts?.Select(a => new AlertSummary
            {
                Name = a.Labels?.GetValueOrDefault("alertname") ?? "unknown",
                Severity = a.Labels?.GetValueOrDefault("severity") ?? "unknown",
                Instance = a.Labels?.GetValueOrDefault("instance")
            }).ToList() ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch alerts");
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
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_truenasUrl}/api/v2.0/pool");
            if (!string.IsNullOrEmpty(_truenasApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _truenasApiKey);

            var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var pools = await response.Content.ReadFromJsonAsync<List<TrueNASPool>>(ct);
            return pools?.Select(p =>
            {
                var allocatedTB = (p.Allocated ?? 0) / (1024.0 * 1024 * 1024 * 1024);
                var sizeTB = (p.Size ?? 0) / (1024.0 * 1024 * 1024 * 1024);
                var usedPercent = sizeTB > 0 ? (allocatedTB / sizeTB) * 100 : 0;

                return new PoolStatus
                {
                    Name = p.Name ?? "unknown",
                    Health = p.Status ?? "unknown",
                    UsedPercent = usedPercent
                };
            }).ToList() ?? [];
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
            var cpuLoad = await QueryPrometheusValue("mktxp_system_cpu_load", ct);
            var memTotal = await QueryPrometheusValue("mktxp_system_total_memory", ct);
            var memFree = await QueryPrometheusValue("mktxp_system_free_memory", ct);
            var uptimeSeconds = await QueryPrometheusValue("mktxp_system_uptime", ct);

            var memUsedPercent = memTotal > 0 ? ((memTotal - memFree) / memTotal) * 100 : 0;

            return new RouterStatus
            {
                CpuPercent = cpuLoad,
                MemoryPercent = memUsedPercent,
                Uptime = TimeSpan.FromSeconds(uptimeSeconds)
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
            var response = await _httpClient.GetAsync($"{_prometheusUrl}/api/v1/targets", ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<PrometheusTargetsResponse>(ct);
            var targets = result?.Data?.ActiveTargets ?? [];

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

    private async Task<double> QueryPrometheusValue(string metric, CancellationToken ct)
    {
        try
        {
            var encodedQuery = Uri.EscapeDataString(metric);
            var response = await _httpClient.GetAsync($"{_prometheusUrl}/api/v1/query?query={encodedQuery}", ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<PrometheusQueryResponse>(ct);
            if (result?.Data?.Result?.FirstOrDefault()?.Value?.Length > 1)
            {
                if (double.TryParse(result.Data.Result[0].Value![1].ToString(), out var value))
                    return value;
            }
            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int CalculateHealthScore(
        List<AlertSummary> alerts,
        List<Models.ContainerStatus> containers,
        List<PoolStatus> pools,
        MonitoringStatus? monitoring)
    {
        var score = 100;

        // Deduct for alerts
        var criticalAlerts = alerts.Count(a => a.Severity == "critical");
        var warningAlerts = alerts.Count(a => a.Severity == "warning");
        score -= criticalAlerts * 20;
        score -= warningAlerts * 5;

        // Deduct for stopped containers
        var stoppedContainers = containers.Count(c => c.State != "running");
        score -= stoppedContainers * 10;

        // Deduct for unhealthy pools
        var unhealthyPools = pools.Count(p => p.Health != "ONLINE");
        score -= unhealthyPools * 25;

        // Deduct for down targets
        if (monitoring != null)
            score -= monitoring.DownTargets * 15;

        return Math.Max(0, Math.Min(100, score));
    }

    private sealed class AlertmanagerAlert
    {
        public Dictionary<string, string>? Labels { get; set; }
    }

    private sealed class TrueNASPool
    {
        public string? Name { get; set; }
        public string? Status { get; set; }
        public long? Size { get; set; }
        public long? Allocated { get; set; }
    }

    private sealed class PrometheusTargetsResponse
    {
        public PrometheusTargetsData? Data { get; set; }
    }

    private sealed class PrometheusTargetsData
    {
        public List<PrometheusTarget> ActiveTargets { get; set; } = [];
    }

    private sealed class PrometheusTarget
    {
        public string Health { get; set; } = "";
    }

    private sealed class PrometheusQueryResponse
    {
        public PrometheusQueryData? Data { get; set; }
    }

    private sealed class PrometheusQueryData
    {
        public List<PrometheusResult>? Result { get; set; }
    }

    private sealed class PrometheusResult
    {
        public JsonElement[]? Value { get; set; }
    }
}
