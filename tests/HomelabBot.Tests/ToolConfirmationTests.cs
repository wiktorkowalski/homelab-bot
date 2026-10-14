using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomelabBot.Plugins;
using HomelabBot.Services;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace HomelabBot.Tests;

// A chat turn runs a tool that changes the real world only after the user of the turn approves it.
// The Discord step is replaced by an ask that answers at once.
public sealed class ToolConfirmationTests : IDisposable
{
    private const ulong ThreadId = 5151;

    private readonly ScriptedClaudeHandler _handler = new();
    private readonly KernelServiceHarness _harness;
    private readonly MemoPlugin _memo = new();
    private readonly List<string> _asked = [];

    public ToolConfirmationTests()
    {
        _harness = new KernelServiceHarness(
            _handler.CreateClient(), morePlugins: (plugins, _) => plugins.AddFromObject(_memo, "Memo"));
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task Approved_TheToolRuns_AfterTheUserWasAskedWithItsArguments()
    {
        ScriptRestart("restarted");
        using var scope = AnswerWith(ToolConfirmationDecision.Approved);

        var answer = await _harness.Service.ProcessMessageAsync(ThreadId, "restart it");

        Assert.Equal("restarted", answer);
        Assert.Equal(1, _harness.Plugin.RestartCalls);
        Assert.Equal(["""Test.Restart {"container":"plex"}"""], _asked);
        Assert.Equal("container restarted", ToolResultSentInRound(1));
    }

    // Padding, a line break or a backtick must not hide the rest of the call from the owner.
    [Fact]
    public async Task ArgumentsAreShownInFull_WithLineBreaksAndBackticksEscaped()
    {
        var hidden = $"plex{new string(' ', 300)}\n\n```\nand-also-db";
        _handler
            .Enqueue(ClaudeResponses.ToolUseWithInput("Test_Restart", JsonSerializer.Serialize(new { container = hidden })))
            .Enqueue(ClaudeResponses.Text("restarted"));
        using var scope = AnswerWith(ToolConfirmationDecision.Approved);

        await _harness.Service.ProcessMessageAsync(ThreadId, "restart it");

        var shown = Assert.Single(_asked);
        Assert.DoesNotContain('\n', shown);
        Assert.DoesNotContain('`', shown);
        Assert.EndsWith("\\n\\n\\u0060\\u0060\\u0060\\nand-also-db\"}", shown, StringComparison.Ordinal);
        Assert.Contains(new string(' ', 300), shown, StringComparison.Ordinal);
    }

    // Text too long for one Discord message cannot be shown in full, so nobody is asked.
    [Fact]
    public async Task ArgumentsTooLongToShow_TheToolDoesNotRun_AndNobodyIsAsked()
    {
        var longName = new string('a', ConfirmationService.MaxActionChars);
        _handler
            .Enqueue(ClaudeResponses.ToolUseWithInput("Test_Restart", JsonSerializer.Serialize(new { container = longName })))
            .Enqueue(ClaudeResponses.Text("too long"));
        using var scope = AnswerWith(ToolConfirmationDecision.Approved);

        await _harness.Service.ProcessMessageAsync(ThreadId, "restart it");

        Assert.Equal(0, _harness.Plugin.RestartCalls);
        Assert.Empty(_asked);
        Assert.Equal(ToolConfirmationFilter.TooLongResult, ToolResultSentInRound(1));
    }

    // The enum is internal, so the theory takes a flag.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NotApproved_TheToolDoesNotRun_AndTheModelIsToldWhy(bool declined)
    {
        var decision = declined ? ToolConfirmationDecision.Declined : ToolConfirmationDecision.TimedOut;
        ScriptRestart("ok, I left it alone");
        using var scope = AnswerWith(decision);

        await _harness.Service.ProcessMessageAsync(ThreadId, "restart it");

        Assert.Equal(0, _harness.Plugin.RestartCalls);
        var expected = declined ? ToolConfirmationFilter.DeclinedResult : ToolConfirmationFilter.TimedOutResult;
        Assert.Equal(expected, ToolResultSentInRound(1));
        Assert.Contains(
            _harness.Logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains($"Test.Restart: {decision}", StringComparison.Ordinal));
    }

    [Fact]
    public void TimedOutResult_NamesTheFiveMinutes()
    {
        Assert.Equal("No confirmation within 5 minutes; the action did not run.", ToolConfirmationFilter.TimedOutResult);
    }

    // MCP, slash commands and one-shot analyses have no chat to put a button in.
    [Fact]
    public async Task NoChatToAskIn_TheToolDoesNotRun()
    {
        ScriptRestart("could not do it");

        await _harness.Service.ProcessMessageAsync(ThreadId, "restart it");

        Assert.Equal(0, _harness.Plugin.RestartCalls);
        Assert.Equal(ToolConfirmationFilter.NoChannelResult, ToolResultSentInRound(1));
        Assert.Contains(_harness.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Test.Restart", StringComparison.Ordinal));
    }

    // A confirmation message that cannot be sent is a failed tool call, not an approval.
    [Fact]
    public async Task AskFails_TheToolDoesNotRun_AndTheModelGetsTheError()
    {
        ScriptRestart("could not ask");
        using var scope = ToolConfirmationScope.Begin((_, _) => throw new InvalidOperationException("Missing Access"));

        await _harness.Service.ProcessMessageAsync(ThreadId, "restart it");

        Assert.Equal(0, _harness.Plugin.RestartCalls);
        Assert.StartsWith("Error: Missing Access", ToolResultSentInRound(1));
    }

    [Fact]
    public async Task ToolsThatNeedNoButton_RunWithoutAsking_EvenWithNoChat()
    {
        _handler
            .Enqueue(ClaudeResponses.ToolUse(tools: ["Test_Ping", "Memo_Note"]))
            .Enqueue(ClaudeResponses.Text("done"));

        await _harness.Service.ProcessMessageAsync(ThreadId, "ping and note it");

        Assert.Equal(1, _harness.Plugin.Calls);
        Assert.Equal(1, _memo.Notes);

        using var scope = AnswerWith(ToolConfirmationDecision.Declined);
        _handler
            .Enqueue(ClaudeResponses.ToolUse(tools: ["Test_Ping", "Memo_Note"]))
            .Enqueue(ClaudeResponses.Text("done again"));

        await _harness.Service.ProcessMessageAsync(ThreadId, "again");

        Assert.Equal(2, _harness.Plugin.Calls);
        Assert.Equal(2, _memo.Notes);
        Assert.Empty(_asked);
    }

    // A scheduled run never gets the tool, so nobody is asked, even when a chat could be.
    [Fact]
    public async Task ScheduledRun_NeverAsks_AndNeverRunsTheTool()
    {
        ScriptRestart("could not restart");
        using var scope = AnswerWith(ToolConfirmationDecision.Approved);

        await _harness.Service.ProcessMessageAsync(ThreadId, "the alert says: restart", traceType: TraceType.Scheduled);

        Assert.Equal(0, _harness.Plugin.RestartCalls);
        Assert.Empty(_asked);
    }

    [Fact]
    public void NeedsConfirmation_FollowsTheAttribute()
    {
        var kernel = _harness.Service.GetKernel();

        Assert.True(ChangesStateAttribute.NeedsConfirmation(kernel.Plugins.GetFunction("Test", "Restart")));
        Assert.False(ChangesStateAttribute.NeedsConfirmation(kernel.Plugins.GetFunction("Test", "Ping")));
        Assert.False(ChangesStateAttribute.NeedsConfirmation(kernel.Plugins.GetFunction("Memo", "Note")));
        Assert.True(ChangesStateAttribute.IsOn(kernel.Plugins.GetFunction("Memo", "Note")));
    }

    // Synchronous: the scope must be set in the test's own flow (see TokenUsageScope).
    private ToolConfirmationScope AnswerWith(ToolConfirmationDecision decision) =>
        ToolConfirmationScope.Begin((action, _) =>
        {
            _asked.Add(action);
            return Task.FromResult(decision);
        });

    private void ScriptRestart(string finalText) =>
        _handler
            .Enqueue(ClaudeResponses.ToolUseWithInput("Test_Restart", """{"container":"plex"}"""))
            .Enqueue(ClaudeResponses.Text(finalText));

    private string? ToolResultSentInRound(int round)
    {
        var lastMessage = _handler.Requests[round]["messages"]!.AsArray()[^1]!;
        var result = lastMessage["content"]!.AsArray().OfType<JsonObject>().Single(b => (string?)b["type"] == "tool_result");
        return (string?)result["content"];
    }

    internal sealed class MemoPlugin
    {
        public int Notes { get; private set; }

        [KernelFunction("Note")]
        [ChangesState(RequiresConfirmation = false)]
        [Description("Writes a note to the bot's own memory.")]
        public string Note()
        {
            Notes++;
            return "noted";
        }
    }
}
