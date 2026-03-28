using HomelabBot.Configuration;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public sealed class HealthScoreBackgroundService : BackgroundService
{
    private readonly IOptionsMonitor<HealthScoreConfiguration> _config;
    private readonly SummaryDataAggregator _aggregator;
    private readonly HealthScoreService _healthScoreService;
    private readonly DiscordBotService _discordBot;
    private readonly ILogger<HealthScoreBackgroundService> _logger;
    private int? _lastNotifiedScore;

    public HealthScoreBackgroundService(
        IOptionsMonitor<HealthScoreConfiguration> config,
        SummaryDataAggregator aggregator,
        HealthScoreService healthScoreService,
        DiscordBotService discordBot,
        ILogger<HealthScoreBackgroundService> logger)
    {
        _config = config;
        _aggregator = aggregator;
        _healthScoreService = healthScoreService;
        _discordBot = discordBot;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Health score background service started, waiting for Discord...");
        await _discordBot.WaitForReadyAsync(stoppingToken);
        _logger.LogInformation("Discord ready, health score tracking running");

        await InitializeNotificationStateAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_config.CurrentValue.Enabled)
                {
                    _logger.LogDebug("Health score tracking disabled, rechecking in 1 minute");
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }

                var intervalMinutes = Math.Max(1, _config.CurrentValue.IntervalMinutes);
                await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);

                await RecordAndCheckScoreAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in health score background service");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }

    private async Task RecordAndCheckScoreAsync(CancellationToken ct)
    {
        var data = await _aggregator.AggregateAsync(ct);
        var result = _healthScoreService.CalculateScore(data);

        // Query previous score BEFORE recording the new one
        var threshold = _config.CurrentValue.AlertDropThreshold;
        var previousScore = await _healthScoreService.GetScoreAtWindowStartAsync(TimeSpan.FromHours(1), ct);

        await _healthScoreService.RecordScoreAsync(result, ct);
        _logger.LogDebug("Recorded health score: {Score}/100", result.Score);

        await _healthScoreService.PruneOldRecordsAsync(TimeSpan.FromDays(30), ct);

        // Only notify once per drop — skip if we already notified at this score or lower
        if (previousScore.HasValue && previousScore.Value - result.Score >= threshold
            && (_lastNotifiedScore == null || result.Score < _lastNotifiedScore))
        {
            var userId = HomelabOwner.DiscordUserId;
            if (userId == 0)
            {
                return;
            }

            var message = $"⚠️ **Health Score Drop Detected**\n" +
                          $"Score went from **{previousScore.Value}** → **{result.Score}** in the last hour\n" +
                          BuildBreakdown(result);

            try
            {
                await _discordBot.SendDmAsync(userId, message);
                _lastNotifiedScore = result.Score;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send health score drop DM");
            }

            _logger.LogWarning("Health score dropped {Points} points: {Old} → {New}",
                previousScore.Value - result.Score, previousScore.Value, result.Score);
        }
        else if (result.Score == 100)
        {
            // Reset notification state when fully recovered
            _lastNotifiedScore = null;
        }
    }

    private async Task InitializeNotificationStateAsync(CancellationToken ct)
    {
        try
        {
            var threshold = _config.CurrentValue.AlertDropThreshold;
            var baseline = await _healthScoreService.GetScoreAtWindowStartAsync(TimeSpan.FromHours(1), ct);
            var latest = await _healthScoreService.GetLatestScoreAsync(ct);

            if (baseline.HasValue && latest.HasValue && baseline.Value - latest.Value >= threshold)
            {
                _lastNotifiedScore = latest.Value;
                _logger.LogInformation(
                    "Health score in drop state on startup ({Baseline} → {Latest}), suppressing duplicate notification",
                    baseline.Value, latest.Value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize notification state, starting fresh");
        }
    }

    private static string BuildBreakdown(Models.HealthScoreResult result)
    {
        var parts = new List<string>();
        if (result.AlertDeductions > 0)
        {
            parts.Add($"Alerts: -{result.AlertDeductions}");
        }

        if (result.ContainerDeductions > 0)
        {
            parts.Add($"Containers: -{result.ContainerDeductions}");
        }

        if (result.PoolDeductions > 0)
        {
            parts.Add($"Pools: -{result.PoolDeductions}");
        }

        if (result.MonitoringDeductions > 0)
        {
            parts.Add($"Monitoring: -{result.MonitoringDeductions}");
        }

        if (result.ConnectivityDeductions > 0)
        {
            parts.Add($"Connectivity: -{result.ConnectivityDeductions}");
        }

        return parts.Count > 0 ? $"Deductions: {string.Join(", ", parts)}" : "No deductions";
    }
}
