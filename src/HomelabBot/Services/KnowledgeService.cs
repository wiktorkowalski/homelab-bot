using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HomelabBot.Data;
using HomelabBot.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel.ChatCompletion;

namespace HomelabBot.Services;

public sealed class KnowledgeService
{
    private static readonly ActivitySource ActivitySource = new("HomelabBot.Chat");

    private readonly IDbContextFactory<HomelabDbContext> _dbFactory;
    private readonly ILogger<KnowledgeService> _logger;

    public KnowledgeService(IDbContextFactory<HomelabDbContext> dbFactory, ILogger<KnowledgeService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<Knowledge> RememberFactAsync(
        string topic,
        string fact,
        string? context = null,
        string source = "discovered",
        double confidence = 0.8)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var existing = await db.Knowledge
            .Where(k => k.Topic == topic && k.Fact == fact && k.IsValid)
            .FirstOrDefaultAsync();

        if (existing != null)
        {
            existing.LastVerified = DateTime.UtcNow;
            existing.Confidence = Math.Max(existing.Confidence, confidence);
            await db.SaveChangesAsync();
            return existing;
        }

        var knowledge = new Knowledge
        {
            Topic = topic,
            Fact = fact,
            Context = context,
            Source = source,
            Confidence = confidence,
            LastVerified = DateTime.UtcNow
        };

        db.Knowledge.Add(knowledge);
        await db.SaveChangesAsync();
        _logger.LogDebug("Remembered: [{Topic}] {Fact}", topic, fact);
        return knowledge;
    }

    public async Task<List<Knowledge>> RecallAsync(string? topic = null, bool includeStale = false)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var query = db.Knowledge.Where(k => k.IsValid);

        if (!string.IsNullOrEmpty(topic))
        {
            query = query.Where(k => k.Topic == topic || k.Topic.StartsWith(topic + ":"));
        }

        var facts = await query.OrderByDescending(k => k.Confidence).ToListAsync();

        foreach (var fact in facts)
        {
            fact.LastUsed = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();

        if (!includeStale)
        {
            facts = facts.Where(f => f.Confidence > 0.3).ToList();
        }

        return facts;
    }

    public async Task<string?> ResolveAliasAsync(string aliasType, string userInput)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var topic = $"alias:{aliasType}";
        var aliases = await db.Knowledge
            .Where(k => k.Topic == topic && k.IsValid)
            .ToListAsync();

        var normalized = userInput.ToLowerInvariant().Trim();

        foreach (var alias in aliases)
        {
            var parts = alias.Fact.Split("→", 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                var aliasName = parts[0].Trim('"', '\'').ToLowerInvariant();
                if (normalized.Contains(aliasName) || aliasName.Contains(normalized))
                {
                    alias.LastUsed = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    return parts[1].Trim('"', '\'');
                }
            }
        }

        return null;
    }

    public async Task LearnCorrectionAsync(string topic, string oldFact, string newFact)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var existing = await db.Knowledge
            .Where(k => k.Topic == topic && k.Fact.Contains(oldFact) && k.IsValid)
            .FirstOrDefaultAsync();

        if (existing != null)
        {
            existing.IsValid = false;
        }

        var corrected = new Knowledge
        {
            Topic = topic,
            Fact = newFact,
            Source = "user_told",
            Confidence = 1.0,
            ContradictsId = existing?.Id,
            LastVerified = DateTime.UtcNow
        };

        db.Knowledge.Add(corrected);
        await db.SaveChangesAsync();
        _logger.LogInformation("Learned correction: [{Topic}] {Old} → {New}", topic, oldFact, newFact);
    }

    public async Task InvalidateAsync(string topic, string factContains)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var matches = await db.Knowledge
            .Where(k => k.Topic == topic && k.Fact.Contains(factContains) && k.IsValid)
            .ToListAsync();

        foreach (var m in matches)
        {
            m.IsValid = false;
        }

        await db.SaveChangesAsync();
    }

    public async Task<string> GenerateKnowledgePromptAsync(IEnumerable<string>? relevantTopics = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var query = db.Knowledge.Where(k => k.IsValid && k.Confidence > 0.3);

        if (relevantTopics != null)
        {
            var topics = relevantTopics.ToList();
            query = query.Where(k => topics.Any(t => k.Topic == t || k.Topic.StartsWith(t + ":")));
        }

        var facts = await query
            .OrderByDescending(k => k.Confidence)
            .Take(50)
            .ToListAsync();

        if (facts.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("\n## Your Current Knowledge");

        var byTopic = facts.GroupBy(f => f.Topic.Split(':')[0]);
        foreach (var group in byTopic)
        {
            sb.AppendLine($"\n### {group.Key}");
            foreach (var fact in group)
            {
                var stale = fact.LastVerified.HasValue &&
                    (DateTime.UtcNow - fact.LastVerified.Value).TotalDays > 30;
                var warning = stale ? " ⚠️" : "";
                sb.AppendLine($"- {fact.Fact}{warning}");
            }
        }

        return sb.ToString();
    }

    public async Task DecayConfidenceAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var now = DateTime.UtcNow;
        var facts = await db.Knowledge.Where(k => k.IsValid).ToListAsync();

        foreach (var fact in facts)
        {
            if (fact.LastVerified.HasValue)
            {
                var daysSinceVerified = (now - fact.LastVerified.Value).TotalDays;
                if (daysSinceVerified > 30)
                {
                    fact.Confidence -= 0.1 * (daysSinceVerified / 30);
                }
            }

            if (fact.LastUsed.HasValue)
            {
                var daysSinceUsed = (now - fact.LastUsed.Value).TotalDays;
                if (daysSinceUsed > 60)
                {
                    fact.Confidence -= 0.05;
                }
            }

            fact.Confidence = Math.Max(0, Math.Min(1, fact.Confidence));
        }

        await db.SaveChangesAsync();
    }

    public async Task<List<string>> GetAllTopicsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.Knowledge
            .Where(k => k.IsValid)
            .Select(k => k.Topic)
            .Distinct()
            .OrderBy(t => t)
            .ToListAsync();
    }

    public async Task<List<Knowledge>> SmartRecallAsync(
        string naturalQuery,
        IChatCompletionService chatService)
    {
        using var activity = ActivitySource.StartActivity("SmartRecall", ActivityKind.Internal);
        activity?.SetTag("langfuse.span.name", "SmartRecall");
        activity?.SetTag("langfuse.span.input", JsonSerializer.Serialize(new { query = naturalQuery }));

        var allTopics = await GetAllTopicsAsync();
        activity?.SetTag("knowledge.available_topics_count", allTopics.Count);
        activity?.SetTag("knowledge.available_topics", JsonSerializer.Serialize(allTopics));

        if (allTopics.Count == 0)
        {
            activity?.SetTag("langfuse.span.output", JsonSerializer.Serialize(new { status = "no_topics", facts_count = 0 }));
            return [];
        }

        var prompt = $"""
            Given these knowledge topics: [{string.Join(", ", allTopics)}]

            Which topics are relevant to answer this query: "{naturalQuery}"

            Return ONLY a JSON array of relevant topic names, e.g. ["topic1", "topic2"]
            If no topics are relevant, return []
            """;

        var history = new ChatHistory();
        history.AddUserMessage(prompt);

        var response = await chatService.GetChatMessageContentAsync(history);
        var content = response.Content ?? "[]";
        activity?.SetTag("knowledge.llm_raw_response", content);

        // Extract JSON array from response (handle markdown code blocks)
        var jsonMatch = System.Text.RegularExpressions.Regex.Match(
            content, @"\[.*?\]", System.Text.RegularExpressions.RegexOptions.Singleline);

        if (!jsonMatch.Success)
        {
            _logger.LogWarning("SmartRecall: Could not parse topics from LLM response: {Response}", content);
            activity?.SetTag("langfuse.span.output", JsonSerializer.Serialize(new { status = "parse_error", error = content }));
            return [];
        }

        List<string>? matchedTopics;
        try
        {
            matchedTopics = JsonSerializer.Deserialize<List<string>>(jsonMatch.Value);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "SmartRecall: Failed to parse JSON: {Json}", jsonMatch.Value);
            activity?.SetTag("langfuse.span.output", JsonSerializer.Serialize(new { status = "json_error", error = jsonMatch.Value }));
            return [];
        }

        if (matchedTopics == null || matchedTopics.Count == 0)
        {
            activity?.SetTag("knowledge.matched_topics", "[]");
            activity?.SetTag("langfuse.span.output", JsonSerializer.Serialize(new { status = "no_matches", matched_topics = Array.Empty<string>(), facts_count = 0 }));
            return [];
        }

        activity?.SetTag("knowledge.matched_topics", JsonSerializer.Serialize(matchedTopics));
        activity?.SetTag("knowledge.matched_topics_count", matchedTopics.Count);

        // Recall facts from all matched topics
        var results = new List<Knowledge>();
        foreach (var topic in matchedTopics)
        {
            var facts = await RecallAsync(topic);
            results.AddRange(facts);
        }

        // Remove duplicates and sort by confidence
        var finalResults = results
            .DistinctBy(k => k.Id)
            .OrderByDescending(k => k.Confidence)
            .ToList();

        activity?.SetTag("knowledge.facts_count", finalResults.Count);
        activity?.SetTag("langfuse.span.output", JsonSerializer.Serialize(new
        {
            status = "success",
            matched_topics = matchedTopics,
            facts_count = finalResults.Count,
            facts = finalResults.Select(f => new { f.Topic, f.Fact, f.Confidence })
        }));

        return finalResults;
    }
}
