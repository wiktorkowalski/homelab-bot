using HomelabBot.Configuration;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public sealed class SecurityAuditService : BackgroundService
{
    private readonly IOptionsMonitor<SecurityAuditConfiguration> _config;
    private readonly KernelService _kernelService;
    private readonly ConversationService _conversationService;
    private readonly DiscordBotService _discordBot;
    private readonly ILogger<SecurityAuditService> _logger;

    public SecurityAuditService(
        IOptionsMonitor<SecurityAuditConfiguration> config,
        KernelService kernelService,
        ConversationService conversationService,
        DiscordBotService discordBot,
        ILogger<SecurityAuditService> logger)
    {
        _config = config;
        _kernelService = kernelService;
        _conversationService = conversationService;
        _discordBot = discordBot;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Security audit service started, waiting for Discord...");
        await _discordBot.WaitForReadyAsync(stoppingToken);
        _logger.LogInformation("Discord ready, security audit service running");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_config.CurrentValue.Enabled)
                {
                    _logger.LogDebug("Security audit disabled, rechecking in 1 minute");
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }

                var delay = ScheduleHelper.CalculateDelayUntilNextRun(_config.CurrentValue.ScheduleTime, _config.CurrentValue.TimeZone, _config.CurrentValue.ScheduleDay);
                _logger.LogInformation("Next security audit in {Delay}", delay);

                await Task.Delay(delay, stoppingToken);

                await RunAuditAndDeliverAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in security audit service");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }

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

            await _discordBot.SendDmSplitAsync(userId, report);

            _logger.LogInformation("Security audit delivered to user {UserId}", userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate security audit");
        }
    }
}
