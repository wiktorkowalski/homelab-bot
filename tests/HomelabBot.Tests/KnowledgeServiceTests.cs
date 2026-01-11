using HomelabBot.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomelabBot.Tests;

public class KnowledgeServiceTests : IClassFixture<DatabaseFixture>
{
    private readonly DatabaseFixture _fixture;
    private readonly KnowledgeService _service;

    public KnowledgeServiceTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
        _service = new KnowledgeService(
            _fixture.DbContextFactory,
            NullLogger<KnowledgeService>.Instance);
    }

    [Fact]
    public async Task RememberFact_CreatesNewKnowledge()
    {
        // Arrange
        var topic = "test";
        var fact = $"test fact {Guid.NewGuid()}";

        // Act
        var result = await _service.RememberFactAsync(topic, fact);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(topic, result.Topic);
        Assert.Equal(fact, result.Fact);
        Assert.True(result.IsValid);
        Assert.Equal(0.8, result.Confidence);
    }

    [Fact]
    public async Task RememberFact_UpdatesExistingKnowledge()
    {
        // Arrange
        var topic = "duplicate-test";
        var fact = $"same fact {Guid.NewGuid()}";

        // Act
        var first = await _service.RememberFactAsync(topic, fact, confidence: 0.5);
        var second = await _service.RememberFactAsync(topic, fact, confidence: 0.9);

        // Assert
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(0.9, second.Confidence); // Should take higher confidence
    }

    [Fact]
    public async Task RecallAsync_ReturnsFactsByTopic()
    {
        // Arrange
        var topic = $"recall-test-{Guid.NewGuid()}";
        await _service.RememberFactAsync(topic, "fact 1");
        await _service.RememberFactAsync(topic, "fact 2");
        await _service.RememberFactAsync("other-topic", "other fact");

        // Act
        var results = await _service.RecallAsync(topic);

        // Assert
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(topic, r.Topic));
    }

    [Fact]
    public async Task RecallAsync_ReturnsAllWhenNoTopic()
    {
        // Arrange - facts already exist from other tests

        // Act
        var results = await _service.RecallAsync();

        // Assert
        Assert.NotEmpty(results);
    }

    [Fact]
    public async Task RecallAsync_ExcludesLowConfidenceFacts()
    {
        // Arrange
        var topic = $"confidence-test-{Guid.NewGuid()}";
        var lowConfFact = await _service.RememberFactAsync(topic, "low confidence", confidence: 0.2);

        // Manually set confidence below threshold
        using (var db = _fixture.DbContextFactory.CreateDbContext())
        {
            var fact = await db.Knowledge.FindAsync(lowConfFact.Id);
            if (fact != null)
            {
                fact.Confidence = 0.2;
                await db.SaveChangesAsync();
            }
        }

        // Act
        var results = await _service.RecallAsync(topic, includeStale: false);

        // Assert
        Assert.DoesNotContain(results, r => r.Id == lowConfFact.Id);
    }

    [Fact]
    public async Task StoreAndResolveAlias_WorksCorrectly()
    {
        // Arrange
        var aliasName = $"my-device-{Guid.NewGuid()}";
        var macAddress = "AA:BB:CC:DD:EE:FF";
        await _service.RememberFactAsync(
            "alias:mac",
            $"\"{aliasName}\" → \"{macAddress}\"",
            source: "user_told",
            confidence: 1.0);

        // Act
        var resolved = await _service.ResolveAliasAsync("mac", aliasName);

        // Assert
        Assert.Equal(macAddress, resolved);
    }

    [Fact]
    public async Task ResolveAlias_ReturnsNullForUnknown()
    {
        // Act
        var resolved = await _service.ResolveAliasAsync("mac", "unknown-device-xyz");

        // Assert
        Assert.Null(resolved);
    }

    [Fact]
    public async Task LearnCorrection_InvalidatesOldAndCreatesNew()
    {
        // Arrange
        var topic = $"correction-test-{Guid.NewGuid()}";
        var oldFact = await _service.RememberFactAsync(topic, "old info");

        // Act
        await _service.LearnCorrectionAsync(topic, "old info", "new corrected info");

        // Assert
        using var db = _fixture.DbContextFactory.CreateDbContext();
        var old = await db.Knowledge.FindAsync(oldFact.Id);
        Assert.False(old?.IsValid);

        var newFact = db.Knowledge.FirstOrDefault(k =>
            k.Topic == topic && k.Fact == "new corrected info");
        Assert.NotNull(newFact);
        Assert.True(newFact.IsValid);
        Assert.Equal(1.0, newFact.Confidence);
        Assert.Equal("user_told", newFact.Source);
    }

    [Fact]
    public async Task InvalidateAsync_MarksFactsAsInvalid()
    {
        // Arrange
        var topic = $"invalidate-test-{Guid.NewGuid()}";
        var fact = await _service.RememberFactAsync(topic, "to be invalidated");

        // Act
        await _service.InvalidateAsync(topic, "invalidated");

        // Assert
        using var db = _fixture.DbContextFactory.CreateDbContext();
        var result = await db.Knowledge.FindAsync(fact.Id);
        Assert.False(result?.IsValid);
    }

    [Fact]
    public async Task GenerateKnowledgePrompt_ReturnsFormattedString()
    {
        // Arrange
        var topic = $"prompt-test-{Guid.NewGuid()}";
        await _service.RememberFactAsync(topic, "fact for prompt");

        // Act
        var prompt = await _service.GenerateKnowledgePromptAsync([topic]);

        // Assert
        Assert.Contains("Your Current Knowledge", prompt);
        Assert.Contains("fact for prompt", prompt);
    }

    [Fact]
    public async Task GenerateKnowledgePrompt_ReturnsEmptyWhenNoFacts()
    {
        // Act
        var prompt = await _service.GenerateKnowledgePromptAsync(["nonexistent-topic-xyz"]);

        // Assert
        Assert.Empty(prompt);
    }
}
