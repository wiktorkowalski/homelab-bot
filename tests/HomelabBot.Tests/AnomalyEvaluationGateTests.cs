using System.Globalization;
using System.Text.Json;
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

    private static readonly DateTime Now = new(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan RepeatAfter = TimeSpan.FromHours(6);

    private static AnomalyDetectionService.EvaluatedFingerprint Evaluated(string fingerprint, DateTime at) =>
        new() { Fingerprint = fingerprint, EvaluatedAt = at };

    [Fact]
    public void ShouldEvaluate_FingerprintChanged_ReturnsTrue()
    {
        var recent = new[] { Evaluated("Container:Critical:14", Now.AddMinutes(-5)) };

        Assert.True(AnomalyDetectionService.ShouldEvaluate("Container:Critical:10", false, recent, Now, RepeatAfter));
    }

    [Fact]
    public void ShouldEvaluate_SameFingerprintInsideWindow_ReturnsFalse()
    {
        var recent = new[] { Evaluated("Container:Critical:10", Now.AddHours(-1)) };

        Assert.False(AnomalyDetectionService.ShouldEvaluate("Container:Critical:10", false, recent, Now, RepeatAfter));
    }

    [Fact]
    public void ShouldEvaluate_SameFingerprintAfterWindow_ReturnsTrue()
    {
        var recent = new[] { Evaluated("Container:Critical:10", Now.AddHours(-6)) };

        Assert.True(AnomalyDetectionService.ShouldEvaluate("Container:Critical:10", false, recent, Now, RepeatAfter));
    }

    [Fact]
    public void ShouldEvaluate_NoPreviousEvaluation_ReturnsTrue()
    {
        Assert.True(AnomalyDetectionService.ShouldEvaluate("Container:Critical:10", false, [], Now, RepeatAfter));
    }

    [Fact]
    public void ShouldEvaluate_ZeroRepeatWindow_AlwaysReturnsTrue()
    {
        var recent = new[] { Evaluated("A", Now) };

        Assert.True(AnomalyDetectionService.ShouldEvaluate("A", false, recent, Now, TimeSpan.Zero));
    }

    [Fact]
    public void ShouldEvaluate_FlickerBackToEarlierSet_SkipsThirdEvaluation()
    {
        List<AnomalyDetectionService.EvaluatedFingerprint> recent = [];

        Assert.True(AnomalyDetectionService.ShouldEvaluate("A", false, recent, Now, RepeatAfter));
        recent = AnomalyDetectionService.RecordEvaluation(recent, "A", Now, 5);

        Assert.True(AnomalyDetectionService.ShouldEvaluate("B", false, recent, Now.AddMinutes(5), RepeatAfter));
        recent = AnomalyDetectionService.RecordEvaluation(recent, "B", Now.AddMinutes(5), 5);

        Assert.False(AnomalyDetectionService.ShouldEvaluate("A", false, recent, Now.AddMinutes(10), RepeatAfter));
        Assert.False(AnomalyDetectionService.ShouldEvaluate("B", false, recent, Now.AddMinutes(15), RepeatAfter));
    }

    [Fact]
    public void ShouldEvaluate_EarlierSetExpired_ReturnsTrue()
    {
        var recent = new[]
        {
            Evaluated("A", Now.AddHours(-7)),
            Evaluated("B", Now.AddMinutes(-5)),
        };

        Assert.True(AnomalyDetectionService.ShouldEvaluate("A", false, recent, Now, RepeatAfter));
    }

    [Fact]
    public void RecordEvaluation_OverCapacity_EvictsOldest()
    {
        List<AnomalyDetectionService.EvaluatedFingerprint> recent = [];
        foreach (var (fingerprint, i) in new[] { "A", "B", "C", "D", "E", "F" }.Select((f, i) => (f, i)))
        {
            recent = AnomalyDetectionService.RecordEvaluation(recent, fingerprint, Now.AddMinutes(i), 5);
        }

        Assert.Equal(["B", "C", "D", "E", "F"], recent.Select(e => e.Fingerprint));
        Assert.True(AnomalyDetectionService.ShouldEvaluate("A", false, recent, Now.AddMinutes(10), RepeatAfter));
    }

    [Fact]
    public void RecordEvaluation_SameFingerprintAgain_MovesToNewestWithoutDuplicate()
    {
        var recent = new[] { Evaluated("A", Now.AddHours(-7)), Evaluated("B", Now.AddHours(-1)) };

        var updated = AnomalyDetectionService.RecordEvaluation(recent, "A", Now, 5);

        Assert.Equal(["B", "A"], updated.Select(e => e.Fingerprint));
        Assert.Equal(Now, updated[^1].EvaluatedAt);
    }

    [Fact]
    public void ParseRecentEvaluations_OnlyLegacyKeys_SeedsRing()
    {
        var evaluatedAt = Now.AddHours(-1);

        var recent = AnomalyDetectionService.ParseRecentEvaluations(
            null, "A", evaluatedAt.ToString("O", CultureInfo.InvariantCulture), 5, NullLogger.Instance);

        var entry = Assert.Single(recent);
        Assert.Equal("A", entry.Fingerprint);
        Assert.Equal(evaluatedAt, entry.EvaluatedAt);
        Assert.False(AnomalyDetectionService.ShouldEvaluate("A", false, recent, Now, RepeatAfter));
    }

    [Theory]
    [InlineData("", "2026-08-05T11:00:00.0000000Z")]  // legacy persist wrote "" before any evaluation
    [InlineData("A", null)]                            // no timestamp used to mean "evaluate"
    [InlineData(null, null)]                           // fresh install
    public void ParseRecentEvaluations_IncompleteLegacyKeys_ReturnsEmpty(string? fingerprint, string? evaluatedAt)
    {
        Assert.Empty(AnomalyDetectionService.ParseRecentEvaluations(null, fingerprint, evaluatedAt, 5, NullLogger.Instance));
    }

    [Fact]
    public void ParseRecentEvaluations_LegacyOlderThanRing_IgnoresLegacyKeys()
    {
        var ringJson = JsonSerializer.Serialize(new[] { Evaluated("A", Now.AddHours(-2)), Evaluated("B", Now.AddHours(-1)) });

        var recent = AnomalyDetectionService.ParseRecentEvaluations(
            ringJson, "C", Now.AddHours(-3).ToString("O", CultureInfo.InvariantCulture), 5, NullLogger.Instance);

        Assert.Equal(["A", "B"], recent.Select(e => e.Fingerprint));
        Assert.Equal(Now.AddHours(-1), recent[1].EvaluatedAt);
    }

    [Fact]
    public void ParseRecentEvaluations_CorruptRing_FallsBackToLegacyKeys()
    {
        var recent = AnomalyDetectionService.ParseRecentEvaluations(
            "{not json", "A", Now.ToString("O", CultureInfo.InvariantCulture), 5, NullLogger.Instance);

        Assert.Equal("A", Assert.Single(recent).Fingerprint);
    }

    [Fact]
    public void ShouldEvaluate_CriticalSetReturnsAfterFlicker_ReturnsTrue()
    {
        var recent = new[]
        {
            Evaluated("A-critical", Now.AddMinutes(-10)),
            Evaluated("B", Now.AddMinutes(-5)),
        };

        Assert.True(AnomalyDetectionService.ShouldEvaluate("A-critical", true, recent, Now, RepeatAfter));
    }

    [Fact]
    public void ShouldEvaluate_CriticalSetUnchanged_ReturnsFalse()
    {
        var recent = new[]
        {
            Evaluated("B", Now.AddMinutes(-10)),
            Evaluated("A-critical", Now.AddMinutes(-5)),
        };

        Assert.False(AnomalyDetectionService.ShouldEvaluate("A-critical", true, recent, Now, RepeatAfter));
    }

    [Fact]
    public void FindSuppressingEvaluation_Skip_ReturnsMatchedEntry()
    {
        var recent = new[] { Evaluated("A", Now.AddHours(-2)), Evaluated("B", Now.AddHours(-1)) };

        var match = AnomalyDetectionService.FindSuppressingEvaluation("A", false, recent, Now, RepeatAfter);

        Assert.Equal(Now.AddHours(-2), match?.EvaluatedAt);
    }

    [Fact]
    public void ParseRecentEvaluations_LegacyNewerThanRing_MergesAsNewest()
    {
        var ringJson = JsonSerializer.Serialize(new[] { Evaluated("A", Now.AddHours(-2)), Evaluated("B", Now.AddHours(-1)) });

        var recent = AnomalyDetectionService.ParseRecentEvaluations(
            ringJson, "C", Now.ToString("O", CultureInfo.InvariantCulture), 5, NullLogger.Instance);

        Assert.Equal(["A", "B", "C"], recent.Select(e => e.Fingerprint));
    }

    [Fact]
    public void ParseRecentEvaluations_LegacyMatchesRingNewest_KeepsRing()
    {
        var ringJson = JsonSerializer.Serialize(new[] { Evaluated("A", Now.AddHours(-2)), Evaluated("B", Now.AddHours(-1)) });

        var recent = AnomalyDetectionService.ParseRecentEvaluations(
            ringJson, "B", Now.AddHours(-1).ToString("O", CultureInfo.InvariantCulture), 5, NullLogger.Instance);

        Assert.Equal(["A", "B"], recent.Select(e => e.Fingerprint));
        Assert.Equal(Now.AddHours(-1), recent[1].EvaluatedAt);
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{\"Fingerprint\":null,\"EvaluatedAt\":\"2026-08-05T11:00:00Z\"}]")]
    [InlineData("[{\"Fingerprint\":\"\",\"EvaluatedAt\":\"2026-08-05T11:00:00Z\"}]")]
    public void ParseRecentEvaluations_BrokenEntries_Dropped(string ringJson)
    {
        Assert.Empty(AnomalyDetectionService.ParseRecentEvaluations(ringJson, null, null, 5, NullLogger.Instance));
    }

    [Fact]
    public void ParseRecentEvaluations_DuplicatesAndOverCapacity_KeepsNewestPerFingerprint()
    {
        var ringJson = JsonSerializer.Serialize(new[]
        {
            Evaluated("A", Now.AddHours(-5)),
            Evaluated("A", Now.AddHours(-1)),
            Evaluated("B", Now.AddHours(-4)),
            Evaluated("C", Now.AddHours(-3)),
        });

        var recent = AnomalyDetectionService.ParseRecentEvaluations(ringJson, null, null, 2, NullLogger.Instance);

        Assert.Equal(["C", "A"], recent.Select(e => e.Fingerprint));
        Assert.Equal(Now.AddHours(-1), recent[1].EvaluatedAt);
    }

    [Fact]
    public void ParseRecentEvaluations_OffsetTimestamp_NormalizedToUtc()
    {
        var ringJson = "[{\"Fingerprint\":\"A\",\"EvaluatedAt\":\"2026-08-05T13:00:00+02:00\"}]";

        var entry = Assert.Single(AnomalyDetectionService.ParseRecentEvaluations(ringJson, null, null, 5, NullLogger.Instance));

        Assert.Equal(DateTimeKind.Utc, entry.EvaluatedAt.Kind);
        Assert.Equal(Now.AddHours(-1), entry.EvaluatedAt);
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
            .CheckContainerHealthAsync(CancellationToken.None) ?? throw new InvalidOperationException("check failed to read");

        var after = await CreateService(CreateDockerPlugin(
            new StoppedContainerInfo { Name = "sonarr", StoppedFor = TimeSpan.FromMinutes(5) }))
            .CheckContainerHealthAsync(CancellationToken.None) ?? throw new InvalidOperationException("check failed to read");

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
            .CheckContainerHealthAsync(CancellationToken.None) ?? throw new InvalidOperationException("check failed to read");

        // Same set, reported in a different order and an hour later.
        var second = await CreateService(CreateDockerPlugin(
            new StoppedContainerInfo { Name = "sonarr", StoppedFor = TimeSpan.FromMinutes(65) },
            new StoppedContainerInfo { Name = "plex", StoppedFor = TimeSpan.FromMinutes(65) }))
            .CheckContainerHealthAsync(CancellationToken.None) ?? throw new InvalidOperationException("check failed to read");

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

        var anomalies = await service.CheckContainerHealthAsync(CancellationToken.None) ?? throw new InvalidOperationException("check failed to read");

        Assert.Empty(anomalies);
    }

    [Fact]
    public async Task CheckContainerHealth_RecentlyStopped_ReportedWithNames()
    {
        var docker = CreateDockerPlugin(
            new StoppedContainerInfo { Name = "sonarr", StoppedFor = TimeSpan.FromDays(10) },
            new StoppedContainerInfo { Name = "grafana", StoppedFor = TimeSpan.FromMinutes(5) });
        var service = CreateService(docker);

        var anomalies = await service.CheckContainerHealthAsync(CancellationToken.None) ?? throw new InvalidOperationException("check failed to read");

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

        var anomalies = await service.CheckContainerHealthAsync(CancellationToken.None) ?? throw new InvalidOperationException("check failed to read");

        Assert.Single(anomalies);
    }

    [Fact]
    public async Task CheckContainerHealth_ZeroGrace_ReportsEveryStoppedContainer()
    {
        var docker = CreateDockerPlugin(
            new StoppedContainerInfo { Name = "sonarr", StoppedFor = TimeSpan.FromDays(10) });
        var service = CreateService(docker, stoppedContainerGraceHours: 0);

        var anomalies = await service.CheckContainerHealthAsync(CancellationToken.None) ?? throw new InvalidOperationException("check failed to read");

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

        var anomalies = await service.CheckContainerHealthAsync(CancellationToken.None) ?? throw new InvalidOperationException("check failed to read");

        Assert.Equal(AnomalyDetectionService.AnomalySeverity.Critical, Assert.Single(anomalies).Severity);
    }

    private static readonly AnomalyDetectionService.Anomaly StandingAnomaly = new()
    {
        Type = "Disk",
        Key = "usage",
        Message = "Disk usage at 90.0%",
        Severity = AnomalyDetectionService.AnomalySeverity.Warning,
        Value = 90,
    };

    [Fact]
    public void ResolveCheckResult_SingleFailedRead_KeepsLastResult()
    {
        var service = CreateService();
        service.ResolveCheckResult("disk", [StandingAnomaly]);

        var resolved = service.ResolveCheckResult("disk", null);

        Assert.Same(StandingAnomaly, Assert.Single(resolved));
    }

    [Fact]
    public void ResolveCheckResult_TwoFailedReadsInARow_ReportsNoFindings()
    {
        var service = CreateService();
        service.ResolveCheckResult("disk", [StandingAnomaly]);
        service.ResolveCheckResult("disk", null);

        var resolved = service.ResolveCheckResult("disk", null);

        Assert.Empty(resolved);
    }

    [Fact]
    public void ResolveCheckResult_RecoveryBetweenFailures_ResetsFailureCount()
    {
        var service = CreateService();
        service.ResolveCheckResult("disk", [StandingAnomaly]);
        service.ResolveCheckResult("disk", null);
        service.ResolveCheckResult("disk", [StandingAnomaly]);

        var resolved = service.ResolveCheckResult("disk", null);

        Assert.Single(resolved);
    }

    [Fact]
    public void ResolveCheckResult_FirstReadFails_ReportsNoFindings()
    {
        var service = CreateService();

        Assert.Empty(service.ResolveCheckResult("disk", null));
    }
}
