using HomelabBot.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomelabBot.Tests;

public sealed class TypingIndicatorTests
{
    [Fact]
    public async Task KeepsTriggering_UntilDisposed()
    {
        var calls = 0;
        var thirdCall = new TaskCompletionSource();

        await using (TypingIndicator.Start(Trigger, NullLogger.Instance, TimeSpan.FromMilliseconds(5)))
        {
            await thirdCall.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var afterDispose = Volatile.Read(ref calls);
        await Task.Delay(50);
        Assert.Equal(afterDispose, Volatile.Read(ref calls));

        Task Trigger()
        {
            if (Interlocked.Increment(ref calls) == 3)
            {
                thirdCall.TrySetResult();
            }

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task TriggerThatThrows_NeverReachesTheCaller()
    {
        var calls = 0;

        var indicator = TypingIndicator.Start(
            () =>
            {
                Interlocked.Increment(ref calls);
                throw new HttpRequestException("discord is down");
            },
            NullLogger.Instance,
            TimeSpan.FromMilliseconds(5));
        await Task.Delay(50);
        await indicator.DisposeAsync();

        // A failing trigger stops the loop; it does not retry every interval.
        Assert.Equal(1, calls);
    }
}
