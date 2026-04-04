using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using DSharpPlus.SlashCommands;
using HomelabBot.Commands;
using HomelabBot.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public sealed class DiscordBotService : BackgroundService
{
    private readonly ILogger<DiscordBotService> _logger;
    private readonly BotConfiguration _config;
    private readonly KernelService _kernelService;
    private readonly ConversationService _conversationService;
    private readonly ConfirmationService _confirmationService;
    private readonly InvestigationService _memoryService;
    private readonly AutoRemediationService _autoRemediationService;
    private readonly IServiceProvider _serviceProvider;
    private readonly Lazy<SmartNotificationService> _smartNotification;
    private DiscordClient? _client;
    private SlashCommandsExtension? _slashCommands;
    private int _reconnectAttempts;
    private const int MaxReconnectAttempts = 10;
    private readonly TaskCompletionSource _readyTcs = new();

    public DiscordBotService(
        IOptions<BotConfiguration> config,
        ILogger<DiscordBotService> logger,
        KernelService kernelService,
        ConversationService conversationService,
        ConfirmationService confirmationService,
        InvestigationService memoryService,
        AutoRemediationService autoRemediationService,
        IServiceProvider serviceProvider)
    {
        _config = config.Value;
        _logger = logger;
        _kernelService = kernelService;
        _conversationService = conversationService;
        _confirmationService = confirmationService;
        _memoryService = memoryService;
        _autoRemediationService = autoRemediationService;
        _serviceProvider = serviceProvider;
        _smartNotification = new Lazy<SmartNotificationService>(
            () => _serviceProvider.GetRequiredService<SmartNotificationService>());
    }

    private SmartNotificationService SmartNotification => _smartNotification.Value;

    public Task WaitForReadyAsync(CancellationToken ct = default)
    {
        return _readyTcs.Task.WaitAsync(ct);
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
                      DiscordIntents.GuildMessages |
                      DiscordIntents.DirectMessages,
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
        _readyTcs.TrySetResult();
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
            var customId = e.Interaction.Data.CustomId;

            if (customId.StartsWith("pattern_helpful_") || customId.StartsWith("pattern_notrelevant_"))
            {
                await HandlePatternFeedbackAsync(e);
                return;
            }

            if (customId.StartsWith(SmartNotificationService.ButtonPrefixNormal)
                || customId.StartsWith(SmartNotificationService.ButtonPrefixInvestigate))
            {
                await HandleNotificationFeedbackAsync(e);
                return;
            }

            if (customId.StartsWith("remediation_approve_") || customId.StartsWith("remediation_reject_")
                || customId.StartsWith("remediation_ok_") || customId.StartsWith("remediation_fail_"))
            {
                await HandleRemediationResponseAsync(e);
                return;
            }

            await _confirmationService.HandleInteractionAsync(e);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling component interaction");
        }
    }

    private async Task HandlePatternFeedbackAsync(ComponentInteractionCreateEventArgs e)
    {
        var customId = e.Interaction.Data.CustomId;
        var isHelpful = customId.StartsWith("pattern_helpful_");
        var prefix = isHelpful ? "pattern_helpful_" : "pattern_notrelevant_";

        var idsString = customId[prefix.Length..];
        var patternIds = idsString.Split(',')
            .Select(s => int.TryParse(s, out var id) ? id : (int?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();

        if (patternIds.Count == 0)
        {
            await e.Interaction.CreateResponseAsync(InteractionResponseType.DeferredMessageUpdate);
            return;
        }

        foreach (var patternId in patternIds)
        {
            try
            {
                await _memoryService.RecordRunbookFeedbackAsync(patternId, isHelpful);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record pattern feedback for {PatternId}", patternId);
            }
        }

        var emoji = isHelpful ? "👍" : "👎";
        var text = isHelpful ? "Thanks! Marked as helpful." : "Noted, marked as not relevant.";

        await e.Interaction.CreateResponseAsync(
            InteractionResponseType.ChannelMessageWithSource,
            new DiscordInteractionResponseBuilder()
                .WithContent($"{emoji} {text}")
                .AsEphemeral());
    }

    private async Task HandleNotificationFeedbackAsync(ComponentInteractionCreateEventArgs e)
    {
        var customId = e.Interaction.Data.CustomId;
        var isSuppress = customId.StartsWith(SmartNotificationService.ButtonPrefixNormal);
        var prefix = isSuppress ? SmartNotificationService.ButtonPrefixNormal : SmartNotificationService.ButtonPrefixInvestigate;
        var hash = customId[prefix.Length..];

        try
        {
            if (isSuppress)
            {
                await SmartNotification.HandleSuppressFeedbackAsync(
                    hash, "Marked as normal via button feedback");

                await e.Interaction.CreateResponseAsync(
                    InteractionResponseType.ChannelMessageWithSource,
                    new DiscordInteractionResponseBuilder()
                        .WithContent("✅ Got it, I'll suppress similar notifications in the future.")
                        .AsEphemeral());
            }
            else
            {
                await e.Interaction.CreateResponseAsync(
                    InteractionResponseType.ChannelMessageWithSource,
                    new DiscordInteractionResponseBuilder()
                        .WithContent("🔍 Noted. Feel free to ask me follow-up questions about this issue.")
                        .AsEphemeral());
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle notification feedback");
            try
            {
                await e.Interaction.CreateResponseAsync(
                    InteractionResponseType.ChannelMessageWithSource,
                    new DiscordInteractionResponseBuilder()
                        .WithContent("Something went wrong processing your feedback.")
                        .AsEphemeral());
            }
            catch
            {
                // Already responded or timed out
            }
        }
    }

    private async Task HandleRemediationResponseAsync(ComponentInteractionCreateEventArgs e)
    {
        var customId = e.Interaction.Data.CustomId;

        // Determine the prefix and parse the action ID
        string prefix;
        if (customId.StartsWith("remediation_approve_"))
        {
            prefix = "remediation_approve_";
        }
        else if (customId.StartsWith("remediation_reject_"))
        {
            prefix = "remediation_reject_";
        }
        else if (customId.StartsWith("remediation_ok_"))
        {
            prefix = "remediation_ok_";
        }
        else if (customId.StartsWith("remediation_fail_"))
        {
            prefix = "remediation_fail_";
        }
        else
        {
            await e.Interaction.CreateResponseAsync(InteractionResponseType.DeferredMessageUpdate);
            return;
        }

        if (!int.TryParse(customId[prefix.Length..], out var actionId))
        {
            await e.Interaction.CreateResponseAsync(InteractionResponseType.DeferredMessageUpdate);
            return;
        }

        var isApproval = prefix is "remediation_approve_" or "remediation_ok_";

        try
        {
            if (prefix is "remediation_approve_" or "remediation_reject_")
            {
                await e.Interaction.CreateResponseAsync(
                    InteractionResponseType.DeferredChannelMessageWithSource,
                    new DiscordInteractionResponseBuilder().AsEphemeral());

                await _autoRemediationService.RecordUserConfirmationAsync(actionId, isApproval, default);

                var responseText = isApproval
                    ? "Remediation approved and executing..."
                    : "Remediation rejected.";

                await e.Interaction.EditOriginalResponseAsync(
                    new DiscordWebhookBuilder().WithContent(responseText));
            }
            else
            {
                // Feedback on auto-executed remediation (ok/fail) — persist to DB + update pattern
                await _autoRemediationService.RecordFeedbackAsync(actionId, isApproval, default);

                var emoji = isApproval ? "✅" : "❌";
                var text = isApproval ? "Noted, remediation was successful." : "Noted, remediation did not help.";

                await e.Interaction.CreateResponseAsync(
                    InteractionResponseType.ChannelMessageWithSource,
                    new DiscordInteractionResponseBuilder()
                        .WithContent($"{emoji} {text}")
                        .AsEphemeral());
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle remediation response for action {ActionId}", actionId);
            try
            {
                await e.Interaction.CreateResponseAsync(
                    InteractionResponseType.ChannelMessageWithSource,
                    new DiscordInteractionResponseBuilder()
                        .WithContent("Failed to process remediation response.")
                        .AsEphemeral());
            }
            catch
            {
                // Response already sent
            }
        }
    }

    private async Task OnMessageCreated(DiscordClient client, MessageCreateEventArgs e)
    {
        // Ignore bots and system messages
        if (e.Author.IsBot || e.Message.MessageType != MessageType.Default)
        {
            return;
        }

        // Handle DM conversations from the owner
        if (e.Channel.IsPrivate && e.Author.Id == HomelabOwner.DiscordUserId)
        {
            await HandleDmMessageAsync(e);
            return;
        }

        // Check if we should respond
        var isMentioned = e.Message.MentionedUsers.Any(u => u.Id == client.CurrentUser.Id);
        var isDedicatedChannel = _config.DedicatedChannels.Contains(e.Channel.Id);
        var isThread = e.Channel.IsThread;

        // Respond in: mentions anywhere, dedicated channels, or threads we're already in
        if (!isMentioned && !isDedicatedChannel && !isThread)
        {
            return;
        }

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

    private async Task HandleDmMessageAsync(MessageCreateEventArgs e)
    {
        var content = e.Message.Content.Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var threadId = SmartNotification.CurrentDailyThreadId;
        SmartNotification.MarkConversationActive();

        _logger.LogDebug("Processing DM from owner, using daily cycle threadId={ThreadId}", threadId);

        try
        {
            await e.Channel.TriggerTypingAsync();

            var response = await _kernelService.ProcessMessageAsync(
                threadId, content, e.Author.Id);

            await SendToChannelSplitAsync(e.Channel, response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling DM message");
            await e.Channel.SendMessageAsync("Something went wrong while processing your message. Please try again later.");
        }
    }

    private static async Task SendResponseAsync(DiscordChannel channel, string response)
    {
        await SendToChannelSplitAsync(channel, response);
    }

    private static async Task SendToChannelSplitAsync(DiscordChannel channel, string content)
    {
        var chunks = MessageSplitService.SplitIntoSections(content);
        foreach (var chunk in chunks)
        {
            await channel.SendMessageAsync(chunk);
            if (chunks.Count > 1)
            {
                await Task.Delay(100);
            }
        }
    }

    private static TimeSpan GetBackoffDelay(int attempt)
    {
        // Exponential backoff: 1s, 2s, 4s, 8s, 16s, 32s, 60s max
        var seconds = Math.Min(Math.Pow(2, attempt - 1), 60);
        return TimeSpan.FromSeconds(seconds);
    }

    public async Task SendDmAsync(ulong userId, DiscordEmbed embed)
    {
        var dm = await GetDmChannelAsync(userId);
        if (dm != null)
        {
            await dm.SendMessageAsync(embed: embed);
        }
    }

    public async Task SendDmAsync(ulong userId, string message)
    {
        var dm = await GetDmChannelAsync(userId);
        if (dm != null)
        {
            await dm.SendMessageAsync(message);
        }
    }

    public async Task SendDmWithComponentsAsync(ulong userId, DiscordEmbed embed, List<DiscordComponent> components)
    {
        var dm = await GetDmChannelAsync(userId);
        if (dm == null)
        {
            return;
        }

        var builder = new DiscordMessageBuilder()
            .WithEmbed(embed)
            .AddComponents(components);

        await dm.SendMessageAsync(builder);
    }

    public async Task SendDmSplitAsync(ulong userId, string content)
    {
        var dm = await GetDmChannelAsync(userId);
        if (dm == null)
        {
            return;
        }

        var chunks = MessageSplitService.SplitIntoSections(content);
        foreach (var chunk in chunks)
        {
            await dm.SendMessageAsync(chunk);
            if (chunks.Count > 1)
            {
                await Task.Delay(100); // Small delay between messages
            }
        }
    }

    public async Task SendDmNotificationFeedbackAsync(ulong userId, string issueHash)
    {
        var dm = await GetDmChannelAsync(userId);
        if (dm == null)
        {
            return;
        }

        var builder = new DiscordMessageBuilder()
            .WithContent("Was this notification useful?")
            .AddComponents(
                new DiscordButtonComponent(
                    ButtonStyle.Secondary,
                    $"{SmartNotificationService.ButtonPrefixNormal}{issueHash}",
                    "Normal, ignore in future"),
                new DiscordButtonComponent(
                    ButtonStyle.Primary,
                    $"{SmartNotificationService.ButtonPrefixInvestigate}{issueHash}",
                    "Investigate further"));

        await dm.SendMessageAsync(builder);
    }

    public async Task<(ulong ThreadId, ulong MessageId)?> CreateThreadInChannelAsync(
        ulong channelId, string name, string initialMessage)
    {
        if (_client == null)
        {
            return null;
        }

        try
        {
            var channel = await _client.GetChannelAsync(channelId);
            var message = await channel.SendMessageAsync(initialMessage);
            var thread = await message.CreateThreadAsync(name, DSharpPlus.AutoArchiveDuration.Day);
            return (thread.Id, message.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create thread in channel {ChannelId}", channelId);
            return null;
        }
    }

    public async Task SendToThreadAsync(ulong threadId, string message)
    {
        if (_client == null)
        {
            return;
        }

        try
        {
            var thread = await _client.GetChannelAsync(threadId);
            await thread.SendMessageAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send message to thread {ThreadId}", threadId);
        }
    }

    public async Task SendToThreadAsync(ulong threadId, DiscordEmbed embed)
    {
        if (_client == null)
        {
            return;
        }

        try
        {
            var thread = await _client.GetChannelAsync(threadId);
            await thread.SendMessageAsync(embed: embed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send embed to thread {ThreadId}", threadId);
        }
    }

    public async Task SendDmFileAsync(ulong userId, string content, string filename)
    {
        var dm = await GetDmChannelAsync(userId);
        if (dm == null)
        {
            return;
        }

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        var message = new DiscordMessageBuilder()
            .AddFile(filename, stream);
        await dm.SendMessageAsync(message);
    }

    private async Task<DiscordDmChannel?> GetDmChannelAsync(ulong userId)
    {
        if (_client == null)
        {
            _logger.LogWarning("Cannot send DM: Discord client not connected");
            return null;
        }

        try
        {
            foreach (var guild in _client.Guilds.Values)
            {
                try
                {
                    var member = await guild.GetMemberAsync(userId);
                    var dm = await member.CreateDmChannelAsync();
                    _logger.LogDebug("Found DM channel for user {UserId}", userId);
                    return dm;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "User {UserId} not found in guild {Guild}", userId, guild.Name);
                }
            }

            _logger.LogWarning("Could not find user {UserId} in any guild", userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find DM channel for user {UserId}", userId);
        }

        return null;
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
