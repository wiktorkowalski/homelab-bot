using HomelabBot.Configuration;
using HomelabBot.Models;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public sealed class DailySummaryService : ScheduledBackgroundService
{
    private readonly IOptionsMonitor<DailySummaryConfiguration> _config;
    private readonly KernelService _kernelService;
    private readonly SmartNotificationService _smartNotification;
    private readonly ILogger<DailySummaryService> _logger;

    public DailySummaryService(
        IOptionsMonitor<DailySummaryConfiguration> config,
        KernelService kernelService,
        SmartNotificationService smartNotification,
        DiscordBotService discordBot,
        ILogger<DailySummaryService> logger)
        : base(discordBot)
    {
        _config = config;
        _kernelService = kernelService;
        _smartNotification = smartNotification;
        _logger = logger;
    }

    protected override bool IsEnabled => _config.CurrentValue.Enabled;

    protected override ILogger Logger => _logger;

    protected override TimeSpan GetDelay() =>
        ScheduleHelper.CalculateDelayUntilNextRun(_config.CurrentValue.ScheduleTime, _config.CurrentValue.TimeZone);

    protected override async Task RunIterationAsync(CancellationToken ct)
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
