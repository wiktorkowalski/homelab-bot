using HomelabBot.Configuration;
using HomelabBot.Models;
using HomelabBot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace HomelabBot.Tests;

public class HealthScoreServiceTests : IClassFixture<DatabaseFixture>
{
    private readonly DatabaseFixture _fixture;
    private readonly HealthScoreConfiguration _config;
    private readonly IOptionsMonitor<HealthScoreConfiguration> _configMonitor;
    private readonly HealthScoreService _service;

    public HealthScoreServiceTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
        _config = new HealthScoreConfiguration();
        _configMonitor = Substitute.For<IOptionsMonitor<HealthScoreConfiguration>>();
        _configMonitor.CurrentValue.Returns(_config);
        _service = new HealthScoreService(
            _fixture.DbContextFactory,
            _configMonitor,
            NullLogger<HealthScoreService>.Instance);
    }

    // --- CalculateScore ---

    [Fact]
    public void CalculateScore_PerfectHealth_Returns100()
    {
        var data = new DailySummaryData
        {
            Containers = [new ContainerStatus { Name = "app", State = "running" }],
            Pools = [new PoolStatus { Name = "tank", Health = "ONLINE" }],
            Router = new RouterStatus(),
            Monitoring = new MonitoringStatus { TotalTargets = 5, UpTargets = 5, DownTargets = 0 },
            Alerts = [],
        };

        var result = _service.CalculateScore(data);

        Assert.Equal(100, result.Score);
        Assert.Equal(0, result.AlertDeductions);
        Assert.Equal(0, result.ContainerDeductions);
        Assert.Equal(0, result.PoolDeductions);
        Assert.Equal(0, result.MonitoringDeductions);
        Assert.Equal(0, result.ConnectivityDeductions);
    }

    [Fact]
    public void CalculateScore_AllDataSourcesMissing_DeductsConnectivity()
    {
        var data = new DailySummaryData
        {
            Containers = [],
            Pools = [],
            Router = null,
            Monitoring = null,
            Alerts = [],
        };

        var result = _service.CalculateScore(data);

        var expectedDeductions = _config.MissingContainersWeight
            + _config.MissingPoolsWeight
            + _config.MissingRouterWeight
            + _config.MissingMonitoringWeight;
        Assert.Equal(expectedDeductions, result.ConnectivityDeductions);
        Assert.Equal(100 - expectedDeductions, result.Score);
    }

    [Fact]
    public void CalculateScore_CriticalAlerts_Deducts()
    {
        var data = new DailySummaryData
        {
            Containers = [new ContainerStatus { Name = "app", State = "running" }],
            Pools = [new PoolStatus { Name = "tank", Health = "ONLINE" }],
            Router = new RouterStatus(),
            Monitoring = new MonitoringStatus(),
            Alerts =
            [
                new AlertSummary { Name = "disk", Severity = "critical" },
                new AlertSummary { Name = "cpu", Severity = "critical" },
            ],
        };

        var result = _service.CalculateScore(data);

        Assert.Equal(2 * _config.CriticalAlertWeight, result.AlertDeductions);
    }

    [Fact]
    public void CalculateScore_WarningAlerts_Deducts()
    {
        var data = new DailySummaryData
        {
            Containers = [new ContainerStatus { Name = "app", State = "running" }],
            Pools = [new PoolStatus { Name = "tank", Health = "ONLINE" }],
            Router = new RouterStatus(),
            Monitoring = new MonitoringStatus(),
            Alerts =
            [
                new AlertSummary { Name = "mem", Severity = "warning" },
            ],
        };

        var result = _service.CalculateScore(data);

        Assert.Equal(_config.WarningAlertWeight, result.AlertDeductions);
    }

    [Fact]
    public void CalculateScore_MixedAlerts_DeductsBoth()
    {
        var data = new DailySummaryData
        {
            Containers = [new ContainerStatus { Name = "app", State = "running" }],
            Pools = [new PoolStatus { Name = "tank", Health = "ONLINE" }],
            Router = new RouterStatus(),
            Monitoring = new MonitoringStatus(),
            Alerts =
            [
                new AlertSummary { Name = "disk", Severity = "critical" },
                new AlertSummary { Name = "mem", Severity = "warning" },
                new AlertSummary { Name = "info", Severity = "info" },
            ],
        };

        var result = _service.CalculateScore(data);

        Assert.Equal(_config.CriticalAlertWeight + _config.WarningAlertWeight, result.AlertDeductions);
    }

    [Fact]
    public void CalculateScore_StoppedContainers_Deducts()
    {
        var data = new DailySummaryData
        {
            Containers =
            [
                new ContainerStatus { Name = "app", State = "running" },
                new ContainerStatus { Name = "db", State = "exited" },
                new ContainerStatus { Name = "cache", State = "dead" },
            ],
            Pools = [new PoolStatus { Name = "tank", Health = "ONLINE" }],
            Router = new RouterStatus(),
            Monitoring = new MonitoringStatus(),
            Alerts = [],
        };

        var result = _service.CalculateScore(data);

        Assert.Equal(2 * _config.StoppedContainerWeight, result.ContainerDeductions);
    }

    [Fact]
    public void CalculateScore_UnhealthyPools_Deducts()
    {
        var data = new DailySummaryData
        {
            Containers = [new ContainerStatus { Name = "app", State = "running" }],
            Pools =
            [
                new PoolStatus { Name = "tank1", Health = "ONLINE" },
                new PoolStatus { Name = "tank2", Health = "DEGRADED" },
            ],
            Router = new RouterStatus(),
            Monitoring = new MonitoringStatus(),
            Alerts = [],
        };

        var result = _service.CalculateScore(data);

        Assert.Equal(_config.UnhealthyPoolWeight, result.PoolDeductions);
    }

    [Fact]
    public void CalculateScore_DownTargets_Deducts()
    {
        var data = new DailySummaryData
        {
            Containers = [new ContainerStatus { Name = "app", State = "running" }],
            Pools = [new PoolStatus { Name = "tank", Health = "ONLINE" }],
            Router = new RouterStatus(),
            Monitoring = new MonitoringStatus { TotalTargets = 10, UpTargets = 7, DownTargets = 3 },
            Alerts = [],
        };

        var result = _service.CalculateScore(data);

        Assert.Equal(3 * _config.DownTargetWeight, result.MonitoringDeductions);
    }

    [Fact]
    public void CalculateScore_NoMonitoring_ZeroMonitoringDeductions()
    {
        var data = new DailySummaryData
        {
            Containers = [new ContainerStatus { Name = "app", State = "running" }],
            Pools = [new PoolStatus { Name = "tank", Health = "ONLINE" }],
            Router = new RouterStatus(),
            Monitoring = null,
            Alerts = [],
        };

        var result = _service.CalculateScore(data);

        Assert.Equal(0, result.MonitoringDeductions);
        // But connectivity deduction for missing monitoring
        Assert.Equal(_config.MissingMonitoringWeight, result.ConnectivityDeductions);
    }

    [Fact]
    public void CalculateScore_ScoreNeverBelowZero()
    {
        var data = new DailySummaryData
        {
            Containers = [],
            Pools = [],
            Router = null,
            Monitoring = null,
            Alerts = Enumerable.Range(0, 20)
                .Select(i => new AlertSummary { Name = $"alert{i}", Severity = "critical" })
                .ToList(),
        };

        var result = _service.CalculateScore(data);

        Assert.Equal(0, result.Score);
    }

    [Fact]
    public void CalculateScore_ScoreNeverAbove100()
    {
        // Perfect data should be exactly 100, never more
        var data = new DailySummaryData
        {
            Containers = [new ContainerStatus { Name = "app", State = "running" }],
            Pools = [new PoolStatus { Name = "tank", Health = "ONLINE" }],
            Router = new RouterStatus(),
            Monitoring = new MonitoringStatus { DownTargets = 0 },
            Alerts = [],
        };

        var result = _service.CalculateScore(data);

        Assert.True(result.Score <= 100);
    }

    [Fact]
    public void CalculateScore_CombinedDeductions()
    {
        var data = new DailySummaryData
        {
            Containers =
            [
                new ContainerStatus { Name = "app", State = "running" },
                new ContainerStatus { Name = "db", State = "exited" },
            ],
            Pools =
            [
                new PoolStatus { Name = "tank", Health = "DEGRADED" },
            ],
            Router = null,
            Monitoring = new MonitoringStatus { DownTargets = 1 },
            Alerts =
            [
                new AlertSummary { Name = "disk", Severity = "critical" },
                new AlertSummary { Name = "mem", Severity = "warning" },
            ],
        };

        var result = _service.CalculateScore(data);

        var expected = 100
            - _config.MissingRouterWeight
            - _config.CriticalAlertWeight - _config.WarningAlertWeight
            - _config.StoppedContainerWeight
            - _config.UnhealthyPoolWeight
            - _config.DownTargetWeight;
        Assert.Equal(expected, result.Score);
    }

    // --- RecordScoreAsync / GetTrendAsync / GetLatestScoreAsync ---

    [Fact]
    public async Task RecordScoreAsync_PersistsToDatabase()
    {
        var result = new HealthScoreResult
        {
            Score = 85,
            AlertDeductions = 5,
            ContainerDeductions = 0,
            PoolDeductions = 0,
            MonitoringDeductions = 10,
            ConnectivityDeductions = 0,
        };

        await _service.RecordScoreAsync(result);

        var latest = await _service.GetLatestScoreAsync();
        Assert.NotNull(latest);
        Assert.Equal(85, latest.Value);
    }

    [Fact]
    public async Task GetTrendAsync_NotEnoughData_ReturnsMessage()
    {
        using var fixture = new DatabaseFixture();
        var svc = new HealthScoreService(
            fixture.DbContextFactory,
            _configMonitor,
            NullLogger<HealthScoreService>.Instance);

        var trend = await svc.GetTrendAsync(TimeSpan.FromHours(1));

        Assert.Equal("Not enough data for trend analysis", trend);
    }

    [Fact]
    public async Task GetTrendAsync_Improving_ReturnsImprovingMessage()
    {
        // Use isolated fixture so prior records don't interfere
        using var fixture = new DatabaseFixture();
        var svc = new HealthScoreService(
            fixture.DbContextFactory,
            _configMonitor,
            NullLogger<HealthScoreService>.Instance);

        await svc.RecordScoreAsync(new HealthScoreResult
        {
            Score = 30,
            AlertDeductions = 0,
            ContainerDeductions = 0,
            PoolDeductions = 0,
            MonitoringDeductions = 0,
            ConnectivityDeductions = 0,
        });

        await svc.RecordScoreAsync(new HealthScoreResult
        {
            Score = 95,
            AlertDeductions = 0,
            ContainerDeductions = 0,
            PoolDeductions = 0,
            MonitoringDeductions = 0,
            ConnectivityDeductions = 0,
        });

        var trend = await svc.GetTrendAsync(TimeSpan.FromHours(1));

        Assert.Contains("Improving", trend);
    }

    [Fact]
    public async Task GetTrendAsync_Dropping_ReturnsDroppingMessage()
    {
        using var fixture = new DatabaseFixture();
        var svc = new HealthScoreService(
            fixture.DbContextFactory,
            _configMonitor,
            NullLogger<HealthScoreService>.Instance);

        await svc.RecordScoreAsync(new HealthScoreResult
        {
            Score = 95,
            AlertDeductions = 0,
            ContainerDeductions = 0,
            PoolDeductions = 0,
            MonitoringDeductions = 0,
            ConnectivityDeductions = 0,
        });

        await svc.RecordScoreAsync(new HealthScoreResult
        {
            Score = 30,
            AlertDeductions = 0,
            ContainerDeductions = 0,
            PoolDeductions = 0,
            MonitoringDeductions = 0,
            ConnectivityDeductions = 0,
        });

        var trend = await svc.GetTrendAsync(TimeSpan.FromHours(1));

        Assert.Contains("Dropping", trend);
    }

    [Fact]
    public async Task GetTrendAsync_Stable_ReturnsStableMessage()
    {
        using var fixture = new DatabaseFixture();
        var svc = new HealthScoreService(
            fixture.DbContextFactory,
            _configMonitor,
            NullLogger<HealthScoreService>.Instance);

        await svc.RecordScoreAsync(new HealthScoreResult
        {
            Score = 80,
            AlertDeductions = 0,
            ContainerDeductions = 0,
            PoolDeductions = 0,
            MonitoringDeductions = 0,
            ConnectivityDeductions = 0,
        });

        await svc.RecordScoreAsync(new HealthScoreResult
        {
            Score = 82,
            AlertDeductions = 0,
            ContainerDeductions = 0,
            PoolDeductions = 0,
            MonitoringDeductions = 0,
            ConnectivityDeductions = 0,
        });

        var trend = await svc.GetTrendAsync(TimeSpan.FromHours(1));

        Assert.Contains("Stable", trend);
    }
}
