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

    // --- SetConfidenceAsync tests ---

    [Fact]
    public async Task SetConfidence_UpdatesExistingFact()
    {
        // Arrange
        var topic = $"confidence-set-{Guid.NewGuid()}";
        var fact = await _service.RememberFactAsync(topic, "some fact", confidence: 0.9);

        // Act
        await _service.SetConfidenceAsync(topic, "some fact", 0.5);

        // Assert
        using var db = _fixture.DbContextFactory.CreateDbContext();
        var updated = await db.Knowledge.FindAsync(fact.Id);
        Assert.Equal(0.5, updated!.Confidence);
    }

    [Fact]
    public async Task SetConfidence_ClampsToValidRange()
    {
        // Arrange
        var topic = $"confidence-clamp-{Guid.NewGuid()}";
        await _service.RememberFactAsync(topic, "high", confidence: 0.5);
        await _service.RememberFactAsync(topic, "low", confidence: 0.5);

        // Act
        await _service.SetConfidenceAsync(topic, "high", 1.5);
        await _service.SetConfidenceAsync(topic, "low", -0.5);

        // Assert
        using var db = _fixture.DbContextFactory.CreateDbContext();
        var high = db.Knowledge.First(k => k.Topic == topic && k.Fact == "high");
        var low = db.Knowledge.First(k => k.Topic == topic && k.Fact == "low");
        Assert.Equal(1.0, high.Confidence);
        Assert.Equal(0.0, low.Confidence);
    }

    [Fact]
    public async Task SetConfidence_DoesNothingForMissingFact()
    {
        // Act & Assert - should not throw
        await _service.SetConfidenceAsync("nonexistent-topic", "nonexistent fact", 0.5);
    }

    [Fact]
    public async Task SetConfidence_DoesNotUpdateInvalidatedFact()
    {
        // Arrange
        var topic = $"confidence-invalid-{Guid.NewGuid()}";
        var fact = await _service.RememberFactAsync(topic, "will invalidate", confidence: 0.8);
        await _service.InvalidateAsync(topic, "will invalidate");

        // Act
        await _service.SetConfidenceAsync(topic, "will invalidate", 0.5);

        // Assert - confidence should remain at 0.8 (fact is IsValid=false, not found by query)
        using var db = _fixture.DbContextFactory.CreateDbContext();
        var result = await db.Knowledge.FindAsync(fact.Id);
        Assert.False(result!.IsValid);
        Assert.Equal(0.8, result.Confidence);
    }

    [Fact]
    public async Task SetConfidence_DoesNotUpdateLastVerified()
    {
        // Arrange
        var topic = $"confidence-noverify-{Guid.NewGuid()}";
        var fact = await _service.RememberFactAsync(topic, "stable fact", confidence: 0.9);
        var originalVerified = fact.LastVerified;

        // Small delay to ensure time difference
        await Task.Delay(50);

        // Act
        await _service.SetConfidenceAsync(topic, "stable fact", 0.6);

        // Assert
        using var db = _fixture.DbContextFactory.CreateDbContext();
        var updated = await db.Knowledge.FindAsync(fact.Id);
        Assert.Equal(originalVerified, updated!.LastVerified);
    }

    // --- RecallByTopicPrefixAsync tests ---

    [Fact]
    public async Task RecallByTopicPrefix_ReturnsMatchingFacts()
    {
        // Arrange
        var prefix = $"prefix-{Guid.NewGuid()}";
        await _service.RememberFactAsync($"{prefix}:alpha", "fact alpha");
        await _service.RememberFactAsync($"{prefix}:beta", "fact beta");
        await _service.RememberFactAsync("other:gamma", "fact gamma");

        // Act
        var results = await _service.RecallByTopicPrefixAsync($"{prefix}:");

        // Assert
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.StartsWith(prefix, r.Topic));
    }

    [Fact]
    public async Task RecallByTopicPrefix_ExcludesInvalidatedFacts()
    {
        // Arrange
        var prefix = $"prefix-inv-{Guid.NewGuid()}";
        await _service.RememberFactAsync($"{prefix}:one", "valid fact");
        await _service.RememberFactAsync($"{prefix}:two", "will invalidate");
        await _service.InvalidateAsync($"{prefix}:two", "will invalidate");

        // Act
        var results = await _service.RecallByTopicPrefixAsync($"{prefix}:");

        // Assert
        Assert.Single(results);
        Assert.Equal("valid fact", results[0].Fact);
    }

    [Fact]
    public async Task RecallByTopicPrefix_ReturnsEmptyForNonMatchingPrefix()
    {
        // Act
        var results = await _service.RecallByTopicPrefixAsync("zzz-nonexistent:");

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public async Task RecallByTopicPrefix_OrdersByConfidenceDescending()
    {
        // Arrange
        var prefix = $"prefix-order-{Guid.NewGuid()}";
        await _service.RememberFactAsync($"{prefix}:low", "low conf", confidence: 0.3);
        await _service.RememberFactAsync($"{prefix}:high", "high conf", confidence: 0.95);
        await _service.RememberFactAsync($"{prefix}:mid", "mid conf", confidence: 0.6);

        // Act
        var results = await _service.RecallByTopicPrefixAsync($"{prefix}:");

        // Assert
        Assert.Equal(3, results.Count);
        Assert.Equal("high conf", results[0].Fact);
        Assert.Equal("mid conf", results[1].Fact);
        Assert.Equal("low conf", results[2].Fact);
    }

    [Fact]
    public async Task RecallByTopicPrefix_DoesNotUpdateLastUsed()
    {
        // Arrange
        var prefix = $"prefix-nouse-{Guid.NewGuid()}";
        var fact = await _service.RememberFactAsync($"{prefix}:item", "no-touch fact");
        Assert.Null(fact.LastUsed);

        // Act
        var results = await _service.RecallByTopicPrefixAsync($"{prefix}:");

        // Assert
        using var db = _fixture.DbContextFactory.CreateDbContext();
        var loaded = await db.Knowledge.FindAsync(results[0].Id);
        Assert.Null(loaded!.LastUsed);
    }
}
