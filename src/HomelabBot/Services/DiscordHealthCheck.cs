using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HomelabBot.Services;

internal sealed class DiscordHealthCheck : IHealthCheck
{
    // Startup and a routine close → resume both flag the gateway down for a few seconds;
    // reporting Unhealthy then only logs an Error line per blip.
    internal static readonly TimeSpan DownGracePeriod = TimeSpan.FromSeconds(60);

    private readonly DiscordBotService _discordBot;

    public DiscordHealthCheck(DiscordBotService discordBot)
    {
        _discordBot = discordBot;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Evaluate(_discordBot.IsReady, _discordBot.DisconnectedFor, DownGracePeriod));
    }

    internal static HealthCheckResult Evaluate(bool isReady, TimeSpan? disconnectedFor, TimeSpan gracePeriod)
    {
        if (isReady)
        {
            return HealthCheckResult.Healthy("Discord gateway connected");
        }

        // No down timestamp yet means ExecuteAsync has not started connecting, which only happens right after startup
        if (disconnectedFor is not { } duration)
        {
            return HealthCheckResult.Healthy("Discord gateway connecting");
        }

        var description = $"Discord gateway down for {duration.TotalSeconds:F0}s";
        return duration < gracePeriod
            ? HealthCheckResult.Healthy($"{description}, within grace period")
            : HealthCheckResult.Unhealthy(description);
    }
}
