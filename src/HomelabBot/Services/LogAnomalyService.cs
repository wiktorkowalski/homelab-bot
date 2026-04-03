using System.Text.Json;
using HomelabBot.Configuration;
using HomelabBot.Models;
using HomelabBot.Plugins;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public sealed class LogAnomalyService : BackgroundService
{
    private readonly IOptionsMonitor<LogAnomalyConfiguration> _config;
    private readonly LokiPlugin _lokiPlugin;
    private readonly SmartNotificationService _smartNotification;
    private readonly DiscordBotService _discordBot;
    private readonly ILogger<LogAnomalyService> _logger;
    private readonly ServiceStateStore _stateStore;
    private readonly Dictionary<string, long> _lastKnownErrorCounts = new();
    private bool _firstRun = true;

    public LogAnomalyService(
        IOptionsMonitor<LogAnomalyConfiguration> config,
        LokiPlugin lokiPlugin,
        SmartNotificationService smartNotification,
        DiscordBotService discordBot,
        ILogger<LogAnomalyService> logger,
        ServiceStateStore stateStore)
    {
        _config = config;
        _lokiPlugin = lokiPlugin;
        _smartNotification = smartNotification;
        _discordBot = discordBot;
        _logger = logger;
        _stateStore = stateStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Log anomaly service started, waiting for Discord...");
        await _discordBot.WaitForReadyAsync(stoppingToken);
        _logger.LogInformation("Discord ready, log anomaly detection running");
        await LoadBaselineAsync();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_config.CurrentValue.Enabled)
                {
                    _logger.LogDebug("Log anomaly detection disabled, rechecking in 1 minute");
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }

                var intervalMinutes = Math.Max(1, _config.CurrentValue.IntervalMinutes);
                await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);

                await ScanForAnomaliesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in log anomaly service");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }

    private async Task ScanForAnomaliesAsync(CancellationToken ct)
    {
        var isFirstRun = _firstRun;
        _firstRun = false;

        var errorCounts = await _lokiPlugin.GetErrorCountsByContainerAsync("1h");
        var criticalPatterns = await _lokiPlugin.DetectCriticalPatterns("1h");

        var hasCritical = !criticalPatterns.Contains("No critical patterns");
        var newSpikes = DetectErrorSpikes(errorCounts);
        await PersistBaselineAsync();

        if (!hasCritical && newSpikes.Count == 0)
        {
            _logger.LogDebug("No log anomalies detected");
            return;
        }

        // Skip notifications on first run (baseline)
        if (isFirstRun)
        {
            _logger.LogInformation("First anomaly scan complete, baseline established");
            return;
        }

        var sb = new System.Text.StringBuilder();
        var summaryParts = new List<string>();

        if (hasCritical)
        {
            summaryParts.Add("critical log patterns (fatal/panic/OOM/segfault)");
            sb.AppendLine("Critical patterns:");
            sb.AppendLine(TruncateForDiscord(criticalPatterns));
        }

        if (newSpikes.Count > 0)
        {
            summaryParts.Add($"{newSpikes.Count} error rate spike(s)");
            sb.AppendLine("Error rate spikes:");
            foreach (var s in newSpikes)
            {
                sb.AppendLine($"• {s.Container}: {s.PreviousCount} → {s.CurrentCount} errors/h");
            }
        }

        await _smartNotification.EvaluateAndNotifyAsync(
            new NotificationCandidate
            {
                Source = "log_anomaly",
                Summary = $"Log anomalies detected: {string.Join(", ", summaryParts)}",
                RawData = sb.ToString(),
                IssueType = hasCritical ? "critical_log_patterns" : "error_rate_spike",
                NeverSuppress = hasCritical,
            },
            ct);
    }

    internal List<ErrorSpike> DetectErrorSpikes(Dictionary<string, long> errorCounts)
    {
        var spikes = new List<ErrorSpike>();
        var threshold = _config.CurrentValue.ErrorThreshold;

        foreach (var (container, count) in errorCounts)
        {
            var previousCount = _lastKnownErrorCounts.GetValueOrDefault(container, 0);
            _lastKnownErrorCounts[container] = count;

            if (count >= threshold && count > previousCount * 2 && previousCount > 0)
            {
                spikes.Add(new ErrorSpike { Container = container, PreviousCount = previousCount, CurrentCount = count });
            }
        }

        return spikes;
    }

    private static string TruncateForDiscord(string text)
    {
        return text.Length > 1800 ? text[..1797] + "..." : text;
    }

    private async Task LoadBaselineAsync()
    {
        try
        {
            var json = await _stateStore.GetAsync("LogAnomaly", "lastKnownErrorCounts");
            if (json != null)
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, long>>(json);
                if (data != null)
                {
                    foreach (var (key, value) in data)
                        _lastKnownErrorCounts[key] = value;
                    _firstRun = false;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load log anomaly baselines");
        }
    }

    private async Task PersistBaselineAsync()
    {
        try
        {
            await _stateStore.SetAsync("LogAnomaly", "lastKnownErrorCounts",
                JsonSerializer.Serialize(_lastKnownErrorCounts));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist log anomaly baselines");
        }
    }

    internal sealed class ErrorSpike
    {
        public required string Container { get; init; }

        public required long PreviousCount { get; init; }

        public required long CurrentCount { get; init; }
    }
}
