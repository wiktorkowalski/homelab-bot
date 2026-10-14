using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using HomelabBot.Configuration;
using HomelabBot.Mcp;
using HomelabBot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HomelabBot.Tests;

// The ask_bot MCP tool: a chat turn as the owner, without Discord.
public sealed class AskBotMcpToolTests : IDisposable
{
    private const string IdPrefix = "conversationId: ";

    private readonly ScriptedClaudeHandler _handler = new();
    private readonly KernelServiceHarness _harness;
    private readonly RecordingLogger<HomelabMcpTools> _logger = new();
    private readonly HomelabMcpTools _tools;

    public AskBotMcpToolTests()
    {
        _harness = new KernelServiceHarness(_handler.CreateClient());

        // ask_bot needs only the kernel; the other tools' services stay unset.
        _tools = new HomelabMcpTools(null!, null!, null!, _harness.Conversations, _harness.Service, _logger);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task NewConversation_ReturnsAFreshId_AndTheAnswer_FromATurnAsTheOwner()
    {
        // Stands in for the HTTP request span of the MCP call, which is never exported.
        using var request = new Activity("test-request").Start();

        // By name: the static source may not exist yet when the listener is added.
        using var spans = new SpanCollector(s => s.DisplayName == "Chat", s => s.Name == "HomelabBot.Chat");
        _handler.Enqueue(ClaudeResponses.Text("all green"));

        var result = await _tools.AskBot("how is the lab?");

        var (id, answer) = Parse(result);
        Assert.Equal("all green", answer);
        Assert.Single(_handler.RequestBodies);
        await using var db = _harness.DbFactory.CreateDbContext();
        Assert.Equal(id, (await db.Conversations.SingleAsync()).ThreadId);
        var idText = id.ToString(CultureInfo.InvariantCulture);
        var turn = Assert.Single(spans.Snapshot(), s => idText.Equals(s.GetTagItem("langfuse.session.id")));
        Assert.Equal(default, turn.ParentSpanId);
        Assert.NotEqual(request.TraceId, turn.TraceId);
        Assert.Same(request, Activity.Current);
        Assert.Equal(HomelabOwner.DiscordUserId.ToString(CultureInfo.InvariantCulture), turn.GetTagItem("langfuse.user.id"));

        // The thread id is logged, the owner's text is not.
        var started = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Information, started.Level);
        Assert.Contains(id.ToString(CultureInfo.InvariantCulture), started.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("how is the lab?", started.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PassingTheReturnedId_ContinuesTheConversation_WithItsHistory()
    {
        _handler.Enqueue(ClaudeResponses.Text("plex is up")).Enqueue(ClaudeResponses.Text("since 3 days"));

        var (id, _) = Parse(await _tools.AskBot("is plex up?"));
        var (sameId, answer) = Parse(await _tools.AskBot("since when?", id.ToString(CultureInfo.InvariantCulture)));

        Assert.Equal(id, sameId);
        Assert.Equal("since 3 days", answer);
        Assert.Equal(["is plex up?", "plex is up", "since when?"], MessageTexts(_handler.Requests[1]));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("0")]
    [InlineData("")]
    public async Task InvalidConversationId_ReturnsAnError_AndRunsNoTurn(string conversationId)
    {
        var result = await _tools.AskBot("hi", conversationId);

        Assert.StartsWith("Error:", result, StringComparison.Ordinal);
        Assert.Contains("conversationId", result, StringComparison.Ordinal);
        await AssertNoTurnAsync();
    }

    // A broken bot must not read as a healthy answer to a prod check.
    [Fact]
    public async Task FailedTurn_IsAnError_WithTheIdAndTheBotsText()
    {
        _handler.EnqueueStatus(529, times: 3);

        var result = await _tools.AskBot("how is the lab?");

        Assert.StartsWith("Error:", result, StringComparison.Ordinal);
        Assert.Contains(IdPrefix, result, StringComparison.Ordinal);
        Assert.Contains("Error processing your request:", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealWorldAction_DoesNotRun_FromAskBot()
    {
        _handler
            .Enqueue(ClaudeResponses.ToolUseWithInput("Test_Restart", """{"container":"plex"}"""))
            .Enqueue(ClaudeResponses.Text("I could not restart it"));

        var (_, answer) = Parse(await _tools.AskBot("restart plex"));

        Assert.Equal("I could not restart it", answer);
        Assert.Equal(0, _harness.Plugin.RestartCalls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n ")]
    public async Task EmptyMessage_ReturnsAnError_AndRunsNoTurn(string message)
    {
        var result = await _tools.AskBot(message);

        Assert.StartsWith("Error:", result, StringComparison.Ordinal);
        await AssertNoTurnAsync();
    }

    [Fact]
    public async Task McpServer_ListsAskBot_WithOnlyTheMessageRequired()
    {
        await using var host = await McpTestHost.StartAsync();

        var tool = Assert.Single(await host.ListToolsAsync(), t => t.Name == "ask_bot");

        var schema = tool.JsonSchema;
        Assert.Equal(["message"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.True(schema.GetProperty("properties").TryGetProperty("conversationId", out _));
        Assert.Contains("Discord", tool.Description, StringComparison.Ordinal);
    }

    private async Task AssertNoTurnAsync()
    {
        Assert.Empty(_handler.RequestBodies);
        await using var db = _harness.DbFactory.CreateDbContext();
        Assert.Empty(db.Conversations);
    }

    private static List<string> MessageTexts(JsonObject request) =>
        request["messages"]!.AsArray()
            .Select(m => string.Concat(m!["content"]!.AsArray()
                .Where(b => (string?)b!["type"] == "text")
                .Select(b => (string?)b!["text"])))
            .ToList();

    private static (ulong Id, string Answer) Parse(string result)
    {
        var parts = result.Split("\n\n", 2);
        Assert.StartsWith(IdPrefix, parts[0], StringComparison.Ordinal);
        return (ulong.Parse(parts[0][IdPrefix.Length..], CultureInfo.InvariantCulture), parts[1]);
    }
}
