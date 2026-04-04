using System.ComponentModel;
using System.Text;
using HomelabBot.Models;
using HomelabBot.Services;
using Microsoft.SemanticKernel;
using ModelContextProtocol.Server;

namespace HomelabBot.Plugins;

[McpServerToolType]
public sealed class InvestigationPlugin
{
    private readonly MemoryService _memoryService;
    private readonly ConversationService _conversationService;
    private readonly IncidentSimilarityService _similarityService;
    private readonly ILogger<InvestigationPlugin> _logger;

    // Track current investigation per thread (in-memory for quick access)
    private readonly Dictionary<ulong, int> _activeInvestigations = new();

    public InvestigationPlugin(
        MemoryService memoryService,
        ConversationService conversationService,
        IncidentSimilarityService similarityService,
        ILogger<InvestigationPlugin> logger)
    {
        _memoryService = memoryService;
        _conversationService = conversationService;
        _similarityService = similarityService;
        _logger = logger;
    }

    [KernelFunction]
    [Description("Start tracking an investigation for a problem. Call this when beginning to diagnose an issue. Returns past similar incidents if any.")]
    public async Task<string> StartInvestigation(
        [Description("Thread/channel ID for this conversation")] ulong threadId,
        [Description("Brief description of the symptom or problem (e.g., 'network slow', 'container crashed')")] string symptom)
    {
        _logger.LogInformation("Starting investigation for '{Symptom}' in thread {ThreadId}", symptom, threadId);

        // Check for existing active investigation
        var existing = await _memoryService.GetActiveInvestigationAsync(threadId);
        if (existing != null)
        {
            _activeInvestigations[threadId] = existing.Id;
            return $"Already have an active investigation (#{existing.Id}): {existing.Trigger}\nUse RecordStep to log findings, or ResolveInvestigation when done.";
        }

        // Start new investigation
        var investigation = await _memoryService.StartInvestigationAsync(threadId, symptom);
        _activeInvestigations[threadId] = investigation.Id;

        var sb = new StringBuilder();
        sb.AppendLine($"Started investigation #{investigation.Id}: {symptom}");

        // Check for similar past incidents with scoring
        var similarIncidents = await _similarityService.FindSimilarAsync(symptom);
        if (similarIncidents.Count > 0)
        {
            sb.AppendLine("\n### Similar Past Incidents");
            foreach (var similar in similarIncidents)
            {
                var age = DateTime.UtcNow - similar.OccurredAt;
                var timeAgo = age.TotalDays > 1 ? $"{(int)age.TotalDays}d ago"
                    : age.TotalHours >= 1 ? $"{(int)age.TotalHours}h ago"
                    : $"{(int)age.TotalMinutes}m ago";
                sb.AppendLine($"- **#{similar.InvestigationId}** ({similar.SimilarityScore:F0}% match, {timeAgo}): {similar.Trigger}");
                if (!string.IsNullOrEmpty(similar.Resolution))
                {
                    sb.AppendLine($"  Fix: {similar.Resolution}");
                }

                if (similar.MatchReasons.Count > 0)
                {
                    sb.AppendLine($"  Match: {string.Join(", ", similar.MatchReasons)}");
                }
            }
        }

        // Also check known patterns
        var pastContext = await _memoryService.GenerateIncidentContextAsync(symptom);
        if (!string.IsNullOrEmpty(pastContext))
        {
            sb.AppendLine(pastContext);
        }

        // Search past conversations for additional context
        var conversationResults = await _conversationService.SearchConversationsAsync(symptom, limit: 3);
        if (conversationResults.Count > 0)
        {
            sb.AppendLine("\n### Relevant Past Conversations");
            foreach (var r in conversationResults)
            {
                sb.AppendLine($"- [{r.TimeAgo}] {r.DisplayTitle}");
                if (r.RelevantMessages.Count > 0)
                {
                    sb.AppendLine($"  > {ConversationSearchResult.Truncate(r.RelevantMessages[0].Content, 100)}");
                }
            }
        }

        if (similarIncidents.Count == 0 && string.IsNullOrEmpty(pastContext) && conversationResults.Count == 0)
        {
            sb.AppendLine("No similar past incidents or conversations found.");
        }

        sb.AppendLine("\nUse RecordStep() to log each diagnostic action.");

        return sb.ToString();
    }

    [KernelFunction]
    [Description("Record a diagnostic step taken during investigation. Call this after each check or action.")]
    public async Task<string> RecordStep(
        [Description("Thread/channel ID")] ulong threadId,
        [Description("What action was taken (e.g., 'checked router CPU', 'listed containers')")] string action,
        [Description("Plugin/tool used (e.g., 'MikroTik', 'Docker')")] string? plugin = null,
        [Description("Brief summary of the result (e.g., 'CPU at 45%', '3 containers stopped')")] string? result = null)
    {
        if (!_activeInvestigations.TryGetValue(threadId, out var investigationId))
        {
            var active = await _memoryService.GetActiveInvestigationAsync(threadId);
            if (active == null)
            {
                return "No active investigation. Call StartInvestigation first.";
            }

            investigationId = active.Id;
            _activeInvestigations[threadId] = investigationId;
        }

        await _memoryService.RecordStepAsync(investigationId, action, plugin, result);
        return $"Recorded: {action}" + (result != null ? $" → {result}" : "");
    }

    [KernelFunction]
    [Description("Resolve and close the current investigation. Call this when the issue is fixed or root cause identified.")]
    public async Task<string> ResolveInvestigation(
        [Description("Thread/channel ID")] ulong threadId,
        [Description("What fixed the issue or what was the root cause")] string resolution)
    {
        if (!_activeInvestigations.TryGetValue(threadId, out var investigationId))
        {
            var active = await _memoryService.GetActiveInvestigationAsync(threadId);
            if (active == null)
            {
                return "No active investigation to resolve.";
            }

            investigationId = active.Id;
        }

        var investigation = await _memoryService.ResolveInvestigationAsync(investigationId, resolution);
        if (investigation == null)
        {
            return "Investigation not found.";
        }

        _activeInvestigations.Remove(threadId);

        var sb = new StringBuilder();
        sb.AppendLine($"Investigation #{investigation.Id} resolved: {resolution}");
        sb.AppendLine($"Steps taken: {investigation.Steps.Count}");

        if (investigation.Steps.Count > 0)
        {
            sb.AppendLine("Summary:");
            foreach (var step in investigation.Steps.TakeLast(5))
            {
                sb.AppendLine($"  - {step.Action}");
            }
        }

        sb.AppendLine("\nThis incident has been saved for future reference.");

        return sb.ToString();
    }

    [KernelFunction]
    [McpServerTool]
    [Description("Search past incidents for similar issues. Use this to check history before starting a new investigation.")]
    public async Task<string> SearchPastIncidents(
        [Description("Symptom or keywords to search for")] string symptom)
    {
        _logger.LogDebug("Searching past incidents for '{Symptom}'", symptom);

        var incidents = await _similarityService.FindSimilarAsync(symptom);

        if (incidents.Count == 0)
        {
            return $"No past incidents found matching '{symptom}'.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Found {incidents.Count} past incident(s):\n");

        foreach (var incident in incidents)
        {
            var age = DateTime.UtcNow - incident.OccurredAt;
            var timeAgo = age.TotalDays > 1 ? $"{(int)age.TotalDays} days ago" : $"{(int)age.TotalHours} hours ago";

            sb.AppendLine($"**#{incident.InvestigationId}** ({timeAgo}, {incident.SimilarityScore:F0}% match)");
            sb.AppendLine($"  Problem: {incident.Trigger}");
            if (!string.IsNullOrEmpty(incident.Resolution))
            {
                sb.AppendLine($"  Resolution: {incident.Resolution}");
            }

            if (incident.MatchReasons.Count > 0)
            {
                sb.AppendLine($"  Match: {string.Join(", ", incident.MatchReasons)}");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    [KernelFunction]
    [Description("Search past conversations for context on an issue. Use this to find what was discussed or done previously about a topic.")]
    public async Task<string> SearchConversations(
        [Description("Keywords to search for (e.g. 'Plex crashed', 'high CPU', 'TLS cert')")] string query)
    {
        _logger.LogDebug("Searching conversations for '{Query}'", query);

        var results = await _conversationService.SearchConversationsAsync(query);

        if (results.Count == 0)
        {
            return $"No past conversations found matching '{query}'.";
        }

        return ConversationSearchResult.FormatResults(results);
    }

    [KernelFunction]
    [Description("Get the current investigation status for this thread.")]
    public async Task<string> GetInvestigationStatus(
        [Description("Thread/channel ID")] ulong threadId)
    {
        var active = await _memoryService.GetActiveInvestigationAsync(threadId);

        if (active == null)
        {
            return "No active investigation in this thread.";
        }

        var sb = new StringBuilder();
        var duration = DateTime.UtcNow - active.StartedAt;

        sb.AppendLine($"**Active Investigation #{active.Id}**");
        sb.AppendLine($"Problem: {active.Trigger}");
        sb.AppendLine($"Duration: {(int)duration.TotalMinutes} minutes");
        sb.AppendLine($"Steps taken: {active.Steps.Count}");

        if (active.Steps.Count > 0)
        {
            sb.AppendLine("\nRecent steps:");
            foreach (var step in active.Steps.TakeLast(5))
            {
                var stepTime = step.Timestamp.ToString("HH:mm");
                sb.AppendLine($"  [{stepTime}] {step.Action}");
                if (!string.IsNullOrEmpty(step.ResultSummary))
                {
                    sb.AppendLine($"          → {step.ResultSummary}");
                }
            }
        }

        return sb.ToString();
    }
}
