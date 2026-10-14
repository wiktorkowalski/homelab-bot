using HomelabBot.Plugins;
using HomelabBot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;

namespace HomelabBot.Tests;

// The model never sees a thread id, so the investigation tools take it from the turn that runs them.
public sealed class InvestigationPluginTests : IDisposable
{
    private const ulong ThreadId = 424242;

    private readonly ScriptedClaudeHandler _handler = new();
    private readonly KernelServiceHarness _harness;

    public InvestigationPluginTests()
    {
        _harness = new KernelServiceHarness(
            _handler.CreateClient(),
            morePlugins: (plugins, harness) => plugins.AddFromObject(CreatePlugin(harness), "Investigation"));
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task InvestigationTools_UseTheThreadOfTheTurn()
    {
        _handler
            .Enqueue(ClaudeResponses.ToolUseWithInput("Investigation_StartInvestigation", """{"symptom":"disk full"}"""))
            .Enqueue(ClaudeResponses.ToolUseWithInput("Investigation_RecordStep", """{"action":"checked pool","result":"91%"}"""))
            .Enqueue(ClaudeResponses.ToolUseWithInput("Investigation_ResolveInvestigation", """{"resolution":"pruned snapshots"}"""))
            .Enqueue(ClaudeResponses.Text("done"));

        await _harness.Service.ProcessMessageAsync(ThreadId, "the disk is full");

        using var db = _harness.DbFactory.CreateDbContext();
        var investigation = await db.Investigations.Include(i => i.Steps).SingleAsync();
        Assert.Equal(ThreadId, investigation.ThreadId);
        Assert.Equal("checked pool", Assert.Single(investigation.Steps).Action);
        Assert.Equal("pruned snapshots", investigation.Resolution);
    }

    [Fact]
    public async Task InvestigationToolSchemas_HaveNoThreadId()
    {
        _handler.Enqueue(ClaudeResponses.Text("hi"));

        await _harness.Service.ProcessMessageAsync(ThreadId, "hello");

        var investigationTools = _handler.Requests[0]["tools"]!.AsArray()
            .Where(t => ((string?)t!["name"])!.StartsWith("Investigation_", StringComparison.Ordinal))
            .ToList();
        Assert.Contains(investigationTools, t => (string?)t!["name"] == "Investigation_StartInvestigation");
        Assert.All(
            investigationTools,
            t => Assert.False(t!["input_schema"]!["properties"]!.AsObject().ContainsKey("threadId"), (string?)t["name"]));
    }

    [Fact]
    public async Task InvestigationTools_OutsideATurn_ReturnAnErrorAndWriteNothing()
    {
        var plugin = CreatePlugin(_harness);

        Assert.StartsWith("Error:", await plugin.StartInvestigation("disk full"), StringComparison.Ordinal);
        Assert.StartsWith("Error:", await plugin.RecordStep("checked pool"), StringComparison.Ordinal);
        Assert.StartsWith("Error:", await plugin.ResolveInvestigation("pruned snapshots"), StringComparison.Ordinal);
        Assert.StartsWith("Error:", await plugin.GetInvestigationStatus(), StringComparison.Ordinal);

        using var db = _harness.DbFactory.CreateDbContext();
        Assert.Empty(db.Investigations);
    }

    private static InvestigationPlugin CreatePlugin(KernelServiceHarness harness)
    {
        var similarity = new IncidentSimilarityService(harness.DbFactory, NullLogger<IncidentSimilarityService>.Instance);
        var investigations = new InvestigationService(
            harness.DbFactory,
            NullLogger<InvestigationService>.Instance,
            new RunbookCompilerService(harness.DbFactory, NullLogger<RunbookCompilerService>.Instance),
            similarity);

        return new InvestigationPlugin(
            investigations, harness.Conversations, similarity, NullLogger<InvestigationPlugin>.Instance);
    }
}
