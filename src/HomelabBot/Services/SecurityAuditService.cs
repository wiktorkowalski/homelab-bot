using HomelabBot.Configuration;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public sealed class SecurityAuditService : ScheduledBackgroundService
{
    private readonly IOptionsMonitor<SecurityAuditConfiguration> _config;
    private readonly KernelService _kernelService;
    private readonly ConversationService _conversationService;
    private readonly ILogger<SecurityAuditService> _logger;

    public SecurityAuditService(
        IOptionsMonitor<SecurityAuditConfiguration> config,
        KernelService kernelService,
        ConversationService conversationService,
        DiscordBotService discordBot,
        ILogger<SecurityAuditService> logger)
        : base(discordBot)
    {
        _config = config;
        _kernelService = kernelService;
        _conversationService = conversationService;
        _logger = logger;
    }

    protected override bool IsEnabled => _config.CurrentValue.Enabled;

    protected override ILogger Logger => _logger;

    protected override TimeSpan GetDelay() =>
        ScheduleHelper.CalculateDelayUntilNextRun(_config.CurrentValue.ScheduleTime, _config.CurrentValue.TimeZone, _config.CurrentValue.ScheduleDay);

    protected override Task RunIterationAsync(CancellationToken ct) => RunAuditAndDeliverAsync(ct);

    public async Task<string> RunAuditAsync(CancellationToken ct = default)
    {
        var threadId = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        try
        {
            var report = await _kernelService.ProcessMessageAsync(
                threadId: threadId,
                userMessage: SecurityAuditPrompts.Investigation,
                traceType: TraceType.Scheduled,
                maxTokens: SecurityAuditPrompts.MaxTokens,
                systemPromptOverride: SecurityAuditPrompts.System,
                ct: ct);

            return report;
        }
        finally
        {
            _conversationService.ClearHistory(threadId);
        }
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
