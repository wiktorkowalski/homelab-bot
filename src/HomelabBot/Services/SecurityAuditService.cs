using HomelabBot.Configuration;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public sealed class SecurityAuditService : ScheduledBackgroundService
{
    private readonly IOptionsMonitor<SecurityAuditConfiguration> _config;
    private readonly KernelService _kernelService;
    private readonly ILogger<SecurityAuditService> _logger;

    public SecurityAuditService(
        IOptionsMonitor<SecurityAuditConfiguration> config,
        KernelService kernelService,
        DiscordBotService discordBot,
        ILogger<SecurityAuditService> logger)
        : base(discordBot)
    {
        _config = config;
        _kernelService = kernelService;
        _logger = logger;
    }

    protected override bool IsEnabled => _config.CurrentValue.Enabled;

    protected override ILogger Logger => _logger;

    protected override TimeSpan GetDelay() =>
        ScheduleHelper.CalculateDelayUntilNextRun(_config.CurrentValue.ScheduleTime, _config.CurrentValue.TimeZone, _config.CurrentValue.ScheduleDay, _logger);

    protected override Task RunIterationAsync(CancellationToken ct) => RunAuditAndDeliverAsync(ct);

    public async Task<string> RunAuditAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Running security audit");
        var report = await _kernelService.ProcessOneShotAsync(
            userMessage: SecurityAuditPrompts.Investigation,
            traceType: TraceType.Scheduled,
            systemPromptOverride: SecurityAuditPrompts.System,
            ct: ct);

        _logger.LogInformation("Security audit completed, report length: {Length}", report.Length);
        return report;
    }

    private async Task RunAuditAndDeliverAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting scheduled security audit");

        var userId = HomelabOwner.DiscordUserId;
        if (userId == 0)
        {
            _logger.LogWarning("DiscordUserId not configured, cannot send security audit");
            return;
        }

        try
        {
            var report = await RunAuditAsync(ct);

            await DiscordBot.SendDmSplitAsync(userId, report);

            _logger.LogInformation("Security audit delivered to user {UserId}", userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate security audit");
        }
    }
}
