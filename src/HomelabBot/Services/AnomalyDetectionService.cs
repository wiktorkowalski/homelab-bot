using System.Text;
using System.Text.Json;
using HomelabBot.Configuration;
using HomelabBot.Data;
using HomelabBot.Data.Entities;
using HomelabBot.Helpers;
using HomelabBot.Models;
using HomelabBot.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public sealed class AnomalyDetectionService : BackgroundService
{
    private readonly IOptionsMonitor<AnomalyDetectionConfiguration> _config;
    private readonly PrometheusQueryService _prometheus;
    private readonly HttpClient _httpClient;
    private readonly SmartNotificationService _smartNotification;
    private readonly DiscordBotService _discordBot;
    private readonly IDbContextFactory<HomelabDbContext> _dbFactory;
    private readonly ILogger<AnomalyDetectionService> _logger;
    private readonly DockerPlugin _dockerPlugin;
    private readonly TrueNASPlugin _truenasPlugin;
    private readonly string _lokiUrl;

    private readonly ServiceStateStore _stateStore;

    // In-memory baselines for rate-of-change detection
    private readonly Dictionary<string, double> _lastMetricValues = new();
    private int _heuristicTick;

    public AnomalyDetectionService(
        IOptionsMonitor<AnomalyDetectionConfiguration> config,
        IHttpClientFactory httpClientFactory,
        PrometheusQueryService prometheus,
        SmartNotificationService smartNotification,
        DiscordBotService discordBot,
        IDbContextFactory<HomelabDbContext> dbFactory,
        ILogger<AnomalyDetectionService> logger,
        DockerPlugin dockerPlugin,
        TrueNASPlugin truenasPlugin,
        IOptions<LokiConfiguration> lokiConfig,
        ServiceStateStore stateStore)
    {
        _config = config;
        _httpClient = httpClientFactory.CreateClient("Default");
        _prometheus = prometheus;
        _smartNotification = smartNotification;
        _discordBot = discordBot;
        _dbFactory = dbFactory;
        _logger = logger;
        _dockerPlugin = dockerPlugin;
        _truenasPlugin = truenasPlugin;
        _lokiUrl = lokiConfig.Value.Host.TrimEnd('/');
        _stateStore = stateStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Anomaly detection service started, waiting for Discord...");
        await _discordBot.WaitForReadyAsync(stoppingToken);
        _logger.LogInformation("Discord ready, anomaly detection running");
        await LoadBaselineAsync();

        var isFirstRun = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_config.CurrentValue.Enabled)
                {
                    _logger.LogDebug("Anomaly detection disabled, rechecking in 1 minute");
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }

                var interval = Math.Max(1, _config.CurrentValue.HeuristicIntervalMinutes);
                var delay = isFirstRun ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(interval);
                isFirstRun = false;
                await Task.Delay(delay, stoppingToken);

                _heuristicTick++;
                var anomalies = await RunHeuristicChecksAsync(stoppingToken);
                await PersistBaselineAsync();

                // Evaluate via LLM every N ticks, but critical anomalies bypass throttle
                var llmInterval = Math.Max(1, _config.CurrentValue.LlmIntervalTicks);
                if (anomalies.Count > 0
                    && (_heuristicTick % llmInterval == 0
                        || anomalies.Any(a => a.Severity == AnomalySeverity.Critical)))
                {
                    await NotifyAnomaliesAsync(anomalies, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in anomaly detection service");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }

    private async Task<List<Anomaly>> RunHeuristicChecksAsync(CancellationToken ct)
    {
        var anomalies = new List<Anomaly>();

        // Run all checks in parallel
        var tasks = new List<Task<List<Anomaly>>>
        {
            CheckCpuAsync(ct),
            CheckMemoryAsync(ct),
            CheckDiskAsync(ct),
            CheckTargetsAsync(ct),
            CheckContainerHealthAsync(ct),
            CheckContainerRestartsAsync(ct),
            CheckRouterHealthAsync(ct),
            CheckNetworkTrafficAsync(ct),
            CheckStoragePoolHealthAsync(ct),
            CheckTraefik5xxAsync(ct),
            CheckCertExpiryAsync(ct),
            CheckPrometheusCardinalityAsync(ct),
            CheckLokiHealthAsync(ct),
        };

        await Task.WhenAll(tasks);

        foreach (var task in tasks)
        {
            anomalies.AddRange(await task);
        }

        if (anomalies.Count > 0)
        {
            _logger.LogDebug("Heuristic check found {Count} anomalies", anomalies.Count);
        }

        return anomalies;
    }

    private async Task<List<Anomaly>> CheckCpuAsync(CancellationToken ct)
    {
        var anomalies = new List<Anomaly>();

        var cpuUsage = await _prometheus.QueryScalarAsync(
            "100 - (avg(rate(node_cpu_seconds_total{mode=\"idle\"}[5m])) * 100)", ct);

        if (cpuUsage == null)
        {
            return anomalies;
        }

        var previous = _lastMetricValues.GetValueOrDefault("cpu", cpuUsage.Value);
        _lastMetricValues["cpu"] = cpuUsage.Value;

        if (cpuUsage > 80)
        {
            anomalies.Add(new Anomaly
            {
                Type = "CPU",
                Message = $"CPU usage at {cpuUsage:F1}%",
                Severity = cpuUsage > 95 ? AnomalySeverity.Critical : AnomalySeverity.Warning,
                Value = cpuUsage.Value,
            });
        }
        else if (cpuUsage > 50 && cpuUsage - previous > 20)
        {
            anomalies.Add(new Anomaly
            {
                Type = "CPU",
                Message = $"CPU spike: {previous:F1}% → {cpuUsage:F1}% (rapid increase)",
                Severity = AnomalySeverity.Warning,
                Value = cpuUsage.Value,
            });
        }

        return anomalies;
    }

    private async Task<List<Anomaly>> CheckMemoryAsync(CancellationToken ct)
    {
        var anomalies = new List<Anomaly>();

        var memUsage = await _prometheus.QueryScalarAsync(
            "(1 - node_memory_MemAvailable_bytes / node_memory_MemTotal_bytes) * 100", ct);

        if (memUsage == null)
        {
            return anomalies;
        }

        if (memUsage > 90)
        {
            anomalies.Add(new Anomaly
            {
                Type = "Memory",
                Message = $"Memory usage at {memUsage:F1}%",
                Severity = memUsage > 95 ? AnomalySeverity.Critical : AnomalySeverity.Warning,
                Value = memUsage.Value,
            });
        }

        return anomalies;
    }

    private async Task<List<Anomaly>> CheckDiskAsync(CancellationToken ct)
    {
        var anomalies = new List<Anomaly>();

        var diskUsage = await _prometheus.QueryScalarAsync(
            "(1 - node_filesystem_avail_bytes{mountpoint=\"/\"} / node_filesystem_size_bytes{mountpoint=\"/\"}) * 100", ct);

        if (diskUsage == null)
        {
            return anomalies;
        }

        if (diskUsage > 85)
        {
            anomalies.Add(new Anomaly
            {
                Type = "Disk",
                Message = $"Disk usage at {diskUsage:F1}%",
                Severity = diskUsage > 95 ? AnomalySeverity.Critical : AnomalySeverity.Warning,
                Value = diskUsage.Value,
            });
        }

        // Check disk fill rate prediction (ratio < 0 means available space crosses zero within 30 days)
        var predictedAvailableRatio = await _prometheus.QueryScalarAsync(
            "predict_linear(node_filesystem_avail_bytes{mountpoint=\"/\"}[7d], 30*24*3600) / node_filesystem_size_bytes{mountpoint=\"/\"}", ct);

        if (predictedAvailableRatio is < 0)
        {
            anomalies.Add(new Anomaly
            {
                Type = "Disk",
                Message = "Disk predicted to fill within 30 days based on current trend",
                Severity = AnomalySeverity.Warning,
                Value = diskUsage ?? 0,
            });
        }

        return anomalies;
    }

    private async Task<List<Anomaly>> CheckTargetsAsync(CancellationToken ct)
    {
        var anomalies = new List<Anomaly>();

        try
        {
            var targets = await _prometheus.GetTargetStatusesAsync(ct);
            var downTargets = targets.Where(t => t.Health == "down").Select(t => t.Job).ToList();

            if (downTargets.Count > 0)
            {
                anomalies.Add(new Anomaly
                {
                    Type = "Monitoring",
                    Message = $"{downTargets.Count} targets down: {string.Join(", ", downTargets.Take(5))}",
                    Severity = downTargets.Count > 2 ? AnomalySeverity.Critical : AnomalySeverity.Warning,
                    Value = downTargets.Count,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to check Prometheus targets");
        }

        return anomalies;
    }

    private async Task<List<Anomaly>> CheckContainerHealthAsync(CancellationToken ct)
    {
        var anomalies = new List<Anomaly>();
        try
        {
            var output = await _dockerPlugin.ListContainers();
            var stoppedCount = output.Split('\n').Count(l => l.Contains("\ud83d\udd34"));
            if (stoppedCount > 0)
            {
                anomalies.Add(new Anomaly
                {
                    Type = "Container",
                    Message = $"{stoppedCount} container(s) stopped",
                    Severity = stoppedCount > 3 ? AnomalySeverity.Critical : AnomalySeverity.Warning,
                    Value = stoppedCount,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to check container health");
        }

        return anomalies;
    }

    private async Task<List<Anomaly>> CheckContainerRestartsAsync(CancellationToken ct)
    {
        var anomalies = new List<Anomaly>();
        try
        {
            var restartRate = await _prometheus.QueryScalarAsync(
                "sum(increase(container_restart_count{name!=\"\"}[5m]))", ct);
            if (restartRate is > 0)
            {
                anomalies.Add(new Anomaly
                {
                    Type = "Container",
                    Message = $"Container restarts detected in last 5m (rate: {restartRate:F1})",
                    Severity = restartRate > 3 ? AnomalySeverity.Critical : AnomalySeverity.Warning,
                    Value = restartRate.Value,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to check container restarts");
        }

        return anomalies;
    }

    private async Task<List<Anomaly>> CheckRouterHealthAsync(CancellationToken ct)
    {
        var anomalies = new List<Anomaly>();
        try
        {
            var cpuLoad = await _prometheus.QueryScalarAsync("mktxp_system_cpu_load", ct);
            if (cpuLoad is > 80)
            {
                anomalies.Add(new Anomaly
                {
                    Type = "Router",
                    Message = $"Router CPU load at {cpuLoad:F0}%",
                    Severity = cpuLoad > 95 ? AnomalySeverity.Critical : AnomalySeverity.Warning,
                    Value = cpuLoad.Value,
                });
            }

            var temp = await _prometheus.QueryScalarAsync("mktxp_system_cpu_temperature", ct);
            if (temp is > 75)
            {
                anomalies.Add(new Anomaly
                {
                    Type = "Router",
                    Message = $"Router CPU temperature at {temp:F1}\u00b0C",
                    Severity = temp > 85 ? AnomalySeverity.Critical : AnomalySeverity.Warning,
                    Value = temp.Value,
                });
            }

            var memTotal = await _prometheus.QueryScalarAsync("mktxp_system_total_memory", ct);
            var memFree = await _prometheus.QueryScalarAsync("mktxp_system_free_memory", ct);
            if (memTotal is > 0)
            {
                var memUsedPct = ((memTotal.Value - (memFree ?? 0)) / memTotal.Value) * 100;
                if (memUsedPct > 90)
                {
                    anomalies.Add(new Anomaly
                    {
                        Type = "Router",
                        Message = $"Router memory at {memUsedPct:F1}%",
                        Severity = AnomalySeverity.Warning,
                        Value = memUsedPct,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to check router health");
        }

        return anomalies;
    }

    private async Task<List<Anomaly>> CheckNetworkTrafficAsync(CancellationToken ct)
    {
        var anomalies = new List<Anomaly>();
        try
        {
            var rxRate = await _prometheus.QueryScalarAsync(
                "sum(rate(mktxp_interface_rx_byte[5m]))", ct);
            if (rxRate == null)
            {
                return anomalies;
            }

            var key = "network_rx";
            var previous = _lastMetricValues.GetValueOrDefault(key, rxRate.Value);
            _lastMetricValues[key] = rxRate.Value;

            if (previous > 0 && rxRate > previous * 3)
            {
                anomalies.Add(new Anomaly
                {
                    Type = "Network",
                    Message = $"Network RX spike: {FormattingHelpers.FormatBytes(previous)}/s -> {FormattingHelpers.FormatBytes(rxRate.Value)}/s",
                    Severity = AnomalySeverity.Warning,
                    Value = rxRate.Value,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to check network traffic");
        }

        return anomalies;
    }

    private async Task<List<Anomaly>> CheckStoragePoolHealthAsync(CancellationToken ct)
    {
        var anomalies = new List<Anomaly>();
        try
        {
            var pools = await _truenasPlugin.GetPoolInfoAsync(ct);

            foreach (var pool in pools)
            {
                if (pool.Status != "ONLINE" || !pool.Healthy)
                {
                    anomalies.Add(new Anomaly
                    {
                        Type = "Storage",
                        Message = $"Pool '{pool.Name}' status: {pool.Status} (healthy: {pool.Healthy})",
                        Severity = pool.Status == "DEGRADED" ? AnomalySeverity.Warning : AnomalySeverity.Critical,
                        Value = 0,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to check storage pools");
        }

        return anomalies;
    }

    private async Task<List<Anomaly>> CheckTraefik5xxAsync(CancellationToken ct)
    {
        var anomalies = new List<Anomaly>();
        try
        {
            var errorRate = await _prometheus.QueryScalarAsync(
                "sum(rate(traefik_service_requests_total{code=~\"5..\"}[5m])) / sum(rate(traefik_service_requests_total[5m])) * 100", ct);

            if (errorRate is > 5)
            {
                anomalies.Add(new Anomaly
                {
                    Type = "Traefik",
                    Message = $"HTTP 5xx error rate at {errorRate:F1}%",
                    Severity = errorRate > 20 ? AnomalySeverity.Critical : AnomalySeverity.Warning,
                    Value = errorRate.Value,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to check Traefik 5xx rate");
        }

        return anomalies;
    }

    private async Task<List<Anomaly>> CheckCertExpiryAsync(CancellationToken ct)
    {
        var anomalies = new List<Anomaly>();
        try
        {
            var minExpiry = await _prometheus.QueryScalarAsync(
                "min(traefik_tls_certs_not_after - time())", ct);

            if (minExpiry == null)
            {
                return anomalies;
            }

            var daysLeft = minExpiry.Value / 86400;
            if (daysLeft < 7)
            {
                anomalies.Add(new Anomaly
                {
                    Type = "Certificate",
                    Message = $"TLS certificate expiring in {daysLeft:F0} days",
                    Severity = daysLeft < 1 ? AnomalySeverity.Critical : AnomalySeverity.Warning,
                    Value = daysLeft,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to check cert expiry");
        }

        return anomalies;
    }

    private async Task<List<Anomaly>> CheckPrometheusCardinalityAsync(CancellationToken ct)
    {
        var anomalies = new List<Anomaly>();
        try
        {
            var headSeries = await _prometheus.QueryScalarAsync("prometheus_tsdb_head_series", ct);
            if (headSeries is > 500_000)
            {
                anomalies.Add(new Anomaly
                {
                    Type = "Monitoring",
                    Message = $"Prometheus cardinality high: {headSeries:N0} series",
                    Severity = headSeries > 1_000_000 ? AnomalySeverity.Critical : AnomalySeverity.Warning,
                    Value = headSeries.Value,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to check Prometheus cardinality");
        }

        return anomalies;
    }

    private async Task<List<Anomaly>> CheckLokiHealthAsync(CancellationToken ct)
    {
        var anomalies = new List<Anomaly>();
        try
        {
            var response = await _httpClient.GetAsync($"{_lokiUrl}/ready", ct);
            if (!response.IsSuccessStatusCode)
            {
                anomalies.Add(new Anomaly
                {
                    Type = "Logging",
                    Message = $"Loki not ready (HTTP {(int)response.StatusCode})",
                    Severity = AnomalySeverity.Warning,
                    Value = (int)response.StatusCode,
                });
            }
        }
        catch (Exception ex)
        {
            anomalies.Add(new Anomaly
            {
                Type = "Logging",
                Message = $"Loki unreachable: {ex.Message}",
                Severity = AnomalySeverity.Warning,
                Value = 0,
            });
        }

        return anomalies;
    }

    private async Task NotifyAnomaliesAsync(List<Anomaly> anomalies, CancellationToken ct)
    {
        var anomalySummary = new StringBuilder();
        foreach (var a in anomalies)
        {
            var emoji = a.Severity == AnomalySeverity.Critical ? "🔴" : "🟡";
            anomalySummary.AppendLine($"{emoji} [{a.Type}] {a.Message}");
        }

        var hasCritical = anomalies.Any(a => a.Severity == AnomalySeverity.Critical);
        var issueType = hasCritical ? "critical_anomaly" : "anomaly_detection";
        var summary = hasCritical
            ? $"{anomalies.Count(a => a.Severity == AnomalySeverity.Critical)} critical anomalies detected"
            : $"{anomalies.Count} anomalies detected by heuristic monitoring";

        await _smartNotification.EvaluateAndNotifyAsync(
            new NotificationCandidate
            {
                Source = "anomaly_detection",
                Summary = summary,
                RawData = anomalySummary.ToString(),
                IssueType = issueType,
                NeverSuppress = hasCritical,
            },
            ct);

        // Record anomaly event (without LLM analysis — that's now done by SmartNotificationService)
        await RecordAnomalyEventAsync(anomalies, "Routed to smart notification service", ct);
    }

    private async Task RecordAnomalyEventAsync(List<Anomaly> anomalies, string analysis, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            db.AnomalyEvents.Add(new AnomalyEvent
            {
                Summary = string.Join("; ", anomalies.Select(a => $"[{a.Type}] {a.Message}")),
                Analysis = analysis.Length > 1000 ? analysis[..997] + "..." : analysis,
                Severity = anomalies.Max(a => a.Severity).ToString(),
                AnomalyCount = anomalies.Count,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record anomaly event");
        }
    }

#pragma warning disable SA1201
    private enum AnomalySeverity
    {
        Warning,
        Critical,
    }

    private sealed class Anomaly
    {
        public required string Type { get; init; }

        public required string Message { get; init; }

        public required AnomalySeverity Severity { get; init; }

        public required double Value { get; init; }
    }

    private async Task LoadBaselineAsync()
    {
        try
        {
            var json = await _stateStore.GetAsync("AnomalyDetection", "lastMetricValues");
            if (json != null)
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, double>>(json);
                if (data != null)
                {
                    foreach (var (key, value) in data)
                        _lastMetricValues[key] = value;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load anomaly detection baselines");
        }
    }

    private async Task PersistBaselineAsync()
    {
        try
        {
            await _stateStore.SetAsync("AnomalyDetection", "lastMetricValues",
                JsonSerializer.Serialize(_lastMetricValues));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist anomaly detection baselines");
        }
    }
#pragma warning restore SA1201
}
