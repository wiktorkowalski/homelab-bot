using System.Text;
using HomelabBot.Data;
using HomelabBot.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HomelabBot.Services;

public sealed class MemoryService
{
    private readonly IDbContextFactory<HomelabDbContext> _dbFactory;
    private readonly ILogger<MemoryService> _logger;

    public MemoryService(IDbContextFactory<HomelabDbContext> dbFactory, ILogger<MemoryService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
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
            query = query.Where(i => i.Resolved == resolved.Value);

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

        _logger.LogInformation("Resolved investigation {Id}: {Resolution}", investigationId, resolution);
        return investigation;
    }

    public async Task<List<Investigation>> SearchPastIncidentsAsync(string symptom, int limit = 5)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var keywords = symptom.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var investigations = await db.Investigations
            .Include(i => i.Steps)
            .Where(i => i.Resolved)
            .OrderByDescending(i => i.StartedAt)
            .Take(50)
            .ToListAsync();

        // Simple keyword matching for relevance
        var scored = investigations
            .Select(i => new
            {
                Investigation = i,
                Score = keywords.Count(k =>
                    i.Trigger.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                    (i.Resolution?.Contains(k, StringComparison.OrdinalIgnoreCase) ?? false))
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Investigation.StartedAt)
            .Take(limit)
            .Select(x => x.Investigation)
            .ToList();

        return scored;
    }

    public async Task<List<Pattern>> GetRelevantPatternsAsync(string symptom, int limit = 3)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var keywords = symptom.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var patterns = await db.Patterns
            .OrderByDescending(p => p.OccurrenceCount)
            .Take(20)
            .ToListAsync();

        var matched = patterns
            .Where(p => keywords.Any(k =>
                p.Symptom.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .Take(limit)
            .ToList();

        return matched;
    }

    public async Task<string> GenerateIncidentContextAsync(string symptom)
    {
        var pastIncidents = await SearchPastIncidentsAsync(symptom);
        var patterns = await GetRelevantPatternsAsync(symptom);

        if (pastIncidents.Count == 0 && patterns.Count == 0)
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
                sb.AppendLine($"- **{p.Symptom}**: Usually caused by {p.CommonCause}");
                if (!string.IsNullOrEmpty(p.Resolution))
                {
                    sb.AppendLine($"  Fix: {p.Resolution}");
                }
            }
        }

        if (pastIncidents.Count > 0)
        {
            sb.AppendLine("\n### Similar Past Issues");
            foreach (var i in pastIncidents)
            {
                var age = DateTime.UtcNow - i.StartedAt;
                var timeAgo = age.TotalDays > 1 ? $"{(int)age.TotalDays}d ago" : $"{(int)age.TotalHours}h ago";
                sb.AppendLine($"- [{timeAgo}] {i.Trigger}");
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
}
