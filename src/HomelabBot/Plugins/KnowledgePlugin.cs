using System.ComponentModel;
using System.Text;
using HomelabBot.Services;
using Microsoft.SemanticKernel;

namespace HomelabBot.Plugins;

public sealed class KnowledgePlugin
{
    private readonly KnowledgeService _knowledgeService;
    private readonly ILogger<KnowledgePlugin> _logger;

    public KnowledgePlugin(KnowledgeService knowledgeService, ILogger<KnowledgePlugin> logger)
    {
        _knowledgeService = knowledgeService;
        _logger = logger;
    }

    [KernelFunction]
    [Description("Remember a fact about the homelab. Call this when you discover something useful during operations.")]
    public async Task<string> RememberFact(
        [Description("Topic category (e.g., 'docker', 'loki', 'network', 'host', 'service', 'alias:mac', 'alias:container')")] string topic,
        [Description("The fact to remember")] string fact,
        [Description("How/when this was learned (optional)")] string? context = null)
    {
        _logger.LogDebug("Remembering fact: [{Topic}] {Fact}", topic, fact);

        await _knowledgeService.RememberFactAsync(topic, fact, context);
        return $"Remembered: [{topic}] {fact}";
    }

    [KernelFunction]
    [Description("Recall what you know about a topic. Call this BEFORE taking actions to check existing knowledge.")]
    public async Task<string> RecallKnowledge(
        [Description("Topic to recall (e.g., 'docker', 'loki', 'network', 'alias'). Leave empty for all.")] string? topic = null)
    {
        _logger.LogDebug("Recalling knowledge for topic: {Topic}", topic ?? "all");

        var facts = await _knowledgeService.RecallAsync(topic, includeStale: true);

        if (facts.Count == 0)
        {
            return topic != null
                ? $"I don't have any knowledge about '{topic}' yet."
                : "I don't have any knowledge stored yet. Use /discover to learn about the homelab.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"**What I know about {topic ?? "the homelab"}:**\n");

        var byTopic = facts.GroupBy(f => f.Topic);
        foreach (var group in byTopic)
        {
            sb.AppendLine($"**{group.Key}**");
            foreach (var fact in group)
            {
                var stale = fact.LastVerified.HasValue &&
                    (DateTime.UtcNow - fact.LastVerified.Value).TotalDays > 30;
                var confidence = fact.Confidence < 0.5 ? " (uncertain)" : "";
                var warning = stale ? " ⚠️" : "";
                sb.AppendLine($"- {fact.Fact}{confidence}{warning}");
            }

            sb.AppendLine();
        }

        return sb.ToString();
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
        _logger.LogDebug("Resolving alias: [{Type}] {Input}", aliasType, userInput);

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
}
