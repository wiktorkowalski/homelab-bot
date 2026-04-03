using System.Text;
using HomelabBot.Data;
using HomelabBot.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HomelabBot.Services;

public sealed class MemoryService
{
    private readonly IDbContextFactory<HomelabDbContext> _dbFactory;
    private readonly ILogger<MemoryService> _logger;
    private readonly RunbookCompilerService _runbookCompiler;
    private readonly IncidentSimilarityService _similarityService;

    public MemoryService(
        IDbContextFactory<HomelabDbContext> dbFactory,
        ILogger<MemoryService> logger,
        RunbookCompilerService runbookCompiler,
        IncidentSimilarityService similarityService)
    {
        _dbFactory = dbFactory;
        _logger = logger;
        _runbookCompiler = runbookCompiler;
        _similarityService = similarityService;
    }

    public async Task<Investigation> StartInvestigationAsync(ulong threadId, string trigger)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var investigation = new Investigation
        {
            ThreadId = threadId,
            Trigger = trigger
        };

        db.Investigations.Add(investigation);
        await db.SaveChangesAsync();

        _logger.LogInformation("Started investigation {Id} for '{Trigger}'", investigation.Id, trigger);
        return investigation;
    }

    public async Task<Investigation?> GetActiveInvestigationAsync(ulong threadId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.Investigations
            .Include(i => i.Steps)
            .Where(i => i.ThreadId == threadId && !i.Resolved)
            .OrderByDescending(i => i.StartedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<Investigation?> GetInvestigationByIdAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.Investigations
            .Include(i => i.Steps.OrderBy(s => s.Timestamp))
            .FirstOrDefaultAsync(i => i.Id == id);
    }

    public async Task<(List<Investigation> Items, int TotalCount)> GetInvestigationsAsync(
        int page = 1,
        int pageSize = 20,
        bool? resolved = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var query = db.Investigations.AsQueryable();

        if (resolved.HasValue)
        {
            query = query.Where(i => i.Resolved == resolved.Value);
        }

        var totalCount = await query.CountAsync();

        var items = await query
            .Include(i => i.Steps)
            .OrderByDescending(i => i.StartedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (items, totalCount);
    }

    public async Task RecordStepAsync(int investigationId, string action, string? plugin, string? result)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var step = new InvestigationStep
        {
            InvestigationId = investigationId,
            Action = action,
            Plugin = plugin,
            ResultSummary = result
        };

        db.InvestigationSteps.Add(step);
        await db.SaveChangesAsync();

        _logger.LogDebug("Recorded step for investigation {Id}: {Action}", investigationId, action);
    }

    public async Task<Investigation?> ResolveInvestigationAsync(int investigationId, string resolution)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var investigation = await db.Investigations
            .Include(i => i.Steps)
            .FirstOrDefaultAsync(i => i.Id == investigationId);

        if (investigation == null)
        {
            return null;
        }

        investigation.Resolved = true;
        investigation.Resolution = resolution;

        // Try to extract a pattern if we have steps
        if (investigation.Steps.Count > 0)
        {
            await TryCreatePatternAsync(db, investigation);
        }

        await db.SaveChangesAsync();

        // Try to compile a runbook from the investigation steps
        try
        {
            await _runbookCompiler.CompileFromInvestigationAsync(investigation);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compile runbook from investigation {Id}", investigationId);
        }

        _logger.LogInformation("Resolved investigation {Id}: {Resolution}", investigationId, resolution);
        return investigation;
    }

    public async Task<List<Pattern>> GetRelevantPatternsAsync(string symptom, int limit = 3)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var keywords = KeywordMatcher.Tokenize(symptom);

        if (keywords.Count == 0)
        {
            return [];
        }

        var patterns = await db.Patterns
            .OrderByDescending(p => p.OccurrenceCount)
            .Take(50)
            .ToListAsync();

        var scored = patterns
            .Select(p =>
            {
                var keywordScore = KeywordMatcher.Score(symptom, p.Symptom);

                if (keywordScore == 0)
                {
                    return new { Pattern = p, Score = 0.0 };
                }

                // Frequency bonus only when there's a keyword match
                var score = keywordScore + Math.Min(p.OccurrenceCount / 3, 3);

                return new { Pattern = p, Score = score };
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Pattern.SuccessRate)
            .Take(limit)
            .Select(x => x.Pattern)
            .ToList();

        return scored;
    }

    public async Task RecordPatternFeedbackAsync(int patternId, bool helpful)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var pattern = await db.Patterns.FindAsync(patternId);
        if (pattern == null)
        {
            return;
        }

        if (helpful)
        {
            pattern.SuccessCount++;
        }
        else
        {
            pattern.FailureCount++;
        }

        await db.SaveChangesAsync();
        _logger.LogDebug("Recorded {Feedback} feedback for pattern {Id}", helpful ? "positive" : "negative", patternId);
    }

    public async Task<string> GenerateIncidentContextAsync(string symptom)
    {
        var similarIncidents = await _similarityService.FindSimilarAsync(symptom);
        var patterns = await GetRelevantPatternsAsync(symptom);

        if (similarIncidents.Count == 0 && patterns.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("\n## Relevant Past Incidents");

        if (patterns.Count > 0)
        {
            sb.AppendLine("\n### Known Patterns");
            foreach (var p in patterns)
            {
                var successInfo = (p.SuccessCount + p.FailureCount) > 0
                    ? $" (resolved {p.SuccessRate:F0}% of cases)"
                    : "";
                sb.AppendLine($"- **{p.Symptom}**: Usually caused by {p.CommonCause}{successInfo}");
                if (!string.IsNullOrEmpty(p.Resolution))
                {
                    sb.AppendLine($"  Fix: {p.Resolution}");
                }
            }
        }

        if (similarIncidents.Count > 0)
        {
            sb.AppendLine("\n### Similar Past Issues");
            foreach (var i in similarIncidents)
            {
                var age = DateTime.UtcNow - i.OccurredAt;
                var timeAgo = age.TotalDays > 1 ? $"{(int)age.TotalDays}d ago" : $"{(int)age.TotalHours}h ago";
                sb.AppendLine($"- [{timeAgo}] {i.Trigger} ({i.SimilarityScore:F0}% match)");
                if (!string.IsNullOrEmpty(i.Resolution))
                {
                    sb.AppendLine($"  Resolved: {i.Resolution}");
                }
            }
        }

        return sb.ToString();
    }

    private async Task TryCreatePatternAsync(HomelabDbContext db, Investigation investigation)
    {
        // Look for existing pattern with same trigger
        var existing = await db.Patterns
            .FirstOrDefaultAsync(p => p.Symptom == investigation.Trigger);

        if (existing != null)
        {
            existing.OccurrenceCount++;
            existing.LastSeen = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(investigation.Resolution))
            {
                existing.Resolution = investigation.Resolution;
            }
        }
        else if (!string.IsNullOrEmpty(investigation.Resolution))
        {
            // Create new pattern from resolved investigation
            var pattern = new Pattern
            {
                Symptom = investigation.Trigger,
                Resolution = investigation.Resolution,
                LastSeen = DateTime.UtcNow
            };

            // Try to identify common cause from steps
            var stepSummary = string.Join("; ", investigation.Steps
                .Where(s => !string.IsNullOrEmpty(s.ResultSummary))
                .Select(s => s.ResultSummary));

            if (!string.IsNullOrEmpty(stepSummary))
            {
                pattern.CommonCause = stepSummary.Length > 200
                    ? stepSummary[..197] + "..."
                    : stepSummary;
            }

            db.Patterns.Add(pattern);
        }
    }

    public async Task<List<Pattern>> ListPatternsAsync(int limit = 50)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Patterns
            .OrderByDescending(p => p.OccurrenceCount)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<Pattern?> GetPatternByIdAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Patterns.FindAsync(id);
    }

    public async Task<bool> DeletePatternAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var pattern = await db.Patterns.FindAsync(id);
        if (pattern == null)
        {
            return false;
        }

        db.Patterns.Remove(pattern);
        await db.SaveChangesAsync();
        return true;
    }
}
