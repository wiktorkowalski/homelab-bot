using System.Collections.Concurrent;
using HomelabBot.Data;
using HomelabBot.Data.Entities;
using HomelabBot.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    public async Task Search_SingleKeywordBeyondTheCandidateCap_ReturnsTheNewestMatches()
    {
        var keyword = $"dockerish{Random.Shared.NextInt64()}";
        var start = new DateTime(2018, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        // Every row scores 1, so only the tie-break decides which 200 reach the scorer.
        var rows = Enumerable.Range(0, 260)
            .Select(i => ((ulong)Random.Shared.NextInt64(), $"{keyword} event {i}", start.AddDays(i)))
            .ToArray();
        await SeedAsync(rows);

        var results = await SearchAsync(keyword);

        Assert.Equal(start.AddDays(259), results[0].Date);
    }

    [Fact]
    public async Task Search_UnknownKeyword_ReturnsNothing()
    {
        var results = await SearchAsync($"nosuchterm{Random.Shared.NextInt64()}");

        Assert.Empty(results);
    }

    [Fact]
    public async Task Search_LongConversation_ReturnsOnlyTheMatchingMessagesInTimeOrder()
    {
        var marker = $"oomkill{Random.Shared.NextInt64()}";
        var start = new DateTime(2022, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        await SeedLongConversationAsync(
            _fixture.DbContextFactory, (ulong)Random.Shared.NextInt64(), start, 300, i => i is 40 or 150 or 290 ? $"container {marker} at step {i}" : $"routine chat {i}");

        var results = await SearchAsync(marker);

        var hit = Assert.Single(results);
        Assert.Equal(1, hit.Score);
        Assert.Equal(
            [$"container {marker} at step 40", $"container {marker} at step 150", $"container {marker} at step 290"],
            hit.RelevantMessages.Select(m => m.Content));
    }

    [Fact]
    public async Task Search_LongConversation_CapsRelevantMessagesAtTheFiveOldestMatches()
    {
        var marker = $"diskfull{Random.Shared.NextInt64()}";
        var start = new DateTime(2022, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        await SeedLongConversationAsync(
            _fixture.DbContextFactory, (ulong)Random.Shared.NextInt64(), start, 200, i => i % 20 == 0 ? $"{marker} #{i}" : $"noise {i}");

        var results = await SearchAsync(marker);

        var hit = Assert.Single(results);
        Assert.Equal(
            [$"{marker} #0", $"{marker} #20", $"{marker} #40", $"{marker} #60", $"{marker} #80"],
            hit.RelevantMessages.Select(m => m.Content));
    }

    [Fact]
    public async Task Search_TwoKeywords_ReturnsMessagesMatchingEitherKeyword()
    {
        var first = $"alpha{Random.Shared.NextInt64()}";
        var second = $"beta{Random.Shared.NextInt64()}";
        var start = new DateTime(2022, 4, 1, 0, 0, 0, DateTimeKind.Utc);

        await SeedLongConversationAsync(
            _fixture.DbContextFactory, (ulong)Random.Shared.NextInt64(), start, 50, i => i switch
            {
                10 => $"only {first} here",
                20 => $"only {second} here",
                _ => $"noise {i}",
            });

        var results = await SearchAsync($"{first} {second}");

        var hit = Assert.Single(results);
        Assert.Equal(2, hit.Score);
        Assert.Equal([$"only {first} here", $"only {second} here"], hit.RelevantMessages.Select(m => m.Content));
    }

    [Fact]
    public async Task Search_MatchesCaseInsensitively()
    {
        var stamp = Random.Shared.NextInt64();
        var now = new DateTime(2022, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        await SeedAsync(((ulong)Random.Shared.NextInt64(), $"Traefik{stamp} returned 502", now));

        var results = await SearchAsync($"TRAEFIK{stamp}");

        var hit = Assert.Single(results);
        Assert.Equal($"Traefik{stamp} returned 502", Assert.Single(hit.RelevantMessages).Content);
    }

    [Fact]
    public async Task FormatResults_KeepsTheTitleAndRolePreviewLayout()
    {
        var marker = $"certexpiry{Random.Shared.NextInt64()}";
        var start = new DateTime(2022, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var longText = $"{marker} {new string('x', 200)}";

        await SeedLongConversationAsync(
            _fixture.DbContextFactory, (ulong)Random.Shared.NextInt64(), start, 30, i => i == 5 ? longText : $"noise {i}");

        var results = await SearchAsync(marker);
        var hit = Assert.Single(results);

        var expected =
            "Found 1 relevant conversation(s):\n" + Environment.NewLine
            + $"**[{hit.TimeAgo}] Untitled**" + Environment.NewLine
            + $"  user: {longText[..147]}..." + Environment.NewLine
            + Environment.NewLine;
        Assert.Equal(expected, Models.ConversationSearchResult.FormatResults(results));
    }

    [Fact]
    public async Task Search_NeverReadsMessagesWithoutAMatchFilter()
    {
        // A long conversation (the owner's permanent DM thread) must not be pulled into memory
        // whole: every query that touches the message table has to filter by the search text.
        var commands = new ConcurrentQueue<string>();
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<HomelabDbContext>()
            .UseSqlite(connection)
            .LogTo(commands.Enqueue, [RelationalEventId.CommandExecuted])
            .Options;
        await using (var context = new HomelabDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();
        }

        var factory = new DatabaseFixture.TestDbContextFactory(options);
        var marker = $"kernelpanic{Random.Shared.NextInt64()}";
        await SeedLongConversationAsync(
            factory, 42, new DateTime(2022, 7, 1, 0, 0, 0, DateTimeKind.Utc), 100, i => i == 50 ? marker : $"noise {i}");
        commands.Clear();

        var service = new ConversationService(factory, NullLogger<ConversationService>.Instance);
        var results = await service.SearchConversationsAsync(marker);

        Assert.Single(results);
        var messageReads = commands.Where(c => c.Contains("\"ConversationMessages\"", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(messageReads);
        Assert.All(messageReads, sql => Assert.Contains("LIKE", sql, StringComparison.Ordinal));
    }

    private static async Task SeedLongConversationAsync(
        IDbContextFactory<HomelabDbContext> factory, ulong threadId, DateTime start, int count, Func<int, string> content)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.Conversations.Add(new Conversation
        {
            ThreadId = threadId,
            LastMessageAt = start.AddMinutes(count - 1),
            Messages = [.. Enumerable.Range(0, count).Select(i => new ConversationMessage
            {
                Role = "user",
                Content = content(i),
                Timestamp = start.AddMinutes(i),
            })],
        });
        await db.SaveChangesAsync();
    }

    private Task<List<Models.ConversationSearchResult>> SearchAsync(string query) =>
        _service.SearchConversationsAsync(query);
}
