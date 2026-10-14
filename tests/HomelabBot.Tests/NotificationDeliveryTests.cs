using HomelabBot.Configuration;
using HomelabBot.Models;
using HomelabBot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.ChatCompletion;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace HomelabBot.Tests;

// The evaluation stores its [NOTIFY] answer in the daily thread. When the DM does not reach the
// owner, the thread must say so: a later evaluation must not assume the owner was told.
public sealed class NotificationDeliveryTests : IDisposable
{
    private const string NotDeliveredMarker = "[Notification not delivered";

    private readonly ScriptedClaudeHandler _handler = new();
    private readonly KernelServiceHarness _harness;
    private readonly DiscordBotService _discordBot;
    private readonly SmartNotificationService _service;

    private readonly NotificationCandidate _candidate = new()
    {
        Source = "AnomalyDetection",
        Summary = "Pool tank is at 91%",
        RawData = "Pool tank is at 91%, up from 80% yesterday.",
        AlreadyInvestigated = true,
    };

    public NotificationDeliveryTests()
    {
        _harness = new KernelServiceHarness(_handler.CreateClient());
        _discordBot = Substitute.For<DiscordBotService>(
            Options.Create(new BotConfiguration { DiscordToken = "x", AnthropicApiKey = "test-key" }),
            NullLogger<DiscordBotService>.Instance,
            null,
            null,
            null,
            null,
            null,
            null);

        var config = Substitute.For<IOptionsMonitor<DailySummaryConfiguration>>();
        config.CurrentValue.Returns(new DailySummaryConfiguration { ScheduleTime = "06:00", TimeZone = "UTC" });

        _service = new SmartNotificationService(
            _harness.Service,
            _harness.Conversations,
            _discordBot,
            new KnowledgeService(_harness.DbFactory, NullLogger<KnowledgeService>.Instance),
            config,
            NullLogger<SmartNotificationService>.Instance);

        _handler.Enqueue(ClaudeResponses.Text($"The pool fills fast.\n{NotificationPrompts.TagNotify}"));
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task DmFails_DailyThreadSaysTheOwnerWasNotTold()
    {
        _discordBot.SendDmSplitAsync(Arg.Any<ulong>(), Arg.Any<string>())
            .ThrowsAsync(new InvalidOperationException("Discord gateway down"));

        var sent = await _service.EvaluateAndNotifyAsync(_candidate, CancellationToken.None);

        Assert.False(sent);
        var history = await _harness.Conversations.GetOrCreateHistoryAsync(_service.CurrentDailyThreadId, "unused");
        Assert.Equal(
            [AuthorRole.System, AuthorRole.User, AuthorRole.Assistant, AuthorRole.User],
            history.Select(m => m.Role));
        Assert.StartsWith(NotDeliveredMarker, history[^1].Content, StringComparison.Ordinal);
        Assert.Contains("Pool tank is at 91%", history[^1].Content, StringComparison.Ordinal);

        // The record is in the database too, so a restart keeps it.
        var restarted = new ConversationService(_harness.DbFactory, NullLogger<ConversationService>.Instance);
        var reloaded = await restarted.GetOrCreateHistoryAsync(_service.CurrentDailyThreadId, "unused");
        Assert.Equal(AuthorRole.User, reloaded[^1].Role);
        Assert.StartsWith(NotDeliveredMarker, reloaded[^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DmSent_DailyThreadEndsWithTheDecision()
    {
        var sent = await _service.EvaluateAndNotifyAsync(_candidate, CancellationToken.None);

        Assert.True(sent);
        await _discordBot.Received(1).SendDmSplitAsync(HomelabOwner.DiscordUserId, _candidate.RawData);
        var history = await _harness.Conversations.GetOrCreateHistoryAsync(_service.CurrentDailyThreadId, "unused");
        Assert.Equal(
            [AuthorRole.System, AuthorRole.User, AuthorRole.Assistant],
            history.Select(m => m.Role));
        Assert.DoesNotContain(history, m => (m.Content ?? "").StartsWith(NotDeliveredMarker, StringComparison.Ordinal));
    }
}
