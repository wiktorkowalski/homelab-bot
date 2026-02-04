using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using DSharpPlus.SlashCommands;
using HomelabBot.Commands;
using HomelabBot.Configuration;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public sealed class DiscordBotService : BackgroundService
{
    private readonly ILogger<DiscordBotService> _logger;
    private readonly BotConfiguration _config;
    private readonly KernelService _kernelService;
    private readonly ConversationService _conversationService;
    private readonly ConfirmationService _confirmationService;
    private readonly IServiceProvider _serviceProvider;
    private DiscordClient? _client;
    private SlashCommandsExtension? _slashCommands;
    private int _reconnectAttempts;
    private const int MaxReconnectAttempts = 10;

    public DiscordBotService(
        IOptions<BotConfiguration> config,
        ILogger<DiscordBotService> logger,
        KernelService kernelService,
        ConversationService conversationService,
        ConfirmationService confirmationService,
        IServiceProvider serviceProvider)
    {
        _config = config.Value;
        _logger = logger;
        _kernelService = kernelService;
        _conversationService = conversationService;
        _confirmationService = confirmationService;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting Discord bot service...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndRunAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _reconnectAttempts++;
                var delay = GetBackoffDelay(_reconnectAttempts);

                _logger.LogError(ex, "Discord connection failed (attempt {Attempt}/{Max}). Retrying in {Delay}s...",
                    _reconnectAttempts, MaxReconnectAttempts, delay.TotalSeconds);

                if (_reconnectAttempts >= MaxReconnectAttempts)
                {
                    _logger.LogCritical("Max reconnect attempts reached. Giving up.");
                    throw;
                }

                await Task.Delay(delay, stoppingToken);
            }
        }
    }

    private async Task ConnectAndRunAsync(CancellationToken stoppingToken)
    {
        var discordConfig = new DiscordConfiguration
        {
            Token = _config.DiscordToken,
            TokenType = TokenType.Bot,
            Intents = DiscordIntents.AllUnprivileged |
                      DiscordIntents.MessageContents |
                      DiscordIntents.GuildMessages,
            AutoReconnect = true
        };

        _client = new DiscordClient(discordConfig);

        // Register slash commands
        _slashCommands = _client.UseSlashCommands(new SlashCommandsConfiguration
        {
            Services = _serviceProvider
        });
        _slashCommands.RegisterCommands<HomeLabCommands>();

        _client.Ready += OnReady;
        _client.MessageCreated += OnMessageCreated;
        _client.ComponentInteractionCreated += OnComponentInteraction;
        _client.SocketErrored += OnSocketError;
        _client.Resumed += OnResumed;

        await _client.ConnectAsync();
        _reconnectAttempts = 0; // Reset on successful connect

        // Keep running until cancellation
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private Task OnReady(DiscordClient client, ReadyEventArgs e)
    {
        _logger.LogInformation("Discord bot connected as {Username}#{Discriminator}",
            client.CurrentUser.Username, client.CurrentUser.Discriminator);
        return Task.CompletedTask;
    }

    private Task OnResumed(DiscordClient client, ReadyEventArgs e)
    {
        _logger.LogInformation("Discord connection resumed");
        return Task.CompletedTask;
    }

    private Task OnSocketError(DiscordClient client, SocketErrorEventArgs e)
    {
        _logger.LogWarning(e.Exception, "Discord socket error");
        return Task.CompletedTask;
    }

    private async Task OnComponentInteraction(DiscordClient client, ComponentInteractionCreateEventArgs e)
    {
        try
        {
            await _confirmationService.HandleInteractionAsync(e);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling component interaction");
        }
    }

    private async Task OnMessageCreated(DiscordClient client, MessageCreateEventArgs e)
    {
        // Ignore bots and system messages
        if (e.Author.IsBot || e.Message.MessageType != MessageType.Default)
            return;

        // Check if we should respond
        var isMentioned = e.Message.MentionedUsers.Any(u => u.Id == client.CurrentUser.Id);
        var isDedicatedChannel = _config.DedicatedChannels.Contains(e.Channel.Id);
        var isThread = e.Channel.IsThread;

        // Respond in: mentions anywhere, dedicated channels, or threads we're already in
        if (!isMentioned && !isDedicatedChannel && !isThread)
            return;

        // Use thread ID for conversation, or channel ID if not in thread
        var conversationId = e.Channel.IsThread ? e.Channel.Id : e.Message.Id;

        _logger.LogDebug("Processing message from {Author} in {Channel}",
            e.Author.Username, e.Channel.Name);

        try
        {
            // Create/get thread if not already in one
            DiscordChannel responseChannel = e.Channel;
            DiscordThreadChannel? newThread = null;
            if (!e.Channel.IsThread && !isDedicatedChannel)
            {
                // Create a thread for the conversation (temp name, will update after)
                newThread = await e.Message.CreateThreadAsync(
                    "...",
                    AutoArchiveDuration.Hour);
                responseChannel = newThread;
                conversationId = newThread.Id;
            }

            // Show typing indicator
            await responseChannel.TriggerTypingAsync();

            // Strip bot mention from message
            var content = e.Message.Content;
            foreach (var mention in e.Message.MentionedUsers.Where(u => u.Id == client.CurrentUser.Id))
            {
                content = content.Replace($"<@{mention.Id}>", "").Replace($"<@!{mention.Id}>", "");
            }
            content = content.Trim();

            if (string.IsNullOrWhiteSpace(content))
            {
                await responseChannel.SendMessageAsync("Hey, what do you need?");
                return;
            }

            // Process with Semantic Kernel
            var response = await _kernelService.ProcessMessageAsync(conversationId, content, e.Author.Id);

            // Send response (split if too long)
            await SendResponseAsync(responseChannel, response);

            // Generate and set thread title for new threads
            if (newThread != null)
            {
                var title = await _kernelService.GenerateThreadTitleAsync(content, newThread.Id, e.Author.Id);
                await newThread.ModifyAsync(t => t.Name = title);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling message");
            await e.Channel.SendMessageAsync($"Something went wrong: {ex.Message}");
        }
    }

    private static async Task SendResponseAsync(DiscordChannel channel, string response)
    {
        const int maxLength = 2000;

        if (response.Length <= maxLength)
        {
            await channel.SendMessageAsync(response);
            return;
        }

        // Split into chunks
        var chunks = SplitMessage(response, maxLength);
        foreach (var chunk in chunks)
        {
            await channel.SendMessageAsync(chunk);
            await Task.Delay(100); // Small delay between messages
        }
    }

    private static List<string> SplitMessage(string message, int maxLength)
    {
        var chunks = new List<string>();
        var remaining = message;

        while (remaining.Length > maxLength)
        {
            // Try to split at a newline
            var splitIndex = remaining.LastIndexOf('\n', maxLength - 1);
            if (splitIndex <= 0)
            {
                // No newline found, split at space
                splitIndex = remaining.LastIndexOf(' ', maxLength - 1);
            }
            if (splitIndex <= 0)
            {
                // No space found, hard split
                splitIndex = maxLength;
            }

            chunks.Add(remaining[..splitIndex]);
            remaining = remaining[splitIndex..].TrimStart();
        }

        if (!string.IsNullOrEmpty(remaining))
        {
            chunks.Add(remaining);
        }

        return chunks;
    }

    private static TimeSpan GetBackoffDelay(int attempt)
    {
        // Exponential backoff: 1s, 2s, 4s, 8s, 16s, 32s, 60s max
        var seconds = Math.Min(Math.Pow(2, attempt - 1), 60);
        return TimeSpan.FromSeconds(seconds);
    }

    public async Task SendDmAsync(ulong userId, DiscordEmbed embed)
    {
        if (_client == null)
        {
            _logger.LogWarning("Cannot send DM: Discord client not connected");
            return;
        }

        try
        {
            // Find user in any guild the bot is in
            foreach (var guild in _client.Guilds.Values)
            {
                try
                {
                    var member = await guild.GetMemberAsync(userId);
                    var dm = await member.CreateDmChannelAsync();
                    await dm.SendMessageAsync(embed: embed);
                    _logger.LogDebug("Sent DM to user {UserId}", userId);
                    return;
                }
                catch
                {
                    // User not in this guild, try next
                }
            }
            _logger.LogWarning("Could not find user {UserId} in any guild", userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send DM to user {UserId}", userId);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Discord bot service...");

        if (_client != null)
        {
            await _client.DisconnectAsync();
            _client.Dispose();
        }

        await base.StopAsync(cancellationToken);
    }
}
