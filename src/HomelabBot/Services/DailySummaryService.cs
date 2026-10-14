using HomelabBot.Configuration;
using HomelabBot.Models;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public sealed class DailySummaryService : ScheduledBackgroundService
{
    private const string IssueType = "daily_healthcheck";

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
        ScheduleHelper.CalculateDelayUntilNextRun(_config.CurrentValue.ScheduleTime, _config.CurrentValue.TimeZone, logger: _logger);

    protected override Task RunIterationAsync(CancellationToken ct) => RunHealthcheckAsync(ct);

    internal async Task RunHealthcheckAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting daily healthcheck cycle");

        // Start a new notification cycle (clears the old daily context). Runs even when the
        // healthcheck is suppressed: the daily thread must still rotate.
        _smartNotification.StartNewCycle();

        // The healthcheck costs ~45-100k tokens; a suppressed result would be dropped anyway.
        if (await _smartNotification.IsSuppressedAsync(IssueType))
        {
            _logger.LogInformation("Issue type '{IssueType}' is suppressed, skipping the daily healthcheck run", IssueType);
            return;
        }

        // Run the full healthcheck investigation and route through smart notification
        try
        {
            var threadId = _smartNotification.CurrentDailyThreadId;

            var report = await _kernelService.ProcessMessageAsync(
                threadId: threadId,
                userMessage: HealthcheckPrompts.Investigation,
                traceType: TraceType.Scheduled,
                systemPromptOverride: HealthcheckPrompts.System,
                ct: ct);

            // Route through smart notification — it will decide whether to notify
            await _smartNotification.EvaluateAndNotifyAsync(
                new NotificationCandidate
                {
                    Source = "daily_healthcheck",
                    Summary = "Daily healthcheck completed. Review the findings below.",
                    RawData = report,
                    IssueType = IssueType,
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
