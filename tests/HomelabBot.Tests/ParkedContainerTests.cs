using HomelabBot.Models;
using HomelabBot.Services;

namespace HomelabBot.Tests;

public class ParkedContainerTests
{
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
    public void MarkParked_FlagsOnlyContainersPastGrace()
    {
        List<ContainerStatus> containers =
        [
            new() { Name = "app", State = "running" },
            new() { Name = "jellyfin", State = "exited", Health = "Exited (0) 3 weeks ago" },
            new() { Name = "db", State = "exited" },
            new() { Name = "unknown", State = "exited" },
            new() { Name = "missing", State = "dead" },
        ];
        List<StoppedContainerInfo> stopped =
        [
            new() { Name = "jellyfin", StoppedFor = TimeSpan.FromDays(21) },
            new() { Name = "db", StoppedFor = TimeSpan.FromMinutes(5) },
            new() { Name = "unknown", StoppedFor = null },
        ];

        var result = SummaryDataAggregator.MarkParked(containers, stopped, graceHours: 24);

        Assert.Equal(["jellyfin"], result.Where(c => c.IsParked).Select(c => c.Name));
        var jellyfin = result.Single(c => c.Name == "jellyfin");
        Assert.Equal("exited", jellyfin.State);
        Assert.Equal("Exited (0) 3 weeks ago", jellyfin.Health);
        Assert.Equal(containers.Select(c => c.Name), result.Select(c => c.Name));
    }

    [Fact]
    public void MarkParked_NoGrace_FlagsNothing()
    {
        List<ContainerStatus> containers = [new() { Name = "jellyfin", State = "exited" }];
        List<StoppedContainerInfo> stopped = [new() { Name = "jellyfin", StoppedFor = TimeSpan.FromDays(21) }];

        var result = SummaryDataAggregator.MarkParked(containers, stopped, graceHours: 0);

        Assert.False(result.Single().IsParked);
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
