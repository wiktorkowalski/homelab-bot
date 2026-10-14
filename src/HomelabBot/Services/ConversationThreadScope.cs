namespace HomelabBot.Services;

// The thread of the turn that runs a tool. The model must not supply it: in an owner DM it never
// sees a thread id and invents one, so the tool would write to a random record.
internal sealed class ConversationThreadScope : IDisposable
{
    private static readonly AsyncLocal<ulong?> CurrentThreadId = new();

    private readonly ulong? _previous;

    private ConversationThreadScope(ulong? previous)
    {
        _previous = previous;
    }

    public static ulong? Current => CurrentThreadId.Value;

    // Synchronous on purpose: an AsyncLocal set inside an async method does not flow back to
    // the caller, so the caller's flow must set it.
    public static ConversationThreadScope Begin(ulong threadId)
    {
        var scope = new ConversationThreadScope(CurrentThreadId.Value);
        CurrentThreadId.Value = threadId;
        return scope;
    }

    // Restores the outer turn's thread when a tool runs a nested turn.
    public void Dispose() => CurrentThreadId.Value = _previous;
}
