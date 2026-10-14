using HomelabBot.Configuration;
using HomelabBot.Data.Entities;
using HomelabBot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace HomelabBot.Tests;

// Own fixture instance: the service loads every notification_preference row, so rows of
// other test classes must not leak in.
public class NotificationPreferenceTests : IClassFixture<DatabaseFixture>
{
    private const string Suppress = "notification_preference:suppress:";

    private readonly KnowledgeService _knowledge;
    private readonly SmartNotificationService _service;

    public NotificationPreferenceTests(DatabaseFixture fixture)
    {
        _knowledge = new KnowledgeService(fixture.DbContextFactory, NullLogger<KnowledgeService>.Instance);

        var config = Substitute.For<IOptionsMonitor<DailySummaryConfiguration>>();
        config.CurrentValue.Returns(new DailySummaryConfiguration { ScheduleTime = "08:00", TimeZone = "UTC" });
        _service = new SmartNotificationService(
            null!, null!, null!, _knowledge, config, NullLogger<SmartNotificationService>.Instance);
    }

    [Fact]
    public void WholeClassRow_SuppressesEveryKind()
    {
        List<Knowledge> prefs = [Pref($"{Suppress}anomaly_detection")];

        Assert.True(SmartNotificationService.IsIssueSuppressed("anomaly_detection", ["Certificate", "Storage"], prefs));
        Assert.True(SmartNotificationService.IsIssueSuppressed("anomaly_detection", [], prefs));
    }

    [Fact]
    public void KindRow_SuppressesOnlyWhenEveryKindIsSuppressed()
    {
        List<Knowledge> prefs = [Pref($"{Suppress}anomaly_detection:certificate")];

        Assert.True(SmartNotificationService.IsIssueSuppressed("anomaly_detection", ["Certificate"], prefs));
        Assert.False(SmartNotificationService.IsIssueSuppressed("anomaly_detection", ["Certificate", "Storage"], prefs));
        Assert.False(SmartNotificationService.IsIssueSuppressed("anomaly_detection", ["Storage"], prefs));
    }

    [Fact]
    public void KindRow_DoesNotSuppressACandidateWithoutKinds()
    {
        List<Knowledge> prefs = [Pref($"{Suppress}anomaly_detection:certificate")];

        Assert.False(SmartNotificationService.IsIssueSuppressed("anomaly_detection", [], prefs));
    }

    [Fact]
    public void KindRowOfAnotherIssueType_DoesNotSuppress()
    {
        List<Knowledge> prefs = [Pref($"{Suppress}health_score_drop:certificate")];

        Assert.False(SmartNotificationService.IsIssueSuppressed("anomaly_detection", ["Certificate"], prefs));
    }

    [Fact]
    public void LowConfidenceRow_IsIgnored()
    {
        List<Knowledge> prefs =
        [
            Pref($"{Suppress}anomaly_detection", confidence: 0.4),
            Pref($"{Suppress}anomaly_detection:certificate", confidence: 0.4),
        ];

        Assert.False(SmartNotificationService.IsIssueSuppressed("anomaly_detection", ["Certificate"], prefs));
    }

    [Fact]
    public void Matching_IgnoresCase()
    {
        List<Knowledge> prefs = [Pref($"{Suppress}Anomaly_Detection:CERTIFICATE")];

        Assert.True(SmartNotificationService.IsIssueSuppressed("anomaly detection", ["certificate"], prefs));
    }

    [Fact]
    public void FeedbackButtonId_CarriesSortedNormalizedKinds()
    {
        var id = SmartNotificationService.BuildFeedbackButtonId("anomaly_detection", ["Storage", "Certificate", "Storage", "Log Spike"]);

        Assert.Equal("notif_normal_anomaly_detection:certificate,log_spike,storage", id);
    }

    [Fact]
    public void FeedbackButtonId_WithoutKinds_IsTheWholeClass()
    {
        Assert.Equal("notif_normal_daily_healthcheck", SmartNotificationService.BuildFeedbackButtonId("daily_healthcheck", []));
    }

    [Fact]
    public void FeedbackButtonId_TooLongForDiscord_FallsBackToTheWholeClass()
    {
        string[] kinds =
        [
            "CPU", "Memory", "Disk", "Monitoring", "Container", "Router", "Network", "Storage", "Traefik",
            "Certificate", "Logging", "LogSpike", "CriticalLog",
        ];

        var id = SmartNotificationService.BuildFeedbackButtonId("anomaly_detection", kinds);

        Assert.Equal("notif_normal_anomaly_detection", id);
    }

    [Fact]
    public void FeedbackButtonId_RealisticAnomalySet_FitsDiscordLimit()
    {
        var id = SmartNotificationService.BuildFeedbackButtonId(
            "critical_anomaly", ["CriticalLog", "Certificate", "Monitoring", "Container", "Storage"]);

        Assert.True(id.Length <= SmartNotificationService.MaxCustomIdLength);
        Assert.Contains(':', id);
    }

    [Fact]
    public async Task KindButton_StoresOneRowPerKind_AndSuppressesThatSetOnly()
    {
        var issueType = $"kinds_{Guid.NewGuid():N}";

        var scope = await _service.HandleSuppressFeedbackAsync($"{issueType}:certificate,container", "test");

        Assert.Contains("certificate", scope, StringComparison.Ordinal);
        var prefs = await _service.ListPreferencesAsync();
        Assert.Contains(prefs, p => p.Topic == $"{Suppress}{issueType}:certificate");
        Assert.Contains(prefs, p => p.Topic == $"{Suppress}{issueType}:container");
        Assert.DoesNotContain(prefs, p => p.Topic == $"{Suppress}{issueType}");
        Assert.True(SmartNotificationService.IsIssueSuppressed(issueType, ["Container"], prefs));
        Assert.False(SmartNotificationService.IsIssueSuppressed(issueType, ["Container", "Disk"], prefs));
        Assert.False(await _service.IsSuppressedAsync(issueType));
    }

    [Fact]
    public async Task OldButtonWithoutKinds_StoresAWholeClassRow()
    {
        var issueType = $"legacy_{Guid.NewGuid():N}";

        await _service.HandleSuppressFeedbackAsync(issueType, "test");

        Assert.True(await _service.IsSuppressedAsync(issueType));
    }

    [Fact]
    public async Task RemovePreference_StopsTheSuppression()
    {
        var issueType = $"remove_{Guid.NewGuid():N}";
        await _service.HandleSuppressFeedbackAsync(issueType, "test");
        var row = (await _service.ListPreferencesAsync()).Single(p => p.Topic == $"{Suppress}{issueType}");

        var removed = await _service.RemovePreferenceAsync(row.Id);

        Assert.NotNull(removed);
        Assert.False(await _service.IsSuppressedAsync(issueType));
        Assert.Null(await _service.RemovePreferenceAsync(row.Id));
    }

    [Fact]
    public async Task RemovePreference_LeavesOtherKnowledgeAlone()
    {
        var other = await _knowledge.RememberFactAsync($"docker:{Guid.NewGuid():N}", "not a preference");

        Assert.Null(await _service.RemovePreferenceAsync(other.Id));
        Assert.Single(await _knowledge.RecallAsync(other.Topic));
    }

    [Fact]
    public void DescribePreference_KeepsTheIssueTypeOfAKindKey()
    {
        Assert.Equal(
            "SUPPRESS anomaly_detection:certificate",
            SmartNotificationService.DescribePreference(Pref($"{Suppress}anomaly_detection:certificate")));
        Assert.Equal(
            "ALWAYS health_score_drop",
            SmartNotificationService.DescribePreference(Pref("notification_preference:always:health_score_drop")));
        Assert.Equal(
            "PREF quiet_hours",
            SmartNotificationService.DescribePreference(Pref("notification_preference:quiet_hours")));
    }

    [Fact]
    public void HealthScoreDropKinds_AreTheCategoriesWithDeductions()
    {
        var result = new Models.HealthScoreResult
        {
            Score = 70,
            AlertDeductions = 0,
            ContainerDeductions = 10,
            PoolDeductions = 20,
            MonitoringDeductions = 0,
            ConnectivityDeductions = 0,
        };

        Assert.Equal(["containers", "pools"], HealthScoreBackgroundService.DeductionKinds(result));
    }

    [Fact]
    public void PreferencesView_CapsTheEntries_AndPointsEachButtonAtItsRow()
    {
        List<Knowledge> prefs =
        [
            .. Enumerable.Range(1, 23).Select(i => new Knowledge
            {
                Id = i,
                Topic = $"{Suppress}type_{i}",
                Fact = "@everyone\nlong fact",
                Confidence = 1.0,
            }),
        ];

        var rows = NotificationPreferencesView.Components(prefs);
        var buttons = rows.SelectMany(r => r.Components).ToList();
        var content = NotificationPreferencesView.Content(prefs, "Removed `SUPPRESS x`.");

        Assert.Equal(4, rows.Count);
        Assert.Equal(NotificationPreferencesView.MaxEntries, buttons.Count);
        Assert.Equal("notif_pref_remove_1", buttons[0].CustomId);
        Assert.StartsWith("Removed", content, StringComparison.Ordinal);
        Assert.Contains("first 20 of 23", content, StringComparison.Ordinal);
        Assert.DoesNotContain("type_21", content, StringComparison.Ordinal);
        Assert.True(content.Length < 2000);
    }

    [Fact]
    public void PreferencesView_LongRealRows_StayUnderTheDiscordLimit_WithAButtonPerShownLine()
    {
        List<Knowledge> prefs =
        [
            new Knowledge { Id = 100, Topic = $"{Suppress}{new string('x', 500)}", Fact = "llm topic", Confidence = 1.0 },
            .. Enumerable.Range(1, 22).Select(i => new Knowledge
            {
                Id = i,
                Topic = $"{Suppress}health_score_drop:containers_and_connectivity_{i}",
                Fact = "Owner marked as normal/ignorable: Marked as normal via button feedback",
                Confidence = 1.0,
            }),
        ];

        var content = NotificationPreferencesView.Content(prefs, $"Removed `SUPPRESS {new string('y', 400)}`.");
        var buttons = NotificationPreferencesView.Components(prefs).SelectMany(r => r.Components).ToList();
        var shownLines = content.Split('\n').Count(l => char.IsDigit(l[0]));

        Assert.True(content.Length <= 2000, $"{content.Length} characters");
        Assert.Equal(shownLines, buttons.Count);
        Assert.Contains($"first {shownLines} of 23", content, StringComparison.Ordinal);
    }

    [Fact]
    public void PreferencesView_Empty_SaysSoWithoutButtons()
    {
        Assert.Empty(NotificationPreferencesView.Components([]));
        Assert.Contains("No notification preferences", NotificationPreferencesView.Content([]), StringComparison.Ordinal);
    }

    private static Knowledge Pref(string topic, double confidence = 1.0) =>
        new() { Topic = topic, Fact = "test", Confidence = confidence };
}
