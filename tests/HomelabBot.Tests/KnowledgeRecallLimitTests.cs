using HomelabBot.Plugins;
using HomelabBot.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomelabBot.Tests;

public sealed class KnowledgeRecallLimitTests : IClassFixture<DatabaseFixture>
{
    private readonly DatabaseFixture _fixture;
    private readonly KnowledgeService _service;
    private readonly KnowledgePlugin _plugin;

    public KnowledgeRecallLimitTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
        _service = new KnowledgeService(_fixture.DbContextFactory, NullLogger<KnowledgeService>.Instance);
        _plugin = new KnowledgePlugin(_service, NullLogger<KnowledgePlugin>.Instance);
    }

    private static int FactLines(string text) => text.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal));

    [Fact]
    public async Task RecallKnowledge_CapsFactCountForTopic()
    {
        var topic = $"cap-{Guid.NewGuid():N}";
        for (var i = 0; i < KnowledgePlugin.MaxFactsPerTopic + 10; i++)
        {
            await _service.RememberFactAsync(topic, $"f{i}");
        }

        var result = await _plugin.RecallKnowledge(topic);

        Assert.Equal(KnowledgePlugin.MaxFactsPerTopic, FactLines(result));
        Assert.Contains(KnowledgePlugin.TruncationNote, result);
    }

    [Fact]
    public async Task RecallKnowledge_NoNoteWhenUnderCap()
    {
        var topic = $"small-{Guid.NewGuid():N}";
        await _service.RememberFactAsync(topic, "only fact");

        var result = await _plugin.RecallKnowledge(topic);

        Assert.Equal(1, FactLines(result));
        Assert.DoesNotContain(KnowledgePlugin.TruncationNote, result);
    }

    [Fact]
    public async Task RecallKnowledge_EmptyTopicReturnsOnlyTopFacts()
    {
        var topic = $"bulk-{Guid.NewGuid():N}";
        for (var i = 0; i < KnowledgePlugin.MaxFactsWithoutTopic + 5; i++)
        {
            await _service.RememberFactAsync(topic, $"bulk fact {i}");
        }

        var result = await _plugin.RecallKnowledge();

        Assert.True(FactLines(result) <= KnowledgePlugin.MaxFactsWithoutTopic);
        Assert.Contains(KnowledgePlugin.TruncationNote, result);
    }

    [Fact]
    public async Task RecallKnowledge_CapsTextSize()
    {
        var topic = $"long-{Guid.NewGuid():N}";
        for (var i = 0; i < 10; i++)
        {
            await _service.RememberFactAsync(topic, $"{i} {new string('x', 1_000)}");
        }

        var result = await _plugin.RecallKnowledge(topic);

        // Discord embed descriptions reject anything over 4096 chars.
        Assert.True(result.Length < 4096);
        Assert.Contains(KnowledgePlugin.TruncationNote, result);
    }

    [Fact]
    public async Task RecallKnowledge_SkipsLowConfidenceFacts()
    {
        var topic = $"stale-{Guid.NewGuid():N}";
        await _service.RememberFactAsync(topic, "fresh fact", confidence: 0.9);
        await _service.RememberFactAsync(topic, "stale fact", confidence: 0.2);

        var result = await _plugin.RecallKnowledge(topic);

        Assert.Contains("fresh fact", result);
        Assert.DoesNotContain("stale fact", result);
    }

    [Fact]
    public async Task RecallAsync_LimitMarksOnlyReturnedFactsAsUsed()
    {
        var topic = $"used-{Guid.NewGuid():N}";
        await _service.RememberFactAsync(topic, "high", confidence: 0.9);
        var low = await _service.RememberFactAsync(topic, "lower", confidence: 0.6);

        var results = await _service.RecallAsync(topic, limit: 1);

        Assert.Single(results);
        Assert.Equal("high", results[0].Fact);

        await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
        var untouched = await db.Knowledge.FindAsync(low.Id);
        Assert.Null(untouched!.LastUsed);
    }
}
