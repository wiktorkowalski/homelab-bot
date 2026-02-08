using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using HomelabBot.Services.Voice;

namespace HomelabBot.Commands;

public class VoiceCommands : ApplicationCommandModule
{
    private readonly DiscordVoiceService _voiceService;
    private readonly ILogger<VoiceCommands> _logger;

    public VoiceCommands(
        DiscordVoiceService voiceService,
        ILogger<VoiceCommands> logger)
    {
        _voiceService = voiceService;
        _logger = logger;
    }

    [SlashCommand("voice-join", "Join a voice channel")]
    public async Task VoiceJoinAsync(
        InteractionContext ctx,
        [Option("channel", "Voice channel to join (defaults to yours)")] DiscordChannel? channel = null)
    {
        await ctx.DeferAsync();

        try
        {
            var targetChannel = channel ?? ctx.Member?.VoiceState?.Channel;
            if (targetChannel == null)
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                    .WithContent("You're not in a voice channel and didn't specify one."));
                return;
            }

            if (targetChannel.Type != DSharpPlus.ChannelType.Voice &&
                targetChannel.Type != DSharpPlus.ChannelType.Stage)
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                    .WithContent("That's not a voice channel."));
                return;
            }

            await _voiceService.JoinChannelAsync(targetChannel);

            await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                .WithContent($"Joined **{targetChannel.Name}**. Listening for voice input."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error joining voice channel");
            await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                .WithContent($"Failed to join: {ex.Message}"));
        }
    }

    [SlashCommand("voice-leave", "Leave the current voice channel")]
    public async Task VoiceLeaveAsync(InteractionContext ctx)
    {
        await ctx.DeferAsync();

        try
        {
            await _voiceService.LeaveChannelAsync();
            await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                .WithContent("Disconnected from voice."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error leaving voice channel");
            await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                .WithContent($"Failed to leave: {ex.Message}"));
        }
    }

    [SlashCommand("voice-status", "Show voice connection status")]
    public async Task VoiceStatusAsync(InteractionContext ctx)
    {
        await ctx.DeferAsync();

        try
        {
            var status = _voiceService.GetStatus();

            var embed = new DiscordEmbedBuilder()
                .WithTitle("Voice Status")
                .WithColor(status.IsConnected ? new DiscordColor("#22C55E") : new DiscordColor("#6B7280"))
                .AddField("Connected", status.IsConnected ? "Yes" : "No", true)
                .AddField("Channel", status.ChannelName ?? "None", true)
                .AddField("Active Users", status.ActiveUsers.ToString(), true);

            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(embed));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting voice status");
            await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                .WithContent($"Error: {ex.Message}"));
        }
    }
}
