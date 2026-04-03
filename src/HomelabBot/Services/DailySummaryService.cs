using HomelabBot.Configuration;
using HomelabBot.Models;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public sealed class DailySummaryService : BackgroundService
{
    private readonly IOptionsMonitor<DailySummaryConfiguration> _config;
    private readonly KernelService _kernelService;
    private readonly SmartNotificationService _smartNotification;
    private readonly DiscordBotService _discordBot;
    private readonly ILogger<DailySummaryService> _logger;

    public DailySummaryService(
        IOptionsMonitor<DailySummaryConfiguration> config,
        KernelService kernelService,
        SmartNotificationService smartNotification,
        DiscordBotService discordBot,
        ILogger<DailySummaryService> logger)
    {
        _config = config;
        _kernelService = kernelService;
        _smartNotification = smartNotification;
        _discordBot = discordBot;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Daily healthcheck service started, waiting for Discord...");
        await _discordBot.WaitForReadyAsync(stoppingToken);
        _logger.LogInformation("Discord ready, daily healthcheck service running");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_config.CurrentValue.Enabled)
                {
                    _logger.LogInformation("Daily healthcheck disabled, rechecking in 1 minute");
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }

                var delay = CalculateDelayUntilNextRun();
                _logger.LogInformation("Next daily healthcheck in {Delay}", delay);

                await Task.Delay(delay, stoppingToken);

                await RunHealthcheckCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in daily healthcheck service");
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
        if (tz.IsInvalidTime(nextRun))
        {
            nextRun = nextRun.AddHours(1);
        }

        var nextRunUtc = TimeZoneInfo.ConvertTimeToUtc(nextRun, tz);

        return nextRunUtc - nowUtc;
    }

    private async Task RunHealthcheckCycleAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting daily healthcheck cycle");

        // Start a new notification cycle (learns from previous day, clears old context)
        await _smartNotification.StartNewCycleAsync(ct);

        // Run the full healthcheck investigation and route through smart notification
        try
        {
            var threadId = _smartNotification.CurrentDailyThreadId;

            var report = await _kernelService.ProcessMessageAsync(
                threadId: threadId,
                userMessage: HealthcheckPrompts.Investigation,
                traceType: TraceType.Scheduled,
                maxTokens: HealthcheckPrompts.MaxTokens,
                systemPromptOverride: HealthcheckPrompts.System,
                ct: ct);

            // Route through smart notification — it will decide whether to notify
            await _smartNotification.EvaluateAndNotifyAsync(
                new NotificationCandidate
                {
                    Source = "daily_healthcheck",
                    Summary = "Daily healthcheck completed. Review the findings below.",
                    RawData = report,
                    IssueType = "daily_healthcheck",
                    AlreadyInvestigated = true,
                },
                ct);

            _logger.LogInformation("Daily healthcheck cycle completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run daily healthcheck");
        }
    }
}
