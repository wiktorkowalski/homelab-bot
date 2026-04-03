using HomelabBot.Configuration;
using HomelabBot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace HomelabBot.Tests;

public class LogAnomalyServiceTests
{
    private readonly LogAnomalyConfiguration _config;
    private readonly LogAnomalyService _service;

    public LogAnomalyServiceTests()
    {
        _config = new LogAnomalyConfiguration { ErrorThreshold = 50 };
        var configMonitor = Substitute.For<IOptionsMonitor<LogAnomalyConfiguration>>();
        configMonitor.CurrentValue.Returns(_config);

        // DetectErrorSpikes only uses _config — other deps are unused, safe to pass null
        _service = new LogAnomalyService(
            configMonitor,
            null!,  // LokiPlugin
            null!,  // SmartNotificationService
            null!,  // DiscordBotService
            NullLogger<LogAnomalyService>.Instance,
            null!); // ServiceStateStore
    }

    [Fact]
    public void DetectErrorSpikes_FirstSeen_NoSpike()
    {
        // previousCount=0 → condition "previousCount > 0" fails
        var errorCounts = new Dictionary<string, long>
        {
            ["nginx"] = 100,
        };

        var spikes = _service.DetectErrorSpikes(errorCounts);

        Assert.Empty(spikes);
    }

    [Fact]
    public void DetectErrorSpikes_DoubledAboveThreshold_ReturnsSpike()
    {
        // Establish baseline
        _service.DetectErrorSpikes(new Dictionary<string, long> { ["nginx"] = 30 });

        // Now double it above threshold
        var spikes = _service.DetectErrorSpikes(new Dictionary<string, long> { ["nginx"] = 100 });

        Assert.Single(spikes);
        Assert.Equal("nginx", spikes[0].Container);
        Assert.Equal(30, spikes[0].PreviousCount);
        Assert.Equal(100, spikes[0].CurrentCount);
    }

    [Fact]
    public void DetectErrorSpikes_BelowThreshold_NoSpike()
    {
        // Establish baseline
        _service.DetectErrorSpikes(new Dictionary<string, long> { ["nginx"] = 10 });

        // Doubled but below threshold (50)
        var spikes = _service.DetectErrorSpikes(new Dictionary<string, long> { ["nginx"] = 30 });

        Assert.Empty(spikes);
    }

    [Fact]
    public void DetectErrorSpikes_NotDoubled_NoSpike()
    {
        // Establish baseline
        _service.DetectErrorSpikes(new Dictionary<string, long> { ["nginx"] = 40 });

        // Above threshold but not doubled (60 is not > 40*2)
        var spikes = _service.DetectErrorSpikes(new Dictionary<string, long> { ["nginx"] = 60 });

        Assert.Empty(spikes);
    }

    [Fact]
    public void DetectErrorSpikes_ExactlyDoubled_NoSpike()
    {
        // Establish baseline
        _service.DetectErrorSpikes(new Dictionary<string, long> { ["nginx"] = 30 });

        // count(60) > previousCount*2(60) is false — must be strictly greater
        var spikes = _service.DetectErrorSpikes(new Dictionary<string, long> { ["nginx"] = 60 });

        Assert.Empty(spikes);
    }

    [Fact]
    public void DetectErrorSpikes_MultipleContainers_OnlySpikedOnesReturned()
    {
        // Establish baselines
        _service.DetectErrorSpikes(new Dictionary<string, long>
        {
            ["nginx"] = 20,
            ["postgres"] = 10,
            ["redis"] = 5,
        });

        // nginx spikes, postgres doesn't double, redis stays same
        var spikes = _service.DetectErrorSpikes(new Dictionary<string, long>
        {
            ["nginx"] = 100,
            ["postgres"] = 15,
            ["redis"] = 5,
        });

        Assert.Single(spikes);
        Assert.Equal("nginx", spikes[0].Container);
    }

    [Fact]
    public void DetectErrorSpikes_UpdatesBaseline_AfterDetection()
    {
        // First call: establish baseline
        _service.DetectErrorSpikes(new Dictionary<string, long> { ["nginx"] = 20 });

        // Second call: spike detected (100 > 20*2 && 100 >= 50)
        var spikes1 = _service.DetectErrorSpikes(new Dictionary<string, long> { ["nginx"] = 100 });
        Assert.Single(spikes1);

        // Third call: same count — no longer a spike because baseline is now 100
        var spikes2 = _service.DetectErrorSpikes(new Dictionary<string, long> { ["nginx"] = 100 });
        Assert.Empty(spikes2);
    }

    [Fact]
    public void DetectErrorSpikes_NewContainerAppearing_NoSpike()
    {
        // Establish baseline for nginx only
        _service.DetectErrorSpikes(new Dictionary<string, long> { ["nginx"] = 20 });

        // New container appears with high count — still first seen (prev=0)
        var spikes = _service.DetectErrorSpikes(new Dictionary<string, long>
        {
            ["nginx"] = 20,
            ["newservice"] = 200,
        });

        Assert.Empty(spikes);
    }

    [Fact]
    public void DetectErrorSpikes_ExactlyAtThreshold_Spikes()
    {
        // Establish baseline
        _service.DetectErrorSpikes(new Dictionary<string, long> { ["nginx"] = 20 });

        // count(50) >= threshold(50) && count(50) > prev*2(40) && prev(20) > 0
        var spikes = _service.DetectErrorSpikes(new Dictionary<string, long> { ["nginx"] = 50 });

        Assert.Single(spikes);
    }

    [Fact]
    public void DetectErrorSpikes_EmptyInput_NoSpikes()
    {
        var spikes = _service.DetectErrorSpikes(new Dictionary<string, long>());

        Assert.Empty(spikes);
    }

    [Fact]
    public void DetectErrorSpikes_ZeroCount_NoSpike()
    {
        _service.DetectErrorSpikes(new Dictionary<string, long> { ["nginx"] = 10 });

        var spikes = _service.DetectErrorSpikes(new Dictionary<string, long> { ["nginx"] = 0 });

        Assert.Empty(spikes);
    }

    [Fact]
    public void DetectErrorSpikes_SpikeValues_ContainCorrectCounts()
    {
        _service.DetectErrorSpikes(new Dictionary<string, long>
        {
            ["nginx"] = 20,
            ["redis"] = 30,
        });

        var spikes = _service.DetectErrorSpikes(new Dictionary<string, long>
        {
            ["nginx"] = 100,
            ["redis"] = 200,
        });

        Assert.Equal(2, spikes.Count);
        var nginx = spikes.First(s => s.Container == "nginx");
        Assert.Equal(20, nginx.PreviousCount);
        Assert.Equal(100, nginx.CurrentCount);
        var redis = spikes.First(s => s.Container == "redis");
        Assert.Equal(30, redis.PreviousCount);
        Assert.Equal(200, redis.CurrentCount);
    }
}
