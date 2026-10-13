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

    // --- GetStopReason / GetRefusal ---

    [Fact]
    public void GetStopReason_Null_ReturnsUnknown()
    {
        Assert.Equal("unknown", KernelService.GetStopReason(null));
    }

    [Fact]
    public void GetStopReason_NoRawMessage_ReturnsUnknown()
    {
        Assert.Equal("unknown", KernelService.GetStopReason(new ChatMessageContent(AuthorRole.Assistant, "x")));
    }

    [Fact]
    public void GetStopReason_FromRawClaudeMessage()
    {
        Assert.Equal("max_tokens", KernelService.GetStopReason(Msg(null, ClaudeResponses.EmptyAtMaxTokens())));
        Assert.Equal("end_turn", KernelService.GetStopReason(Msg("x", ClaudeResponses.Text("x"))));
    }

    [Fact]
    public void GetRefusal_OnlyForStopReasonRefusal()
    {
        Assert.Null(KernelService.GetRefusal(null));
        Assert.Null(KernelService.GetRefusal(new ChatMessageContent(AuthorRole.Assistant, "x")));
        Assert.Null(KernelService.GetRefusal(Msg(null, ClaudeResponses.EmptyAtMaxTokens())));
        Assert.Equal(("bio", "declined by policy"), KernelService.GetRefusal(Msg(null, ClaudeResponses.Refusal("bio"))));
    }

    [Fact]
    public void GetRefusal_WithoutStopDetails_StillReportsTheRefusal()
    {
        var raw = ClaudeResponses.Refusal(category: null);

        Assert.Equal(("unspecified", "no explanation given"), KernelService.GetRefusal(Msg(null, raw)));
    }

    [Fact]
    public void RefusalMessage_NamesTheCategory()
    {
        Assert.Contains("category: cyber", KernelService.RefusalMessage("cyber"), StringComparison.Ordinal);
    }

    // --- TelemetryText ---

    [Fact]
    public void TelemetryText_ToolTurnsWithoutText_ShowCallAndResult()
    {
        var call = new ChatMessageContent(AuthorRole.Assistant, [new FunctionCallContent("Docker_List", id: "toolu_1")]);
        var result = new ChatMessageContent(AuthorRole.Tool, [new FunctionResultContent("Docker_List", callId: "toolu_1", result: "3 containers")]);

        Assert.StartsWith("[tool call] Docker_List", KernelService.TelemetryText(call), StringComparison.Ordinal);
        Assert.Equal("[tool result] 3 containers", KernelService.TelemetryText(result));
        Assert.Equal("plain", KernelService.TelemetryText(new ChatMessageContent(AuthorRole.User, "plain")));
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
            return Task.FromResult(Msg(null, ClaudeResponses.EmptyAtMaxTokens()));
        }

        var result = await KernelService.InvokeWithRetryAsync(
            Invoke, history, "test-model", 3, threadId: 0, logger, _ => TimeSpan.Zero);

        Assert.True(KernelService.IsEmptyResponse(result));
        Assert.Equal(3, calls);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("stopReason=max_tokens", StringComparison.Ordinal));
        Assert.Equal(2, logger.Entries.Count(e => e.Level == LogLevel.Warning));
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
    public async Task InvokeWithRetry_Refusal_IsReturnedWithoutRetry()
    {
        var history = NewHistory();
        var calls = 0;
        var logger = new ListLogger();
        Task<ChatMessageContent> Invoke(CancellationToken _)
        {
            calls++;
            return Task.FromResult(Msg(null, ClaudeResponses.Refusal()));
        }

        var result = await KernelService.InvokeWithRetryAsync(
            Invoke, history, "test-model", 3, threadId: 0, logger, _ => TimeSpan.Zero);

        Assert.NotNull(KernelService.GetRefusal(result));
        Assert.Equal(1, calls);

        // The caller reports the refusal; the retry helper must not log it as an empty response.
        Assert.Empty(logger.Entries);
    }

    // The Anthropic SDK has already retried a transient failure, per request. A second layer here
    // would repeat whole tool loops, so every exception goes straight to the caller.
    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task InvokeWithRetry_Exception_IsNeverRetried(Type exceptionType)
    {
        var history = NewHistory();
        var calls = 0;
        Task<ChatMessageContent> Invoke(CancellationToken _)
        {
            calls++;
            throw (Exception)Activator.CreateInstance(exceptionType, "boom")!;
        }

        await Assert.ThrowsAsync(exceptionType, () => KernelService.InvokeWithRetryAsync(
            Invoke, history, "test-model", 3, threadId: 0, NullLogger.Instance, _ => TimeSpan.Zero));

        Assert.Equal(1, calls);
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

    // rawClaudeJson: the Messages API response the bridge keeps as InnerContent.
    private static ChatMessageContent Msg(string? content, string? rawClaudeJson = null) =>
        new(AuthorRole.Assistant, content)
        {
            InnerContent = rawClaudeJson is null ? null : ClaudeResponses.Parse(rawClaudeJson),
        };

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
