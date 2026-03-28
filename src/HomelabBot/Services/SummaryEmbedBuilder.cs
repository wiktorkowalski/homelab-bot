using DSharpPlus.Entities;
using HomelabBot.Models;

namespace HomelabBot.Services;

public static class SummaryEmbedBuilder
{
    public static DiscordEmbed Build(DailySummaryData data, string? analysis = null)
    {
        var color = data.HealthScore switch
        {
            > 80 => DiscordColor.Green,
            > 50 => DiscordColor.Yellow,
            _ => DiscordColor.Red
        };

        var builder = new DiscordEmbedBuilder()
            .WithTitle("📊 Daily Homelab Summary")
            .WithColor(color)
            .WithTimestamp(data.GeneratedAt);

        // AI Analysis (if provided)
        if (!string.IsNullOrWhiteSpace(analysis))
        {
            builder.WithDescription(analysis);
        }

        // Alerts
        if (data.Alerts.Count > 0)
        {
            var criticalCount = data.Alerts.Count(a => a.Severity == "critical");
            var warningCount = data.Alerts.Count(a => a.Severity == "warning");
            var alertText = $"🔴 {criticalCount} critical, 🟡 {warningCount} warning";

            if (data.Alerts.Count <= 3)
            {
                alertText += "\n" + string.Join("\n", data.Alerts.Select(a => $"• {a.Name}"));
            }

            builder.AddField("⚠️ Alerts", alertText, true);
        }
        else
        {
            builder.AddField("⚠️ Alerts", "✅ None", true);
        }

        // Containers
        var runningCount = data.Containers.Count(c => c.State == "running");
        var stoppedCount = data.Containers.Count(c => c.State != "running");
        var containerText = $"🟢 {runningCount} running";
        if (stoppedCount > 0)
        {
            containerText += $", 🔴 {stoppedCount} stopped";
            var stoppedNames = data.Containers
                .Where(c => c.State != "running")
                .Take(3)
                .Select(c => c.Name);
            containerText += "\n" + string.Join(", ", stoppedNames);
        }

        builder.AddField("🐳 Containers", containerText, true);

        // Storage
        if (data.Pools.Count > 0)
        {
            var poolText = string.Join("\n", data.Pools.Select(p =>
            {
                var emoji = p.Health == "ONLINE" ? "🟢" : "🔴";
                return $"{emoji} {p.Name}: {p.UsedPercent:F0}%";
            }));
            builder.AddField("💾 Storage", poolText, true);
        }

        // Network/Router
        if (data.Router != null)
        {
            var routerText = $"CPU: {data.Router.CpuPercent:F0}%, Mem: {data.Router.MemoryPercent:F0}%\n" +
                             $"Uptime: {data.Router.Uptime.Days}d {data.Router.Uptime.Hours}h";
            builder.AddField("🌐 Router", routerText, true);
        }

        // Monitoring
        if (data.Monitoring != null)
        {
            var monText = $"✅ {data.Monitoring.UpTargets}/{data.Monitoring.TotalTargets} targets up";
            if (data.Monitoring.DownTargets > 0)
            {
                monText += $"\n❌ {data.Monitoring.DownTargets} down";
            }

            builder.AddField("📊 Monitoring", monText, true);
        }

        builder.WithFooter($"Health Score: {data.HealthScore}/100");

        return builder.Build();
    }
}
