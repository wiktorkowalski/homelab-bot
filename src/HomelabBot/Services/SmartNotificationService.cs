using System.Security.Cryptography;
using System.Text;
using HomelabBot.Configuration;
using HomelabBot.Data.Entities;
using HomelabBot.Models;

namespace HomelabBot.Services;

public sealed class SmartNotificationService
{
    private const string TopicPrefixSuppress = "notification_preference:suppress:";
    private const string TopicPrefixAlways = "notification_preference:always:";
    private const string TopicPrefix = "notification_preference";
    internal const string ButtonPrefixNormal = "notif_normal_";
    internal const string ButtonPrefixInvestigate = "notif_investigate_";

    private readonly KernelService _kernelService;
    private readonly ConversationService _conversationService;
    private readonly DiscordBotService _discordBot;
    private readonly KnowledgeService _knowledgeService;
    private readonly ILogger<SmartNotificationService> _logger;
    private readonly Lock _cycleLock = new();

    private string _currentCycleDate = "";
    private bool _hasConversation;

    public SmartNotificationService(
        KernelService kernelService,
        ConversationService conversationService,
        DiscordBotService discordBot,
        KnowledgeService knowledgeService,
        ILogger<SmartNotificationService> logger)
    {
        _kernelService = kernelService;
        _conversationService = conversationService;
        _discordBot = discordBot;
        _knowledgeService = knowledgeService;
        _logger = logger;

        lock (_cycleLock)
        {
            // Use yesterday so the first StartNewCycleAsync call always rotates to today,
            // ensuring cleanup of any orphaned conversation from a previous process.
            _currentCycleDate = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");
        }
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

    /// <summary>
    /// Start a new daily cycle. Runs end-of-cycle learning on the previous conversation,
    /// then resets context for the new day.
    /// </summary>
    public async Task StartNewCycleAsync(CancellationToken ct)
    {
        string previousDate;
        bool hadConversation;
        ulong previousThreadId;

        lock (_cycleLock)
        {
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            if (today == _currentCycleDate)
            {
                return;
            }

            previousDate = _currentCycleDate;
            hadConversation = _hasConversation;
            previousThreadId = GenerateDailyThreadId(_currentCycleDate);

            _currentCycleDate = today;
            _hasConversation = false;
        }

        if (hadConversation && previousThreadId != 0)
        {
            await RunEndOfCycleLearningAsync(previousThreadId, previousDate, ct);
        }

        if (previousThreadId != 0)
        {
            _conversationService.ClearHistory(previousThreadId);
        }

        _logger.LogInformation("Started new notification cycle {Date}", _currentCycleDate);
    }

    /// <summary>
    /// Evaluate a finding: investigate via LLM, decide if it's worth notifying the owner.
    /// Returns true if a notification was actually sent.
    /// </summary>
    public async Task<bool> EvaluateAndNotifyAsync(NotificationCandidate candidate, CancellationToken ct)
    {
        _logger.LogInformation("Evaluating notification candidate from {Source}: {Summary}",
            candidate.Source, candidate.Summary);

        var preferences = await LoadNotificationPreferencesAsync();

        if (candidate.IssueType != null && IsIssueSuppressed(candidate.IssueType, preferences))
        {
            _logger.LogInformation("Issue type '{IssueType}' is suppressed, skipping", candidate.IssueType);
            return false;
        }

        var prefsText = FormatPreferences(preferences);
        var prompt = candidate.AlreadyInvestigated
            ? NotificationPrompts.BuildDecisionOnlyPrompt(candidate.Summary, candidate.RawData, prefsText)
            : NotificationPrompts.BuildInvestigationPrompt(candidate.Summary, candidate.RawData, prefsText);

        string analysis;
        try
        {
            analysis = await _kernelService.ProcessMessageAsync(
                threadId: CurrentDailyThreadId,
                userMessage: prompt,
                traceType: TraceType.Scheduled,
                maxTokens: NotificationPrompts.MaxTokens,
                systemPromptOverride: NotificationPrompts.InvestigationSystem,
                ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run LLM investigation for {Source}", candidate.Source);
            return false;
        }

        var shouldNotify = analysis.Contains(NotificationPrompts.TagNotify, StringComparison.OrdinalIgnoreCase);

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

        return await SendNotificationAsync(report, candidate);
    }

    /// <summary>
    /// Handle "Normal, ignore in future" button press.
    /// </summary>
    public async Task HandleSuppressFeedbackAsync(string issueType, string description)
    {
        await _knowledgeService.RememberFactAsync(
            topic: $"{TopicPrefixSuppress}{issueType}",
            fact: $"Owner marked as normal/ignorable: {description}",
            source: "user_feedback",
            confidence: 1.0);

        _logger.LogInformation("Stored suppression preference for issue type '{IssueType}'", issueType);
    }

    /// <summary>
    /// Record that the owner replied in DMs (for end-of-cycle learning).
    /// </summary>
    public void MarkConversationActive()
    {
        lock (_cycleLock)
        {
            _hasConversation = true;
        }
    }

    private async Task<bool> SendNotificationAsync(string report, NotificationCandidate candidate)
    {
        var userId = HomelabOwner.DiscordUserId;
        if (userId == 0)
        {
            return false;
        }

        var issueType = NormalizeIssueType(candidate.IssueType ?? candidate.Source);

        try
        {
            await _discordBot.SendDmSplitAsync(userId, report);
            await _discordBot.SendDmNotificationFeedbackAsync(userId, issueType);

            _logger.LogInformation("Sent smart notification for {Source}", candidate.Source);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send notification for {Source}", candidate.Source);
            return false;
        }
    }

    private async Task RunEndOfCycleLearningAsync(ulong threadId, string cycleDate, CancellationToken ct)
    {
        _logger.LogInformation("Running end-of-cycle learning for {Date}", cycleDate);

        try
        {
            var learningThreadId = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var analysis = await _kernelService.ProcessMessageAsync(
                threadId: learningThreadId,
                userMessage: NotificationPrompts.EndOfCycleLearning,
                traceType: TraceType.Scheduled,
                maxTokens: 1024,
                ct: ct);

            _conversationService.ClearHistory(learningThreadId);

            if (analysis.Contains(NotificationPrompts.TagNoUpdates, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("No notification preference updates from cycle conversation");
                return;
            }

            foreach (var line in analysis.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                await TryStoreLearningPreferenceAsync(line, "SUPPRESS:", "suppress",
                    "Owner considers this non-actionable", 0.8);
                await TryStoreLearningPreferenceAsync(line, "ALWAYS:", "always",
                    "Owner wants to always be notified", 0.9);
            }

            _logger.LogInformation("End-of-cycle learning completed for {Date}", cycleDate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to run end-of-cycle learning");
        }
    }

    private async Task TryStoreLearningPreferenceAsync(
        string line, string prefix, string topicSegment, string factTemplate, double confidence)
    {
        if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var content = line[prefix.Length..].Trim();
        var parts = content.Split('—', 2);
        var issueType = NormalizeIssueType(parts[0].Trim());
        var reason = parts.Length > 1 ? parts[1].Trim() : "Inferred from conversation";

        await _knowledgeService.RememberFactAsync(
            topic: $"notification_preference:{topicSegment}:{issueType}",
            fact: $"{factTemplate}: {reason}",
            source: "cycle_analysis",
            confidence: confidence);
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

    private static bool IsIssueSuppressed(string issueType, List<Knowledge> preferences)
    {
        var normalizedType = NormalizeIssueType(issueType);
        return preferences.Any(p =>
            p.Topic.StartsWith(TopicPrefixSuppress, StringComparison.OrdinalIgnoreCase) &&
            p.Topic.Contains(normalizedType, StringComparison.OrdinalIgnoreCase) &&
            p.Confidence >= 0.5);
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
            var type = pref.Topic.StartsWith(TopicPrefixSuppress)
                ? "SUPPRESS" : pref.Topic.StartsWith(TopicPrefixAlways)
                    ? "ALWAYS" : "PREF";
            var key = pref.Topic.Split(':').LastOrDefault() ?? pref.Topic;
            sb.AppendLine($"- {type} {key}: {pref.Fact}");
        }

        return sb.ToString();
    }

    private static string NormalizeIssueType(string issueType) =>
        issueType.ToLowerInvariant().Replace(' ', '_');

    private static ulong GenerateDailyThreadId(string dateString)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"daily_notification_{dateString}"));
        return BitConverter.ToUInt64(hash, 0);
    }
}
