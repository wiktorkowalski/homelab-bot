namespace HomelabBot.Services;

// Discord shows "typing" for about 10 s per trigger, and a turn with tools can take a minute.
// The indicator triggers again until it is disposed. It never throws into the caller: it is
// cosmetic, and a failed trigger must not fail the reply.
internal sealed class TypingIndicator : IAsyncDisposable
{
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(8);

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private TypingIndicator(Func<Task> trigger, ILogger logger, TimeSpan interval)
    {
        _loop = RunAsync(trigger, logger, interval, _cts.Token);
    }

    public static TypingIndicator Start(Func<Task> trigger, ILogger logger, TimeSpan? interval = null) =>
        new(trigger, logger, interval ?? DefaultInterval);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await _loop;
        _cts.Dispose();
    }

    private static async Task RunAsync(Func<Task> trigger, ILogger logger, TimeSpan interval, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await trigger();
                await Task.Delay(interval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Disposed: the reply is ready.
        }
        catch (Exception ex)
        {
            // One warning, then stop: a missing permission would fail every trigger of the turn.
            logger.LogWarning(ex, "Could not show the typing indicator; continuing without it");
        }
    }
}
