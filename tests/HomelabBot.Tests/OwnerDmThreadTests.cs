using System.Text.Json.Nodes;
using DSharpPlus.Entities;
using HomelabBot.Data.Entities;
using HomelabBot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel.ChatCompletion;

namespace HomelabBot.Tests;

// The owner's DM thread: messages the bot sent go into its context, and turns on one thread
// never interleave with each other or with such a record.
public sealed class OwnerDmThreadTests : IDisposable
{
    private const ulong ThreadId = 4242;
    private const string Marker = "[Bot message sent to the owner at ";

    private readonly ScriptedClaudeHandler _handler = new();
    private readonly KernelServiceHarness _harness;

    public OwnerDmThreadTests()
    {
        _harness = new KernelServiceHarness(_handler.CreateClient());
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task RecordedMessage_ReachesTheNextRequest_AsAUserMessageOfItsOwn()
    {
        _handler.Enqueue(ClaudeResponses.Text("hello")).Enqueue(ClaudeResponses.Text("it was the disk"));

        await _harness.Service.ProcessMessageAsync(ThreadId, "hi");
        await _harness.Conversations.RecordBotMessageAsync(ThreadId, "Disk usage on pool tank is 81%.");
        await _harness.Service.ProcessMessageAsync(ThreadId, "what was that about?");

        var texts = MessageTexts(_handler.Requests[1]);
        Assert.Equal(["user", "assistant", "user", "user"], texts.Select(t => t.Role));
        Assert.StartsWith(Marker, texts[2].Text, StringComparison.Ordinal);
        Assert.EndsWith("Disk usage on pool tank is 81%.", texts[2].Text, StringComparison.Ordinal);
        Assert.Equal("what was that about?", texts[3].Text);
    }

    [Fact]
    public async Task RecordedMessage_SurvivesARestart_AndDoesNotBecomeTheTitle()
    {
        await _harness.Conversations.RecordBotMessageAsync(ThreadId, "Backup job finished.");

        var restarted = new ConversationService(_harness.DbFactory, NullLogger<ConversationService>.Instance);
        var history = await restarted.GetOrCreateHistoryAsync(ThreadId, "system");

        Assert.Equal(2, history.Count);
        Assert.Equal(AuthorRole.User, history[1].Role);
        Assert.StartsWith(Marker, history[1].Content, StringComparison.Ordinal);

        await using var db = _harness.DbFactory.CreateDbContext();
        var conversation = await db.Conversations.Include(c => c.Messages).SingleAsync(c => c.ThreadId == ThreadId);
        Assert.True(string.IsNullOrEmpty(conversation.Title));
        Assert.Equal(ConversationService.BotMessageRole, conversation.Messages.Single().Role);
    }

    [Fact]
    public async Task EachTurn_GetsItsCallersSystemPrompt_OnAHistoryAnotherCallerCreated()
    {
        _handler.Enqueue(ClaudeResponses.Text("checked")).Enqueue(ClaudeResponses.Text("hi there"));

        await _harness.Service.ProcessMessageAsync(ThreadId, "run the healthcheck", systemPromptOverride: "You are the healthcheck.");
        await _harness.Service.ProcessMessageAsync(ThreadId, "hello");

        Assert.Contains("You are the healthcheck.", _handler.Requests[0]["system"]!.ToJsonString(), StringComparison.Ordinal);
        var second = _handler.Requests[1]["system"]!.ToJsonString();
        Assert.Contains("You are HomeLabBot", second, StringComparison.Ordinal);
        Assert.DoesNotContain("You are the healthcheck.", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoTurnsOnOneThread_RunOneAfterTheOther()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.EnqueueGated(ClaudeResponses.Text("first answer"), arrived, gate.Task).Enqueue(ClaudeResponses.Text("second answer"));

        var first = _harness.Service.ProcessMessageAsync(ThreadId, "first");
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = _harness.Service.ProcessMessageAsync(ThreadId, "second");
        await Task.Delay(200);

        Assert.Single(_handler.RequestBodies);

        gate.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        var texts = MessageTexts(_handler.Requests[1]);
        Assert.Equal(["first", "first answer", "second"], texts.Select(t => t.Text));
    }

    [Fact]
    public async Task Record_WaitsForARunningTurn_ThenFollowsItsAnswer()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.EnqueueGated(ClaudeResponses.Text("answer"), arrived, gate.Task);

        var turn = _harness.Service.ProcessMessageAsync(ThreadId, "question");
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var record = _harness.Conversations.RecordBotMessageAsync(ThreadId, "Container grafana restarted.");
        await Task.Delay(200);

        Assert.False(record.IsCompleted);

        gate.SetResult();
        await Task.WhenAll(turn, record).WaitAsync(TimeSpan.FromSeconds(10));

        var history = await _harness.Conversations.GetOrCreateHistoryAsync(ThreadId, "unused");
        Assert.Equal(
            [AuthorRole.System, AuthorRole.User, AuthorRole.Assistant, AuthorRole.User],
            history.Select(m => m.Role));
        Assert.Equal("answer", history[2].Content);
        Assert.StartsWith(Marker, history[3].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_ReadsOnlyTheNewestRows_AndOpensWithAUserMessage()
    {
        // Rows 0..50 alternate user/assistant. The newest 32 start at row 19, an answer.
        await using (var db = _harness.DbFactory.CreateDbContext())
        {
            var conversation = new Conversation { ThreadId = ThreadId, CreatedAt = DateTime.UtcNow };
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();

            var start = DateTime.UtcNow.AddHours(-1);
            db.ConversationMessages.AddRange(Enumerable.Range(0, 51).Select(i => new ConversationMessage
            {
                ConversationId = conversation.Id,
                Role = i % 2 == 0 ? "user" : "assistant",
                Content = $"row {i}",
                Timestamp = start.AddSeconds(i),
            }));
            await db.SaveChangesAsync();
        }

        var history = await _harness.Conversations.GetOrCreateHistoryAsync(ThreadId, "system");

        Assert.Equal(31, history.Count - 1);
        Assert.Equal(AuthorRole.User, history[1].Role);
        Assert.Equal("row 20", history[1].Content);
        Assert.Equal("row 50", history[^1].Content);
    }

    [Fact]
    public void FormatBotMessage_CapsALongText_AndKeepsTheMarker()
    {
        var record = ConversationService.FormatBotMessage(
            new DateTime(2026, 10, 14, 6, 5, 0, DateTimeKind.Utc), new string('x', 20_000));

        Assert.StartsWith("[Bot message sent to the owner at 2026-10-14 06:05 UTC]\n", record, StringComparison.Ordinal);
        Assert.True(record.Length <= ConversationService.MaxToolResultChars);
        Assert.Contains("[truncated: kept", record, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbedText_HoldsTitleFieldsAndDescription()
    {
        var embed = new DiscordEmbedBuilder()
            .WithTitle("🔥 HighDiskUsage")
            .AddField("Status", "FIRING", true)
            .AddField("Instance", "nas:9100", true)
            .WithDescription("Pool tank is at 91%.")
            .Build();

        Assert.Equal(
            "🔥 HighDiskUsage\nStatus: FIRING\nInstance: nas:9100\nPool tank is at 91%.",
            DiscordBotService.EmbedText(embed));
    }

    [Fact]
    public void WithQuote_PutsTheRepliedMessageBeforeTheOwnersText()
    {
        var embed = new DiscordEmbedBuilder().WithTitle("🔥 HighDiskUsage").WithDescription("Pool tank is at 91%.").Build();

        var text = DiscordBotService.WithQuote("what does this mean?", DiscordBotService.QuoteText("", [embed]));

        Assert.Equal(
            $"{DiscordBotService.QuoteStart}\n🔥 HighDiskUsage\nPool tank is at 91%.\n{DiscordBotService.QuoteEnd}\nwhat does this mean?",
            text);
    }

    [Fact]
    public void WithQuote_CapsALongQuote_AndKeepsTheOwnersTextWhole()
    {
        var text = DiscordBotService.WithQuote("why?", new string('x', 10_000));

        Assert.True(text.Length < 2_100, $"length {text.Length}");
        Assert.EndsWith($"...\n{DiscordBotService.QuoteEnd}\nwhy?", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithQuote_WithNothingToQuote_IsTheOwnersText() =>
        Assert.Equal("hi", DiscordBotService.WithQuote("hi", DiscordBotService.QuoteText(null, [])));

    [Fact]
    public async Task QuotedReply_ReachesTheRequest()
    {
        _handler.Enqueue(ClaudeResponses.Text("that was the disk alert"));

        await _harness.Service.ProcessMessageAsync(
            ThreadId, DiscordBotService.WithQuote("what does this mean?", "Pool tank is at 91%."));

        var texts = MessageTexts(_handler.Requests[0]);
        Assert.StartsWith(DiscordBotService.QuoteStart, texts[^1].Text, StringComparison.Ordinal);
        Assert.Contains("Pool tank is at 91%.", texts[^1].Text, StringComparison.Ordinal);
    }

    private static List<(string Role, string Text)> MessageTexts(JsonObject request) =>
        request["messages"]!.AsArray()
            .Select(m =>
            {
                var content = m!["content"]!;
                var text = content is JsonArray blocks
                    ? string.Concat(blocks.Where(b => (string?)b!["type"] == "text").Select(b => (string?)b!["text"]))
                    : (string?)content ?? "";
                return ((string?)m["role"] ?? "", text);
            })
            .ToList();
}
