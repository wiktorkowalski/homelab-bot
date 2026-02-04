using HomelabBot.Configuration;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public sealed class DailySummaryService : BackgroundService
{
    private readonly IOptionsMonitor<DailySummaryConfiguration> _config;
    private readonly SummaryDataAggregator _aggregator;
    private readonly DiscordBotService _discordBot;
    private readonly ILogger<DailySummaryService> _logger;

    public DailySummaryService(
        IOptionsMonitor<DailySummaryConfiguration> config,
        SummaryDataAggregator aggregator,
        DiscordBotService discordBot,
        ILogger<DailySummaryService> logger)
    {
        _config = config;
        _aggregator = aggregator;
        _discordBot = discordBot;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.CurrentValue.Enabled)
        {
            _logger.LogInformation("Daily summary is disabled");
            return;
        }

        _logger.LogInformation("Daily summary service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delay = CalculateDelayUntilNextRun();
                _logger.LogInformation("Next daily summary in {Delay}", delay);

                await Task.Delay(delay, stoppingToken);

                if (!_config.CurrentValue.Enabled)
                {
                    _logger.LogInformation("Daily summary disabled, skipping");
                    continue;
                }

                await GenerateAndDeliverSummaryAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in daily summary service");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }

    private TimeSpan CalculateDelayUntilNextRun()
    {
        var config = _config.CurrentValue;

        if (!TimeOnly.TryParse(config.ScheduleTime, out var scheduleTime))
        {
            _logger.LogWarning("Invalid ScheduleTime '{Time}', defaulting to 08:00", config.ScheduleTime);
            scheduleTime = new TimeOnly(8, 0);
        }

        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(config.TimeZone);
        }
        catch
        {
            _logger.LogWarning("Invalid TimeZone '{TZ}', defaulting to UTC", config.TimeZone);
            tz = TimeZoneInfo.Utc;
        }

        var nowUtc = DateTime.UtcNow;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var todaySchedule = nowLocal.Date + scheduleTime.ToTimeSpan();

        var nextRun = nowLocal < todaySchedule ? todaySchedule : todaySchedule.AddDays(1);
        var nextRunUtc = TimeZoneInfo.ConvertTimeToUtc(nextRun, tz);

        return nextRunUtc - nowUtc;
    }

    private async Task GenerateAndDeliverSummaryAsync(CancellationToken ct)
    {
        _logger.LogInformation("Generating daily summary");

        var data = await _aggregator.AggregateAsync(ct);
        var embed = SummaryEmbedBuilder.Build(data);

        var userId = _config.CurrentValue.DiscordUserId;
        if (userId == 0)
        {
            _logger.LogWarning("DiscordUserId not configured, cannot send DM");
            return;
        }

        await _discordBot.SendDmAsync(userId, embed);
        _logger.LogInformation("Daily summary delivered to user {UserId}", userId);
    }
}
