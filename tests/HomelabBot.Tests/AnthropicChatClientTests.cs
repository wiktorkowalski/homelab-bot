using System.Diagnostics;
using System.Text.Json.Nodes;
using Anthropic.Exceptions;
using HomelabBot.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using FunctionInvocationContext = Microsoft.SemanticKernel.FunctionInvocationContext;

namespace HomelabBot.Tests;

// The whole LLM path below KernelService: Semantic Kernel bridge, function-invocation loop,
// the two delegating clients and the Anthropic SDK, on a scripted HTTP transport.
public class AnthropicChatClientTests
{
    private readonly ScriptedClaudeHandler _handler = new();
    private readonly PingPlugin _plugin = new();
    private readonly RecordingLogger<AnthropicChatClientTests> _logger = new();
    private readonly RecordingLogger<AnthropicAttemptLogHandler> _attemptLog = new();
    private readonly Kernel _kernel;
    private readonly IChatCompletionService _chat;

    public AnthropicChatClientTests()
    {
        _kernel = ClaudeTestKernel.Build(_handler.CreateClient(_attemptLog), _plugin, _logger);
        _chat = _kernel.GetRequiredService<IChatCompletionService>();
    }

    private static PromptExecutionSettings AutoTools => new()
    {
        FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
        ExtensionData = new Dictionary<string, object> { ["max_tokens"] = 4321 },
    };

    private static PromptExecutionSettings NoTools => new()
    {
        FunctionChoiceBehavior = FunctionChoiceBehavior.None(functions: []),
    };

    [Fact]
    public async Task ToolLoop_RunsTheToolOnce_AndSendsTheThinkingBlockBackUnchanged()
    {
        _handler.Enqueue(ClaudeResponses.ToolUse()).Enqueue(ClaudeResponses.Text());
        var history = NewHistory();

        var response = await _chat.GetChatMessageContentAsync(history, AutoTools, _kernel);

        Assert.Equal("pong received", response.Content);
        Assert.Equal(1, _plugin.Calls);
        Assert.Equal(2, _handler.RequestBodies.Count);

        // Round 2 must replay the assistant turn exactly: thinking (same signature) before tool_use.
        var messages = Request(1)["messages"]!.AsArray();
        Assert.Equal(["user", "assistant", "user"], messages.Select(m => (string?)m!["role"]));
        var assistant = messages[1]!["content"]!.AsArray();
        Assert.Equal("thinking", (string?)assistant[0]!["type"]);
        Assert.Equal(ClaudeResponses.Signature, (string?)assistant[0]!["signature"]);
        Assert.Equal("tool_use", (string?)assistant[1]!["type"]);
        Assert.Equal("toolu_1", (string?)assistant[1]!["id"]);

        var result = messages[2]!["content"]!.AsArray().Single()!;
        Assert.Equal("tool_result", (string?)result["type"]);
        Assert.Equal("toolu_1", (string?)result["tool_use_id"]);
        Assert.Equal("pong", (string?)result["content"]);
    }

    [Fact]
    public async Task EveryRequest_GoesToTheClaudeApi_WithTheRulesOfTheModel()
    {
        _handler.Enqueue(ClaudeResponses.ToolUse()).Enqueue(ClaudeResponses.Text());

        await _chat.GetChatMessageContentAsync(NewHistory(), AutoTools, _kernel);

        for (var i = 0; i < _handler.RequestBodies.Count; i++)
        {
            Assert.Equal("https://api.anthropic.com/v1/messages", _handler.RequestUris[i]!.GetLeftPart(UriPartial.Path));
            Assert.Equal("test-key", _handler.RequestHeaders[i]["x-api-key"]);

            var request = Request(i);
            Assert.Equal(ClaudeResponses.Model, (string?)request["model"]);
            Assert.Equal(4321, (int?)request["max_tokens"]);
            Assert.Equal("auto", (string?)request["tool_choice"]!["type"]);
            Assert.Equal("medium", (string?)request["output_config"]!["effort"]);
            Assert.Equal("adaptive", (string?)request["thinking"]!["type"]);
            Assert.Null(request["thinking"]!["budget_tokens"]);

            // Each of these is a 400 on claude-sonnet-5-5.
            Assert.Null(request["temperature"]);
            Assert.Null(request["top_p"]);
            Assert.Null(request["top_k"]);
        }
    }

    [Fact]
    public async Task ToolLoop_PutsCacheBreakpointsOnToolsSystemAndTheMovingTail()
    {
        _handler.Enqueue(ClaudeResponses.ToolUse()).Enqueue(ClaudeResponses.Text());

        await _chat.GetChatMessageContentAsync(NewHistory(), AutoTools, _kernel);

        foreach (var request in new[] { Request(0), Request(1) })
        {
            Assert.Null(request["cache_control"]);
            Assert.Equal("ephemeral", (string?)request["system"]!.AsArray()[^1]!["cache_control"]?["type"]);

            var tools = request["tools"]!.AsArray();
            Assert.Equal("ephemeral", (string?)tools[^1]!["cache_control"]?["type"]);
            Assert.All(tools.Take(tools.Count - 1), t => Assert.Null(t!["cache_control"]));

            var messages = request["messages"]!.AsArray();
            Assert.Equal("ephemeral", (string?)messages[^1]!["content"]!.AsArray()[^1]!["cache_control"]?["type"]);

            // Only the newest message carries one: a mark left on an older message would add up
            // over the rounds and pass the API limit of four.
            Assert.All(messages.Take(messages.Count - 1), m => Assert.Equal(0, CountBreakpoints(m!)));
            Assert.Equal(3, CountBreakpoints(request));
        }

        Assert.Equal("text", (string?)Request(0)["messages"]!.AsArray()[^1]!["content"]!.AsArray()[^1]!["type"]);
        Assert.Equal("tool_result", (string?)Request(1)["messages"]!.AsArray()[^1]!["content"]!.AsArray()[^1]!["type"]);
    }

    [Fact]
    public async Task Tools_AreSentInNameOrder_SoTheCachedPrefixIsStable()
    {
        _handler.Enqueue(ClaudeResponses.Text());

        await _chat.GetChatMessageContentAsync(NewHistory(), AutoTools, _kernel);

        var names = Request(0)["tools"]!.AsArray().Select(t => (string?)t!["name"]).ToList();
        Assert.Equal(["Test_Explode", "Test_Ping", "Test_Restart", "Test_TimeOut"], names);
    }

    [Fact]
    public async Task NoToolsOffered_SendsNoToolFields_AndNoTailBreakpoint()
    {
        _handler.Enqueue(ClaudeResponses.Text());

        await _chat.GetChatMessageContentAsync(NewHistory(), NoTools, _kernel);

        var request = Request(0);
        Assert.Null(request["tools"]);
        Assert.Null(request["tool_choice"]);
        Assert.Equal("ephemeral", (string?)request["system"]!.AsArray()[^1]!["cache_control"]?["type"]);
        Assert.Equal(1, CountBreakpoints(request));
        Assert.Equal(0, _plugin.Calls);
    }

    [Fact]
    public async Task NoToolsOffered_HistoryWithAToolTurn_DeclaresThatToolByNameOnly_AndForbidsCalls()
    {
        _handler.Enqueue(ClaudeResponses.ToolUse()).Enqueue(ClaudeResponses.Text()).Enqueue(ClaudeResponses.Text("summary"));
        var history = NewHistory();
        history.Add(await _chat.GetChatMessageContentAsync(history, AutoTools, _kernel));
        history.AddUserMessage("summarize, no tools");

        var response = await _chat.GetChatMessageContentAsync(history, NoTools, _kernel);

        // The API answers 400 to tool_use / tool_result blocks in a request that defines no tool.
        Assert.Equal("summary", response.Content);
        var request = Request(2);
        var tool = request["tools"]!.AsArray().Single()!;
        Assert.Equal("Test_Ping", (string?)tool["name"]);
        Assert.Empty(tool["input_schema"]!["properties"]!.AsObject());
        Assert.Null(tool["cache_control"]);
        Assert.Equal("none", (string?)request["tool_choice"]!["type"]);

        // The tool turn keeps its structure: a tool result must not become user text.
        var messages = request["messages"]!.AsArray();
        Assert.Equal(["user", "assistant", "user", "assistant", "user"], messages.Select(m => (string?)m!["role"]));
        Assert.Equal("tool_use", (string?)messages[1]!["content"]!.AsArray().Single()!["type"]);
        Assert.Equal("tool_result", (string?)messages[2]!["content"]!.AsArray().Single()!["type"]);
        Assert.Equal(1, CountBreakpoints(request));
        Assert.Equal(1, _plugin.Calls);
    }

    [Fact]
    public async Task NextTurn_DoesNotReplayThinkingBlocksOfAFinishedTurn()
    {
        _handler.Enqueue(ClaudeResponses.ToolUse()).Enqueue(ClaudeResponses.Text()).Enqueue(ClaudeResponses.Text("again"));
        var history = NewHistory();
        history.Add(await _chat.GetChatMessageContentAsync(history, AutoTools, _kernel));
        history.AddUserMessage("and again");

        await _chat.GetChatMessageContentAsync(history, AutoTools, _kernel);

        // ConversationService trims and truncates stored turns. A replayed thinking block after
        // such an edit is rejected by the API, so stored history must not carry any.
        Assert.DoesNotContain("\"thinking\"", Request(2)["messages"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("\"tool_use\"", Request(2)["messages"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolLoop_AppendsToolTurnsToTheCallersHistory()
    {
        _handler.Enqueue(ClaudeResponses.ToolUse()).Enqueue(ClaudeResponses.Text());
        var history = NewHistory();

        await _chat.GetChatMessageContentAsync(history, AutoTools, _kernel);

        // KernelService relies on the Tool-role message to know that a tool ran, and
        // ConversationService on FunctionResultContent to cap stored results.
        Assert.Equal([AuthorRole.System, AuthorRole.User, AuthorRole.Assistant, AuthorRole.Tool], history.Select(m => m.Role));
        Assert.IsType<Microsoft.SemanticKernel.FunctionCallContent>(history[2].Items.Single());
        Assert.Equal(
            "pong",
            Assert.IsType<Microsoft.SemanticKernel.FunctionResultContent>(history[3].Items.Single()).Result?.ToString());
    }

    [Fact]
    public async Task ParallelToolCalls_ComeBackInOneUserMessage()
    {
        _handler.Enqueue(ClaudeResponses.ToolUse(tools: ["Test_Ping", "Test_Restart"])).Enqueue(ClaudeResponses.Text());

        await _chat.GetChatMessageContentAsync(NewHistory(), AutoTools, _kernel);

        Assert.Equal(1, _plugin.Calls);
        Assert.Equal(1, _plugin.RestartCalls);
        var messages = Request(1)["messages"]!.AsArray();
        Assert.Equal(3, messages.Count);
        var results = messages[2]!["content"]!.AsArray();
        Assert.Equal(["toolu_1", "toolu_2"], results.Select(r => (string?)r!["tool_use_id"]));
        Assert.Equal(3, CountBreakpoints(Request(1)));
    }

    [Fact]
    public async Task ToolThatThrows_TellsTheModelWhy_AndTheLoopGoesOn()
    {
        _handler.Enqueue(ClaudeResponses.ToolUse(tools: ["Test_Explode"])).Enqueue(ClaudeResponses.Text("it failed"));

        var response = await _chat.GetChatMessageContentAsync(NewHistory(), AutoTools, _kernel);

        Assert.Equal("it failed", response.Content);
        var result = Request(1)["messages"]!.AsArray()[2]!["content"]!.AsArray().Single()!;
        Assert.Equal("Error: docker socket unreachable", (string?)result["content"]);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Test.Explode", StringComparison.Ordinal));
    }

    [Fact]
    public async Task KernelFunctionFilters_StillRunForEveryToolCall()
    {
        var filter = new CountingFilter();
        _kernel.FunctionInvocationFilters.Add(filter);
        _handler.Enqueue(ClaudeResponses.ToolUse()).Enqueue(ClaudeResponses.Text());

        await _chat.GetChatMessageContentAsync(NewHistory(), AutoTools, _kernel);

        Assert.Equal(["Test.Ping"], filter.Invoked);
    }

    [Fact]
    public async Task Usage_IsSummedOverEveryRound_WithTheCachedPartInsidePromptTokens()
    {
        _handler
            .Enqueue(ClaudeResponses.ToolUse(input: 100, output: 20, cacheWrite: 900, cacheRead: 0))
            .Enqueue(ClaudeResponses.Text(input: 120, output: 50, cacheWrite: 0, cacheRead: 900));

        using var scope = TokenUsageScope.Begin();
        await _chat.GetChatMessageContentAsync(NewHistory(), AutoTools, _kernel);
        var usage = scope.Snapshot();

        Assert.Equal(2, usage.Rounds);
        Assert.Equal(0, scope.MissedRounds);
        Assert.Equal((100 + 900) + (120 + 900), usage.PromptTokens);
        Assert.Equal(70, usage.CompletionTokens);
        Assert.Equal(900, usage.CachedPromptTokens);
        Assert.Equal(900, usage.CacheWriteTokens);
    }

    [Fact]
    public async Task Usage_EveryRoundIsLoggedWithTheFourNativeCounts()
    {
        _handler
            .Enqueue(ClaudeResponses.ToolUse(input: 100, output: 20, cacheWrite: 900, cacheRead: 0))
            .Enqueue(ClaudeResponses.Text(input: 120, output: 50, cacheWrite: 0, cacheRead: 900));

        await _chat.GetChatMessageContentAsync(NewHistory(), AutoTools, _kernel);

        var lines = _logger.Entries.Where(e => e.Level == LogLevel.Information).Select(e => e.Message).ToList();
        Assert.Equal(
            [
                "Claude round usage for claude-sonnet-5-5: 100 input, 0 cache read, 900 cache write, 20 output, stop reason tool_use",
                "Claude round usage for claude-sonnet-5-5: 120 input, 900 cache read, 0 cache write, 50 output, stop reason end_turn",
            ],
            lines);
    }

    [Fact]
    public async Task Usage_WithoutCacheFields_LeavesCacheCountsNull()
    {
        _handler.Enqueue(ClaudeResponses.Text(input: 10, output: 5, cacheWrite: null, cacheRead: null));

        using var scope = TokenUsageScope.Begin();
        await _chat.GetChatMessageContentAsync("hi");
        var usage = scope.Snapshot();

        Assert.Equal(1, usage.Rounds);
        Assert.Equal(10, usage.PromptTokens);
        Assert.Null(usage.CachedPromptTokens);
        Assert.Null(usage.CacheWriteTokens);
    }

    [Fact]
    public async Task Usage_OutsideAScope_IsStillLoggedAndDoesNotThrow()
    {
        _handler.Enqueue(ClaudeResponses.Text());

        var response = await _chat.GetChatMessageContentAsync("hi");

        Assert.Equal("pong received", response.Content);
        Assert.Single(_logger.Entries, e => e.Message.StartsWith("Claude round usage", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Usage_ResponseWithoutARawClaudeMessage_CountsAsMissedRound()
    {
        var client = new AnthropicUsageChatClient(new FixedResponseClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "x"))), _logger);

        using var scope = TokenUsageScope.Begin();
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal(0, scope.Snapshot().Rounds);
        Assert.Equal(1, scope.MissedRounds);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task DefaultSettings_UseTheConfiguredMaxTokens()
    {
        _handler.Enqueue(ClaudeResponses.Text());

        await _chat.GetChatMessageContentAsync("hi");

        Assert.Equal(16000, (int?)Request(0)["max_tokens"]);
        Assert.Null(Request(0)["system"]);
        Assert.Equal(0, CountBreakpoints(Request(0)));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(529)]
    [InlineData(429)]
    [InlineData(408)]
    public async Task TransientStatus_IsRetriedByTheSdk_AndOnlyTheGoodRoundIsCounted(int status)
    {
        _handler.EnqueueStatus(status, times: 2).Enqueue(ClaudeResponses.Text());

        using var scope = TokenUsageScope.Begin();
        var response = await _chat.GetChatMessageContentAsync("hi");

        Assert.Equal("pong received", response.Content);
        Assert.Equal(3, _handler.RequestBodies.Count);
        Assert.Equal(1, scope.Snapshot().Rounds);

        var warnings = AttemptWarnings();
        Assert.Equal(2, warnings.Count);
        Assert.Contains("attempt 1 ", warnings[0], StringComparison.Ordinal);
        Assert.Contains($"status {status} ", warnings[0], StringComparison.Ordinal);
        Assert.Contains("req_test", warnings[0], StringComparison.Ordinal);
        Assert.Contains("retry-after 1 ms", warnings[0], StringComparison.Ordinal);
        Assert.Contains("attempt 2 ", warnings[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectionFailure_IsRetriedByTheSdk()
    {
        _handler.EnqueueConnectionFailure(times: 2).Enqueue(ClaudeResponses.Text());

        var response = await _chat.GetChatMessageContentAsync("hi");

        Assert.Equal("pong received", response.Content);
        Assert.Equal(3, _handler.RequestBodies.Count);

        var warnings = AttemptWarnings();
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.Contains("scripted connection reset", w, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TransientStatus_ThatStays_FailsAfterThreeRequests()
    {
        _handler.EnqueueStatus(529, times: 5);

        await Assert.ThrowsAsync<Anthropic5xxException>(() => _chat.GetChatMessageContentAsync("hi"));

        Assert.Equal(3, _handler.RequestBodies.Count);

        var warnings = AttemptWarnings();
        Assert.Equal(3, warnings.Count);
        for (var i = 0; i < warnings.Count; i++)
        {
            Assert.Contains($"attempt {i + 1} ", warnings[i], StringComparison.Ordinal);
        }
    }

    // The attempt number in the log line comes from this SDK header. An SDK upgrade that drops it
    // fails here, not silently as "attempt 1" on every line.
    [Fact]
    public async Task RetriedCall_CarriesTheSdkRetryCountHeader()
    {
        _handler.EnqueueStatus(529, times: 2).Enqueue(ClaudeResponses.Text());

        await _chat.GetChatMessageContentAsync("hi");

        Assert.Equal(["0", "1", "2"], _handler.RequestHeaders.Select(h => h["x-stainless-retry-count"]));
    }

    [Fact]
    public async Task SuccessfulCall_WritesNoAttemptLine()
    {
        _handler.Enqueue(ClaudeResponses.Text());

        await _chat.GetChatMessageContentAsync("hi");

        Assert.Empty(_attemptLog.Entries);
    }

    [Fact]
    public async Task CallerCancel_WritesOneTimeoutOrCancelLine()
    {
        using var caller = new CancellationTokenSource();
        _handler.EnqueueCallerCancel(caller);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _chat.GetChatMessageContentAsync("hi", cancellationToken: caller.Token));

        var warning = Assert.Single(AttemptWarnings());
        Assert.Contains("timeout or cancel", warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(400, typeof(AnthropicBadRequestException))]
    [InlineData(401, typeof(AnthropicUnauthorizedException))]
    public async Task PermanentStatus_IsNotRetried(int status, Type expected)
    {
        _handler.EnqueueStatus(status, times: 3);

        var ex = await Assert.ThrowsAnyAsync<AnthropicApiException>(() => _chat.GetChatMessageContentAsync("hi"));

        Assert.IsType(expected, ex);
        Assert.Single(_handler.RequestBodies);
        Assert.Empty(_attemptLog.Entries);
    }

    [Fact]
    public async Task TransientStatus_InRoundTwo_IsRetriedWithoutRunningTheToolAgain()
    {
        _handler.Enqueue(ClaudeResponses.ToolUse(tools: ["Test_Restart"])).EnqueueStatus(529, times: 2).Enqueue(ClaudeResponses.Text());

        var response = await _chat.GetChatMessageContentAsync(NewHistory(), AutoTools, _kernel);

        Assert.Equal("pong received", response.Content);
        Assert.Equal(4, _handler.RequestBodies.Count);
        Assert.Equal(1, _plugin.RestartCalls);
    }

    [Fact]
    public async Task Refusal_ComesBackAsAnEmptyMessageWithTheRawStopDetails()
    {
        _handler.Enqueue(ClaudeResponses.Refusal("cyber"));

        var response = await _chat.GetChatMessageContentAsync("hi");

        Assert.True(string.IsNullOrEmpty(response.Content));
        Assert.Equal("refusal", KernelService.GetStopReason(response));
        Assert.Equal(("cyber", "declined by policy"), KernelService.GetRefusal(response));
    }

    [Fact]
    public async Task EveryRound_GetsItsOwnChatSpan_WithUsageAndTheToolSpanBetweenThem()
    {
        using var root = new Activity("test-root").Start();
        using var spans = new SpanCollector(
            root.TraceId,
            s => s.Name == TelemetryConstants.LlmActivitySourceName || s.Name.StartsWith("Microsoft.SemanticKernel", StringComparison.Ordinal));
        _handler
            .Enqueue(ClaudeResponses.ToolUse(input: 100, output: 20, cacheWrite: 900, cacheRead: 0))
            .Enqueue(ClaudeResponses.Text(input: 120, output: 50, cacheWrite: 0, cacheRead: 900));

        await _chat.GetChatMessageContentAsync(NewHistory(), AutoTools, _kernel);

        var mine = spans.Snapshot();

        var chats = mine.Where(s => (string?)s.GetTagItem("gen_ai.operation.name") == "chat").ToList();
        Assert.Equal(2, chats.Count);
        Assert.All(chats, s => Assert.Equal(ClaudeResponses.Model, s.GetTagItem("gen_ai.request.model")));
        Assert.Equal([1000, 1020], chats.Select(s => Convert.ToInt32(s.GetTagItem("gen_ai.usage.input_tokens"))));
        Assert.Equal([20, 50], chats.Select(s => Convert.ToInt32(s.GetTagItem("gen_ai.usage.output_tokens"))));
        Assert.Equal([900, 0], chats.Select(s => Convert.ToInt32(s.GetTagItem(AnthropicUsageChatClient.CacheCreationTag))));
        Assert.All(chats, s => Assert.NotNull(s.GetTagItem("gen_ai.input.messages")));
        Assert.All(chats, s => Assert.NotNull(s.GetTagItem("gen_ai.output.messages")));

        // No wrapper spans from the loop: the tool span and the chat spans sit side by side under
        // the caller's span, and no span repeats the token counts of the rounds.
        var tool = Assert.Single(mine, s => (string?)s.GetTagItem("gen_ai.operation.name") == "execute_tool");
        Assert.Equal("Microsoft.SemanticKernel", tool.Source.Name);
        Assert.Equal(3, mine.Count);
        Assert.All(mine, s => Assert.Equal(root.SpanId, s.ParentSpanId));
    }

    [Fact]
    public void Shape_DoesNotTouchTheCallersMessagesOrOptions()
    {
        var system = new ChatMessage(ChatRole.System, "sys");
        var user = new ChatMessage(ChatRole.User, "hi");
        var tool = AIFunctionFactory.Create(() => "x", "b_tool");
        var options = new ChatOptions { Tools = [tool, AIFunctionFactory.Create(() => "y", "a_tool")] };

        var (messages, shaped) = AnthropicRequestChatClient.Shape([system, user], options, ReasoningEffort.Low);

        Assert.False(HasBreakpoint(system.Contents[0]));
        Assert.False(HasBreakpoint(user.Contents[0]));
        Assert.Null(options.Reasoning);
        Assert.Equal(["b_tool", "a_tool"], options.Tools.Select(t => t.Name));
        Assert.Same(tool, options.Tools[0]);

        Assert.True(HasBreakpoint(messages[0].Contents[0]));
        Assert.True(HasBreakpoint(messages[1].Contents[0]));
        Assert.Equal(ReasoningEffort.Low, shaped.Reasoning!.Effort);
        Assert.Equal(["a_tool", "b_tool"], shaped.Tools!.Select(t => t.Name));
    }

    [Fact]
    public void Shape_LastRoundOfTheLoop_KeepsToolBlocksButDropsThinkingBlocks()
    {
        // What the loop sends when it hits its round limit: the tools are gone, the messages
        // still hold the thinking blocks of the rounds before.
        var assistant = new ChatMessage(ChatRole.Assistant,
        [
            new TextReasoningContent(string.Empty) { ProtectedData = ClaudeResponses.Signature },
            new Microsoft.Extensions.AI.FunctionCallContent("toolu_1", "Test_Ping"),
        ]);
        var tool = new ChatMessage(ChatRole.Tool, [new Microsoft.Extensions.AI.FunctionResultContent("toolu_1", "pong")]);

        var (messages, shaped) = AnthropicRequestChatClient.Shape(
            [new ChatMessage(ChatRole.User, "hi"), assistant, tool], new ChatOptions(), ReasoningEffort.Medium);

        // A thinking block is bound to the tool list it was produced with; the list is now the
        // name-only declarations, so a replayed block would be rejected.
        Assert.Equal(["Test_Ping"], shaped.Tools!.Select(t => t.Name));
        Assert.Equal(ChatToolMode.None, shaped.ToolMode);
        Assert.DoesNotContain(messages.SelectMany(m => m.Contents), c => c is TextReasoningContent);
        Assert.IsType<Microsoft.Extensions.AI.FunctionCallContent>(messages[1].Contents.Single());
        Assert.IsType<Microsoft.Extensions.AI.FunctionResultContent>(messages[2].Contents.Single());
        Assert.Equal(2, assistant.Contents.Count);
    }

    [Fact]
    public async Task ToolThatTimesOut_TellsTheModelWhy_LikeAnyOtherFailure()
    {
        _handler.Enqueue(ClaudeResponses.ToolUse(tools: ["Test_TimeOut"])).Enqueue(ClaudeResponses.Text("it timed out"));

        var response = await _chat.GetChatMessageContentAsync(NewHistory(), AutoTools, _kernel);

        Assert.Equal("it timed out", response.Content);
        var result = Request(1)["messages"]!.AsArray()[2]!["content"]!.AsArray().Single()!;
        Assert.Equal(
            "Error: The invocation of function 'TimeOut' was canceled. (docker request timed out)",
            (string?)result["content"]);
    }

    [Fact]
    public async Task RefusalAfterAToolCall_EndsTheLoop_AndTheToolDoesNotRun()
    {
        _handler.Enqueue(ClaudeResponses.RefusalAfterToolUse("Test_Restart")).Enqueue(ClaudeResponses.Text("must not be asked for"));

        var response = await _chat.GetChatMessageContentAsync(NewHistory(), AutoTools, _kernel);

        Assert.Equal(0, _plugin.RestartCalls);
        Assert.Single(_handler.RequestBodies);
        Assert.Equal(("cyber", "declined by policy"), KernelService.GetRefusal(response));
    }

    [Fact]
    public void ApiClient_AlwaysTargetsThePublicApi()
    {
        Assert.Equal("https://api.anthropic.com", AnthropicChatClientFactory.CreateApiClient("k").BaseUrl.TrimEnd('/'));
    }

    [Fact]
    public void Shape_KeepsAnEffortTheCallerSet()
    {
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } };

        var (_, shaped) = AnthropicRequestChatClient.Shape([new ChatMessage(ChatRole.User, "hi")], options, ReasoningEffort.Low);

        Assert.Equal(ReasoningEffort.High, shaped.Reasoning!.Effort);
    }

    // The tag is written by this repo and the request by the SDK: both must name the same level.
    [Theory]
    [InlineData(ReasoningEffort.Low)]
    [InlineData(ReasoningEffort.Medium)]
    [InlineData(ReasoningEffort.High)]
    [InlineData(ReasoningEffort.ExtraHigh)]
    public async Task ChatSpan_CarriesTheEffortTheRequestWasSentWith(ReasoningEffort effort)
    {
        using var root = new Activity("test-root").Start();
        using var spans = new SpanCollector(root.TraceId, s => s.Name == TelemetryConstants.LlmActivitySourceName);
        var kernel = ClaudeTestKernel.Build(_handler.CreateClient(), _plugin, _logger, effort: effort);
        _handler.Enqueue(ClaudeResponses.Text());

        await kernel.GetRequiredService<IChatCompletionService>().GetChatMessageContentAsync(NewHistory(), NoTools, kernel);

        var sent = (string?)Request(0)["output_config"]!["effort"];
        Assert.NotNull(sent);
        Assert.Equal(sent, Assert.Single(spans.Snapshot()).GetTagItem(AnthropicRequestChatClient.EffortTag));
    }

    [Fact]
    public async Task EffortTag_WithoutAnLlmSpan_StaysOffTheCallersSpan()
    {
        using var foreign = new Activity("test-root").Start();
        var client = new AnthropicRequestChatClient(
            new FixedResponseClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "x"))), ReasoningEffort.Medium);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Null(foreign.GetTagItem(AnthropicRequestChatClient.EffortTag));
    }

    private static ChatHistory NewHistory() => ClaudeTestKernel.NewHistory();

    private static bool HasBreakpoint(AIContent content) =>
        content.AdditionalProperties?.Values.Any(v => v is Anthropic.Models.Messages.CacheControlEphemeral) == true;

    private static int CountBreakpoints(JsonNode node) => node switch
    {
        JsonObject obj => (obj.ContainsKey("cache_control") ? 1 : 0) + obj.Sum(p => p.Value is null ? 0 : CountBreakpoints(p.Value)),
        JsonArray arr => arr.Sum(n => n is null ? 0 : CountBreakpoints(n)),
        _ => 0,
    };

    private JsonObject Request(int index) => _handler.Requests[index];

    private List<string> AttemptWarnings() =>
        _attemptLog.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();

    private sealed class CountingFilter : IFunctionInvocationFilter
    {
        public List<string> Invoked { get; } = [];

        public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
        {
            Invoked.Add($"{context.Function.PluginName}.{context.Function.Name}");
            await next(context);
        }
    }

    private sealed class FixedResponseClient(ChatResponse response) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(response);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
