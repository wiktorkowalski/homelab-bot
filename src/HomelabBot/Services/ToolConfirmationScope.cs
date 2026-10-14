namespace HomelabBot.Services;

internal enum ToolConfirmationDecision
{
    Approved,
    Declined,
    TimedOut,
}

// How the current turn asks its user to approve a tool call: the channel and the user of a chat
// turn, bound by the caller. A turn without one (slash commands, one-shot analyses, scheduled runs)
// cannot ask, so ToolConfirmationFilter does not run the tool.
internal sealed class ToolConfirmationScope : IDisposable
{
    private static readonly AsyncLocal<Func<string, CancellationToken, Task<ToolConfirmationDecision>>?> CurrentAsk = new();

    private readonly Func<string, CancellationToken, Task<ToolConfirmationDecision>>? _previous;

    private ToolConfirmationScope(Func<string, CancellationToken, Task<ToolConfirmationDecision>>? previous)
    {
        _previous = previous;
    }

    // Gets the action text, shows it to the user, and returns the user's answer.
    public static Func<string, CancellationToken, Task<ToolConfirmationDecision>>? Current => CurrentAsk.Value;

    // Synchronous on purpose: an AsyncLocal set inside an async method does not flow back to
    // the caller, so the caller's flow must set it.
    public static ToolConfirmationScope Begin(Func<string, CancellationToken, Task<ToolConfirmationDecision>> ask)
    {
        var scope = new ToolConfirmationScope(CurrentAsk.Value);
        CurrentAsk.Value = ask;
        return scope;
    }

    public void Dispose() => CurrentAsk.Value = _previous;
}
