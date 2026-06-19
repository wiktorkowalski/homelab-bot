using System.Net;
using HomelabBot.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace HomelabBot.Tests;

public class KernelServiceRetryTests
{
    // --- IsEmptyResponse ---

    [Fact]
    public void IsEmptyResponse_Null_ReturnsTrue()
    {
        Assert.True(KernelService.IsEmptyResponse(null));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("<think>just reasoning, no answer</think>", true)]
    [InlineData("real answer", false)]
    [InlineData("<think>reasoning</think>actual answer", false)]
    public void IsEmptyResponse_ClassifiesContent(string? content, bool expected)
    {
        var response = new ChatMessageContent(AuthorRole.Assistant, content);
        Assert.Equal(expected, KernelService.IsEmptyResponse(response));
    }

    // --- IsTransient ---

    [Theory]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(504, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(402, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(422, false)]
    public void IsTransient_ClassifiesStatusCodes(int code, bool expected)
    {
        var ex = new HttpOperationException((HttpStatusCode)code, null, "boom", null);
        Assert.Equal(expected, KernelService.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_NoStatusCode_TreatedAsTransient()
    {
        // No HTTP response received (connection reset / timeout / DNS).
        var ex = new HttpOperationException((HttpStatusCode?)null, null, "transport failure", null);
        Assert.True(KernelService.IsTransient(ex));
    }

    // --- GetFinishReason ---

    [Fact]
    public void GetFinishReason_Null_ReturnsUnknown()
    {
        Assert.Equal("unknown", KernelService.GetFinishReason(null));
    }

    [Fact]
    public void GetFinishReason_NoMetadata_ReturnsUnknown()
    {
        Assert.Equal("unknown", KernelService.GetFinishReason(new ChatMessageContent(AuthorRole.Assistant, "x")));
    }

    [Fact]
    public void GetFinishReason_FromMetadata()
    {
        Assert.Equal("length", KernelService.GetFinishReason(Msg(null, "length")));
    }

    // --- BackoffDelay ---

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void BackoffDelay_WithinFullJitterBounds(int attempt)
    {
        var ceiling = Math.Min(8000, 500 * Math.Pow(2, attempt - 1));
        for (var i = 0; i < 200; i++)
        {
            var ms = KernelService.BackoffDelay(attempt).TotalMilliseconds;
            Assert.InRange(ms, 0, ceiling);
        }
    }

    // --- InvokeWithRetryAsync ---

    [Fact]
    public async Task InvokeWithRetry_FirstResponseNonEmpty_NoRetry()
    {
        var history = NewHistory();
        var calls = 0;
        Task<ChatMessageContent> Invoke(CancellationToken _)
        {
            calls++;
            return Task.FromResult(Msg("hello"));
        }

        var result = await KernelService.InvokeWithRetryAsync(
            Invoke, history, "test-model", 3, threadId: 0, NullLogger.Instance, _ => TimeSpan.Zero);

        Assert.Equal("hello", result!.Content);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task InvokeWithRetry_EmptyThenSucceeds()
    {
        var history = NewHistory();
        var calls = 0;
        Task<ChatMessageContent> Invoke(CancellationToken _)
        {
            calls++;
            return Task.FromResult(calls < 3 ? Msg(null) : Msg("recovered"));
        }

        var result = await KernelService.InvokeWithRetryAsync(
            Invoke, history, "test-model", 3, threadId: 0, NullLogger.Instance, _ => TimeSpan.Zero);

        Assert.Equal("recovered", result!.Content);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task InvokeWithRetry_AllEmpty_ReturnsLastAndLogsError()
    {
        var history = NewHistory();
        var calls = 0;
        var logger = new ListLogger();
        Task<ChatMessageContent> Invoke(CancellationToken _)
        {
            calls++;
            return Task.FromResult(Msg(null, "length"));
        }

        var result = await KernelService.InvokeWithRetryAsync(
            Invoke, history, "test-model", 3, threadId: 0, logger, _ => TimeSpan.Zero);

        Assert.True(KernelService.IsEmptyResponse(result));
        Assert.Equal(3, calls);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task InvokeWithRetry_TrimsHistoryBackToBaselineBetweenAttempts()
    {
        var history = NewHistory();
        var baseline = history.Count;
        var observedCounts = new List<int>();
        var calls = 0;
        Task<ChatMessageContent> Invoke(CancellationToken _)
        {
            observedCounts.Add(history.Count);
            calls++;
            // A non-tool partial message (e.g. SK's empty final assistant message) - safe to trim and retry.
            history.AddAssistantMessage($"partial {calls}");
            return Task.FromResult(calls < 3 ? Msg(null) : Msg("done"));
        }

        var result = await KernelService.InvokeWithRetryAsync(
            Invoke, history, "test-model", 3, threadId: 0, NullLogger.Instance, _ => TimeSpan.Zero);

        Assert.Equal("done", result!.Content);
        // Every attempt should have started from the same clean baseline, not stacked on prior junk.
        Assert.All(observedCounts, c => Assert.Equal(baseline, c));
        Assert.Equal(3, observedCounts.Count);
    }

    [Fact]
    public async Task InvokeWithRetry_EmptyAfterToolExecution_DoesNotRetry()
    {
        var history = NewHistory();
        var calls = 0;
        var logger = new ListLogger();
        Task<ChatMessageContent> Invoke(CancellationToken _)
        {
            calls++;
            // A tool actually ran (side effect happened); SK appends a Tool-role result message.
            history.AddMessage(AuthorRole.Tool, "container restarted");
            return Task.FromResult(Msg(null));
        }

        var result = await KernelService.InvokeWithRetryAsync(
            Invoke, history, "test-model", 3, threadId: 0, logger, _ => TimeSpan.Zero);

        Assert.True(KernelService.IsEmptyResponse(result));
        Assert.Equal(1, calls); // retrying would re-execute the tool
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task InvokeWithRetry_TransientAfterToolExecution_DoesNotRetry()
    {
        var history = NewHistory();
        var calls = 0;
        Task<ChatMessageContent> Invoke(CancellationToken _)
        {
            calls++;
            history.AddMessage(AuthorRole.Tool, "container restarted");
            throw new HttpOperationException(HttpStatusCode.ServiceUnavailable, null, "503", null);
        }

        await Assert.ThrowsAsync<HttpOperationException>(() => KernelService.InvokeWithRetryAsync(
            Invoke, history, "test-model", 3, threadId: 0, NullLogger.Instance, _ => TimeSpan.Zero));

        Assert.Equal(1, calls); // retrying would re-execute the tool
    }

    [Fact]
    public async Task InvokeWithRetry_TransientThenSucceeds()
    {
        var history = NewHistory();
        var calls = 0;
        Task<ChatMessageContent> Invoke(CancellationToken _)
        {
            calls++;
            if (calls == 1)
            {
                throw new HttpOperationException(HttpStatusCode.ServiceUnavailable, null, "503", null);
            }

            return Task.FromResult(Msg("ok"));
        }

        var result = await KernelService.InvokeWithRetryAsync(
            Invoke, history, "test-model", 3, threadId: 0, NullLogger.Instance, _ => TimeSpan.Zero);

        Assert.Equal("ok", result!.Content);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task InvokeWithRetry_NonTransientHttpError_ThrowsImmediately()
    {
        var history = NewHistory();
        var calls = 0;
        Task<ChatMessageContent> Invoke(CancellationToken _)
        {
            calls++;
            throw new HttpOperationException(HttpStatusCode.BadRequest, null, "400", null);
        }

        await Assert.ThrowsAsync<HttpOperationException>(() => KernelService.InvokeWithRetryAsync(
            Invoke, history, "test-model", 3, threadId: 0, NullLogger.Instance, _ => TimeSpan.Zero));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task InvokeWithRetry_AllTransient_ThrowsAfterMaxAttempts()
    {
        var history = NewHistory();
        var calls = 0;
        var logger = new ListLogger();
        Task<ChatMessageContent> Invoke(CancellationToken _)
        {
            calls++;
            throw new HttpOperationException(HttpStatusCode.GatewayTimeout, null, "504", null);
        }

        await Assert.ThrowsAsync<HttpOperationException>(() => KernelService.InvokeWithRetryAsync(
            Invoke, history, "test-model", 3, threadId: 0, logger, _ => TimeSpan.Zero));

        Assert.Equal(3, calls);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task InvokeWithRetry_Cancellation_Propagates()
    {
        var history = NewHistory();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var calls = 0;
        Task<ChatMessageContent> Invoke(CancellationToken _)
        {
            calls++;
            throw new OperationCanceledException();
        }

        await Assert.ThrowsAsync<OperationCanceledException>(() => KernelService.InvokeWithRetryAsync(
            Invoke, history, "test-model", 3, threadId: 0, NullLogger.Instance, _ => TimeSpan.Zero, cts.Token));

        Assert.Equal(1, calls);
    }

    // --- helpers ---

    private static ChatHistory NewHistory()
    {
        var history = new ChatHistory();
        history.AddSystemMessage("system");
        history.AddUserMessage("hi");
        return history;
    }

    private static ChatMessageContent Msg(string? content, string? finishReason = null)
    {
        return finishReason is null
            ? new ChatMessageContent(AuthorRole.Assistant, content)
            : new ChatMessageContent(
                AuthorRole.Assistant,
                content,
                metadata: new Dictionary<string, object?> { ["FinishReason"] = finishReason });
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
