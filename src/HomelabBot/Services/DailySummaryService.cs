using HomelabBot.Configuration;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public sealed class DailySummaryService : BackgroundService
{
    private readonly IOptionsMonitor<DailySummaryConfiguration> _config;
    private readonly KernelService _kernelService;
    private readonly ConversationService _conversationService;
    private readonly DiscordBotService _discordBot;
    private readonly ILogger<DailySummaryService> _logger;

    public DailySummaryService(
        IOptionsMonitor<DailySummaryConfiguration> config,
        KernelService kernelService,
        ConversationService conversationService,
        DiscordBotService discordBot,
        ILogger<DailySummaryService> logger)
    {
        _config = config;
        _kernelService = kernelService;
        _conversationService = conversationService;
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

                await GenerateAndDeliverHealthcheckAsync(stoppingToken);
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
        var nextRunUtc = TimeZoneInfo.ConvertTimeToUtc(nextRun, tz);

        return nextRunUtc - nowUtc;
    }

    private async Task GenerateAndDeliverHealthcheckAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting daily healthcheck investigation");

        var userId = HomelabOwner.DiscordUserId;
        if (userId == 0)
        {
            _logger.LogWarning("DiscordUserId not configured, cannot send DM");
            return;
        }

        // Use a unique thread ID to avoid conversation history buildup
        var threadId = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        try
        {
            var report = await _kernelService.ProcessMessageAsync(
                threadId: threadId,
                userMessage: HealthcheckPrompts.Investigation,
                traceType: TraceType.Scheduled,
                maxTokens: HealthcheckPrompts.MaxTokens,
                systemPromptOverride: HealthcheckPrompts.System,
                ct: ct);

            if (report.Length > 1900)
            {
                var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
                await _discordBot.SendDmFileAsync(userId, report, $"healthcheck-{date}.md");
            }
            else
            {
                await _discordBot.SendDmAsync(userId, report);
            }

            _logger.LogInformation("Daily healthcheck delivered to user {UserId}", userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate daily healthcheck");
        }
        finally
        {
            // Clean up ephemeral conversation history to prevent memory leak
            _conversationService.ClearHistory(threadId);
        }
    }
}
