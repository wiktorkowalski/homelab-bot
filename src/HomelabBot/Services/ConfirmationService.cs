using System.Collections.Concurrent;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;

namespace HomelabBot.Services;

internal enum ConfirmationClick
{
    Answered,
    NotYours,
    Unknown,
}

// The Discord side of ToolConfirmationFilter: a message with Approve and Reject buttons in the
// channel of the turn, answered by the user who started the turn.
public sealed class ConfirmationService
{
    internal const string ApprovePrefix = "toolconfirm_approve_";
    internal const string RejectPrefix = "toolconfirm_reject_";

    internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, PendingConfirmation> _pending = new(StringComparer.Ordinal);
    private readonly ILogger<ConfirmationService> _logger;
    private readonly TimeSpan _timeout;

    public ConfirmationService(ILogger<ConfirmationService> logger)
        : this(logger, Timeout)
    {
    }

    // Tests pass a short timeout.
    internal ConfirmationService(ILogger<ConfirmationService> logger, TimeSpan timeout)
    {
        _logger = logger;
        _timeout = timeout;
    }

    public async Task<bool> HandleInteractionAsync(ComponentInteractionCreateEventArgs e)
    {
        var customId = e.Interaction.Data.CustomId;
        var approve = customId.StartsWith(ApprovePrefix, StringComparison.Ordinal);
        if (!approve && !customId.StartsWith(RejectPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var id = customId[(approve ? ApprovePrefix : RejectPrefix).Length..];
        var (click, pending) = Answer(id, e.User.Id, approve);

        if (click != ConfirmationClick.Answered)
        {
            var text = click == ConfirmationClick.NotYours
                ? "Only the person who asked for this action can approve or reject it."
                : "This confirmation has expired or was already answered.";
            await e.Interaction.CreateResponseAsync(
                InteractionResponseType.ChannelMessageWithSource,
                new DiscordInteractionResponseBuilder().WithContent(text).AsEphemeral());
            return true;
        }

        var decision = approve ? ToolConfirmationDecision.Approved : ToolConfirmationDecision.Declined;
        try
        {
            await e.Interaction.CreateResponseAsync(
                InteractionResponseType.UpdateMessage,
                new DiscordInteractionResponseBuilder()
                    .WithContent(OutcomeText(decision, pending!.Action, e.User.Username))
                    .AddComponents(Buttons(id, disabled: true)));
        }
        catch (Exception ex)
        {
            // The decision already stands; only the message still shows live buttons.
            _logger.LogWarning(ex, "Could not update confirmation message {ConfirmId} after the answer", id);
        }

        return true;
    }

    // The ask of ToolConfirmationScope for one chat turn. onSent gets the prompt text once it is in
    // the channel, so an owner DM can record it.
    internal Func<string, CancellationToken, Task<ToolConfirmationDecision>> AskIn(
        DiscordChannel channel, ulong userId, Action<string>? onSent) =>
        (action, ct) => AskAsync(channel, userId, action, onSent, ct);

    internal async Task<ToolConfirmationDecision> AskAsync(
        DiscordChannel channel, ulong userId, string action, Action<string>? onSent, CancellationToken ct)
    {
        var pending = Register(userId, action);
        var prompt = PromptText(action);

        DiscordMessage message;
        try
        {
            message = await channel.SendMessageAsync(
                new DiscordMessageBuilder().WithContent(prompt).AddComponents(Buttons(pending.Id, disabled: false)));
        }
        catch
        {
            _pending.TryRemove(pending.Id, out _);
            throw;
        }

        onSent?.Invoke(prompt);

        try
        {
            var decision = await WaitAsync(pending, ct);
            if (decision == ToolConfirmationDecision.TimedOut)
            {
                await TryEditAsync(message, pending.Id, OutcomeText(decision, action, by: null));
            }

            return decision;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryEditAsync(message, pending.Id, $"🚫 **Cancelled**, did not run\n{Code(action)}");
            throw;
        }
    }

    internal PendingConfirmation Register(ulong userId, string action)
    {
        var pending = new PendingConfirmation(Guid.NewGuid().ToString("N"), userId, action);
        _pending[pending.Id] = pending;
        return pending;
    }

    // Removing the entry claims the answer: of a click and the timeout, or of two clicks that
    // arrive together, only the one that removes it decides.
    internal (ConfirmationClick Click, PendingConfirmation? Pending) Answer(string id, ulong userId, bool approve)
    {
        if (!_pending.TryGetValue(id, out var pending))
        {
            return (ConfirmationClick.Unknown, null);
        }

        if (pending.UserId != userId)
        {
            return (ConfirmationClick.NotYours, pending);
        }

        if (!_pending.TryRemove(id, out _))
        {
            return (ConfirmationClick.Unknown, null);
        }

        pending.Decision.TrySetResult(approve ? ToolConfirmationDecision.Approved : ToolConfirmationDecision.Declined);
        return (ConfirmationClick.Answered, pending);
    }

    internal async Task<ToolConfirmationDecision> WaitAsync(PendingConfirmation pending, CancellationToken ct)
    {
        try
        {
            return await pending.Decision.Task.WaitAsync(_timeout, ct);
        }
        catch (TimeoutException)
        {
            // A click that claimed the entry just before the timeout still decides.
            return _pending.TryRemove(pending.Id, out _) ? ToolConfirmationDecision.TimedOut : await pending.Decision.Task;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _pending.TryRemove(pending.Id, out _);
            throw;
        }
    }

    private static string PromptText(string action) =>
        $"⚠️ **Confirm action**\n{Code(action)}\nApprove or reject within {Timeout.TotalMinutes:0} minutes. Without an answer it does not run.";

    private static string OutcomeText(ToolConfirmationDecision decision, string action, string? by) =>
        decision switch
        {
            ToolConfirmationDecision.Approved => $"✅ **Approved** by {by}\n{Code(action)}",
            ToolConfirmationDecision.Declined => $"❌ **Rejected** by {by}, did not run\n{Code(action)}",
            _ => $"⌛ **No answer within {Timeout.TotalMinutes:0} minutes**, did not run\n{Code(action)}",
        };

    // A backtick in an argument would end the code span early.
    private static string Code(string action) => $"`{action.Replace('`', '\'')}`";

    private static DiscordComponent[] Buttons(string id, bool disabled) =>
    [
        new DiscordButtonComponent(ButtonStyle.Success, $"{ApprovePrefix}{id}", "Approve", disabled),
        new DiscordButtonComponent(ButtonStyle.Secondary, $"{RejectPrefix}{id}", "Reject", disabled),
    ];

    private async Task TryEditAsync(DiscordMessage message, string id, string text)
    {
        try
        {
            await message.ModifyAsync(new DiscordMessageBuilder().WithContent(text).AddComponents(Buttons(id, disabled: true)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update confirmation message {ConfirmId}", id);
        }
    }

    internal sealed class PendingConfirmation
    {
        public PendingConfirmation(string id, ulong userId, string action)
        {
            Id = id;
            UserId = userId;
            Action = action;
        }

        public string Id { get; }

        public ulong UserId { get; }

        public string Action { get; }

        // Asynchronous continuations: the waiting turn must not run its tool and the next LLM
        // round on the Discord event handler that set the result.
        public TaskCompletionSource<ToolConfirmationDecision> Decision { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
