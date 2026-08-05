using HomelabBot.Data.Entities;
using HomelabBot.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomelabBot.Tests;

public class ConversationSearchTests : IClassFixture<DatabaseFixture>
{
    private readonly DatabaseFixture _fixture;
    private readonly ConversationService _service;

    public ConversationSearchTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
        _service = new ConversationService(
            _fixture.DbContextFactory,
            NullLogger<ConversationService>.Instance);
    }

    private async Task SeedAsync(params (ulong ThreadId, string Content, DateTime LastMessageAt)[] rows)
    {
        await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();

        foreach (var (threadId, content, lastMessageAt) in rows)
        {
            db.Conversations.Add(new Conversation
            {
                ThreadId = threadId,
                LastMessageAt = lastMessageAt,
                Messages = [new ConversationMessage { Role = "user", Content = content, Timestamp = lastMessageAt }],
            });
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Search_FindsOldConversationBuriedUnderNewerOnes()
    {
        var marker = $"zfsdegraded{Random.Shared.NextInt64()}";
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await SeedAsync((ThreadId: (ulong)Random.Shared.NextInt64(), Content: $"pool {marker} on tank", LastMessageAt: old));

        // 150 newer conversations would have pushed the match out of the old 100-row window.
        var noise = Enumerable.Range(0, 150)
            .Select(i => ((ulong)Random.Shared.NextInt64(), $"routine alert {i}", old.AddDays(i + 1)))
            .ToArray();
        await SeedAsync(noise);

        var results = await _service.SearchConversationsAsync(marker);

        var hit = Assert.Single(results);
        Assert.Contains(marker, hit.RelevantMessages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_TreatsPercentAsLiteralText()
    {
        var stamp = Random.Shared.NextInt64();
        var now = new DateTime(2021, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        await SeedAsync(
            ((ulong)Random.Shared.NextInt64(), $"disk at 95%full{stamp}", now),
            // An unescaped %95%full…% would match this one too, through the wildcard.
            ((ulong)Random.Shared.NextInt64(), $"disk at 95 percent, nearly full{stamp}", now));

        var results = await SearchAsync($"95%full{stamp}");

        Assert.Single(results);
    }

    [Fact]
    public async Task Search_TreatsUnderscoreAsLiteralText()
    {
        var stamp = Random.Shared.NextInt64();
        var now = new DateTime(2021, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        await SeedAsync(
            ((ulong)Random.Shared.NextInt64(), $"service node_exporter{stamp} down", now),
            // Unescaped, the underscore is a single-character wildcard and matches this too.
            ((ulong)Random.Shared.NextInt64(), $"service nodeXexporter{stamp} down", now));

        var results = await SearchAsync($"node_exporter{stamp}");

        Assert.Single(results);
    }

    [Fact]
    public async Task Search_MatchesLiteralBackslash()
    {
        var stamp = Random.Shared.NextInt64();
        var now = new DateTime(2021, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        // Escaping backslash out of order turns this into an escape character and breaks the match.
        await SeedAsync(((ulong)Random.Shared.NextInt64(), $@"path C:\logs\app{stamp}.txt", now));

        var results = await SearchAsync($@"C:\logs\app{stamp}.txt");

        Assert.Single(results);
    }

    [Fact]
    public async Task Search_CommonKeyword_DoesNotCrowdOutTheSpecificMatch()
    {
        var marker = $"zfsdegraded{Random.Shared.NextInt64()}";
        var old = new DateTime(2019, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        await SeedAsync(((ulong)Random.Shared.NextInt64(), $"pool {marker} on tank", old));

        // 250 newer conversations all matching the common keyword "on".
        var noise = Enumerable.Range(0, 250)
            .Select(i => ((ulong)Random.Shared.NextInt64(), $"restart on container {i}", old.AddDays(i + 1)))
            .ToArray();
        await SeedAsync(noise);

        var results = await SearchAsync($"{marker} on");

        // The two-keyword match must outrank 250 newer one-keyword matches.
        Assert.Equal(2, results[0].Score);
        Assert.Contains(marker, results[0].RelevantMessages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_UnknownKeyword_ReturnsNothing()
    {
        var results = await SearchAsync($"nosuchterm{Random.Shared.NextInt64()}");

        Assert.Empty(results);
    }

    private Task<List<Models.ConversationSearchResult>> SearchAsync(string query) =>
        _service.SearchConversationsAsync(query);
}
