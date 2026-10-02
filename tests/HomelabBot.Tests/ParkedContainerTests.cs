using HomelabBot.Models;
using HomelabBot.Services;

namespace HomelabBot.Tests;

public class ParkedContainerTests
{
    private static readonly IReadOnlySet<string> NoneConfirmed = new HashSet<string>(StringComparer.Ordinal);

    [Theory]
    [InlineData(null, 24, false)]
    [InlineData(23.9, 24, false)]
    [InlineData(24.0, 24, true)]
    [InlineData(500.0, 24, true)]
    [InlineData(500.0, 0, false)]
    [InlineData(500.0, -1, false)]
    public void IsParked_AppliesGraceWindow(double? stoppedHours, int graceHours, bool expected)
    {
        TimeSpan? stoppedFor = stoppedHours is { } h ? TimeSpan.FromHours(h) : null;

        Assert.Equal(expected, StoppedContainerGrace.IsParked(stoppedFor, graceHours));
    }

    [Fact]
    public void Resolve_ParksOnlyContainersPastGrace()
    {
        List<StoppedContainerInfo> stopped =
        [
            new() { Name = "jellyfin", StoppedFor = TimeSpan.FromDays(21) },
            new() { Name = "db", StoppedFor = TimeSpan.FromMinutes(5) },
            new() { Name = "unknown", StoppedFor = null },
        ];

        var result = StoppedContainerGrace.Resolve(stopped, NoneConfirmed, graceHours: 24);

        Assert.Equal(["jellyfin"], result.Parked);
        Assert.Equal(["jellyfin"], result.Confirmed);
    }

    [Fact]
    public void Resolve_FailedReadAfterConfirmedParked_BridgesOneCycleOnly()
    {
        List<StoppedContainerInfo> healthy = [new() { Name = "jellyfin", StoppedFor = TimeSpan.FromDays(21) }];
        List<StoppedContainerInfo> failedRead = [new() { Name = "jellyfin", StoppedFor = null }];

        var first = StoppedContainerGrace.Resolve(healthy, NoneConfirmed, graceHours: 24);
        var second = StoppedContainerGrace.Resolve(failedRead, first.Confirmed, graceHours: 24);
        var third = StoppedContainerGrace.Resolve(failedRead, second.Confirmed, graceHours: 24);

        Assert.Contains("jellyfin", second.Parked);
        Assert.Empty(second.Confirmed);
        Assert.Empty(third.Parked);
    }

    [Fact]
    public void Resolve_FailedReadWithoutPriorParked_CountsAsDown()
    {
        List<StoppedContainerInfo> failedRead = [new() { Name = "db", StoppedFor = null }];

        var result = StoppedContainerGrace.Resolve(failedRead, NoneConfirmed, graceHours: 24);

        Assert.Empty(result.Parked);
    }

    [Fact]
    public void Resolve_NoGrace_ParksNothingEvenWhenPreviouslyConfirmed()
    {
        List<StoppedContainerInfo> stopped =
        [
            new() { Name = "jellyfin", StoppedFor = TimeSpan.FromDays(21) },
            new() { Name = "sonarr", StoppedFor = null },
        ];
        var confirmed = new HashSet<string>(StringComparer.Ordinal) { "sonarr" };

        var result = StoppedContainerGrace.Resolve(stopped, confirmed, graceHours: 0);

        Assert.Empty(result.Parked);
    }

    [Fact]
    public void MarkParked_FlagsNamedContainersAndKeepsOtherFields()
    {
        List<ContainerStatus> containers =
        [
            new() { Name = "app", State = "running" },
            new() { Name = "jellyfin", State = "exited", Health = "Exited (0) 3 weeks ago" },
            new() { Name = "db", State = "exited" },
        ];
        var parked = new HashSet<string>(StringComparer.Ordinal) { "jellyfin" };

        var result = SummaryDataAggregator.MarkParked(containers, parked);

        Assert.Equal(["jellyfin"], result.Where(c => c.IsParked).Select(c => c.Name));
        Assert.Equal(
            new ContainerStatus { Name = "jellyfin", State = "exited", Health = "Exited (0) 3 weeks ago", IsParked = true },
            result.Single(c => c.Name == "jellyfin"));
        Assert.Equal(containers.Select(c => c.Name), result.Select(c => c.Name));
    }

    [Theory]
    [InlineData("running", false, false)]
    [InlineData("exited", false, true)]
    [InlineData("exited", true, false)]
    [InlineData("restarting", false, true)]
    public void CountsAsDown_SkipsRunningAndParked(string state, bool isParked, bool expected)
    {
        var container = new ContainerStatus { Name = "c", State = state, IsParked = isParked };

        Assert.Equal(expected, container.CountsAsDown);
    }
}
