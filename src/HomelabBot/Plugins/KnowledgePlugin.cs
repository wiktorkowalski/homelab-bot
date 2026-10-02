using System.ComponentModel;
using System.Text;
using HomelabBot.Services;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ModelContextProtocol.Server;

namespace HomelabBot.Plugins;

[McpServerToolType]
public sealed class KnowledgePlugin
{
    // Limits keep one recall from flooding the shared chat history (#119). An empty topic once
    // returned ~130k chars, which every later call in that thread re-sent.
    internal const int MaxFactsPerTopic = 30;
    internal const int MaxFactsWithoutTopic = 15;
    internal const int MaxRecallChars = 3_500;
    internal const int MinFactLineChars = 40;
    internal const string TruncationNote =
        "_More facts exist. Use SmartRecallKnowledge with a specific question, or recall a narrower topic._";

    private readonly KnowledgeService _knowledgeService;
    private readonly ILogger<KnowledgePlugin> _logger;

    public KnowledgePlugin(KnowledgeService knowledgeService, ILogger<KnowledgePlugin> logger)
    {
        _knowledgeService = knowledgeService;
        _logger = logger;
    }

    [KernelFunction]
    [Description("Remember a single atomic fact about the homelab. Keep facts small and specific (1-2 sentences max). Call multiple times for multiple facts - don't combine unrelated info into one fact.")]
    public async Task<string> RememberFact(
        [Description("Topic category (e.g., 'docker', 'loki', 'network', 'host', 'service', 'alias:mac', 'alias:container')")] string topic,
        [Description("The fact to remember")] string fact,
        [Description("How/when this was learned (optional)")] string? context = null)
    {
        _logger.LogInformation("Remembering fact: [{Topic}] {Fact}", topic, fact);

        await _knowledgeService.RememberFactAsync(topic, fact, context);
        return $"Remembered: [{topic}] {fact}";
    }

    [KernelFunction]
    [McpServerTool(Name = "SearchKnowledge")]
    [Description("Recall what you know about one specific topic. Call this BEFORE taking actions to check existing knowledge. Always pass a topic; for broad or fuzzy questions use SmartRecallKnowledge instead.")]
    public async Task<string> RecallKnowledge(
        [Description("Topic to recall (e.g., 'docker', 'loki', 'network', 'alias'). Required in practice: an empty topic returns only the top facts, not the whole knowledge base.")] string? topic = null)
    {
        topic = string.IsNullOrWhiteSpace(topic) ? null : topic.Trim();
        _logger.LogInformation("Recalling knowledge for topic: {Topic}", topic ?? "all");

        var limit = topic is null ? MaxFactsWithoutTopic : MaxFactsPerTopic;

        // One extra row tells us whether the cap cut anything off without a separate count query.
        var facts = await _knowledgeService.RecallAsync(topic, includeStale: false, limit: limit + 1);

        if (facts.Count == 0)
        {
            return topic != null
                ? $"I don't have any knowledge about '{topic}' yet."
                : "I don't have any knowledge stored yet. Use /discover to learn about the homelab.";
        }

        var moreFacts = facts.Count > limit;
        if (moreFacts)
        {
            facts = facts.Take(limit).ToList();
            _logger.LogDebug("Knowledge recall for {Topic} capped at {Limit} facts", topic ?? "all", limit);
        }

        return FormatKnowledgeFacts(facts, $"What I know about {topic ?? "the homelab"}", moreFacts);
    }

    [KernelFunction]
    [Description("Search knowledge using natural language. Use this when you don't know the exact topic name or want to find related information.")]
    public async Task<string> SmartRecallKnowledge(
        [Description("Natural language query (e.g., 'what port is portainer on', 'docker container info')")] string query,
        Kernel kernel)
    {
        _logger.LogInformation("Smart recalling knowledge for: {Query}", query);

        var chatService = kernel.GetRequiredService<IChatCompletionService>();
        var facts = await _knowledgeService.SmartRecallAsync(query, chatService);

        if (facts.Count == 0)
        {
            return $"No relevant knowledge found for: {query}";
        }

        return FormatKnowledgeFacts(facts, $"Relevant knowledge for \"{query}\"", moreFacts: false);
    }

    [KernelFunction]
    [Description("Learn a correction from the user. Call this when the user tells you something you knew was wrong.")]
    public async Task<string> LearnCorrection(
        [Description("Topic being corrected")] string topic,
        [Description("What was wrong")] string oldFact,
        [Description("The correct information")] string newFact)
    {
        _logger.LogInformation("Learning correction: [{Topic}] {Old} → {New}", topic, oldFact, newFact);

        await _knowledgeService.LearnCorrectionAsync(topic, oldFact, newFact);
        return $"Got it, updated my knowledge: {newFact}";
    }

    [KernelFunction]
    [Description("Resolve an alias to its actual value. Use this to translate user-friendly names to technical identifiers.")]
    public async Task<string> ResolveAlias(
        [Description("Alias type: 'mac' for device MACs, 'container' for Docker containers, 'entity' for Home Assistant")] string aliasType,
        [Description("The user's input containing the alias")] string userInput)
    {
        _logger.LogInformation("Resolving alias: [{Type}] {Input}", aliasType, userInput);

        var resolved = await _knowledgeService.ResolveAliasAsync(aliasType, userInput);

        if (resolved != null)
        {
            return $"Resolved '{userInput}' to: {resolved}";
        }

        return $"No alias found for '{userInput}' in {aliasType} aliases.";
    }

    [KernelFunction]
    [Description("Store a device alias for natural language references. Use format 'name → value'.")]
    public async Task<string> StoreAlias(
        [Description("Alias type: 'mac', 'container', or 'entity'")] string aliasType,
        [Description("User-friendly name (e.g., 'my PC', 'media server')")] string name,
        [Description("Technical value (e.g., MAC address, container name, entity_id)")] string value)
    {
        _logger.LogInformation("Storing alias: [{Type}] {Name} → {Value}", aliasType, name, value);

        var topic = $"alias:{aliasType}";
        var fact = $"\"{name}\" → \"{value}\"";
        await _knowledgeService.RememberFactAsync(topic, fact, source: "user_told", confidence: 1.0);

        return $"Stored alias: {name} → {value}";
    }

    [KernelFunction]
    [Description("Mark a piece of knowledge as outdated/invalid.")]
    public async Task<string> InvalidateKnowledge(
        [Description("Topic of the knowledge")] string topic,
        [Description("Part of the fact text to identify it")] string factContains)
    {
        _logger.LogInformation("Invalidating knowledge: [{Topic}] containing '{Contains}'", topic, factContains);

        await _knowledgeService.InvalidateAsync(topic, factContains);
        return $"Marked knowledge about '{factContains}' in {topic} as outdated.";
    }

    // Caps the text as well as the fact count: a few long facts can still flood the chat history,
    // and /knowledge puts this string into a Discord embed description (4096-char limit).
    internal static string FormatKnowledgeFacts(List<Data.Entities.Knowledge> facts, string heading, bool moreFacts)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**{heading}:**\n");

        var budgetHit = false;
        foreach (var group in facts.GroupBy(f => f.Topic))
        {
            var topicHeader = $"**{group.Key}**";
            if (sb.Length + topicHeader.Length + MinFactLineChars > MaxRecallChars)
            {
                budgetHit = true;
                break;
            }

            sb.AppendLine(topicHeader);
            foreach (var fact in group)
            {
                var stale = fact.LastVerified.HasValue &&
                    (DateTime.UtcNow - fact.LastVerified.Value).TotalDays > 30;
                var confidence = fact.Confidence < 0.5 ? " (uncertain)" : "";
                var warning = stale ? " ⚠️" : "";
                var line = $"- {fact.Fact}{confidence}{warning}";

                var room = MaxRecallChars - sb.Length;
                if (line.Length > room)
                {
                    // Shorten the fact that crosses the budget instead of dropping it, unless too
                    // little room is left for the fragment to carry meaning.
                    if (room >= MinFactLineChars)
                    {
                        sb.AppendLine(line[..(room - 2)] + "…");
                    }

                    budgetHit = true;
                    break;
                }

                sb.AppendLine(line);
            }

            if (budgetHit)
            {
                break;
            }

            sb.AppendLine();
        }

        if (moreFacts || budgetHit)
        {
            sb.AppendLine();
            sb.AppendLine(TruncationNote);
        }

        return sb.ToString();
    }
}
