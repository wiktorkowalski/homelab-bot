using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using HomelabBot.Plugins;

namespace HomelabBot.Commands;

public class HomeLabCommands : ApplicationCommandModule
{
    private readonly DockerPlugin _dockerPlugin;
    private readonly ILogger<HomeLabCommands> _logger;

    public HomeLabCommands(DockerPlugin dockerPlugin, ILogger<HomeLabCommands> logger)
    {
        _dockerPlugin = dockerPlugin;
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
}
