using HomelabBot.Data.Entities;
using HomelabBot.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomelabBot.Tests;

public class IncidentSimilarityServiceTests : IClassFixture<DatabaseFixture>, IDisposable
{
    private readonly DatabaseFixture _fixture;
    private readonly IncidentSimilarityService _service;

    public IncidentSimilarityServiceTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
        _service = new IncidentSimilarityService(
            _fixture.DbContextFactory,
            NullLogger<IncidentSimilarityService>.Instance);
    }

    public void Dispose()
    {
        using var db = _fixture.DbContextFactory.CreateDbContext();
        db.Investigations.RemoveRange(db.Investigations);
        db.RemediationActions.RemoveRange(db.RemediationActions);
        db.SaveChanges();
    }

    [Fact]
    public async Task FindSimilar_NoInvestigations_ReturnsEmpty()
    {
        var results = await _service.FindSimilarAsync("nginx container down");

        Assert.Empty(results);
    }

    [Fact]
    public async Task FindSimilar_MatchingKeywords_ReturnsScored()
    {
        using (var db = _fixture.DbContextFactory.CreateDbContext())
        {
            db.Investigations.Add(new Investigation
            {
                ThreadId = 1,
                Trigger = "nginx container crashed",
                Resolved = true,
                Resolution = "restarted nginx",
                Steps = [new InvestigationStep { Action = "checked logs", Timestamp = DateTime.UtcNow }]
            });
            await db.SaveChangesAsync();
        }

        var results = await _service.FindSimilarAsync("nginx container down");

        Assert.Single(results);
        Assert.True(results[0].SimilarityScore > 0);
        Assert.Contains("keywords match", results[0].MatchReasons[0]);
        Assert.Equal("restarted nginx", results[0].Resolution);
    }

    [Fact]
    public async Task FindSimilar_NoKeywordOverlap_ReturnsEmpty()
    {
        using (var db = _fixture.DbContextFactory.CreateDbContext())
        {
            db.Investigations.Add(new Investigation
            {
                ThreadId = 2,
                Trigger = "grafana dashboard broken",
                Resolved = true,
                Resolution = "fixed config",
                Steps = [new InvestigationStep { Action = "checked config", Timestamp = DateTime.UtcNow }]
            });
            await db.SaveChangesAsync();
        }

        var results = await _service.FindSimilarAsync("plex transcoding slow");

        Assert.Empty(results);
    }

    [Fact]
    public async Task FindSimilar_SameContainer_GetsBonus()
    {
        using (var db = _fixture.DbContextFactory.CreateDbContext())
        {
            db.Investigations.Add(new Investigation
            {
                ThreadId = 3,
                Trigger = "nginx high memory usage",
                Resolved = true,
                Resolution = "restarted",
                Steps = [new InvestigationStep { Action = "check", Timestamp = DateTime.UtcNow }]
            });
            db.Investigations.Add(new Investigation
            {
                ThreadId = 4,
                Trigger = "high memory usage on server",
                Resolved = true,
                Resolution = "cleared cache",
                Steps = [new InvestigationStep { Action = "check", Timestamp = DateTime.UtcNow }]
            });
            await db.SaveChangesAsync();
        }

        var results = await _service.FindSimilarAsync("memory usage high", containerName: "nginx");

        Assert.True(results.Count >= 1);
        // The one mentioning nginx should score higher
        var nginxResult = results.FirstOrDefault(r => r.Trigger.Contains("nginx"));
        var otherResult = results.FirstOrDefault(r => !r.Trigger.Contains("nginx"));
        if (nginxResult != null && otherResult != null)
        {
            Assert.True(nginxResult.SimilarityScore > otherResult.SimilarityScore);
        }
    }

    [Fact]
    public async Task FindSimilar_SameAlertName_GetsBonus()
    {
        using (var db = _fixture.DbContextFactory.CreateDbContext())
        {
            db.Investigations.Add(new Investigation
            {
                ThreadId = 5,
                Trigger = "ContainerDown alert for redis",
                Resolved = true,
                Resolution = "restarted redis",
                Steps = [new InvestigationStep { Action = "restart", Timestamp = DateTime.UtcNow }]
            });
            await db.SaveChangesAsync();
        }

        var labels = new Dictionary<string, string> { ["alertname"] = "ContainerDown" };
        var results = await _service.FindSimilarAsync(
            "ContainerDown alert firing", alertLabels: labels);

        Assert.Single(results);
        Assert.Contains("same alert", results[0].MatchReasons);
    }

    [Fact]
    public async Task FindSimilar_OnlyResolvedIncidents()
    {
        using (var db = _fixture.DbContextFactory.CreateDbContext())
        {
            db.Investigations.Add(new Investigation
            {
                ThreadId = 6,
                Trigger = "plex container crashed",
                Resolved = false,
                Steps = [new InvestigationStep { Action = "checking", Timestamp = DateTime.UtcNow }]
            });
            await db.SaveChangesAsync();
        }

        var results = await _service.FindSimilarAsync("plex container down");

        Assert.Empty(results);
    }

    [Fact]
    public async Task FindSimilar_WithSuccessfulRemediation_GetsBonus()
    {
        var now = DateTime.UtcNow;
        using (var db = _fixture.DbContextFactory.CreateDbContext())
        {
            db.Investigations.Add(new Investigation
            {
                ThreadId = 7,
                Trigger = "nginx container down",
                Resolved = true,
                Resolution = "restarted",
                StartedAt = now.AddMinutes(-30),
                Steps = [new InvestigationStep { Action = "restart", Timestamp = now.AddMinutes(-29) }]
            });
            db.RemediationActions.Add(new RemediationAction
            {
                ContainerName = "nginx",
                ActionType = "restart",
                Trigger = "pattern",
                BeforeState = "exited",
                AfterState = "running",
                Success = true,
                ExecutedAt = now.AddMinutes(-28)
            });
            await db.SaveChangesAsync();
        }

        var results = await _service.FindSimilarAsync(
            "nginx container down", containerName: "nginx");

        Assert.Single(results);
        Assert.Contains("has successful remediation", results[0].MatchReasons);
    }

    [Fact]
    public void FormatDejaVuContext_EmptyList_ReturnsEmpty()
    {
        var result = IncidentSimilarityService.FormatDejaVuContext([]);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void FormatDejaVuContext_WithResults_FormatsCorrectly()
    {
        var incidents = new List<Models.SimilarIncident>
        {
            new()
            {
                InvestigationId = 42,
                Trigger = "nginx crashed",
                Resolution = "restarted nginx",
                SimilarityScore = 85,
                MatchReasons = ["3/3 keywords match", "same container"],
                OccurredAt = DateTime.UtcNow.AddDays(-2)
            }
        };

        var result = IncidentSimilarityService.FormatDejaVuContext(incidents);

        Assert.Contains("#42", result);
        Assert.Contains("85%", result);
        Assert.Contains("restarted nginx", result);
    }
}
