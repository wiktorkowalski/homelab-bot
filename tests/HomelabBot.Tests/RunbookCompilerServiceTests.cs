using HomelabBot.Data.Entities;
using HomelabBot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomelabBot.Tests;

public class RunbookCompilerServiceTests : IClassFixture<DatabaseFixture>, IDisposable
{
    private readonly DatabaseFixture _fixture;
    private readonly RunbookCompilerService _service;

    public RunbookCompilerServiceTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
        _service = new RunbookCompilerService(
            _fixture.DbContextFactory,
            NullLogger<RunbookCompilerService>.Instance);
    }

    public void Dispose()
    {
        using var db = _fixture.DbContextFactory.CreateDbContext();
        db.Runbooks.RemoveRange(db.Runbooks);
        db.Investigations.RemoveRange(db.Investigations);
        db.RemediationActions.RemoveRange(db.RemediationActions);
        db.Patterns.RemoveRange(db.Patterns);
        db.SaveChanges();
    }

    [Fact]
    public async Task CompileFromRemediation_SuccessfulAction_CreatesRunbook()
    {
        var action = new RemediationAction
        {
            ContainerName = "nginx",
            ActionType = "restart",
            Trigger = "pattern",
            BeforeState = "running",
            AfterState = "running",
            Success = true
        };

        var pattern = new Pattern
        {
            Symptom = "nginx high cpu",
            CommonCause = "traffic spike",
            Resolution = "restart container"
        };

        var result = await _service.CompileFromRemediationAsync(action, pattern);

        Assert.NotNull(result);
        Assert.Contains("nginx high cpu", result.Name);
        Assert.Equal(RunbookSourceType.AutoCompiled, result.SourceType);
        Assert.Equal(TrustLevel.Risky, result.TrustLevel);
    }

    [Fact]
    public async Task CompileFromRemediation_FailedAction_ReturnsNull()
    {
        var action = new RemediationAction
        {
            ContainerName = "nginx",
            ActionType = "restart",
            Trigger = "pattern",
            BeforeState = "running",
            Success = false
        };

        var result = await _service.CompileFromRemediationAsync(action, null);

        Assert.Null(result);
    }

    [Fact]
    public async Task CompileFromInvestigation_WithSteps_CreatesRunbook()
    {
        var investigation = new Investigation
        {
            Trigger = "plex container down",
            Resolution = "restarted plex and cleared cache",
            Steps =
            [
                new InvestigationStep
                {
                    Action = "ListContainers",
                    Plugin = "Docker",
                    ResultSummary = "Found plex exited",
                    Timestamp = DateTime.UtcNow.AddMinutes(-5)
                },
                new InvestigationStep
                {
                    Action = "RestartContainer",
                    Plugin = "Docker",
                    ResultSummary = "Container restarted",
                    Timestamp = DateTime.UtcNow.AddMinutes(-4)
                }
            ]
        };

        var result = await _service.CompileFromInvestigationAsync(investigation);

        Assert.NotNull(result);
        Assert.Contains("plex container down", result.Name);
        Assert.Equal(RunbookSourceType.AutoCompiled, result.SourceType);
    }

    [Fact]
    public async Task CompileFromInvestigation_NoSteps_ReturnsNull()
    {
        var investigation = new Investigation
        {
            Trigger = "test issue",
            Resolution = "fixed it",
            Steps = []
        };

        var result = await _service.CompileFromInvestigationAsync(investigation);

        Assert.Null(result);
    }

    [Fact]
    public async Task CompileFromInvestigation_NoResolution_ReturnsNull()
    {
        var investigation = new Investigation
        {
            Trigger = "test issue",
            Steps =
            [
                new InvestigationStep
                {
                    Action = "ListContainers",
                    Plugin = "Docker",
                    Timestamp = DateTime.UtcNow
                }
            ]
        };

        var result = await _service.CompileFromInvestigationAsync(investigation);

        Assert.Null(result);
    }

    [Fact]
    public async Task Compile_SimilarTrigger_VersionsExistingRunbook()
    {
        // Create an existing runbook
        using (var db = _fixture.DbContextFactory.CreateDbContext())
        {
            db.Runbooks.Add(new Runbook
            {
                Name = "Auto: nginx restart",
                TriggerCondition = "nginx container down",
                StepsJson = "[]",
                TrustLevel = TrustLevel.ReadOnly,
                Version = 1
            });
            await db.SaveChangesAsync();
        }

        var action = new RemediationAction
        {
            ContainerName = "nginx",
            ActionType = "restart",
            Trigger = "pattern",
            BeforeState = "exited",
            AfterState = "running",
            Success = true
        };

        var pattern = new Pattern
        {
            Symptom = "nginx container down restart",
            CommonCause = "OOM",
            Resolution = "restart"
        };

        var result = await _service.CompileFromRemediationAsync(action, pattern);

        Assert.NotNull(result);
        Assert.Equal(2, result.Version);
        Assert.NotNull(result.ParentRunbookId);

        // Old runbook should be disabled
        using (var db = _fixture.DbContextFactory.CreateDbContext())
        {
            var old = await db.Runbooks.FirstAsync(r => r.Version == 1);
            Assert.False(old.Enabled);
        }
    }

    [Fact]
    public async Task CompileFromInvestigation_StepsWithoutPlugin_Skipped()
    {
        var investigation = new Investigation
        {
            Trigger = "dns resolution failure",
            Resolution = "restarted dns",
            Steps =
            [
                new InvestigationStep
                {
                    Action = "checked logs manually",
                    Plugin = null,
                    Timestamp = DateTime.UtcNow.AddMinutes(-5)
                },
                new InvestigationStep
                {
                    Action = "GetContainerStatus",
                    Plugin = "Docker",
                    Timestamp = DateTime.UtcNow.AddMinutes(-4)
                }
            ]
        };

        var result = await _service.CompileFromInvestigationAsync(investigation);

        Assert.NotNull(result);
        Assert.Contains("\"Order\":1", result.StepsJson);
        Assert.DoesNotContain("\"Order\":2", result.StepsJson);
    }
}
