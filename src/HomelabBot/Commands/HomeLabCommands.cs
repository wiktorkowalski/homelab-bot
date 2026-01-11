using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using HomelabBot.Plugins;
using HomelabBot.Services;

namespace HomelabBot.Commands;

public class HomeLabCommands : ApplicationCommandModule
{
    private readonly DockerPlugin _dockerPlugin;
    private readonly KnowledgePlugin _knowledgePlugin;
    private readonly KernelService _kernelService;
    private readonly ILogger<HomeLabCommands> _logger;

    public HomeLabCommands(
        DockerPlugin dockerPlugin,
        KnowledgePlugin knowledgePlugin,
        KernelService kernelService,
        ILogger<HomeLabCommands> logger)
    {
        _dockerPlugin = dockerPlugin;
        _knowledgePlugin = knowledgePlugin;
        _kernelService = kernelService;
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
                    3. Check for any WoL-capable devices you find
                    4. Remember gateway IPs, interface names, and network topology
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
                    1. Check Prometheus targets and their status
                    2. List Grafana dashboards available
                    3. Note which services are being scraped
                    4. Remember target count, scrape endpoints, and dashboard names
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
                    1. List available entities/devices
                    2. Note entity types (lights, switches, sensors, etc.)
                    3. Group entities by room/area if visible
                    4. Remember entity IDs and their friendly names for alias storage
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
                _ => """
                    Do a comprehensive homelab discovery:
                    1. List Docker containers (count running/stopped)
                    2. Check Prometheus targets status
                    3. List Loki labels available
                    4. Check for any active alerts
                    5. Get TrueNAS pool health summary
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
}
