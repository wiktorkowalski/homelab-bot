using HomelabBot.Configuration;
using HomelabBot.Data.Entities;
using HomelabBot.Models;
using HomelabBot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task ExecuteChain_StepThatAnswersWithAnError_StopsTheChainAndFailsIt()
    {
        var kernel = new StepKernel();
        var chain = new HealingChain { Trigger = "plex down", StepsJson = "[]" };

        var result = await CreateService().ExecuteChainAsync(
            chain, StepKernel.Steps("Works", "AnswersError", "Works"), kernel.Kernel, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, result.StepsExecuted);
        Assert.Equal("Healing chain failed at step 2/3.", result.Message);
        Assert.Null(result.GeneratedRunbookId);
        Assert.Equal(["Works", "AnswersError"], kernel.Calls);
    }

    [Theory]
    [InlineData("AnswersError")]
    [InlineData("Throws")]
    public async Task ExecuteChain_FailedStepAfterAGoodOne_StoresTheChainAsFailed(string failingFunction)
    {
        var chain = new HealingChain { Trigger = "plex down", StepsJson = "[]", Status = HealingChainStatus.Executing };
        await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
        {
            db.HealingChains.Add(chain);
            await db.SaveChangesAsync();
        }

        await CreateService().ExecuteChainAsync(
            chain, StepKernel.Steps("Works", failingFunction), new StepKernel().Kernel, CancellationToken.None);

        await using var read = await _fixture.DbContextFactory.CreateDbContextAsync();
        Assert.Equal(HealingChainStatus.Failed, (await read.HealingChains.SingleAsync(c => c.Id == chain.Id)).Status);
    }

    [Fact]
    public async Task PlanAndExecute_AsksForThePlanWithoutTools_AndRunsIt()
    {
        var handler = new ScriptedClaudeHandler();
        using var harness = new KernelServiceHarness(handler.CreateClient());
        handler.Enqueue(ClaudeResponses.Text(
            """[{"Order":1,"Description":"ping","PluginName":"Test","FunctionName":"Ping","Parameters":{}}]"""));
        var service = CreatePlanningService(harness, NullLogger<HealingChainService>.Instance);

        var result = await service.PlanAndExecuteAsync("plex down", "plex", CancellationToken.None, similarIncidents: []);

        var request = Assert.Single(handler.Requests);
        Assert.Null(request["tools"]);
        Assert.Null(request["tool_choice"]);
        Assert.True(result!.Success);
        Assert.Equal(1, harness.Plugin.Calls);
    }

    [Fact]
    public async Task PlanAndExecute_AnswerIsNotAStepList_LogsTheAnswerHead()
    {
        var handler = new ScriptedClaudeHandler();
        using var harness = new KernelServiceHarness(handler.CreateClient());
        handler.Enqueue(ClaudeResponses.Text("no plan, sorry"));
        var logger = new RecordingLogger<HealingChainService>();
        var service = CreatePlanningService(harness, logger);

        var result = await service.PlanAndExecuteAsync("plex down", "plex", CancellationToken.None, similarIncidents: []);

        Assert.False(result!.Success);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("no plan, sorry", StringComparison.Ordinal));
    }

    private static HealingChainService CreatePlanningService(KernelServiceHarness harness, ILogger<HealingChainService> logger) =>
        new(
            harness.DbFactory,
            harness.Service,
            new RunbookCompilerService(harness.DbFactory, NullLogger<RunbookCompilerService>.Instance),
            new IncidentSimilarityService(harness.DbFactory, NullLogger<IncidentSimilarityService>.Instance),
            Options.Create(new HealingChainConfiguration()),
            logger);

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
