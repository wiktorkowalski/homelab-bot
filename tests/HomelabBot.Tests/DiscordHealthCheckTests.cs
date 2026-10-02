using HomelabBot.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HomelabBot.Tests;

public class DiscordHealthCheckTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);

    [Fact]
    public void Evaluate_Ready_ReturnsHealthy()
    {
        var result = DiscordHealthCheck.Evaluate(isReady: true, disconnectedFor: null, Grace);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void Evaluate_NotStartedConnecting_ReturnsHealthy()
    {
        var result = DiscordHealthCheck.Evaluate(isReady: false, disconnectedFor: null, Grace);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(59)]
    public void Evaluate_DownWithinGrace_ReturnsHealthy(int seconds)
    {
        var result = DiscordHealthCheck.Evaluate(isReady: false, TimeSpan.FromSeconds(seconds), Grace);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("grace", result.Description);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(300)]
    public void Evaluate_DownPastGrace_ReturnsUnhealthy(int seconds)
    {
        var result = DiscordHealthCheck.Evaluate(isReady: false, TimeSpan.FromSeconds(seconds), Grace);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal($"Discord gateway down for {seconds}s", result.Description);
    }
}
