using System.Text.Json;
using HomelabBot.Configuration;
using HomelabBot.Models;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public sealed class DailySummaryService : BackgroundService
{
    private readonly IOptionsMonitor<DailySummaryConfiguration> _config;
    private readonly SummaryDataAggregator _aggregator;
    private readonly KernelService _kernelService;
    private readonly DiscordBotService _discordBot;
    private readonly ILogger<DailySummaryService> _logger;

    public DailySummaryService(
        IOptionsMonitor<DailySummaryConfiguration> config,
        SummaryDataAggregator aggregator,
        KernelService kernelService,
        DiscordBotService discordBot,
        ILogger<DailySummaryService> logger)
    {
        _config = config;
        _aggregator = aggregator;
        _kernelService = kernelService;
        _discordBot = discordBot;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Daily summary service started, waiting for Discord...");
        await _discordBot.WaitForReadyAsync(stoppingToken);
        _logger.LogInformation("Discord ready, scheduling daily summaries");

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
        var analysis = await GenerateAnalysisAsync(data);
        var embed = SummaryEmbedBuilder.Build(data, analysis);

        var userId = _config.CurrentValue.DiscordUserId;
        if (userId == 0)
        {
            _logger.LogWarning("DiscordUserId not configured, cannot send DM");
            return;
        }

        await _discordBot.SendDmAsync(userId, embed);
        _logger.LogInformation("Daily summary delivered to user {UserId}", userId);
    }

    private async Task<string?> GenerateAnalysisAsync(DailySummaryData data)
    {
        try
        {
            var prompt = $"""
                Analyze this homelab status and provide a brief 2-3 sentence summary.
                Focus on anything noteworthy: issues, warnings, recommendations.
                If everything looks good, say so briefly.
                Be concise and direct. No greetings or fluff.

                Data:
                - Health Score: {data.HealthScore}/100
                - Alerts (last 24h): {data.Alerts.Count} ({data.Alerts.Count(a => a.Severity == "critical")} critical, {data.Alerts.Count(a => a.Severity == "warning")} warning)
                {(data.Alerts.Count > 0 ? "  Names: " + string.Join(", ", data.Alerts.Take(5).Select(a => a.Name)) : "")}
                - Containers: {data.Containers.Count(c => c.State == "running")} running, {data.Containers.Count(c => c.State != "running")} stopped
                {(data.Containers.Any(c => c.State != "running") ? "  Stopped: " + string.Join(", ", data.Containers.Where(c => c.State != "running").Take(5).Select(c => c.Name)) : "")}
                - Storage Pools: {string.Join(", ", data.Pools.Select(p => $"{p.Name}: {p.Health} ({p.UsedPercent:F0}%)"))}
                - Router: {(data.Router != null ? $"CPU {data.Router.CpuPercent:F0}%, Mem {data.Router.MemoryPercent:F0}%, Up {data.Router.Uptime.Days}d" : "unavailable")}
                - Monitoring: {(data.Monitoring != null ? $"{data.Monitoring.UpTargets}/{data.Monitoring.TotalTargets} targets up" : "unavailable")}
                """;

            var response = await _kernelService.ProcessMessageAsync(
                threadId: 0,
                userMessage: prompt);

            return response.Length > 500 ? response[..500] + "..." : response;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate AI analysis");
            return null;
        }
    }
}
