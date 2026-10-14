using System.Text.Json;
using HomelabBot.Data.Entities;
using HomelabBot.Plugins;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomelabBot.Tests;

public class RunbookStepResultTests : IClassFixture<DatabaseFixture>, IDisposable
{
    private readonly DatabaseFixture _fixture;

    public RunbookStepResultTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    public void Dispose()
    {
        using var db = _fixture.DbContextFactory.CreateDbContext();
        db.Runbooks.RemoveRange(db.Runbooks);
        db.SaveChanges();
    }

    [Fact]
    public async Task ExecuteRunbook_StepThatAnswersWithAnError_IsAFailedStepAndTheRunbookIsNotASuccess()
    {
        var kernel = new StepKernel();

        var result = await ExecuteAsync(kernel, "Works", "AnswersError", "Works");

        Assert.StartsWith("⚠️ **Runbook: test**", result);
        Assert.Contains("✅ Step 1: step Works", result);
        Assert.Contains($"❌ Step 2: step AnswersError — {StepKernel.ErrorAnswer}", result);
        Assert.DoesNotContain("✅ Step 2", result);

        // A runbook goes on after a failed step, as it does after a step that throws.
        Assert.Contains("✅ Step 3: step Works", result);
        Assert.Equal(["Works", "AnswersError", "Works"], kernel.Calls);
    }

    [Fact]
    public async Task ExecuteRunbook_StepThatThrows_ShowsOneErrorPrefix()
    {
        var result = await ExecuteAsync(new StepKernel(), "Throws");

        Assert.Contains("❌ Step 1: step Throws — Error: ", result);
        Assert.DoesNotContain("Error: Error:", result);
    }

    [Fact]
    public async Task ExecuteRunbook_EveryStepWorks_IsASuccess()
    {
        var result = await ExecuteAsync(new StepKernel(), "Works", "Works");

        Assert.StartsWith("✅ **Runbook: test**", result);
        Assert.DoesNotContain("❌", result);
    }

    // Stored before the marks: ReadOnly with a notification step must still wait for confirmation.
    [Fact]
    public async Task ExecuteRunbook_StoredReadOnlyWithAStateChangingStep_NeedsConfirmation()
    {
        var kernel = new StepKernel();

        var result = await ExecuteAsync(kernel, TrustLevel.ReadOnly, "Works", "SendNotification");

        Assert.Contains("requires manual confirmation", result);
        Assert.Empty(kernel.Calls);
    }

    [Fact]
    public async Task ExecuteRunbook_StoredRiskyWithOnlyReadSteps_StillNeedsConfirmation()
    {
        var kernel = new StepKernel();

        var result = await ExecuteAsync(kernel, TrustLevel.Risky, "Works");

        Assert.Contains("requires manual confirmation", result);
        Assert.Empty(kernel.Calls);
    }

    [Fact]
    public async Task ListRunbooks_ShowsTheLevelTheStepsCallFor()
    {
        await ExecuteAsync(new StepKernel(), TrustLevel.ReadOnly, "SendNotification");

        var plugin = new RunbookPlugin(_fixture.DbContextFactory, NullLogger<RunbookPlugin>.Instance);
        var list = await plugin.ListRunbooks();

        Assert.Contains("Trust: Risky", list);
        Assert.DoesNotContain("Trust: ReadOnly", list);
    }

    private Task<string> ExecuteAsync(StepKernel kernel, params string[] functions) =>
        ExecuteAsync(kernel, TrustLevel.ReadOnly, functions);

    private async Task<string> ExecuteAsync(StepKernel kernel, TrustLevel trustLevel, params string[] functions)
    {
        var runbook = new Runbook
        {
            Name = "test",
            TriggerCondition = "alertname=Test",
            StepsJson = JsonSerializer.Serialize(StepKernel.Steps(functions)),
            TrustLevel = trustLevel,
        };

        await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
        {
            db.Runbooks.Add(runbook);
            await db.SaveChangesAsync();
        }

        var plugin = new RunbookPlugin(_fixture.DbContextFactory, NullLogger<RunbookPlugin>.Instance);
        return await plugin.ExecuteRunbook(runbook.Id, kernel.Kernel);
    }
}
