using System.Collections.Concurrent;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;

namespace HomelabBot.Services;

public sealed class ConfirmationService
{
    private readonly ConcurrentDictionary<string, PendingConfirmation> _pendingConfirmations = new();
    private readonly ILogger<ConfirmationService> _logger;
    private readonly TimeSpan _confirmationTimeout = TimeSpan.FromMinutes(2);

    public ConfirmationService(ILogger<ConfirmationService> logger)
    {
        _logger = logger;
    }

    public async Task<(DiscordMessage Message, string ConfirmId)> RequestConfirmationAsync(
        DiscordChannel channel,
        string action,
        string details)
    {
        var confirmId = Guid.NewGuid().ToString()[..8];

        var embed = new DiscordEmbedBuilder()
            .WithTitle("Confirmation Required")
            .WithDescription($"**Action:** {action}\n\n{details}")
            .WithColor(DiscordColor.Orange)
            .WithFooter($"Expires in {_confirmationTimeout.TotalMinutes} minutes")
            .Build();

        var confirmButton = new DiscordButtonComponent(
            ButtonStyle.Danger,
            $"confirm_{confirmId}",
            "Confirm",
            false,
            new DiscordComponentEmoji("✅"));

        var cancelButton = new DiscordButtonComponent(
            ButtonStyle.Secondary,
            $"cancel_{confirmId}",
            "Cancel",
            false,
            new DiscordComponentEmoji("❌"));

        var builder = new DiscordMessageBuilder()
            .WithEmbed(embed)
            .AddComponents(confirmButton, cancelButton);

        var message = await channel.SendMessageAsync(builder);

        var pending = new PendingConfirmation
        {
            Action = action,
            Details = details,
            Message = message,
            ExpiresAt = DateTime.UtcNow + _confirmationTimeout
        };

        _pendingConfirmations[confirmId] = pending;

        _logger.LogDebug("Created confirmation request {ConfirmId} for action: {Action}", confirmId, action);

        // Schedule cleanup
        _ = Task.Run(async () =>
        {
            await Task.Delay(_confirmationTimeout);
            if (_pendingConfirmations.TryRemove(confirmId, out var expired))
            {
                _logger.LogDebug("Confirmation {ConfirmId} expired", confirmId);
                await UpdateMessageAsExpired(expired.Message);
            }
        });

        return (message, confirmId);
    }

    public async Task<bool> HandleInteractionAsync(ComponentInteractionCreateEventArgs e)
    {
        var customId = e.Interaction.Data.CustomId;

        if (!customId.StartsWith("confirm_") && !customId.StartsWith("cancel_"))
        {
            return false;
        }

        var parts = customId.Split('_');
        if (parts.Length != 2)
        {
            return false;
        }

        var confirmId = parts[1];
        var isConfirm = parts[0] == "confirm";

        if (!_pendingConfirmations.TryRemove(confirmId, out var pending))
        {
            await e.Interaction.CreateResponseAsync(
                InteractionResponseType.UpdateMessage,
                new DiscordInteractionResponseBuilder()
                    .WithContent("This confirmation has expired or was already handled.")
                    .AsEphemeral());
            return true;
        }

        if (isConfirm)
        {
            _logger.LogInformation("User {User} confirmed action: {Action}",
                e.User.Username, pending.Action);

            var embed = new DiscordEmbedBuilder()
                .WithTitle("Action Confirmed")
                .WithDescription($"**Action:** {pending.Action}\n\nExecuting...")
                .WithColor(DiscordColor.Green)
                .Build();

            await e.Interaction.CreateResponseAsync(
                InteractionResponseType.UpdateMessage,
                new DiscordInteractionResponseBuilder()
                    .AddEmbed(embed));

            pending.Confirmed = true;
            pending.CompletionSource.SetResult(true);
        }
        else
        {
            _logger.LogInformation("User {User} cancelled action: {Action}",
                e.User.Username, pending.Action);

            var embed = new DiscordEmbedBuilder()
                .WithTitle("Action Cancelled")
                .WithDescription($"**Action:** {pending.Action}\n\nCancelled by user.")
                .WithColor(DiscordColor.Gray)
                .Build();

            await e.Interaction.CreateResponseAsync(
                InteractionResponseType.UpdateMessage,
                new DiscordInteractionResponseBuilder()
                    .AddEmbed(embed));

            pending.CompletionSource.SetResult(false);
        }

        return true;
    }

    public async Task<bool> WaitForConfirmationAsync(string confirmId, CancellationToken ct = default)
    {
        if (!_pendingConfirmations.TryGetValue(confirmId, out var pending))
        {
            return false;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_confirmationTimeout);

            return await pending.CompletionSource.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Confirmation {ConfirmId} wait was cancelled", confirmId);
            return false;
        }
    }

    private static async Task UpdateMessageAsExpired(DiscordMessage message)
    {
        try
        {
            var embed = new DiscordEmbedBuilder()
                .WithTitle("Confirmation Expired")
                .WithDescription("This confirmation request has expired.")
                .WithColor(DiscordColor.Gray)
                .Build();

            await message.ModifyAsync(new DiscordMessageBuilder().WithEmbed(embed));
        }
        catch
        {
            // Message may have been deleted
        }
    }

    private sealed class PendingConfirmation
    {
        public required string Action { get; init; }
        public required string Details { get; init; }
        public required DiscordMessage Message { get; init; }
        public required DateTime ExpiresAt { get; init; }
        public bool Confirmed { get; set; }
        public TaskCompletionSource<bool> CompletionSource { get; } = new();
    }
}
