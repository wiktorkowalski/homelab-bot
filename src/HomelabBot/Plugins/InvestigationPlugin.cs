using System.ComponentModel;
using System.Text;
using HomelabBot.Services;
using Microsoft.SemanticKernel;

namespace HomelabBot.Plugins;

public sealed class InvestigationPlugin
{
    private readonly MemoryService _memoryService;
    private readonly ILogger<InvestigationPlugin> _logger;

    // Track current investigation per thread (in-memory for quick access)
    private readonly Dictionary<ulong, int> _activeInvestigations = new();

    public InvestigationPlugin(MemoryService memoryService, ILogger<InvestigationPlugin> logger)
    {
        _memoryService = memoryService;
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

        // Check for past similar incidents
        var pastContext = await _memoryService.GenerateIncidentContextAsync(symptom);
        if (!string.IsNullOrEmpty(pastContext))
        {
            sb.AppendLine(pastContext);
        }
        else
        {
            sb.AppendLine("No similar past incidents found.");
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
    [Description("Search past incidents for similar issues. Use this to check history before starting a new investigation.")]
    public async Task<string> SearchPastIncidents(
        [Description("Symptom or keywords to search for")] string symptom)
    {
        _logger.LogDebug("Searching past incidents for '{Symptom}'", symptom);

        var incidents = await _memoryService.SearchPastIncidentsAsync(symptom);

        if (incidents.Count == 0)
        {
            return $"No past incidents found matching '{symptom}'.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Found {incidents.Count} past incident(s):\n");

        foreach (var incident in incidents)
        {
            var age = DateTime.UtcNow - incident.StartedAt;
            var timeAgo = age.TotalDays > 1 ? $"{(int)age.TotalDays} days ago" : $"{(int)age.TotalHours} hours ago";

            sb.AppendLine($"**#{incident.Id}** ({timeAgo})");
            sb.AppendLine($"  Problem: {incident.Trigger}");
            if (!string.IsNullOrEmpty(incident.Resolution))
            {
                sb.AppendLine($"  Resolution: {incident.Resolution}");
            }

            if (incident.Steps.Count > 0)
            {
                sb.AppendLine($"  Steps: {incident.Steps.Count}");
            }

            sb.AppendLine();
        }

        return sb.ToString();
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
