using HomelabBot.Data;
using HomelabBot.Data.Entities;
using HomelabBot.Models;
using Microsoft.EntityFrameworkCore;

namespace HomelabBot.Services;

public sealed class IncidentSimilarityService
{
    private readonly IDbContextFactory<HomelabDbContext> _dbFactory;
    private readonly ILogger<IncidentSimilarityService> _logger;

    public IncidentSimilarityService(
        IDbContextFactory<HomelabDbContext> dbFactory,
        ILogger<IncidentSimilarityService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<List<SimilarIncident>> FindSimilarAsync(
        string symptom,
        string? containerName = null,
        Dictionary<string, string>? alertLabels = null,
        int limit = 5,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var keywords = symptom.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(k => k.Length > 2)
            .ToArray();

        if (keywords.Length == 0)
        {
            return [];
        }

        var alertName = alertLabels?.GetValueOrDefault("alertname");

        var investigationsTask = db.Investigations
            .Where(i => i.Resolved)
            .OrderByDescending(i => i.StartedAt)
            .Take(100)
            .ToListAsync(ct);

        var remediationsByContainer = new Dictionary<string, List<RemediationAction>>();
        if (containerName != null)
        {
            var containerLower = containerName.ToLowerInvariant();
            var remediations = await db.RemediationActions
                .Where(a => a.Success && a.ContainerName == containerName)
                .OrderByDescending(a => a.ExecutedAt)
                .Take(50)
                .ToListAsync(ct);

            if (remediations.Count > 0)
            {
                remediationsByContainer[containerLower] = remediations;
            }
        }

        var investigations = await investigationsTask;
        var scored = new List<SimilarIncident>();

        foreach (var investigation in investigations)
        {
            var (score, reasons) = ComputeSimilarity(
                keywords, investigation, containerName, alertName);

            if (score <= 0)
            {
                continue;
            }

            var successfulRemediations = new List<RemediationAction>();
            if (containerName != null &&
                remediationsByContainer.TryGetValue(containerName.ToLowerInvariant(), out var containerRemediations))
            {
                successfulRemediations = containerRemediations
                    .Where(a => a.ExecutedAt >= investigation.StartedAt.AddHours(-1)
                             && a.ExecutedAt <= investigation.StartedAt.AddHours(1))
                    .ToList();
            }

            if (successfulRemediations.Count > 0)
            {
                score += 10;
                reasons.Add("has successful remediation");
            }

            scored.Add(new SimilarIncident
            {
                InvestigationId = investigation.Id,
                Trigger = investigation.Trigger,
                Resolution = investigation.Resolution,
                SimilarityScore = Math.Min(100, score),
                MatchReasons = reasons,
                OccurredAt = investigation.StartedAt,
                SuccessfulRemediations = successfulRemediations
            });
        }

        var results = scored
            .OrderByDescending(s => s.SimilarityScore)
            .Take(limit)
            .ToList();

        if (results.Count > 0)
        {
            _logger.LogDebug("Found {Count} similar incidents for '{Symptom}' (top score: {Score})",
                results.Count, symptom, results[0].SimilarityScore);
        }

        return results;
    }

    public static string FormatDejaVuContext(List<SimilarIncident> incidents)
    {
        if (incidents.Count == 0)
        {
            return string.Empty;
        }

        var top = incidents[0];
        var lines = new List<string>
        {
            $"**Similar to past incident #{top.InvestigationId}** ({top.SimilarityScore:F0}% match)"
        };

        if (!string.IsNullOrEmpty(top.Resolution))
        {
            lines.Add($"Previous fix: {top.Resolution}");
        }

        if (top.SuccessfulRemediations.Count > 0)
        {
            var rem = top.SuccessfulRemediations[0];
            lines.Add($"Successful remediation: {rem.ActionType} on {rem.ContainerName}");
        }

        if (top.MatchReasons.Count > 0)
        {
            lines.Add($"Match: {string.Join(", ", top.MatchReasons)}");
        }

        return string.Join("\n", lines);
    }

    private static (double Score, List<string> Reasons) ComputeSimilarity(
        string[] keywords,
        Investigation investigation,
        string? containerName,
        string? alertName)
    {
        var score = 0.0;
        var reasons = new List<string>();

        var triggerLower = investigation.Trigger.ToLowerInvariant();
        var resolutionLower = investigation.Resolution?.ToLowerInvariant() ?? "";

        var triggerMatches = keywords.Count(k => triggerLower.Contains(k));
        if (triggerMatches == 0)
        {
            var resolutionMatches = keywords.Count(k => resolutionLower.Contains(k));
            if (resolutionMatches == 0)
            {
                return (0, reasons);
            }

            score += resolutionMatches * 8;
            reasons.Add("keyword match in resolution");
        }
        else
        {
            var matchRatio = (double)triggerMatches / keywords.Length;
            score += matchRatio * 40;
            reasons.Add($"{triggerMatches}/{keywords.Length} keywords match");
        }

        if (containerName != null && triggerLower.Contains(containerName.ToLowerInvariant()))
        {
            score += 25;
            reasons.Add("same container");
        }

        if (alertName != null && triggerLower.Contains(alertName.ToLowerInvariant()))
        {
            score += 20;
            reasons.Add("same alert");
        }

        var age = DateTime.UtcNow - investigation.StartedAt;
        if (age.TotalDays < 7)
        {
            score += 5;
        }
        else if (age.TotalDays < 30)
        {
            score += 2;
        }

        return (score, reasons);
    }
}
