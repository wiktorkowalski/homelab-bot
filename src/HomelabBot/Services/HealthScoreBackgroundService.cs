using HomelabBot.Configuration;
using HomelabBot.Models;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public sealed class HealthScoreBackgroundService : ScheduledBackgroundService
{
    private readonly IOptionsMonitor<HealthScoreConfiguration> _config;
    private readonly SummaryDataAggregator _aggregator;
    private readonly HealthScoreService _healthScoreService;
    private readonly SmartNotificationService _smartNotification;
    private readonly ILogger<HealthScoreBackgroundService> _logger;
    private int? _lastNotifiedScore;

    public HealthScoreBackgroundService(
        IOptionsMonitor<HealthScoreConfiguration> config,
        SummaryDataAggregator aggregator,
        HealthScoreService healthScoreService,
        SmartNotificationService smartNotification,
        DiscordBotService discordBot,
        ILogger<HealthScoreBackgroundService> logger)
        : base(discordBot)
    {
        _config = config;
        _aggregator = aggregator;
        _healthScoreService = healthScoreService;
        _smartNotification = smartNotification;
        _logger = logger;
    }

    protected override bool IsEnabled => _config.CurrentValue.Enabled;

    protected override ILogger Logger => _logger;

    protected override TimeSpan GetDelay() =>
        TimeSpan.FromMinutes(Math.Max(1, _config.CurrentValue.IntervalMinutes));

    protected override Task OnStartedAsync(CancellationToken ct) => InitializeNotificationStateAsync(ct);

    protected override Task RunIterationAsync(CancellationToken ct) => RecordAndCheckScoreAsync(ct);

    private async Task RecordAndCheckScoreAsync(CancellationToken ct)
    {
        var data = await _aggregator.AggregateAsync(ct);
        var result = _healthScoreService.CalculateScore(data);

        // Query previous score BEFORE recording the new one
        var threshold = _config.CurrentValue.AlertDropThreshold;
        var previousScore = await _healthScoreService.GetScoreAtWindowStartAsync(TimeSpan.FromHours(1), ct);

        await _healthScoreService.RecordScoreAsync(result, ct);

        await _healthScoreService.PruneOldRecordsAsync(TimeSpan.FromDays(30), ct);

        // Only notify once per drop — skip if we already notified at this score or lower
        if (previousScore.HasValue && previousScore.Value - result.Score >= threshold
            && (_lastNotifiedScore == null || result.Score < _lastNotifiedScore))
        {
            var rawData = $"Score: {previousScore.Value} → {result.Score} in the last hour\n{BuildBreakdown(result)}";

            var notified = await _smartNotification.EvaluateAndNotifyAsync(
                new NotificationCandidate
                {
                    Source = "health_score",
                    Summary = $"Health score dropped {previousScore.Value - result.Score} points ({previousScore.Value} → {result.Score})",
                    RawData = rawData,
                    IssueType = "health_score_drop",
                },
                ct);

            if (notified)
            {
                _lastNotifiedScore = result.Score;
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
