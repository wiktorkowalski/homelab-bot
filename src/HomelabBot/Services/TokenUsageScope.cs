namespace HomelabBot.Services;

// Collects usage of every billed LLM request during one interaction. SK auto function invocation
// sends one request per tool round but only surfaces the last response's usage to the caller.
internal sealed class TokenUsageScope : IDisposable
{
    private static readonly AsyncLocal<TokenUsageScope?> CurrentScope = new();

    private readonly TokenUsageScope? _parent;
    private int _rounds;
    private int _missedRounds;
    private int _promptTokens;
    private int _completionTokens;
    private int _cachedPromptTokens;
    private int _cacheWriteTokens;

    // Cache fields are only meaningful when the provider reported them at least once; null keeps
    // "not reported" distinct from "reported zero".
    private int _cacheDetailsReported;
    private int _disposed;

    private TokenUsageScope(TokenUsageScope? parent)
    {
        _parent = parent;
    }

    public static TokenUsageScope? Current => CurrentScope.Value;

    // Rounds whose response carried no readable usage; the sum undercounts when this is > 0.
    public int MissedRounds => Volatile.Read(ref _missedRounds);

    // Synchronous on purpose: an AsyncLocal set inside an async method does not flow back to
    // the caller, so the caller's flow must set it.
    public static TokenUsageScope Begin()
    {
        var scope = new TokenUsageScope(CurrentScope.Value);
        CurrentScope.Value = scope;
        return scope;
    }

    public void Add(int promptTokens, int completionTokens, int? cachedPromptTokens, int? cacheWriteTokens)
    {
        Interlocked.Increment(ref _rounds);
        Interlocked.Add(ref _promptTokens, promptTokens);
        Interlocked.Add(ref _completionTokens, completionTokens);

        if (cachedPromptTokens.HasValue || cacheWriteTokens.HasValue)
        {
            Interlocked.Exchange(ref _cacheDetailsReported, 1);
            Interlocked.Add(ref _cachedPromptTokens, cachedPromptTokens ?? 0);
            Interlocked.Add(ref _cacheWriteTokens, cacheWriteTokens ?? 0);
        }
    }

    public void AddMissedRound() => Interlocked.Increment(ref _missedRounds);

    public LlmTokenUsage Snapshot()
    {
        var rounds = Volatile.Read(ref _rounds);
        if (rounds == 0)
        {
            return LlmTokenUsage.None;
        }

        var cacheReported = Volatile.Read(ref _cacheDetailsReported) == 1;
        return new LlmTokenUsage
        {
            PromptTokens = Volatile.Read(ref _promptTokens),
            CompletionTokens = Volatile.Read(ref _completionTokens),
            CachedPromptTokens = cacheReported ? Volatile.Read(ref _cachedPromptTokens) : null,
            CacheWriteTokens = cacheReported ? Volatile.Read(ref _cacheWriteTokens) : null,
            Rounds = rounds,
        };
    }

    // A nested scope's requests were billed inside the outer interaction too, so roll them up.
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        CurrentScope.Value = _parent;
        _parent?.Merge(this);
    }

    private void Merge(TokenUsageScope child)
    {
        Interlocked.Add(ref _rounds, Volatile.Read(ref child._rounds));
        Interlocked.Add(ref _missedRounds, Volatile.Read(ref child._missedRounds));
        Interlocked.Add(ref _promptTokens, Volatile.Read(ref child._promptTokens));
        Interlocked.Add(ref _completionTokens, Volatile.Read(ref child._completionTokens));
        Interlocked.Add(ref _cachedPromptTokens, Volatile.Read(ref child._cachedPromptTokens));
        Interlocked.Add(ref _cacheWriteTokens, Volatile.Read(ref child._cacheWriteTokens));
        if (Volatile.Read(ref child._cacheDetailsReported) == 1)
        {
            Interlocked.Exchange(ref _cacheDetailsReported, 1);
        }
    }
}
