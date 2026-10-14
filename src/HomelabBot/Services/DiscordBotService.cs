using System.Globalization;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using DSharpPlus.SlashCommands;
using HomelabBot.Commands;
using HomelabBot.Configuration;
using HomelabBot.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public class DiscordBotService : BackgroundService
{
    internal const string QuoteStart = "[Quote of the message the owner replied to]";
    internal const string QuoteEnd = "[End of quote]";

    // A quoted alert or report can be long; the start says which message it was.
    private const int MaxQuoteChars = 2000;

    private const string NoTextDmReply = "I can read text only, not attachments or stickers. Please write your question as a message.";

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
    private long _disconnectedSinceTicks;

    // Unlike _disconnectedSinceTicks, reconnect attempts and watchdog rebuilds don't re-stamp this, so a lasting outage stays visible to /health
    private long _outageSinceTicks;
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxDisconnectedDuration = TimeSpan.FromMinutes(5);
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

    // A routine close → resume flags the gateway down for about a second; DiscordHealthCheck applies the grace period.
    public bool IsReady =>
        _readyTcs.Task.IsCompletedSuccessfully && Interlocked.Read(ref _disconnectedSinceTicks) == 0;

    public TimeSpan? DisconnectedFor
    {
        get
        {
            var since = Interlocked.Read(ref _outageSinceTicks);
            return since == 0 ? null : DateTime.UtcNow - new DateTime(since, DateTimeKind.Utc);
        }
    }

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

                _logger.LogError(ex, "Discord connection failed (attempt {Attempt}). Retrying in {Delay}s...",
                    _reconnectAttempts, delay.TotalSeconds);

                await DisposeClientAsync();
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
        _client.SocketClosed += OnSocketClosed;
        _client.Zombied += OnZombied;

        // Down until Ready proves otherwise, so a handshake that never completes still trips the watchdog
        Interlocked.Exchange(ref _disconnectedSinceTicks, DateTime.UtcNow.Ticks);
        Interlocked.CompareExchange(ref _outageSinceTicks, DateTime.UtcNow.Ticks, 0);
        await _client.ConnectAsync();
        _reconnectAttempts = 0; // Reset on successful connect

        // DSharpPlus AutoReconnect gives up silently after its own retries; rebuild the client if it stays down
        await WatchGatewayAsync(stoppingToken);
    }

    private async Task WatchGatewayAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(WatchdogInterval, stoppingToken);

            var since = Interlocked.Read(ref _disconnectedSinceTicks);
            if (since == 0)
            {
                continue;
            }

            var downDuration = DateTime.UtcNow - new DateTime(since, DateTimeKind.Utc);
            if (downDuration < MaxDisconnectedDuration)
            {
                continue;
            }

            _logger.LogWarning("Discord gateway down for {DownDuration} without recovery. Rebuilding client",
                downDuration);
            await DisposeClientAsync();
            return;
        }
    }

    private async Task DisposeClientAsync()
    {
        var client = Interlocked.Exchange(ref _client, null);
        if (client == null)
        {
            return;
        }

        try
        {
            await client.DisconnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disconnect of stale Discord client failed");
        }

        client.Dispose();
    }

    private bool IsCurrent(DiscordClient client) => ReferenceEquals(client, _client);

    private void MarkDisconnected()
    {
        var now = DateTime.UtcNow.Ticks;
        Interlocked.CompareExchange(ref _disconnectedSinceTicks, now, 0);
        Interlocked.CompareExchange(ref _outageSinceTicks, now, 0);
    }

    private void MarkConnected()
    {
        Interlocked.Exchange(ref _disconnectedSinceTicks, 0);
        Interlocked.Exchange(ref _outageSinceTicks, 0);
    }

    private Task OnReady(DiscordClient client, ReadyEventArgs e)
    {
        // Late events from a disposed client must not touch the watchdog state of the current one
        if (!IsCurrent(client))
        {
            return Task.CompletedTask;
        }

        _logger.LogInformation("Discord bot connected as {Username}#{Discriminator}",
            client.CurrentUser.Username, client.CurrentUser.Discriminator);
        MarkConnected();
        _readyTcs.TrySetResult();
        return Task.CompletedTask;
    }

    private Task OnResumed(DiscordClient client, ReadyEventArgs e)
    {
        if (!IsCurrent(client))
        {
            return Task.CompletedTask;
        }

        _logger.LogInformation("Discord connection resumed");
        MarkConnected();
        return Task.CompletedTask;
    }

    private Task OnSocketClosed(DiscordClient client, SocketCloseEventArgs e)
    {
        if (!IsCurrent(client))
        {
            return Task.CompletedTask;
        }

        MarkDisconnected();
        return Task.CompletedTask;
    }

    private Task OnZombied(DiscordClient client, ZombiedEventArgs e)
    {
        if (!IsCurrent(client))
        {
            return Task.CompletedTask;
        }

        _logger.LogWarning("Discord connection zombied after {MissedHeartbeatCount} missed heartbeats", e.Failures);
        MarkDisconnected();
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

            if (customId.StartsWith(SmartNotificationService.ButtonPrefixNormal, StringComparison.Ordinal))
            {
                await HandleNotificationFeedbackAsync(e);
                return;
            }

            if (customId.StartsWith(SmartNotificationService.ButtonPrefixRemovePreference, StringComparison.Ordinal))
            {
                await HandleRemovePreferenceAsync(e);
                return;
            }

            if (customId.StartsWith(SmartNotificationService.ButtonPrefixInvestigate, StringComparison.Ordinal))
            {
                await HandleRetiredButtonAsync(e);
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

    // Old DMs still carry the removed "Investigate further" button. A click must still get an
    // answer, or Discord shows "This interaction failed".
    private async Task HandleRetiredButtonAsync(ComponentInteractionCreateEventArgs e)
    {
        _logger.LogDebug("Retired button {CustomId} clicked", e.Interaction.Data.CustomId);
        await e.Interaction.CreateResponseAsync(
            InteractionResponseType.ChannelMessageWithSource,
            new DiscordInteractionResponseBuilder()
                .WithContent("This button is no longer used.")
                .AsEphemeral());
    }

    // A "Remove" button of the /preferences list: remove the row, then redraw the list in place.
    private async Task HandleRemovePreferenceAsync(ComponentInteractionCreateEventArgs e)
    {
        if (e.User.Id != HomelabOwner.DiscordUserId)
        {
            _logger.LogInformation("Preference removal refused for {UserId}: not the owner", e.User.Id);
            await e.Interaction.CreateResponseAsync(
                InteractionResponseType.ChannelMessageWithSource,
                new DiscordInteractionResponseBuilder()
                    .WithContent("Only the owner can manage notification preferences.")
                    .AsEphemeral());
            return;
        }

        try
        {
            var idText = e.Interaction.Data.CustomId[SmartNotificationService.ButtonPrefixRemovePreference.Length..];
            var removed = int.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                ? await SmartNotification.RemovePreferenceAsync(id)
                : null;

            var notice = removed != null
                ? $"Removed `{SmartNotificationService.DescribePreference(removed)}`."
                : "That preference was already removed.";
            var preferences = await SmartNotification.ListPreferencesAsync();

            // Facts can come from the model, so they must not ping anyone.
            await e.Interaction.CreateResponseAsync(
                InteractionResponseType.UpdateMessage,
                new DiscordInteractionResponseBuilder()
                    .WithContent(NotificationPreferencesView.Content(preferences, notice))
                    .AddComponents(NotificationPreferencesView.Components(preferences))
                    .AddMentions(Mentions.None));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove notification preference");
            try
            {
                await e.Interaction.CreateResponseAsync(
                    InteractionResponseType.ChannelMessageWithSource,
                    new DiscordInteractionResponseBuilder()
                        .WithContent("Something went wrong removing the preference.")
                        .AsEphemeral());
            }
            catch (Exception innerEx)
            {
                _logger.LogWarning(innerEx, "Failed to send error response for interaction");
            }
        }
    }

    private async Task HandleNotificationFeedbackAsync(ComponentInteractionCreateEventArgs e)
    {
        var payload = e.Interaction.Data.CustomId[SmartNotificationService.ButtonPrefixNormal.Length..];

        try
        {
            var scope = await SmartNotification.HandleSuppressFeedbackAsync(
                payload, "Marked as normal via button feedback");

            await e.Interaction.CreateResponseAsync(
                InteractionResponseType.ChannelMessageWithSource,
                new DiscordInteractionResponseBuilder()
                    .WithContent($"✅ Got it, I'll suppress {scope}. Use /preferences to undo.")
                    .AsEphemeral());
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
            catch (Exception innerEx)
            {
                _logger.LogWarning(innerEx, "Failed to send error response for interaction");
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

                var report = await _autoRemediationService.RecordUserConfirmationAsync(actionId, isApproval, default);

                await e.Interaction.EditOriginalResponseAsync(
                    new DiscordWebhookBuilder().WithContent(report));
                RecordForOwner(e.User.Id, report);
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
            catch (Exception innerEx)
            {
                _logger.LogWarning(innerEx, "Failed to send error response for remediation interaction");
            }
        }
    }

    private async Task OnMessageCreated(DiscordClient client, MessageCreateEventArgs e)
    {
        // Ignore bots and system messages. A Discord reply is a normal message with a link to the
        // message it answers.
        if (e.Author.IsBot || e.Message.MessageType is not (MessageType.Default or MessageType.Reply))
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

        // Only treat a thread as "ours" if the bot created it (mention-spawned
        // conversation threads and war rooms). Threads created by other bots or
        // users — e.g. the owner chatting with a different bot — must not trigger
        // us unless we're explicitly mentioned.
        var isOwnThread = e.Channel is DiscordThreadChannel threadChannel
            && threadChannel.CreatorId == client.CurrentUser.Id;

        // Respond in: mentions anywhere, dedicated channels, or threads we created
        if (!isMentioned && !isDedicatedChannel && !isOwnThread)
        {
            return;
        }

        // Use thread ID for conversation, or channel ID if not in thread
        var conversationId = e.Channel.IsThread ? e.Channel.Id : e.Message.Id;

        _logger.LogInformation("Processing message from {Author} in {Channel}",
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

            // Stopped before the send: a trigger right after the reply would show "typing" for 10 s more.
            string response;
            await using (TypingIndicator.Start(responseChannel.TriggerTypingAsync, _logger))
            {
                using var confirmation = ToolConfirmationScope.Begin(
                    _confirmationService.AskIn(responseChannel, e.Author.Id, onSent: null));
                response = await _kernelService.ProcessMessageAsync(conversationId, content, e.Author.Id);
            }

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
            // An attachment or a sticker alone: the model gets text only. Say so instead of staying silent.
            _logger.LogInformation(
                "DM from owner has no text ({AttachmentCount} attachments); asking for text", e.Message.Attachments.Count);
            try
            {
                await e.Channel.SendMessageAsync(NoTextDmReply);
                _ = RecordForOwnerAsync(NoTextDmReply);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not answer a DM from owner that has no text");
            }

            return;
        }

        _logger.LogInformation("Processing DM from owner on thread {ThreadId}", HomelabOwner.DmThreadId);

        if (e.Message.ReferencedMessage is { } replied)
        {
            content = WithQuote(content, QuoteText(replied.Content, replied.Embeds));
        }

        try
        {
            // Stopped before the send: a trigger right after the reply would show "typing" for 10 s more.
            string response;
            await using (TypingIndicator.Start(e.Channel.TriggerTypingAsync, _logger))
            {
                using var confirmation = ToolConfirmationScope.Begin(
                    _confirmationService.AskIn(e.Channel, e.Author.Id, onSent: text => RecordForOwner(e.Author.Id, text)));
                response = await _kernelService.ProcessMessageAsync(
                    HomelabOwner.DmThreadId, content, e.Author.Id);
            }

            await SendToChannelSplitAsync(e.Channel, response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling DM message");
            const string failure = "Something went wrong while processing your message. Please try again later.";
            await e.Channel.SendMessageAsync(failure);
            _ = RecordForOwnerAsync(failure);
        }
    }

    // Every message the owner gets in DMs goes into the DM thread, so the owner can ask about it.
    // Called after a send worked. Not awaited: the alert webhook waits on the send, and the record
    // waits for a running turn on the thread.
    private void RecordForOwner(ulong userId, string text)
    {
        if (userId == HomelabOwner.DiscordUserId && !string.IsNullOrWhiteSpace(text))
        {
            _ = RecordForOwnerAsync(text);
        }
    }

    private async Task RecordForOwnerAsync(string text)
    {
        try
        {
            await _conversationService.RecordBotMessageAsync(HomelabOwner.DmThreadId, text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record a sent message in the owner DM thread");
        }
    }

    // The owner replied to an earlier message: the model gets that message as a quote before the
    // owner's text, so "what does this mean?" has something to point at.
    internal static string WithQuote(string ownerText, string? quotedText)
    {
        if (string.IsNullOrWhiteSpace(quotedText))
        {
            return ownerText;
        }

        return $"{QuoteStart}\n{ConversationSearchResult.Truncate(quotedText, MaxQuoteChars)}\n{QuoteEnd}\n{ownerText}";
    }

    internal static string QuoteText(string? content, IEnumerable<DiscordEmbed> embeds) =>
        JoinLines([content, .. embeds.Select(EmbedText)]);

    // The text an embed shows: title, fields, description. Colors and timestamps are left out.
    internal static string EmbedText(DiscordEmbed embed) =>
        JoinLines([embed.Title, .. (embed.Fields ?? []).Select(f => $"{f.Name}: {f.Value}"), embed.Description]);

    private static string JoinLines(string?[] parts) =>
        string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

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

    public Task SendDmAsync(ulong userId, DiscordEmbed embed) =>
        SendDmMessageAsync(userId, new DiscordMessageBuilder().WithEmbed(embed), EmbedText(embed));

    public Task SendDmAsync(ulong userId, string message) =>
        SendDmMessageAsync(userId, new DiscordMessageBuilder().WithContent(message), message);

    public Task SendDmWithComponentsAsync(ulong userId, DiscordEmbed embed, List<DiscordComponent> components) =>
        SendDmMessageAsync(
            userId, new DiscordMessageBuilder().WithEmbed(embed).AddComponents(components), EmbedText(embed));

    public virtual async Task SendDmSplitAsync(ulong userId, string content)
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

        // One record for the whole text: the split is only Discord's length limit.
        RecordForOwner(userId, content);
    }

    // Buttons only, nothing the owner would ask about: not recorded.
    public virtual Task SendDmNotificationFeedbackAsync(ulong userId, string buttonId) =>
        SendDmMessageAsync(
            userId,
            new DiscordMessageBuilder()
                .WithContent("Was this notification useful?")
                .AddComponents(
                    new DiscordButtonComponent(ButtonStyle.Secondary, buttonId, "Normal, ignore in future")),
            recordText: null);

    public async Task<(ulong ThreadId, ulong MessageId)?> CreateThreadInChannelAsync(
        ulong channelId, string name, string initialMessage)
    {
        var client = _client;
        if (client == null)
        {
            return null;
        }

        try
        {
            var channel = await client.GetChannelAsync(channelId);
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
        var client = _client;
        if (client == null)
        {
            return;
        }

        try
        {
            var thread = await client.GetChannelAsync(threadId);
            await thread.SendMessageAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send message to thread {ThreadId}", threadId);
        }
    }

    public async Task SendToThreadAsync(ulong threadId, DiscordEmbed embed)
    {
        var client = _client;
        if (client == null)
        {
            return;
        }

        try
        {
            var thread = await client.GetChannelAsync(threadId);
            await thread.SendMessageAsync(embed: embed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send embed to thread {ThreadId}", threadId);
        }
    }

    public async Task SendDmFileAsync(ulong userId, string content, string filename)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        await SendDmMessageAsync(userId, new DiscordMessageBuilder().AddFile(filename, stream), recordText: null);
    }

    // Sends one DM. A record text goes into the owner's DM thread once the send worked.
    private async Task SendDmMessageAsync(ulong userId, DiscordMessageBuilder message, string? recordText)
    {
        var dm = await GetDmChannelAsync(userId);
        if (dm == null)
        {
            return;
        }

        await dm.SendMessageAsync(message);
        if (recordText is not null)
        {
            RecordForOwner(userId, recordText);
        }
    }

    private async Task<DiscordDmChannel?> GetDmChannelAsync(ulong userId)
    {
        var client = _client;
        if (client == null)
        {
            _logger.LogWarning("Cannot send DM: Discord client not connected");
            return null;
        }

        try
        {
            foreach (var guild in client.Guilds.Values)
            {
                try
                {
                    var member = await guild.GetMemberAsync(userId);
                    var dm = await member.CreateDmChannelAsync();
                    _logger.LogInformation("Found DM channel for user {UserId}", userId);
                    return dm;
                }
                catch (Exception ex)
                {
                    _logger.LogInformation(ex, "User {UserId} not found in guild {Guild}", userId, guild.Name);
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

        // Cancel and drain ExecuteAsync first, otherwise it can log a spurious failure or build a client nobody disposes
        await base.StopAsync(cancellationToken);
        await DisposeClientAsync();
    }
}
