using HomelabBot.Configuration;
using HomelabBot.Models;
using HomelabBot.Models.Prometheus;
using HomelabBot.Plugins;
using HomelabBot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace HomelabBot.Tests;

public class AnomalyEvaluationGateTests
{
    private static AnomalyDetectionService CreateService(
        DockerPlugin? dockerPlugin = null,
        int stoppedContainerGraceHours = 24)
    {
        var config = new AnomalyDetectionConfiguration
        {
            StoppedContainerGraceHours = stoppedContainerGraceHours,
        };
        var configMonitor = Substitute.For<IOptionsMonitor<AnomalyDetectionConfiguration>>();
        configMonitor.CurrentValue.Returns(config);

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());

        // Only the container check and the pure gate helpers are exercised here — the remaining
        // dependencies are never touched, so nulls are safe.
        return new AnomalyDetectionService(
            configMonitor,
            httpClientFactory,
            null!,  // PrometheusQueryService
            null!,  // SmartNotificationService
            null!,  // DiscordBotService
            null!,  // IDbContextFactory
            NullLogger<AnomalyDetectionService>.Instance,
            dockerPlugin!,
            null!,  // TrueNASPlugin
            null!,  // LokiPlugin
            Options.Create(new LokiConfiguration()),
            null!); // ServiceStateStore
    }

    private static DockerPlugin CreateDockerPlugin(params StoppedContainerInfo[] stopped)
    {
        var plugin = Substitute.For<DockerPlugin>(null, NullLogger<DockerPlugin>.Instance);
        plugin.GetStoppedContainersAsync(Arg.Any<CancellationToken>()).Returns(stopped.ToList());
        return plugin;
    }

    [Fact]
    public void ShouldEvaluate_FingerprintChanged_ReturnsTrue()
    {
        var now = new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);

        var result = AnomalyDetectionService.ShouldEvaluate(
            "Container:Critical:10", "Container:Critical:14", now.AddMinutes(-5), now, TimeSpan.FromHours(6));

        Assert.True(result);
    }

    [Fact]
    public void ShouldEvaluate_SameFingerprintInsideWindow_ReturnsFalse()
    {
        var now = new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);

        var result = AnomalyDetectionService.ShouldEvaluate(
            "Container:Critical:10", "Container:Critical:10", now.AddHours(-1), now, TimeSpan.FromHours(6));

        Assert.False(result);
    }

    [Fact]
    public void ShouldEvaluate_SameFingerprintAfterWindow_ReturnsTrue()
    {
        var now = new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);

        var result = AnomalyDetectionService.ShouldEvaluate(
            "Container:Critical:10", "Container:Critical:10", now.AddHours(-6), now, TimeSpan.FromHours(6));

        Assert.True(result);
    }

    [Fact]
    public void ShouldEvaluate_NoPreviousEvaluation_ReturnsTrue()
    {
        var now = new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);

        var result = AnomalyDetectionService.ShouldEvaluate(
            "Container:Critical:10", null, null, now, TimeSpan.FromHours(6));

        Assert.True(result);
    }

    [Fact]
    public void BuildFingerprint_OrderIndependent()
    {
        var cpu = new AnomalyDetectionService.Anomaly
        {
            Type = "CPU",
            Message = "CPU usage at 87.3%",
            Severity = AnomalyDetectionService.AnomalySeverity.Warning,
            Value = 87.3,
        };
        var container = new AnomalyDetectionService.Anomaly
        {
            Type = "Container",
            Message = "9 container(s) stopped",
            Severity = AnomalyDetectionService.AnomalySeverity.Critical,
            Value = 9,
        };

        Assert.Equal(
            AnomalyDetectionService.BuildFingerprint([cpu, container]),
            AnomalyDetectionService.BuildFingerprint([container, cpu]));
    }

    [Fact]
    public void BuildFingerprint_JitteringValue_StaysStable()
    {
        var first = AnomalyDetectionService.BuildFingerprint([
            new AnomalyDetectionService.Anomaly
            {
                Type = "CPU",
                Message = "CPU usage at 87.3%",
                Severity = AnomalyDetectionService.AnomalySeverity.Warning,
                Value = 87.3,
            }
        ]);

        var second = AnomalyDetectionService.BuildFingerprint([
            new AnomalyDetectionService.Anomaly
            {
                Type = "CPU",
                Message = "CPU usage at 88.1%",
                Severity = AnomalyDetectionService.AnomalySeverity.Warning,
                Value = 88.1,
            }
        ]);

        Assert.Equal(first, second);
    }

    [Fact]
    public void BuildFingerprint_SeverityEscalation_ChangesFingerprint()
    {
        var warning = AnomalyDetectionService.BuildFingerprint([
            new AnomalyDetectionService.Anomaly
            {
                Type = "Container",
                Message = "3 container(s) stopped",
                Severity = AnomalyDetectionService.AnomalySeverity.Warning,
                Value = 3,
            }
        ]);

        var critical = AnomalyDetectionService.BuildFingerprint([
            new AnomalyDetectionService.Anomaly
            {
                Type = "Container",
                Message = "9 container(s) stopped",
                Severity = AnomalyDetectionService.AnomalySeverity.Critical,
                Value = 9,
            }
        ]);

        Assert.NotEqual(warning, critical);
    }

    [Fact]
    public void BuildFingerprint_SecondPoolDegrades_ChangesFingerprint()
    {
        // Storage anomalies all carry Value = 0, so only the key and the entry count differ.
        var tank = new AnomalyDetectionService.Anomaly
        {
            Type = "Storage",
            Key = "tank",
            Message = "Pool 'tank' status: DEGRADED",
            Severity = AnomalyDetectionService.AnomalySeverity.Warning,
            Value = 0,
        };
        var backup = new AnomalyDetectionService.Anomaly
        {
            Type = "Storage",
            Key = "backup",
            Message = "Pool 'backup' status: DEGRADED",
            Severity = AnomalyDetectionService.AnomalySeverity.Warning,
            Value = 0,
        };

        Assert.NotEqual(
            AnomalyDetectionService.BuildFingerprint([tank]),
            AnomalyDetectionService.BuildFingerprint([tank, backup]));
    }

    [Fact]
    public void BuildFingerprint_StoppedAndRestartsSameBucket_DoNotCollide()
    {
        var stopped = new AnomalyDetectionService.Anomaly
        {
            Type = "Container",
            Key = "stopped",
            Message = "9 container(s) stopped: a",
            Severity = AnomalyDetectionService.AnomalySeverity.Critical,
            Value = 9,
        };
        var restarts = new AnomalyDetectionService.Anomaly
        {
            Type = "Container",
            Key = "restarts",
            Message = "Container restarts detected in last 5m (rate: 9.0)",
            Severity = AnomalyDetectionService.AnomalySeverity.Critical,
            Value = 9,
        };

        Assert.NotEqual(
            AnomalyDetectionService.BuildFingerprint([stopped]),
            AnomalyDetectionService.BuildFingerprint([stopped, restarts]));
    }

    [Fact]
    public void DownTargetIds_SwappedProbeUnderSameJob_ChangesIds()
    {
        var before = AnomalyDetectionService.DownTargetIds([
            new PrometheusTargetInfo { Job = "blackbox", Instance = "https://nas.lan", Health = "down" },
            new PrometheusTargetInfo { Job = "node", Instance = "ubuntu:9100", Health = "up" },
        ]);

        var after = AnomalyDetectionService.DownTargetIds([
            new PrometheusTargetInfo { Job = "blackbox", Instance = "https://git.lan", Health = "down" },
            new PrometheusTargetInfo { Job = "node", Instance = "ubuntu:9100", Health = "up" },
        ]);

        Assert.Single(before);
        Assert.Single(after);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void DownTargetIds_SameTargetsInDifferentOrder_AreEqual()
    {
        var first = AnomalyDetectionService.DownTargetIds([
            new PrometheusTargetInfo { Job = "blackbox", Instance = "https://nas.lan", Health = "down" },
            new PrometheusTargetInfo { Job = "node", Instance = "ubuntu:9100", Health = "down" },
        ]);

        var second = AnomalyDetectionService.DownTargetIds([
            new PrometheusTargetInfo { Job = "node", Instance = "ubuntu:9100", Health = "down" },
            new PrometheusTargetInfo { Job = "blackbox", Instance = "https://nas.lan", Health = "down" },
        ]);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task CheckContainerHealth_SwappedContainerAtSameCount_ChangesFingerprint()
    {
        var before = await CreateService(CreateDockerPlugin(
            new StoppedContainerInfo { Name = "plex", StoppedFor = TimeSpan.FromMinutes(5) }))
            .CheckContainerHealthAsync(CancellationToken.None);

        var after = await CreateService(CreateDockerPlugin(
            new StoppedContainerInfo { Name = "sonarr", StoppedFor = TimeSpan.FromMinutes(5) }))
            .CheckContainerHealthAsync(CancellationToken.None);

        Assert.NotEqual(
            AnomalyDetectionService.BuildFingerprint(before),
            AnomalyDetectionService.BuildFingerprint(after));
    }

    [Fact]
    public async Task CheckContainerHealth_SameContainersStopped_KeepsFingerprint()
    {
        var first = await CreateService(CreateDockerPlugin(
            new StoppedContainerInfo { Name = "plex", StoppedFor = TimeSpan.FromMinutes(5) },
            new StoppedContainerInfo { Name = "sonarr", StoppedFor = TimeSpan.FromMinutes(5) }))
            .CheckContainerHealthAsync(CancellationToken.None);

        // Same set, reported in a different order and an hour later.
        var second = await CreateService(CreateDockerPlugin(
            new StoppedContainerInfo { Name = "sonarr", StoppedFor = TimeSpan.FromMinutes(65) },
            new StoppedContainerInfo { Name = "plex", StoppedFor = TimeSpan.FromMinutes(65) }))
            .CheckContainerHealthAsync(CancellationToken.None);

        Assert.Equal(
            AnomalyDetectionService.BuildFingerprint(first),
            AnomalyDetectionService.BuildFingerprint(second));
    }

    [Theory]
    [InlineData(9, 20)]      // real jump in stopped containers
    [InlineData(87.3, 150)]  // CPU warning escalating into a spike
    public void MagnitudeBucket_LargeChange_ChangesBucket(double before, double after)
    {
        Assert.NotEqual(
            AnomalyDetectionService.MagnitudeBucket(before),
            AnomalyDetectionService.MagnitudeBucket(after));
    }

    [Fact]
    public async Task CheckContainerHealth_LongStoppedContainers_NotReported()
    {
        var docker = CreateDockerPlugin(
            new StoppedContainerInfo { Name = "sonarr", StoppedFor = TimeSpan.FromDays(10) },
            new StoppedContainerInfo { Name = "radarr", StoppedFor = TimeSpan.FromDays(10) });
        var service = CreateService(docker);

        var anomalies = await service.CheckContainerHealthAsync(CancellationToken.None);

        Assert.Empty(anomalies);
    }

    [Fact]
    public async Task CheckContainerHealth_RecentlyStopped_ReportedWithNames()
    {
        var docker = CreateDockerPlugin(
            new StoppedContainerInfo { Name = "sonarr", StoppedFor = TimeSpan.FromDays(10) },
            new StoppedContainerInfo { Name = "grafana", StoppedFor = TimeSpan.FromMinutes(5) });
        var service = CreateService(docker);

        var anomalies = await service.CheckContainerHealthAsync(CancellationToken.None);

        var anomaly = Assert.Single(anomalies);
        Assert.Equal("Container", anomaly.Type);
        Assert.Equal(AnomalyDetectionService.AnomalySeverity.Warning, anomaly.Severity);
        Assert.Contains("grafana", anomaly.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sonarr", anomaly.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckContainerHealth_UnknownStopTime_TreatedAsRecent()
    {
        var docker = CreateDockerPlugin(new StoppedContainerInfo { Name = "mystery", StoppedFor = null });
        var service = CreateService(docker);

        var anomalies = await service.CheckContainerHealthAsync(CancellationToken.None);

        Assert.Single(anomalies);
    }

    [Fact]
    public async Task CheckContainerHealth_ZeroGrace_ReportsEveryStoppedContainer()
    {
        var docker = CreateDockerPlugin(
            new StoppedContainerInfo { Name = "sonarr", StoppedFor = TimeSpan.FromDays(10) });
        var service = CreateService(docker, stoppedContainerGraceHours: 0);

        var anomalies = await service.CheckContainerHealthAsync(CancellationToken.None);

        Assert.Contains("sonarr", Assert.Single(anomalies).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckContainerHealth_MoreThanThreeRecentlyStopped_IsCritical()
    {
        var docker = CreateDockerPlugin(
            new StoppedContainerInfo { Name = "a", StoppedFor = TimeSpan.FromMinutes(1) },
            new StoppedContainerInfo { Name = "b", StoppedFor = TimeSpan.FromMinutes(2) },
            new StoppedContainerInfo { Name = "c", StoppedFor = TimeSpan.FromMinutes(3) },
            new StoppedContainerInfo { Name = "d", StoppedFor = TimeSpan.FromMinutes(4) });
        var service = CreateService(docker);

        var anomalies = await service.CheckContainerHealthAsync(CancellationToken.None);

        Assert.Equal(AnomalyDetectionService.AnomalySeverity.Critical, Assert.Single(anomalies).Severity);
    }
}
