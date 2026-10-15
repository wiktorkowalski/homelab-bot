namespace HomelabBot.Services;

internal enum ToolConfirmationDecision
{
    Approved,
    Declined,
    TimedOut,
}

// The chat of a turn: its user, and the guild channel or thread it runs in. A null channel is the owner DM.
internal sealed record TurnChat
{
    public required ulong UserId { get; init; }

    public ulong? ChannelId { get; init; }
}

// How the current turn asks its user to approve a tool call: the channel and the user of a chat
// turn, bound by the caller. A turn without one (slash commands, one-shot analyses, scheduled runs)
// cannot ask, so ToolConfirmationFilter does not run the tool.
internal sealed class ToolConfirmationScope : IDisposable
{
    private static readonly AsyncLocal<State?> CurrentState = new();

    private readonly State? _previous;

    private ToolConfirmationScope(State? previous)
    {
        _previous = previous;
    }

    // Gets the action text, shows it to the user, and returns the user's answer.
    public static Func<string, CancellationToken, Task<ToolConfirmationDecision>>? Current => CurrentState.Value?.Ask;

    // The chat the ask goes to. A tool that answers later (ask_claude) posts there.
    public static TurnChat? CurrentChat => CurrentState.Value?.Chat;

    // Synchronous on purpose: an AsyncLocal set inside an async method does not flow back to
    // the caller, so the caller's flow must set it.
    public static ToolConfirmationScope Begin(
        Func<string, CancellationToken, Task<ToolConfirmationDecision>> ask, TurnChat? chat = null)
    {
        var scope = new ToolConfirmationScope(CurrentState.Value);
        CurrentState.Value = new State { Ask = ask, Chat = chat };
        return scope;
    }

    public void Dispose() => CurrentState.Value = _previous;

    private sealed class State
    {
        public required Func<string, CancellationToken, Task<ToolConfirmationDecision>> Ask { get; init; }

        public TurnChat? Chat { get; init; }
    }
}
