using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HomelabBot.Services;

internal sealed class DiscordHealthCheck : IHealthCheck
{
    private readonly DiscordBotService _discordBot;

    public DiscordHealthCheck(DiscordBotService discordBot)
    {
        _discordBot = discordBot;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_discordBot.IsReady)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Discord gateway connected"));
        }

        var description = _discordBot.DisconnectedFor is { } duration
            ? $"Discord gateway down for {duration.TotalSeconds:F0}s"
            : "Discord gateway not connected yet";

        return Task.FromResult(HealthCheckResult.Unhealthy(description));
    }
}
