using HomelabBot.Configuration;
using HomelabBot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace HomelabBot.Tests;

public sealed class DailyHealthcheckSuppressionTests : IDisposable
{
    private readonly ScriptedClaudeHandler _handler = new();
    private readonly KernelServiceHarness _harness;
    private readonly KnowledgeService _knowledge;
    private readonly DailySummaryService _service;

    public DailyHealthcheckSuppressionTests()
    {
        _harness = new KernelServiceHarness(_handler.CreateClient());
        _knowledge = new KnowledgeService(_harness.DbFactory, NullLogger<KnowledgeService>.Instance);

        var config = Substitute.For<IOptionsMonitor<DailySummaryConfiguration>>();
        config.CurrentValue.Returns(new DailySummaryConfiguration { ScheduleTime = "08:00", TimeZone = "UTC" });

        var smartNotification = new SmartNotificationService(
            _harness.Service,
            _harness.Conversations,
            null!,
            _knowledge,
            config,
            NullLogger<SmartNotificationService>.Instance);

        _service = new DailySummaryService(
            config, _harness.Service, smartNotification, null!, NullLogger<DailySummaryService>.Instance);
    }

    [Fact]
    public async Task SuppressedHealthcheck_MakesNoLlmCall()
    {
        await _knowledge.RememberFactAsync(
            "notification_preference:suppress:daily_healthcheck", "Owner marked as normal", confidence: 1.0);

        await _service.RunHealthcheckAsync(CancellationToken.None);

        Assert.Empty(_handler.RequestBodies);
    }

    [Fact]
    public async Task HealthcheckWithoutSuppression_RunsTheInvestigationAndTheDecision()
    {
        _handler
            .Enqueue(ClaudeResponses.Text("All systems healthy."))
            .Enqueue(ClaudeResponses.Text(NotificationPrompts.TagSilent));

        await _service.RunHealthcheckAsync(CancellationToken.None);

        Assert.Equal(2, _handler.RequestBodies.Count);
    }

    [Fact]
    public async Task SuppressionOfAnotherKindOnly_StillRunsTheHealthcheck()
    {
        await _knowledge.RememberFactAsync(
            "notification_preference:suppress:anomaly_detection", "Owner marked as normal", confidence: 1.0);
        _handler
            .Enqueue(ClaudeResponses.Text("All systems healthy."))
            .Enqueue(ClaudeResponses.Text(NotificationPrompts.TagSilent));

        await _service.RunHealthcheckAsync(CancellationToken.None);

        Assert.Equal(2, _handler.RequestBodies.Count);
    }

    public void Dispose() => _harness.Dispose();
}
