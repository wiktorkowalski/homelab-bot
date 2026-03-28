using HomelabBot.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomelabBot.Tests;

public class MemoryServiceTests : IClassFixture<DatabaseFixture>
{
    private readonly DatabaseFixture _fixture;
    private readonly MemoryService _service;

    public MemoryServiceTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
        var runbookCompiler = new RunbookCompilerService(
            _fixture.DbContextFactory,
            NullLogger<RunbookCompilerService>.Instance);
        _service = new MemoryService(
            _fixture.DbContextFactory,
            NullLogger<MemoryService>.Instance,
            runbookCompiler);
    }

    [Fact]
    public async Task StartInvestigation_CreatesNewInvestigation()
    {
        // Arrange
        var threadId = (ulong)Random.Shared.NextInt64();
        var trigger = "network slow";

        // Act
        var investigation = await _service.StartInvestigationAsync(threadId, trigger);

        // Assert
        Assert.NotNull(investigation);
        Assert.Equal(threadId, investigation.ThreadId);
        Assert.Equal(trigger, investigation.Trigger);
        Assert.False(investigation.Resolved);
    }

    [Fact]
    public async Task GetActiveInvestigation_ReturnsActiveForThread()
    {
        // Arrange
        var threadId = (ulong)Random.Shared.NextInt64();
        var created = await _service.StartInvestigationAsync(threadId, "test issue");

        // Act
        var active = await _service.GetActiveInvestigationAsync(threadId);

        // Assert
        Assert.NotNull(active);
        Assert.Equal(created.Id, active.Id);
    }

    [Fact]
    public async Task GetActiveInvestigation_ReturnsNullWhenNone()
    {
        // Arrange
        var threadId = (ulong)Random.Shared.NextInt64();

        // Act
        var active = await _service.GetActiveInvestigationAsync(threadId);

        // Assert
        Assert.Null(active);
    }

    [Fact]
    public async Task RecordStep_AddsStepToInvestigation()
    {
        // Arrange
        var threadId = (ulong)Random.Shared.NextInt64();
        var investigation = await _service.StartInvestigationAsync(threadId, "test");

        // Act
        await _service.RecordStepAsync(investigation.Id, "checked router", "MikroTik", "CPU at 45%");

        // Assert
        var updated = await _service.GetActiveInvestigationAsync(threadId);
        Assert.Single(updated!.Steps);
        Assert.Equal("checked router", updated.Steps[0].Action);
        Assert.Equal("MikroTik", updated.Steps[0].Plugin);
        Assert.Equal("CPU at 45%", updated.Steps[0].ResultSummary);
    }

    [Fact]
    public async Task ResolveInvestigation_MarksAsResolved()
    {
        // Arrange
        var threadId = (ulong)Random.Shared.NextInt64();
        var investigation = await _service.StartInvestigationAsync(threadId, "test issue");
        await _service.RecordStepAsync(investigation.Id, "checked something", null, "found problem");

        // Act
        var resolved = await _service.ResolveInvestigationAsync(investigation.Id, "fixed by restarting");

        // Assert
        Assert.NotNull(resolved);
        Assert.True(resolved.Resolved);
        Assert.Equal("fixed by restarting", resolved.Resolution);

        // Active investigation should now be null
        var active = await _service.GetActiveInvestigationAsync(threadId);
        Assert.Null(active);
    }

    [Fact]
    public async Task ResolveInvestigation_CreatesPattern()
    {
        // Arrange
        var threadId = (ulong)Random.Shared.NextInt64();
        var uniqueTrigger = $"unique issue {Guid.NewGuid()}";
        var investigation = await _service.StartInvestigationAsync(threadId, uniqueTrigger);
        await _service.RecordStepAsync(investigation.Id, "diagnosed", null, "found root cause");

        // Act
        await _service.ResolveInvestigationAsync(investigation.Id, "applied fix");

        // Assert
        using var db = _fixture.DbContextFactory.CreateDbContext();
        var pattern = db.Patterns.FirstOrDefault(p => p.Symptom == uniqueTrigger);
        Assert.NotNull(pattern);
        Assert.Equal("applied fix", pattern.Resolution);
    }

    [Fact]
    public async Task SearchPastIncidents_FindsSimilarIssues()
    {
        // Arrange
        var threadId1 = (ulong)Random.Shared.NextInt64();
        var threadId2 = (ulong)Random.Shared.NextInt64();

        var inv1 = await _service.StartInvestigationAsync(threadId1, "container crashed nginx");
        await _service.ResolveInvestigationAsync(inv1.Id, "restarted container");

        var inv2 = await _service.StartInvestigationAsync(threadId2, "container stopped nginx");
        await _service.ResolveInvestigationAsync(inv2.Id, "out of memory");

        // Act
        var results = await _service.SearchPastIncidentsAsync("nginx container");

        // Assert
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Trigger.Contains("nginx"));
    }

    [Fact]
    public async Task SearchPastIncidents_ReturnsEmptyForNoMatch()
    {
        // Act
        var results = await _service.SearchPastIncidentsAsync("xyznonexistent123");

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public async Task GetRelevantPatterns_ReturnsMatchingPatterns()
    {
        // Arrange - create a resolved investigation to generate pattern
        var threadId = (ulong)Random.Shared.NextInt64();
        var inv = await _service.StartInvestigationAsync(threadId, "database connection failed");
        await _service.RecordStepAsync(inv.Id, "checked postgres", "Docker", "container restarting");
        await _service.ResolveInvestigationAsync(inv.Id, "increased memory limit");

        // Act
        var patterns = await _service.GetRelevantPatternsAsync("database");

        // Assert
        Assert.NotEmpty(patterns);
        Assert.Contains(patterns, p => p.Symptom.Contains("database"));
    }

    [Fact]
    public async Task GenerateIncidentContext_ReturnsFormattedContext()
    {
        // Arrange - ensure we have some resolved incidents
        var threadId = (ulong)Random.Shared.NextInt64();
        var inv = await _service.StartInvestigationAsync(threadId, "api timeout error");
        await _service.ResolveInvestigationAsync(inv.Id, "backend overloaded");

        // Act
        var context = await _service.GenerateIncidentContextAsync("api timeout");

        // Assert
        Assert.Contains("Past Incident", context);
    }

    [Fact]
    public async Task GenerateIncidentContext_ReturnsEmptyWhenNoMatch()
    {
        // Act
        var context = await _service.GenerateIncidentContextAsync("totallyuniquenomatch999");

        // Assert
        Assert.Empty(context);
    }

    [Fact]
    public async Task MultipleSteps_AreRecordedInOrder()
    {
        // Arrange
        var threadId = (ulong)Random.Shared.NextInt64();
        var investigation = await _service.StartInvestigationAsync(threadId, "multi-step test");

        // Act
        await _service.RecordStepAsync(investigation.Id, "step 1", null, null);
        await Task.Delay(10); // Ensure different timestamps
        await _service.RecordStepAsync(investigation.Id, "step 2", null, null);
        await Task.Delay(10);
        await _service.RecordStepAsync(investigation.Id, "step 3", null, null);

        // Assert
        var updated = await _service.GetActiveInvestigationAsync(threadId);
        Assert.Equal(3, updated!.Steps.Count);
        Assert.Equal("step 1", updated.Steps[0].Action);
        Assert.Equal("step 2", updated.Steps[1].Action);
        Assert.Equal("step 3", updated.Steps[2].Action);
    }

    [Fact]
    public async Task ResolveInvestigation_Nonexistent_ReturnsNull()
    {
        // Act
        var result = await _service.ResolveInvestigationAsync(999999, "some resolution");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveInvestigation_SetsCommonCause_FromSteps()
    {
        // Arrange
        var threadId = (ulong)Random.Shared.NextInt64();
        var trigger = $"cause-test-{Guid.NewGuid()}";
        var inv = await _service.StartInvestigationAsync(threadId, trigger);
        await _service.RecordStepAsync(inv.Id, "check logs", null, "OOM detected");
        await _service.RecordStepAsync(inv.Id, "check memory", null, "swap exhausted");

        // Act
        await _service.ResolveInvestigationAsync(inv.Id, "added more memory");

        // Assert
        using var db = _fixture.DbContextFactory.CreateDbContext();
        var pattern = db.Patterns.FirstOrDefault(p => p.Symptom == trigger);
        Assert.NotNull(pattern);
        Assert.Contains("OOM detected", pattern.CommonCause);
        Assert.Contains("swap exhausted", pattern.CommonCause);
    }

    [Fact]
    public async Task TryCreatePattern_ExistingSymptom_IncrementsOccurrenceCount()
    {
        // Arrange
        var trigger = $"recurring-{Guid.NewGuid()}";

        var threadId1 = (ulong)Random.Shared.NextInt64();
        var inv1 = await _service.StartInvestigationAsync(threadId1, trigger);
        await _service.RecordStepAsync(inv1.Id, "step", null, "result");
        await _service.ResolveInvestigationAsync(inv1.Id, "fix 1");

        var threadId2 = (ulong)Random.Shared.NextInt64();
        var inv2 = await _service.StartInvestigationAsync(threadId2, trigger);
        await _service.RecordStepAsync(inv2.Id, "step", null, "result");
        await _service.ResolveInvestigationAsync(inv2.Id, "fix 2");

        // Assert
        using var db = _fixture.DbContextFactory.CreateDbContext();
        var pattern = db.Patterns.FirstOrDefault(p => p.Symptom == trigger);
        Assert.NotNull(pattern);
        Assert.Equal(2, pattern.OccurrenceCount);
    }

    [Fact]
    public async Task ListPatterns_ReturnsOrderedByOccurrenceCount()
    {
        // Arrange — create patterns with different occurrence counts
        var trigger1 = $"list-low-{Guid.NewGuid()}";
        var trigger2 = $"list-high-{Guid.NewGuid()}";

        // Create pattern with 1 occurrence
        var t1 = (ulong)Random.Shared.NextInt64();
        var inv1 = await _service.StartInvestigationAsync(t1, trigger1);
        await _service.RecordStepAsync(inv1.Id, "s", null, "r");
        await _service.ResolveInvestigationAsync(inv1.Id, "fix");

        // Create pattern with 2 occurrences
        var t2 = (ulong)Random.Shared.NextInt64();
        var inv2 = await _service.StartInvestigationAsync(t2, trigger2);
        await _service.RecordStepAsync(inv2.Id, "s", null, "r");
        await _service.ResolveInvestigationAsync(inv2.Id, "fix");

        var t3 = (ulong)Random.Shared.NextInt64();
        var inv3 = await _service.StartInvestigationAsync(t3, trigger2);
        await _service.RecordStepAsync(inv3.Id, "s", null, "r");
        await _service.ResolveInvestigationAsync(inv3.Id, "fix again");

        // Act
        var patterns = await _service.ListPatternsAsync();

        // Assert
        var idx1 = patterns.FindIndex(p => p.Symptom == trigger1);
        var idx2 = patterns.FindIndex(p => p.Symptom == trigger2);
        Assert.True(idx2 < idx1, "Higher occurrence pattern should come first");
    }

    [Fact]
    public async Task DeletePattern_Nonexistent_ReturnsFalse()
    {
        // Act
        var result = await _service.DeletePatternAsync(999999);

        // Assert
        Assert.False(result);
    }
}
