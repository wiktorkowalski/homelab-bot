using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using HomelabBot.Models;
using HomelabBot.Plugins;
using HomelabBot.Services;

namespace HomelabBot.Commands;

public class HomeLabCommands : ApplicationCommandModule
{
    private readonly DockerPlugin _dockerPlugin;
    private readonly KnowledgePlugin _knowledgePlugin;
    private readonly KernelService _kernelService;
    private readonly SummaryDataAggregator _summaryAggregator;
    private readonly ILogger<HomeLabCommands> _logger;

    public HomeLabCommands(
        DockerPlugin dockerPlugin,
        KnowledgePlugin knowledgePlugin,
        KernelService kernelService,
        SummaryDataAggregator summaryAggregator,
        ILogger<HomeLabCommands> logger)
    {
        _dockerPlugin = dockerPlugin;
        _knowledgePlugin = knowledgePlugin;
        _kernelService = kernelService;
        _summaryAggregator = summaryAggregator;
        _logger = logger;
    }

    [SlashCommand("status", "Get a quick overview of system status")]
    public async Task StatusCommand(InteractionContext ctx)
    {
        await ctx.DeferAsync();

        try
        {
            _logger.LogDebug("Status command invoked by {User}", ctx.User.Username);

            var containers = await _dockerPlugin.ListContainers();

            var embed = new DiscordEmbedBuilder()
                .WithTitle("System Status")
                .WithDescription(containers)
                .WithColor(DiscordColor.Green)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .Build();

            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(embed));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in status command");
            await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                .WithContent($"Error getting status: {ex.Message}"));
        }
    }

    [SlashCommand("containers", "List all Docker containers")]
    public async Task ContainersCommand(InteractionContext ctx)
    {
        await ctx.DeferAsync();

        try
        {
            _logger.LogDebug("Containers command invoked by {User}", ctx.User.Username);

            var containers = await _dockerPlugin.ListContainers();

            var embed = new DiscordEmbedBuilder()
                .WithTitle("Docker Containers")
                .WithDescription(containers)
                .WithColor(DiscordColor.Blurple)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .Build();

            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(embed));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in containers command");
            await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                .WithContent($"Error listing containers: {ex.Message}"));
        }
    }

    [SlashCommand("container", "Get details about a specific container")]
    public async Task ContainerCommand(
        InteractionContext ctx,
        [Option("name", "Container name or ID")] string containerName)
    {
        await ctx.DeferAsync();

        try
        {
            _logger.LogDebug("Container command invoked for {Container} by {User}",
                containerName, ctx.User.Username);

            var status = await _dockerPlugin.GetContainerStatus(containerName);

            var embed = new DiscordEmbedBuilder()
                .WithTitle("Container Details")
                .WithDescription(status)
                .WithColor(DiscordColor.Blurple)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .Build();

            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(embed));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in container command");
            await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                .WithContent($"Error getting container details: {ex.Message}"));
        }
    }

    [SlashCommand("logs", "Get container logs")]
    public async Task LogsCommand(
        InteractionContext ctx,
        [Option("name", "Container name or ID")] string containerName,
        [Option("lines", "Number of lines (default 50)")] long lines = 50)
    {
        await ctx.DeferAsync();

        try
        {
            _logger.LogDebug("Logs command invoked for {Container} by {User}",
                containerName, ctx.User.Username);

            var logs = await _dockerPlugin.GetContainerLogs(containerName, (int)lines);

            // Discord has 2000 char limit, send as file if too long
            if (logs.Length > 1900)
            {
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(logs));
                await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                    .WithContent($"Logs for **{containerName}** (last {lines} lines):")
                    .AddFile($"{containerName}-logs.txt", stream));
            }
            else
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent(logs));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in logs command");
            await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                .WithContent($"Error getting logs: {ex.Message}"));
        }
    }

    [SlashCommand("discover", "Learn about the current homelab state")]
    public async Task DiscoverCommand(
        InteractionContext ctx,
        [Option("scope", "What to explore")]
        [Choice("All", "all")]
        [Choice("Docker", "docker")]
        [Choice("Network", "network")]
        [Choice("Storage", "storage")]
        [Choice("Monitoring", "monitoring")]
        [Choice("Loki", "loki")]
        [Choice("Home Assistant", "homeassistant")]
        [Choice("Alerts", "alerts")]
        [Choice("Grafana", "grafana")]
        string scope = "all")
    {
        await ctx.DeferAsync();

        try
        {
            _logger.LogInformation("Discover command invoked by {User} with scope {Scope}",
                ctx.User.Username, scope);

            var prompt = scope switch
            {
                "docker" => """
                    Explore Docker containers in detail:
                    1. List all containers with their states
                    2. Note which ones are running vs stopped
                    3. Group them by purpose if you can tell (monitoring, media, infra, etc.)
                    4. Remember container count and any notable configs
                    Use RememberFact for each discovery.
                    """,
                "network" => """
                    Explore network infrastructure:
                    1. Check MikroTik router status and interfaces
                    2. Note interface traffic levels and any issues
                    3. List DHCP leases to see connected devices
                    4. List WiFi clients and their signal strength
                    5. Remember gateway IPs, interface names, device counts, and network topology
                    Use RememberFact for each discovery.
                    """,
                "storage" => """
                    Explore storage systems:
                    1. Check TrueNAS pools and their health
                    2. Note pool usage percentages and available space
                    3. List datasets if available
                    4. Remember pool names, capacities, and health status
                    Use RememberFact for each discovery.
                    """,
                "monitoring" => """
                    Explore monitoring stack:
                    1. Get Prometheus targets to see all monitored services
                    2. Note which targets are up vs down
                    3. Get node stats for host resource usage
                    4. Remember target count, health status, and key metrics
                    Use RememberFact for each discovery.
                    """,
                "loki" => """
                    Explore Loki logging setup:
                    1. List all available labels
                    2. For key labels (container_name, compose_service, job), list their values
                    3. Note how logs are labeled in this setup
                    4. Remember the label structure for future log queries
                    Use RememberFact for each discovery.
                    """,
                "homeassistant" => """
                    Explore Home Assistant:
                    1. List available entities by domain (light, switch, sensor, etc.)
                    2. List available automations
                    3. Note entity types and their current states
                    4. Group entities by room/area if visible
                    5. Remember entity IDs, friendly names, and automation names for alias storage
                    Use RememberFact for each discovery.
                    """,
                "alerts" => """
                    Explore alerting setup:
                    1. Check current Alertmanager alerts (firing and resolved)
                    2. List alert rules if accessible
                    3. Note alert severity levels and grouping
                    4. Remember common alert patterns and their meaning
                    Use RememberFact for each discovery.
                    """,
                "grafana" => """
                    Explore Grafana dashboards:
                    1. List all available dashboards with their UIDs
                    2. Check Grafana health status
                    3. Group dashboards by folder
                    4. Remember dashboard names and UIDs for quick access
                    Use RememberFact for each discovery.
                    """,
                _ => """
                    Do a comprehensive homelab discovery:
                    1. List Docker containers (count running/stopped)
                    2. Check Prometheus targets status
                    3. List Grafana dashboards available
                    4. List Loki labels available
                    5. Check for any active alerts
                    6. Get TrueNAS pool health summary
                    For each area, use RememberFact to save key findings.
                    Provide a summary of the homelab state.
                    """
            };

            var response = await _kernelService.ProcessMessageAsync(
                ctx.Channel.Id,
                prompt);

            if (response.Length > 1900)
            {
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(response));
                await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                    .WithContent($"Discovery ({scope}) complete. See attached:")
                    .AddFile("discovery.txt", stream));
            }
            else
            {
                var embed = new DiscordEmbedBuilder()
                    .WithTitle($"Discovery: {scope}")
                    .WithDescription(response)
                    .WithColor(DiscordColor.Azure)
                    .WithTimestamp(DateTimeOffset.UtcNow)
                    .Build();

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(embed));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in discover command");
            await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                .WithContent($"Error during discovery: {ex.Message}"));
        }
    }

    [SlashCommand("knowledge", "Show what I know about the homelab")]
    public async Task KnowledgeCommand(
        InteractionContext ctx,
        [Option("topic", "Filter by topic (optional)")] string? topic = null)
    {
        await ctx.DeferAsync();

        try
        {
            _logger.LogDebug("Knowledge command invoked by {User}", ctx.User.Username);

            var knowledge = await _knowledgePlugin.RecallKnowledge(topic);

            var embed = new DiscordEmbedBuilder()
                .WithTitle(topic != null ? $"Knowledge: {topic}" : "All Knowledge")
                .WithDescription(knowledge)
                .WithColor(DiscordColor.Gold)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .Build();

            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(embed));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in knowledge command");
            await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                .WithContent($"Error getting knowledge: {ex.Message}"));
        }
    }

    [SlashCommand("summary", "Get a daily summary of homelab status")]
    public async Task SummaryCommand(InteractionContext ctx)
    {
        await ctx.DeferAsync();

        try
        {
            _logger.LogDebug("Summary command invoked by {User}", ctx.User.Username);

            var data = await _summaryAggregator.AggregateAsync();
            var analysis = await GenerateSummaryAnalysisAsync(data);
            var embed = SummaryEmbedBuilder.Build(data, analysis);

            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(embed));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in summary command");
            await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                .WithContent($"Error generating summary: {ex.Message}"));
        }
    }

    [SlashCommand("roast", "Roast your homelab")]
    public async Task RoastCommand(InteractionContext ctx)
    {
        await ctx.DeferAsync();

        try
        {
            _logger.LogDebug("Roast command invoked by {User}", ctx.User.Username);

            var prompt = """
                Look around the homelab using your tools. Find something embarrassing,
                inefficient, or roastable (old containers, high resource usage, weird configs,
                too many alerts, neglected services, etc.).

                Then write a short, witty roast (2-3 sentences max) about what you found.
                Be playfully mean but not too harsh.
                """;

            var response = await _kernelService.ProcessMessageAsync(
                ctx.Channel.Id, prompt, ctx.User.Id, TraceType.Chat);

            await ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in roast command");
            await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                .WithContent($"Error roasting homelab: {ex.Message}"));
        }
    }

    [SlashCommand("randomfact", "Get a random fact about your homelab")]
    public async Task RandomFactCommand(InteractionContext ctx)
    {
        await ctx.DeferAsync();

        try
        {
            _logger.LogDebug("RandomFact command invoked by {User}", ctx.User.Username);

            var prompt = """
                Explore the homelab using your tools and discover something interesting.
                Could be: total containers running, uptime stats, storage used,
                network throughput, alert history, service dependencies, etc.

                Present it as a single interesting fact (1-2 sentences).
                Start with "Did you know..." or similar.
                """;

            var response = await _kernelService.ProcessMessageAsync(
                ctx.Channel.Id, prompt, ctx.User.Id, TraceType.Chat);

            await ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in randomfact command");
            await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                .WithContent($"Error getting random fact: {ex.Message}"));
        }
    }

    private async Task<string?> GenerateSummaryAnalysisAsync(DailySummaryData data)
    {
        try
        {
            var prompt = $"""
                Analyze this homelab status and provide a brief 2-3 sentence summary.
                Focus on anything noteworthy: issues, warnings, recommendations.
                If everything looks good, say so briefly.
                Be concise and direct. No greetings or fluff.

                Data:
                - Health Score: {data.HealthScore}/100
                - Alerts (last 24h): {data.Alerts.Count} ({data.Alerts.Count(a => a.Severity == "critical")} critical, {data.Alerts.Count(a => a.Severity == "warning")} warning)
                {(data.Alerts.Count > 0 ? "  Names: " + string.Join(", ", data.Alerts.Take(5).Select(a => a.Name)) : "")}
                - Containers: {data.Containers.Count(c => c.State == "running")} running, {data.Containers.Count(c => c.State != "running")} stopped
                {(data.Containers.Any(c => c.State != "running") ? "  Stopped: " + string.Join(", ", data.Containers.Where(c => c.State != "running").Take(5).Select(c => c.Name)) : "")}
                - Storage Pools: {string.Join(", ", data.Pools.Select(p => $"{p.Name}: {p.Health} ({p.UsedPercent:F0}%)"))}
                - Router: {(data.Router != null ? $"CPU {data.Router.CpuPercent:F0}%, Mem {data.Router.MemoryPercent:F0}%, Up {data.Router.Uptime.Days}d" : "unavailable")}
                - Monitoring: {(data.Monitoring != null ? $"{data.Monitoring.UpTargets}/{data.Monitoring.TotalTargets} targets up" : "unavailable")}
                """;

            return await _kernelService.ProcessMessageAsync(threadId: 0, userMessage: prompt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate AI analysis");
            return null;
        }
    }
}
