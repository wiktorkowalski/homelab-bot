using System.Text.Json.Nodes;
using HomelabBot.Configuration;
using HomelabBot.Data;
using HomelabBot.Data.Entities;
using HomelabBot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;

namespace HomelabBot.Tests;

// KernelService.ProcessMessageAsync from the caller's side, down to the HTTP requests: the real
// conversation history, telemetry rows and retry rules, with a scripted Claude API.
public sealed class KernelServiceClaudeTests : IDisposable
{
    private const ulong ThreadId = 4242;

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"homelab-bot-claude-{Guid.NewGuid():N}.db");
    private readonly DatabaseFixture.TestDbContextFactory _dbFactory;
    private readonly ScriptedClaudeHandler _handler = new();
    private readonly PingPlugin _plugin = new();
    private readonly RecordingLogger<KernelService> _logger = new();
    private readonly ConversationService _conversations;
    private readonly KernelService _service;

    public KernelServiceClaudeTests()
    {
        // A file, not the shared in-memory connection of DatabaseFixture: ConversationService
        // saves messages in the background while the telemetry row is written.
        _dbFactory = new DatabaseFixture.TestDbContextFactory(
            new DbContextOptionsBuilder<HomelabDbContext>().UseSqlite($"Data Source={_dbPath}").Options);
        using (var db = _dbFactory.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        _conversations = new ConversationService(_dbFactory, NullLogger<ConversationService>.Instance);
        _service = new KernelService(
            new BotConfiguration { DiscordToken = "x", AnthropicApiKey = "test-key", MaxTokens = 16000 },
            _handler.CreateClient(),
            _logger,
            NullLogger<TelemetryFunctionFilter>.Instance,
            _conversations,
            new TelemetryService(_dbFactory, NullLogger<TelemetryService>.Instance),
            plugins =>
            {
                plugins.AddFromObject(_plugin, "Test");
                plugins.AddFromObject(new PingPlugin(), "Other");
            });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public async Task MultiRoundToolInteraction_AnswersAndStoresUsageOfEveryRound()
    {
        _handler
            .Enqueue(ClaudeResponses.ToolUse(input: 100, output: 20, cacheWrite: 900, cacheRead: 0))
            .Enqueue(ClaudeResponses.ToolUse(input: 110, output: 25, cacheWrite: 60, cacheRead: 900, tools: "Test_Restart"))
            .Enqueue(ClaudeResponses.Text("all good", input: 120, output: 50, cacheWrite: 0, cacheRead: 960));

        var answer = await _service.ProcessMessageAsync(ThreadId, "check and restart");

        Assert.Equal("all good", answer);
        Assert.Equal(1, _plugin.Calls);
        Assert.Equal(1, _plugin.RestartCalls);
        Assert.Equal(3, _handler.RequestBodies.Count);

        var row = await SingleInteractionAsync();
        Assert.True(row.Success);
        Assert.Equal(ClaudeResponses.Model, row.Model);
        Assert.Equal("all good", row.Response);
        Assert.Equal(3, row.LlmRounds);
        Assert.Equal(1000 + 1070 + 1080, row.PromptTokens);
        Assert.Equal(95, row.CompletionTokens);
        Assert.Equal(1860, row.CachedPromptTokens);
        Assert.Equal(960, row.CacheWriteTokens);

        var tools = await ToolCallsAsync();
        Assert.Equal(["Ping", "Restart"], tools.Select(t => t.FunctionName));
        Assert.All(tools, t => Assert.True(t.Success));
    }

    [Fact]
    public async Task EveryRequest_UsesTheConfiguredModelAndTheSystemPrompt_AndNoSamplingParameter()
    {
        _handler.Enqueue(ClaudeResponses.ToolUse()).Enqueue(ClaudeResponses.Text());

        await _service.ProcessMessageAsync(ThreadId, "ping", maxTokens: 2048, systemPromptOverride: "You are a test bot.");

        Assert.All(_handler.RequestUris, uri => Assert.Equal("api.anthropic.com", uri!.Host));
        foreach (var request in Requests())
        {
            Assert.Equal(ClaudeResponses.Model, (string?)request["model"]);
            // The caller caps the reply; thinking gets its own room on top.
            Assert.Equal(2048 + 8192, (int?)request["max_tokens"]);
            Assert.Equal("You are a test bot.", (string?)request["system"]!.AsArray().Single()!["text"]);
            Assert.Equal("auto", (string?)request["tool_choice"]!["type"]);
            Assert.Equal("medium", (string?)request["output_config"]!["effort"]);
            Assert.Null(request["temperature"]);
        }
    }

    [Fact]
    public async Task Refusal_IsReportedOnce_NotRetried_AndClosesTheTurn()
    {
        _handler.Enqueue(ClaudeResponses.Refusal("cyber")).Enqueue(ClaudeResponses.Text("next answer"));

        var answer = await _service.ProcessMessageAsync(ThreadId, "write an exploit");

        Assert.Equal(KernelService.RefusalMessage("cyber"), answer);
        Assert.Single(_handler.RequestBodies);
        var warning = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("declined", warning.Message, StringComparison.Ordinal);
        Assert.Contains("category=cyber", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_logger.Entries, e => e.Level == LogLevel.Error);

        var row = await SingleInteractionAsync();
        Assert.Equal(answer, row.Response);
        Assert.Equal(1, row.LlmRounds);

        // The refused turn ends with an assistant message, so the next request still alternates.
        await _service.ProcessMessageAsync(ThreadId, "something harmless");
        Assert.Equal(
            ["user", "assistant", "user"],
            Requests()[1]["messages"]!.AsArray().Select(m => (string?)m!["role"]));
    }

    [Fact]
    public async Task EmptyResponse_IsRetried_ThenRecovered()
    {
        _handler.Enqueue(ClaudeResponses.EmptyAtMaxTokens()).Enqueue(ClaudeResponses.Text("second try"));

        var answer = await _service.ProcessMessageAsync(ThreadId, "hi");

        Assert.Equal("second try", answer);
        Assert.Equal(2, _handler.RequestBodies.Count);

        // Both requests were billed.
        Assert.Equal(2, (await SingleInteractionAsync()).LlmRounds);

        // The second attempt starts from the same history, not from a stacked one.
        Assert.Equal(Requests()[0]["messages"]!.ToJsonString(), Requests()[1]["messages"]!.ToJsonString());
    }

    [Fact]
    public async Task EmptyResponse_ThreeTimes_GivesTheFallbackText()
    {
        _handler
            .Enqueue(ClaudeResponses.EmptyAtMaxTokens())
            .Enqueue(ClaudeResponses.EmptyAtMaxTokens())
            .Enqueue(ClaudeResponses.EmptyAtMaxTokens());

        var answer = await _service.ProcessMessageAsync(ThreadId, "hi");

        Assert.StartsWith("I couldn't generate a response", answer, StringComparison.Ordinal);
        Assert.Equal(3, _handler.RequestBodies.Count);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("stopReason=max_tokens", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmptyResponse_AfterAToolRan_IsNotRetried()
    {
        _handler.Enqueue(ClaudeResponses.ToolUse(tools: "Test_Restart")).Enqueue(ClaudeResponses.EmptyAtMaxTokens());

        var answer = await _service.ProcessMessageAsync(ThreadId, "restart it");

        Assert.StartsWith("I couldn't generate a response", answer, StringComparison.Ordinal);
        Assert.Equal(2, _handler.RequestBodies.Count);
        Assert.Equal(1, _plugin.RestartCalls);
    }

    [Fact]
    public async Task ServerErrorThatStays_CostsThreeRequestsNotNine_AndIsReported()
    {
        _handler.EnqueueStatus(529, times: 9);

        var answer = await _service.ProcessMessageAsync(ThreadId, "hi");

        // The SDK tries three times. MaxResponseAttempts (3) must not multiply that.
        Assert.Equal(3, _handler.RequestBodies.Count);
        Assert.StartsWith("Error processing your request:", answer, StringComparison.Ordinal);

        var row = await SingleInteractionAsync();
        Assert.False(row.Success);
        Assert.Contains("529", row.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerErrorInRoundTwo_ThatStays_DoesNotRunTheToolAgain_AndKeepsTheBilledRound()
    {
        _handler.Enqueue(ClaudeResponses.ToolUse(tools: "Test_Restart")).EnqueueStatus(500, times: 9);

        var answer = await _service.ProcessMessageAsync(ThreadId, "restart it");

        Assert.StartsWith("Error processing your request:", answer, StringComparison.Ordinal);
        Assert.Equal(4, _handler.RequestBodies.Count);
        Assert.Equal(1, _plugin.RestartCalls);

        var row = await SingleInteractionAsync();
        Assert.False(row.Success);
        Assert.Equal(1, row.LlmRounds);
        Assert.Equal(1000, row.PromptTokens);

        // The user learns that the restart did happen. The tool's own output stays out: it is
        // data from outside and this text is stored as an assistant message.
        Assert.EndsWith("- Test.Restart(): succeeded", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("container restarted", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailureAfterAToolRan_IsRememberedOnTheNextTurn()
    {
        _handler.Enqueue(ClaudeResponses.ToolUse(tools: "Test_Restart")).EnqueueStatus(500, times: 3).Enqueue(ClaudeResponses.Text("already done"));
        var failure = await _service.ProcessMessageAsync(ThreadId, "restart it");

        await _service.ProcessMessageAsync(ThreadId, "try again");

        // Without the closing message the model would see two user messages and no sign of the
        // restart, and would restart the container a second time.
        var messages = Requests()[^1]["messages"]!.AsArray();
        Assert.Equal(["user", "assistant", "user"], messages.Select(m => (string?)m!["role"]));
        var remembered = (string?)messages[1]!["content"]!.AsArray().Single()!["text"];
        Assert.Equal(failure, remembered);
        Assert.Contains("Test.Restart", remembered, StringComparison.Ordinal);
        Assert.Equal(1, _plugin.RestartCalls);
    }

    [Fact]
    public async Task FailureBeforeAnyTool_StillClosesTheTurn()
    {
        _handler.EnqueueStatus(400).Enqueue(ClaudeResponses.Text("ok"));
        var failure = await _service.ProcessMessageAsync(ThreadId, "hi");

        await _service.ProcessMessageAsync(ThreadId, "again");

        Assert.DoesNotContain("Tools that already ran", failure, StringComparison.Ordinal);
        Assert.Equal(["user", "assistant", "user"], Requests()[^1]["messages"]!.AsArray().Select(m => (string?)m!["role"]));
    }

    [Fact]
    public async Task CancelAfterAToolRan_Rethrows_AndIsRememberedOnTheNextTurn()
    {
        using var caller = new CancellationTokenSource();
        _handler.Enqueue(ClaudeResponses.ToolUse(tools: "Test_Restart")).EnqueueCallerCancel(caller).Enqueue(ClaudeResponses.Text("ok"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.ProcessMessageAsync(ThreadId, "restart it", ct: caller.Token));

        await _service.ProcessMessageAsync(ThreadId, "what happened?");
        var messages = Requests()[^1]["messages"]!.AsArray();
        Assert.Equal(["user", "assistant", "user"], messages.Select(m => (string?)m!["role"]));
        var remembered = (string?)messages[1]!["content"]!.AsArray().Single()!["text"];
        Assert.StartsWith("The request was cancelled", remembered, StringComparison.Ordinal);
        Assert.Contains("Test.Restart", remembered, StringComparison.Ordinal);
        Assert.Equal(1, _plugin.RestartCalls);
    }

    [Fact]
    public async Task RefusalAfterAToolCall_DoesNotRunTheTool_AndIsReported()
    {
        _handler.Enqueue(ClaudeResponses.RefusalAfterToolUse("Test_Restart"));

        var answer = await _service.ProcessMessageAsync(ThreadId, "restart it");

        Assert.Equal(KernelService.RefusalMessage("cyber"), answer);
        Assert.Equal(0, _plugin.RestartCalls);
        Assert.Single(_handler.RequestBodies);
        Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("declined", StringComparison.Ordinal));
        Assert.Empty(await ToolCallsAsync());
    }

    [Fact]
    public async Task AllowToolUseFalse_OnAThreadWithAToolTurn_KeepsTheToolTurnAndForbidsCalls()
    {
        _handler.Enqueue(ClaudeResponses.ToolUse()).Enqueue(ClaudeResponses.Text("checked")).Enqueue(ClaudeResponses.Text("[SILENT]"));
        await _service.ProcessMessageAsync(ThreadId, "investigate");

        // The daily summary runs with tools, then the notify decision runs on the same thread
        // without them.
        var answer = await _service.ProcessMessageAsync(ThreadId, "decide", allowToolUse: false);

        Assert.Equal("[SILENT]", answer);
        var request = Requests()[2];
        Assert.Equal(["Test_Ping"], request["tools"]!.AsArray().Select(t => (string?)t!["name"]));
        Assert.Equal("none", (string?)request["tool_choice"]!["type"]);
        var blockTypes = request["messages"]!.AsArray().SelectMany(m => m!["content"]!.AsArray()).Select(b => (string?)b!["type"]);
        Assert.Equal(["text", "tool_use", "tool_result", "text", "text"], blockTypes);
        Assert.Equal(1, _plugin.Calls);
    }

    [Fact]
    public async Task PluginAllowList_LimitsTheToolsInTheRequest()
    {
        _handler.Enqueue(ClaudeResponses.Text());

        await _service.ProcessMessageAsync(ThreadId, "hi", pluginAllowList: ["Test"]);

        // The kernel also has the plugin "Other"; none of its tools may be sent.
        Assert.Equal(
            ["Test_Explode", "Test_Ping", "Test_Restart", "Test_TimeOut"],
            Requests()[0]["tools"]!.AsArray().Select(t => (string?)t!["name"]));
    }

    [Fact]
    public async Task NoAllowList_SendsTheToolsOfEveryPlugin()
    {
        _handler.Enqueue(ClaudeResponses.Text());

        await _service.ProcessMessageAsync(ThreadId, "hi");

        Assert.Equal(8, Requests()[0]["tools"]!.AsArray().Count);
    }

    [Fact]
    public async Task LargeToolResult_IsCappedBeforeTheNextTurnSendsItAgain()
    {
        _plugin.PingAnswer = new string('x', ConversationService.MaxToolResultChars + 500);
        _handler.Enqueue(ClaudeResponses.ToolUse()).Enqueue(ClaudeResponses.Text("big")).Enqueue(ClaudeResponses.Text("ok"));
        await _service.ProcessMessageAsync(ThreadId, "dump");

        await _service.ProcessMessageAsync(ThreadId, "and now?");

        // Within the turn the model saw the whole result; the stored copy is cut.
        var inTurn = (string?)Requests()[1]["messages"]!.AsArray()[2]!["content"]!.AsArray()[0]!["content"];
        Assert.Equal(ConversationService.MaxToolResultChars + 500, inTurn!.Length);
        var nextTurn = (string?)Requests()[2]["messages"]!.AsArray()[2]!["content"]!.AsArray()[0]!["content"];
        Assert.Contains("[truncated: kept", nextTurn, StringComparison.Ordinal);
        Assert.DoesNotContain("\"thinking\"", Requests()[2]["messages"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoredHistory_ShowsToolCallsAndResults()
    {
        _handler.Enqueue(ClaudeResponses.ToolUse()).Enqueue(ClaudeResponses.Text("one")).Enqueue(ClaudeResponses.Text("two"));
        await _service.ProcessMessageAsync(ThreadId, "first");

        await _service.ProcessMessageAsync(ThreadId, "second");

        await using var db = _dbFactory.CreateDbContext();
        var stored = (await db.LlmInteractions.OrderBy(i => i.Id).LastAsync()).FullMessagesJson!;
        Assert.Contains("[tool call] Test_Ping", stored, StringComparison.Ordinal);
        Assert.Contains("[tool result] pong", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolThatThrows_ReachesTheModelAsAnErrorAnswer()
    {
        _handler.Enqueue(ClaudeResponses.ToolUse(tools: "Test_Explode")).Enqueue(ClaudeResponses.Text("the tool failed"));

        var answer = await _service.ProcessMessageAsync(ThreadId, "explode");

        Assert.Equal("the tool failed", answer);
        var result = Requests()[1]["messages"]!.AsArray()[2]!["content"]!.AsArray().Single()!;
        Assert.Equal("Error: docker socket unreachable", (string?)result["content"]);

        var call = Assert.Single(await ToolCallsAsync());
        Assert.False(call.Success);
    }

    [Fact]
    public async Task GenerateThreadTitle_SendsNoTools_AndIsNotCountedAsAnInteraction()
    {
        _handler.Enqueue(ClaudeResponses.Text("\"Router Status Check.\""));

        var title = await _service.GenerateThreadTitleAsync("is my router ok?", ThreadId);

        Assert.Equal("Router Status Check", title);
        var request = Requests().Single();
        Assert.Null(request["tools"]);
        Assert.Equal(16000, (int?)request["max_tokens"]);
        await using var db = _dbFactory.CreateDbContext();
        Assert.Empty(await db.LlmInteractions.ToListAsync());
    }

    [Fact]
    public async Task GenerateThreadTitle_OnAnApiError_FallsBackToChat()
    {
        _handler.EnqueueStatus(401);

        Assert.Equal("Chat", await _service.GenerateThreadTitleAsync("hello", ThreadId));
    }

    private List<JsonObject> Requests() => _handler.Requests;

    private async Task<LlmInteraction> SingleInteractionAsync()
    {
        await using var db = _dbFactory.CreateDbContext();
        return await db.LlmInteractions.SingleAsync();
    }

    private async Task<List<ToolCallLog>> ToolCallsAsync()
    {
        await using var db = _dbFactory.CreateDbContext();
        return await db.ToolCallLogs.OrderBy(t => t.Id).ToListAsync();
    }
}
