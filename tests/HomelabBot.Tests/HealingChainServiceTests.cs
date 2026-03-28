using HomelabBot.Configuration;
using HomelabBot.Data.Entities;
using HomelabBot.Models;
using HomelabBot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HomelabBot.Tests;

public class HealingChainServiceTests : IClassFixture<DatabaseFixture>, IDisposable
{
    private readonly DatabaseFixture _fixture;

    public HealingChainServiceTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    public void Dispose()
    {
        using var db = _fixture.DbContextFactory.CreateDbContext();
        db.HealingChains.RemoveRange(db.HealingChains);
        db.SaveChanges();
    }

    [Fact]
    public void ParseStepsFromResponse_ValidJson_ReturnsSteps()
    {
        var json = """
            [
                {"Order":1,"Description":"Check status","PluginName":"Docker","FunctionName":"GetContainerStatus","Parameters":{"containerName":"nginx"}},
                {"Order":2,"Description":"Restart","PluginName":"Docker","FunctionName":"RestartContainer","Parameters":{"containerName":"nginx"}}
            ]
            """;

        var result = InvokeParseSteps(json);

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("GetContainerStatus", result[0].FunctionName);
        Assert.Equal("RestartContainer", result[1].FunctionName);
    }

    [Fact]
    public void ParseStepsFromResponse_MarkdownFenced_ReturnsSteps()
    {
        var json = """
            ```json
            [{"Order":1,"Description":"Check","PluginName":"Docker","FunctionName":"ListContainers","Parameters":{}}]
            ```
            """;

        var result = InvokeParseSteps(json);

        Assert.NotNull(result);
        Assert.Single(result);
    }

    [Fact]
    public void ParseStepsFromResponse_InvalidJson_ReturnsNull()
    {
        var result = InvokeParseSteps("not json at all");

        Assert.Null(result);
    }

    [Fact]
    public void ParseStepsFromResponse_EmptyArray_ReturnsEmpty()
    {
        var result = InvokeParseSteps("[]");

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseStepsFromResponse_JsonWithSurroundingText_ExtractsArray()
    {
        var json = """
            Here's the plan:
            [{"Order":1,"Description":"Check","PluginName":"Docker","FunctionName":"ListContainers","Parameters":{}}]
            Hope this helps!
            """;

        var result = InvokeParseSteps(json);

        Assert.NotNull(result);
        Assert.Single(result);
    }

    [Fact]
    public async Task PlanAndExecute_WhenDisabled_ReturnsNull()
    {
        var service = CreateService(new HealingChainConfiguration { Enabled = false });

        var result = await service.PlanAndExecuteAsync("test", null, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public void HealingChainEntity_DefaultValues()
    {
        var chain = new HealingChain
        {
            Trigger = "test",
            StepsJson = "[]"
        };

        Assert.Equal(HealingChainStatus.Planned, chain.Status);
        Assert.Equal("[]", chain.ExecutionLogJson);
        Assert.False(chain.RequiredConfirmation);
        Assert.Null(chain.CompletedAt);
        Assert.Null(chain.GeneratedRunbookId);
    }

    [Fact]
    public async Task HealingChain_PersistsToDb()
    {
        await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();

        db.HealingChains.Add(new HealingChain
        {
            Trigger = "nginx down",
            StepsJson = "[{\"Order\":1}]",
            Status = HealingChainStatus.Completed,
            CompletedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var saved = await db.HealingChains.FirstAsync();
        Assert.Equal("nginx down", saved.Trigger);
        Assert.Equal(HealingChainStatus.Completed, saved.Status);
    }

    private static List<RunbookStep>? InvokeParseSteps(string response) =>
        HealingChainService.ParseStepsFromResponse(response);

    private HealingChainService CreateService(HealingChainConfiguration? config = null)
    {
        config ??= new HealingChainConfiguration();
        var runbookCompiler = new RunbookCompilerService(
            _fixture.DbContextFactory,
            NullLogger<RunbookCompilerService>.Instance);
        var similarityService = new IncidentSimilarityService(
            _fixture.DbContextFactory,
            NullLogger<IncidentSimilarityService>.Instance);

        // KernelService and RunbookPlugin can't be easily created without full config,
        // so tests that need them are integration tests
        return new HealingChainService(
            _fixture.DbContextFactory,
            null!, // KernelService - not needed for disabled/entity tests
            runbookCompiler,
            similarityService,
            Options.Create(config),
            NullLogger<HealingChainService>.Instance);
    }
}
