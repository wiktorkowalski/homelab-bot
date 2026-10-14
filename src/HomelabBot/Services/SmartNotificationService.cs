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
    internal const string TopicPrefix = "notification_preference";
    internal const string ButtonPrefixNormal = "notif_normal_";
    internal const string ButtonPrefixInvestigate = "notif_investigate_";

    private readonly KernelService _kernelService;
    private readonly ConversationService _conversationService;
    private readonly DiscordBotService _discordBot;
    private readonly KnowledgeService _knowledgeService;
    private readonly ILogger<SmartNotificationService> _logger;
    private readonly Lock _cycleLock = new();

    private string _currentCycleDate = "";

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
            // Use yesterday so the first StartNewCycle call always rotates to today,
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
            && IsIssueSuppressed(candidate.IssueType, preferences))
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
            // The decision-only prompt tells the model not to use tools; sending their schemas
            // anyway is pure cost.
            analysis = await _kernelService.ProcessMessageAsync(
                threadId: CurrentDailyThreadId,
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

        return await SendNotificationAsync(report, candidate);
    }

    // Handle "Normal, ignore in future" button press.
    public async Task HandleSuppressFeedbackAsync(string issueType, string description)
    {
        await _knowledgeService.RememberFactAsync(
            topic: $"{TopicPrefixSuppress}{issueType}",
            fact: $"Owner marked as normal/ignorable: {description}",
            source: "user_feedback",
            confidence: 1.0);

        _logger.LogInformation("Stored suppression preference for issue type '{IssueType}'", issueType);
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
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send notification for {Source}", candidate.Source);
            return false;
        }

        try
        {
            await _discordBot.SendDmNotificationFeedbackAsync(userId, issueType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send feedback buttons for {Source}", candidate.Source);
        }

        _logger.LogInformation("Sent smart notification for {Source}", candidate.Source);
        return true;
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
        var expectedTopic = $"{TopicPrefixSuppress}{NormalizeIssueType(issueType)}";
        return preferences.Any(p =>
            p.Topic.Equals(expectedTopic, StringComparison.OrdinalIgnoreCase) &&
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
