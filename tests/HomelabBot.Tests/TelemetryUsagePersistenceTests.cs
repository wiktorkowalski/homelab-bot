using HomelabBot.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomelabBot.Tests;

public class TelemetryUsagePersistenceTests : IClassFixture<DatabaseFixture>
{
    private readonly DatabaseFixture _fixture;
    private readonly TelemetryService _telemetry;

    public TelemetryUsagePersistenceTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
        _telemetry = new TelemetryService(fixture.DbContextFactory, NullLogger<TelemetryService>.Instance);
    }

    [Fact]
    public async Task LogInteractionErrorAsync_StoresUsageOfRoundsBeforeFailure()
    {
        var id = await StartAsync();
        var usage = new LlmTokenUsage
        {
            PromptTokens = 3000,
            CompletionTokens = 40,
            CachedPromptTokens = 2000,
            CacheWriteTokens = 900,
            Rounds = 3,
        };

        await _telemetry.LogInteractionErrorAsync(id, "boom", 10, usage);

        var row = await LoadAsync(id);
        Assert.False(row.Success);
        Assert.Equal(3000, row.PromptTokens);
        Assert.Equal(40, row.CompletionTokens);
        Assert.Equal(2000, row.CachedPromptTokens);
        Assert.Equal(900, row.CacheWriteTokens);
        Assert.Equal(3, row.LlmRounds);
    }

    [Fact]
    public async Task LogInteractionErrorAsync_NoRounds_LeavesUsageNull()
    {
        var id = await StartAsync();

        await _telemetry.LogInteractionErrorAsync(id, "boom", 10, LlmTokenUsage.None);

        var row = await LoadAsync(id);
        Assert.Null(row.PromptTokens);
        Assert.Null(row.LlmRounds);
    }

    [Fact]
    public async Task LogInteractionCompleteAsync_FallbackUsage_StoresZeroRounds()
    {
        var id = await StartAsync();

        await _telemetry.LogInteractionCompleteAsync(
            id, "ok", new LlmTokenUsage { PromptTokens = 100, CompletionTokens = 5 }, 10);

        var row = await LoadAsync(id);
        Assert.True(row.Success);
        Assert.Equal(100, row.PromptTokens);
        Assert.Equal(0, row.LlmRounds);
        Assert.Null(row.CachedPromptTokens);
    }

    [Fact]
    public async Task TryLogInteractionCancelledAsync_StoresUsage()
    {
        var id = await StartAsync();

        await _telemetry.TryLogInteractionCancelledAsync(
            id, 10, new LlmTokenUsage { PromptTokens = 500, CompletionTokens = 7, Rounds = 1 });

        var row = await LoadAsync(id);
        Assert.False(row.Success);
        Assert.Equal("Cancelled", row.ErrorMessage);
        Assert.Equal(500, row.PromptTokens);
        Assert.Equal(1, row.LlmRounds);
    }

    [Fact]
    public async Task TryLogInteractionCancelledAsync_MissingInteraction_DoesNotThrow()
    {
        await _telemetry.TryLogInteractionCancelledAsync(int.MaxValue, 10, LlmTokenUsage.None);
    }

    private async Task<int> StartAsync() =>
        (await _telemetry.LogInteractionStartAsync(1, "anthropic/claude-sonnet-5.5", "hi", null)).Id;

    private async Task<HomelabBot.Data.Entities.LlmInteraction> LoadAsync(int id)
    {
        await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
        return (await db.LlmInteractions.FindAsync(id))!;
    }
}
