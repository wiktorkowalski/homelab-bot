using HomelabBot.Configuration;
using HomelabBot.Data.Entities;
using HomelabBot.Models;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public sealed class KnowledgeRefreshService : BackgroundService
{
    private static readonly string[] TopicPrefixes = ["docker:", "storage:", "network:", "monitoring:"];

    private readonly IOptionsMonitor<KnowledgeRefreshConfiguration> _config;
    private readonly SummaryDataAggregator _aggregator;
    private readonly KnowledgeService _knowledge;
    private readonly DiscordBotService _discordBot;
    private readonly ILogger<KnowledgeRefreshService> _logger;

    public KnowledgeRefreshService(
        IOptionsMonitor<KnowledgeRefreshConfiguration> config,
        SummaryDataAggregator aggregator,
        KnowledgeService knowledge,
        DiscordBotService discordBot,
        ILogger<KnowledgeRefreshService> logger)
    {
        _config = config;
        _aggregator = aggregator;
        _knowledge = knowledge;
        _discordBot = discordBot;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Knowledge refresh service started, waiting for Discord...");
        await _discordBot.WaitForReadyAsync(stoppingToken);
        _logger.LogInformation("Discord ready, knowledge refresh service running");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_config.CurrentValue.Enabled)
                {
                    _logger.LogDebug("Knowledge refresh disabled, rechecking in 1 minute");
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }

                var delay = CalculateDelayUntilNextRun();
                _logger.LogInformation("Next knowledge refresh in {Delay}", delay);

                await Task.Delay(delay, stoppingToken);

                await RunRefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in knowledge refresh service");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }

    private TimeSpan CalculateDelayUntilNextRun()
    {
        var config = _config.CurrentValue;

        if (!TimeOnly.TryParse(config.ScheduleTime, out var scheduleTime))
        {
            _logger.LogWarning("Invalid ScheduleTime '{Time}', defaulting to 03:00", config.ScheduleTime);
            scheduleTime = new TimeOnly(3, 0);
        }

        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(config.TimeZone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            _logger.LogWarning(ex, "Invalid TimeZone '{TZ}', defaulting to UTC", config.TimeZone);
            tz = TimeZoneInfo.Utc;
        }

        var nowUtc = DateTime.UtcNow;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var todaySchedule = nowLocal.Date + scheduleTime.ToTimeSpan();

        var nextRun = nowLocal < todaySchedule ? todaySchedule : todaySchedule.AddDays(1);
        var nextRunUtc = TimeZoneInfo.ConvertTimeToUtc(nextRun, tz);

        return nextRunUtc - nowUtc;
    }

    private async Task RunRefreshAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting knowledge refresh cycle");

        var data = await _aggregator.AggregateAsync(ct);

        if (data.Containers.Count == 0 && data.Pools.Count == 0
            && data.Router == null && data.Monitoring == null)
        {
            _logger.LogError("Knowledge refresh: no data sources were reachable, skipping reconciliation");
            return;
        }

        var discovered = TransformToFacts(data);
        var result = await ReconcileAsync(discovered);

        try
        {
            await _knowledge.DecayConfidenceAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Confidence decay failed (reconciliation was successful)");
        }

        _logger.LogInformation(
            "Knowledge refresh complete: {Added} added, {Verified} verified, {Stale} stale, {Errors} errors",
            result.AddedFacts, result.VerifiedFacts, result.StaleFacts, result.Errors.Count);

        if (_config.CurrentValue.NotifyOnChanges && _config.CurrentValue.DiscordUserId != 0
            && (result.AddedFacts > 0 || result.StaleFacts > 0))
        {
            await SendNotificationAsync(result);
        }
    }

    private static List<DiscoveredFact> TransformToFacts(DailySummaryData data)
    {
        var facts = new List<DiscoveredFact>();

        foreach (var c in data.Containers)
        {
            facts.Add(new DiscoveredFact
            {
                Topic = $"docker:{c.Name}",
                Fact = $"Container '{c.Name}' is {c.State}{(c.Health != null ? $" ({c.Health})" : "")}",
                Confidence = 0.9
            });
        }

        foreach (var p in data.Pools)
        {
            facts.Add(new DiscoveredFact
            {
                Topic = $"storage:{p.Name}",
                Fact = $"Pool '{p.Name}' is {p.Health}, {p.UsedPercent:F0}% used",
                Confidence = 0.9
            });
        }

        if (data.Router != null)
        {
            facts.Add(new DiscoveredFact
            {
                Topic = "network:router",
                Fact = $"Router uptime {data.Router.Uptime.Days}d, CPU {data.Router.CpuPercent:F0}%, memory {data.Router.MemoryPercent:F0}%",
                Confidence = 0.9
            });
        }

        if (data.Monitoring != null)
        {
            facts.Add(new DiscoveredFact
            {
                Topic = "monitoring:summary",
                Fact = $"Prometheus: {data.Monitoring.UpTargets}/{data.Monitoring.TotalTargets} targets up, {data.Monitoring.DownTargets} down",
                Confidence = 0.9
            });
        }

        return facts;
    }

    private async Task<RefreshResult> ReconcileAsync(List<DiscoveredFact> discovered)
    {
        var result = new RefreshResult();

        // Load existing facts by topic prefix for reconciliation
        var allExisting = new List<Knowledge>();
        foreach (var prefix in TopicPrefixes)
        {
            try
            {
                allExisting.AddRange(await _knowledge.RecallByTopicPrefixAsync(prefix));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load existing facts for prefix {Prefix}", prefix);
                result.Errors.Add($"load {prefix}: {ex.Message}");
            }
        }

        var existingByTopic = allExisting
            .GroupBy(f => f.Topic)
            .ToDictionary(g => g.Key, g => g.ToList());

        var discoveredTopics = new HashSet<string>();

        foreach (var disc in discovered)
        {
            discoveredTopics.Add(disc.Topic);

            try
            {
                await _knowledge.RememberFactAsync(
                    disc.Topic, disc.Fact, disc.Context, "auto_refresh", disc.Confidence);

                if (existingByTopic.ContainsKey(disc.Topic))
                    result.VerifiedFacts++;
                else
                    result.AddedFacts++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to reconcile fact for topic {Topic}", disc.Topic);
                result.Errors.Add($"{disc.Topic}: {ex.Message}");
            }
        }

        // Only mark stale for prefixes where we actually received data (source was reachable)
        var activePrefixes = TopicPrefixes
            .Where(prefix => discovered.Any(f => f.Topic.StartsWith(prefix)))
            .ToHashSet();

        foreach (var (topic, facts) in existingByTopic)
        {
            if (discoveredTopics.Contains(topic))
                continue;

            if (!activePrefixes.Any(p => topic.StartsWith(p)))
                continue;

            foreach (var fact in facts)
            {
                if (fact.Source == "user_told")
                    continue;

                try
                {
                    var newConfidence = fact.Confidence - 0.3;
                    if (newConfidence <= 0)
                    {
                        await _knowledge.InvalidateAsync(fact.Topic, fact.Fact);
                    }
                    else
                    {
                        await _knowledge.SetConfidenceAsync(fact.Topic, fact.Fact, newConfidence);
                    }

                    result.StaleFacts++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to mark stale fact for topic {Topic}", fact.Topic);
                    result.Errors.Add($"stale {fact.Topic}: {ex.Message}");
                }
            }
        }

        return result;
    }

    private async Task SendNotificationAsync(RefreshResult result)
    {
        var lines = new List<string> { "**Knowledge Refresh Summary**" };

        if (result.AddedFacts > 0)
            lines.Add($"+ {result.AddedFacts} new facts discovered");
        if (result.VerifiedFacts > 0)
            lines.Add($"~ {result.VerifiedFacts} facts verified");
        if (result.StaleFacts > 0)
            lines.Add($"- {result.StaleFacts} facts went stale");
        if (result.Errors.Count > 0)
            lines.Add($"! {result.Errors.Count} errors");

        var message = string.Join("\n", lines);

        try
        {
            await _discordBot.SendDmAsync(_config.CurrentValue.DiscordUserId, message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send knowledge refresh notification");
        }
    }
}
