using System.Security.Cryptography;
using System.Text;
using HomelabBot.Configuration;
using HomelabBot.Data.Entities;
using HomelabBot.Models;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public sealed class SmartNotificationService
{
    private const string TopicPrefixSuppress = "notification_preference:suppress:";
    private const string TopicPrefixAlways = "notification_preference:always:";
    internal const string TopicPrefix = "notification_preference";
    internal const string ButtonPrefixNormal = "notif_normal_";
    internal const string ButtonPrefixRemovePreference = "notif_pref_remove_";

    // Discord rejects a component custom_id longer than this.
    internal const int MaxCustomIdLength = 100;

    // Separators inside a "Normal" button id: <issueType>:<kind>,<kind>. An id without ':' comes
    // from an old button or a source without kinds, and suppresses the whole issue type.
    private const char KindSeparator = ':';
    private const char KindListSeparator = ',';

    // Retired: no longer sent, but old DMs still carry it and a click must get an answer.
    internal const string ButtonPrefixInvestigate = "notif_investigate_";

    private readonly KernelService _kernelService;
    private readonly ConversationService _conversationService;
    private readonly DiscordBotService _discordBot;
    private readonly KnowledgeService _knowledgeService;
    private readonly ILogger<SmartNotificationService> _logger;
    private readonly Lock _cycleLock = new();

    private string _currentCycleDate;

    public SmartNotificationService(
        KernelService kernelService,
        ConversationService conversationService,
        DiscordBotService discordBot,
        KnowledgeService knowledgeService,
        IOptionsMonitor<DailySummaryConfiguration> dailySummaryConfig,
        ILogger<SmartNotificationService> logger)
    {
        _kernelService = kernelService;
        _conversationService = conversationService;
        _discordBot = discordBot;
        _knowledgeService = knowledgeService;
        _logger = logger;

        // After a restart, continue the cycle of the last daily run: its thread is reloaded from
        // the database. A cycle runs from one daily run to the next, not from midnight.
        var config = dailySummaryConfig.CurrentValue;
        var lastRunUtc = DateTime.UtcNow + ScheduleHelper.CalculateDelayUntilNextRun(config.ScheduleTime, config.TimeZone) - TimeSpan.FromDays(1);
        _currentCycleDate = lastRunUtc.ToString("yyyy-MM-dd");
    }

    public ulong CurrentDailyThreadId
    {
        get
        {
            lock (_cycleLock)
            {
                return GenerateDailyThreadId(_currentCycleDate);
            }
        }
    }

    // Start a new daily cycle: the evaluations of the new day get a fresh context. The owner's
    // DMs have their own thread (HomelabOwner.DmThreadId), which is never reset.
    public void StartNewCycle()
    {
        ulong previousThreadId;

        lock (_cycleLock)
        {
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            if (today == _currentCycleDate)
            {
                return;
            }

            previousThreadId = GenerateDailyThreadId(_currentCycleDate);
            _currentCycleDate = today;
        }

        if (previousThreadId != 0)
        {
            _conversationService.ClearHistory(previousThreadId);
        }

        _logger.LogInformation("Started new notification cycle {Date}", _currentCycleDate);
    }

    // Evaluate a finding: investigate via LLM, decide if it's worth notifying the owner.
    // Returns true if a notification was actually sent.
    public async Task<bool> EvaluateAndNotifyAsync(NotificationCandidate candidate, CancellationToken ct)
    {
        _logger.LogInformation("Evaluating notification candidate from {Source}: {Summary}",
            candidate.Source, candidate.Summary);

        var preferences = await LoadNotificationPreferencesAsync();

        if (!candidate.NeverSuppress
            && candidate.IssueType != null
            && IsIssueSuppressed(candidate.IssueType, candidate.Kinds, preferences))
        {
            _logger.LogInformation("Issue type '{IssueType}' with kinds [{Kinds}] is suppressed, skipping",
                candidate.IssueType, string.Join(", ", candidate.Kinds));
            return false;
        }

        var prefsText = FormatPreferences(preferences);
        var prompt = candidate.AlreadyInvestigated
            ? NotificationPrompts.BuildDecisionOnlyPrompt(candidate.Summary, candidate.RawData, prefsText)
            : NotificationPrompts.BuildInvestigationPrompt(candidate.Summary, candidate.RawData, prefsText);

        // Read once: a new cycle can start while the model works, and a not-delivered record must
        // land in the thread that holds the decision.
        var threadId = CurrentDailyThreadId;

        string analysis;
        try
        {
            // The decision-only prompt tells the model not to use tools; sending their schemas
            // anyway is pure cost.
            analysis = await _kernelService.ProcessMessageAsync(
                threadId: threadId,
                userMessage: prompt,
                traceType: TraceType.Scheduled,
                maxTokens: NotificationPrompts.MaxTokens,
                systemPromptOverride: NotificationPrompts.InvestigationSystem,
                pluginAllowList: NotificationPrompts.InvestigationPlugins,
                allowToolUse: !candidate.AlreadyInvestigated,
                ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run LLM investigation for {Source}", candidate.Source);
            return false;
        }

        // Parse the last non-empty line for the decision tag to avoid false positives
        var lastLine = analysis.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? "";
        var shouldNotify = lastLine.Equals(NotificationPrompts.TagNotify, StringComparison.OrdinalIgnoreCase);

        if (!shouldNotify)
        {
            _logger.LogInformation("LLM decided not to notify for {Source}: {Summary}",
                candidate.Source, candidate.Summary);
            return false;
        }

        var report = candidate.AlreadyInvestigated
            ? candidate.RawData
            : analysis
                .Replace(NotificationPrompts.TagNotify, "", StringComparison.OrdinalIgnoreCase)
                .Replace(NotificationPrompts.TagSilent, "", StringComparison.OrdinalIgnoreCase)
                .Trim();

        var sent = await SendNotificationAsync(report, candidate);
        if (!sent)
        {
            await RecordNotDeliveredAsync(threadId, candidate);
        }

        return sent;
    }

    // Lets a caller skip expensive work whose result would be suppressed anyway. A failed load
    // returns no preferences, so the work still runs.
    public async Task<bool> IsSuppressedAsync(string issueType) =>
        IsIssueSuppressed(issueType, [], await LoadNotificationPreferencesAsync());

    // Handle "Normal, ignore in future" button press. The payload is the button id after the
    // prefix. Returns what is now suppressed, in words for the owner.
    public async Task<string> HandleSuppressFeedbackAsync(string buttonPayload, string description)
    {
        var separatorAt = buttonPayload.IndexOf(KindSeparator, StringComparison.Ordinal);
        var issueType = NormalizeKey(separatorAt < 0 ? buttonPayload : buttonPayload[..separatorAt]);
        var kindList = separatorAt < 0 ? "" : buttonPayload[separatorAt..].TrimStart(KindSeparator);
        List<string> kinds =
            [.. kindList
                .Split(KindListSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(NormalizeKey)
                .Distinct(StringComparer.Ordinal)];

        // One row per kind: a later finding with only some of these kinds is suppressed too.
        string[] topics = kinds.Count == 0
            ? [$"{TopicPrefixSuppress}{issueType}"]
            : [.. kinds.Select(kind => $"{TopicPrefixSuppress}{issueType}{KindSeparator}{kind}")];

        foreach (var topic in topics)
        {
            await _knowledgeService.RememberFactAsync(
                topic: topic,
                fact: $"Owner marked as normal/ignorable: {description}",
                source: "user_feedback",
                confidence: 1.0);
        }

        _logger.LogInformation("Stored suppression preference for issue type '{IssueType}' with kinds [{Kinds}]",
            issueType, string.Join(", ", kinds));

        return kinds.Count == 0
            ? $"all {issueType} notifications"
            : $"{issueType} notifications about {string.Join(", ", kinds)}";
    }

    // The preferences that affect notifications: what the suppression check and the LLM prompt see.
    public Task<List<Knowledge>> ListPreferencesAsync() => _knowledgeService.RecallAsync(TopicPrefix);

    // Returns the removed row, or null when the id is not a valid notification preference.
    public async Task<Knowledge?> RemovePreferenceAsync(int id)
    {
        var removed = await _knowledgeService.InvalidateByIdAsync(id, TopicPrefix);
        if (removed != null)
        {
            _logger.LogInformation("Removed notification preference {Topic} (knowledge {KnowledgeId})", removed.Topic, id);
        }

        return removed;
    }

    // A source without kinds, or a kind list too long for Discord, gets a whole-class button.
    internal static string BuildFeedbackButtonId(string issueType, IEnumerable<string> kinds)
    {
        var wholeClass = $"{ButtonPrefixNormal}{NormalizeKey(issueType)}";
        var normalized = kinds.Select(NormalizeKey).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (normalized.Count == 0)
        {
            return wholeClass;
        }

        var id = $"{wholeClass}{KindSeparator}{string.Join(KindListSeparator, normalized)}";
        return id.Length <= MaxCustomIdLength ? id : wholeClass;
    }

    // A whole-class row (also every row stored before kinds existed) suppresses every finding of
    // the issue type. Kind rows suppress a finding only when every one of its kinds is suppressed.
    internal static bool IsIssueSuppressed(string issueType, IReadOnlyCollection<string> kinds, List<Knowledge> preferences)
    {
        var classTopic = $"{TopicPrefixSuppress}{NormalizeKey(issueType)}";
        var active = preferences
            .Where(p => p.Confidence >= 0.5)
            .Select(p => p.Topic)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (active.Contains(classTopic))
        {
            return true;
        }

        return kinds.Count > 0 && kinds.All(kind => active.Contains($"{classTopic}{KindSeparator}{NormalizeKey(kind)}"));
    }

    internal static string DescribePreference(Knowledge pref)
    {
        if (pref.Topic.StartsWith(TopicPrefixSuppress, StringComparison.OrdinalIgnoreCase))
        {
            return $"SUPPRESS {pref.Topic[TopicPrefixSuppress.Length..]}";
        }

        if (pref.Topic.StartsWith(TopicPrefixAlways, StringComparison.OrdinalIgnoreCase))
        {
            return $"ALWAYS {pref.Topic[TopicPrefixAlways.Length..]}";
        }

        const string prefPrefix = $"{TopicPrefix}:";
        var key = pref.Topic.StartsWith(prefPrefix, StringComparison.OrdinalIgnoreCase)
            ? pref.Topic[prefPrefix.Length..]
            : pref.Topic;
        return $"PREF {key}";
    }

    private async Task<bool> SendNotificationAsync(string report, NotificationCandidate candidate)
    {
        var userId = HomelabOwner.DiscordUserId;
        if (userId == 0)
        {
            return false;
        }

        var buttonId = BuildFeedbackButtonId(candidate.IssueType ?? candidate.Source, candidate.Kinds);
        if (candidate.Kinds.Count > 0 && !buttonId.Contains(KindSeparator, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Kinds [{Kinds}] do not fit a Discord button id; the feedback button suppresses the whole issue type",
                string.Join(", ", candidate.Kinds));
        }

        try
        {
            await _discordBot.SendDmSplitAsync(userId, report);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send notification for {Source}", candidate.Source);
            return false;
        }

        try
        {
            await _discordBot.SendDmNotificationFeedbackAsync(userId, buttonId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send feedback buttons for {Source}", candidate.Source);
        }

        _logger.LogInformation("Sent smart notification for {Source}", candidate.Source);
        return true;
    }

    // A user-role note of its own, like the bot-message records in the DM thread. The evaluation
    // turn has released the thread lock; this waits for any other turn on the thread. No ct: the
    // note must land, or the decision keeps saying the owner was told.
    private async Task RecordNotDeliveredAsync(ulong threadId, NotificationCandidate candidate)
    {
        using (await _conversationService.LockThreadAsync(threadId))
        {
            await _conversationService.AddUserMessageAsync(
                threadId, NotificationPrompts.BuildNotDeliveredNote(candidate.Source, candidate.Summary));
        }

        _logger.LogInformation(
            "Recorded in daily thread {ThreadId} that the notification for {Source} was not delivered",
            threadId, candidate.Source);
    }

    private async Task<List<Knowledge>> LoadNotificationPreferencesAsync()
    {
        try
        {
            return await _knowledgeService.RecallAsync(TopicPrefix);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load notification preferences");
            return [];
        }
    }

    private static string FormatPreferences(List<Knowledge> preferences)
    {
        if (preferences.Count == 0)
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (var pref in preferences)
        {
            sb.AppendLine($"- {DescribePreference(pref)}: {pref.Fact}");
        }

        return sb.ToString();
    }

    // The separators are replaced too, so a kind can never split a button id or a topic.
    private static string NormalizeKey(string key) =>
        key.Trim().ToLowerInvariant().Replace(' ', '_').Replace(KindSeparator, '_').Replace(KindListSeparator, '_');

    internal static ulong GenerateDailyThreadId(string dateString)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"daily_notification_{dateString}"));
        return BitConverter.ToUInt64(hash, 0);
    }
}
