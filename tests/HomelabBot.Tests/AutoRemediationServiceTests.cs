using Docker.DotNet;
using HomelabBot.Configuration;
using HomelabBot.Data.Entities;
using HomelabBot.Models;
using HomelabBot.Plugins;
using HomelabBot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace HomelabBot.Tests;

public class AutoRemediationServiceTests : IClassFixture<DatabaseFixture>, IDisposable
{
    private readonly DatabaseFixture _fixture;
    private readonly DockerPlugin _dockerPlugin;
    private readonly ServiceStateStore _stateStore;

    public AutoRemediationServiceTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
        var dockerClient = new DockerClientConfiguration().CreateClient();
        _dockerPlugin = Substitute.For<DockerPlugin>(dockerClient, NullLogger<DockerPlugin>.Instance);
        _stateStore = Substitute.For<ServiceStateStore>(_fixture.DbContextFactory);

        // LoadStateAsync calls GetAsync twice during construction
        _stateStore.GetAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns((string?)null);
    }

    public void Dispose()
    {
        // Clean up DB state between tests
        using var db = _fixture.DbContextFactory.CreateDbContext();
        db.RemediationActions.RemoveRange(db.RemediationActions);
        db.ContainerCriticalities.RemoveRange(db.ContainerCriticalities);
        db.SaveChanges();
    }

    private AutoRemediationService CreateService(AutoRemediationConfiguration? config = null)
    {
        config ??= new AutoRemediationConfiguration();
        var runbookCompiler = new RunbookCompilerService(
            _fixture.DbContextFactory,
            NullLogger<RunbookCompilerService>.Instance);
        return new AutoRemediationService(
            _dockerPlugin,
            _fixture.DbContextFactory,
            Options.Create(config),
            NullLogger<AutoRemediationService>.Instance,
            _stateStore,
            runbookCompiler);
    }

    private static AlertmanagerWebhookAlert CreateAlert(Dictionary<string, string>? labels = null)
    {
        return new AlertmanagerWebhookAlert
        {
            Labels = labels ?? new Dictionary<string, string> { ["container"] = "nginx" },
        };
    }

    private static Runbook CreateQualifiedRunbook(int successCount = 8, int failureCount = 2)
    {
        return new Runbook
        {
            Name = "Pattern: container down",
            TriggerCondition = "container down",
            StepsJson = "[]",
            SourceType = RunbookSourceType.AutoCompiled,
            SuccessCount = successCount,
            FailureCount = failureCount,
        };
    }

    // --- TryAutoRemediate ---

    [Fact]
    public async Task TryAutoRemediate_WhenDisabled_ReturnsNull()
    {
        var config = new AutoRemediationConfiguration { Enabled = false };
        var svc = CreateService(config);
        var alert = CreateAlert();
        var runbooks = new List<Runbook> { CreateQualifiedRunbook() };

        var result = await svc.TryAutoRemediateAsync(alert, runbooks, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryAutoRemediate_NoQualifiedRunbooks_ReturnsNull()
    {
        var svc = CreateService();
        var alert = CreateAlert();

        // Runbook below MinSuccessRate (80%) and MinFeedbackCount (3)
        var runbooks = new List<Runbook>
        {
            new() { Name = "low rate", TriggerCondition = "low rate", StepsJson = "[]", SuccessCount = 1, FailureCount = 9 },  // 10% rate, 10 feedback
            new() { Name = "low count", TriggerCondition = "low count", StepsJson = "[]", SuccessCount = 1, FailureCount = 0 }, // 100% rate, 1 feedback
        };

        var result = await svc.TryAutoRemediateAsync(alert, runbooks, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryAutoRemediate_CooldownExceeded_ReturnsSkipMessage()
    {
        var config = new AutoRemediationConfiguration { MaxRestartsPerHour = 1 };
        var svc = CreateService(config);

        _dockerPlugin.GetContainerStatus(Arg.Any<string>())
            .Returns("Status: running");
        _dockerPlugin.RestartContainer(Arg.Any<string>())
            .Returns("Restarted container 'nginx'.");

        var alert = CreateAlert();
        var runbooks = new List<Runbook> { CreateQualifiedRunbook() };

        // Persist the runbook so EF can find it by ID
        using (var db = _fixture.DbContextFactory.CreateDbContext())
        {
            db.Runbooks.Add(runbooks[0]);
            db.SaveChanges();
        }

        // First call succeeds and records a cooldown
        await svc.TryAutoRemediateAsync(alert, runbooks, CancellationToken.None);

        // Second call should hit cooldown
        var result = await svc.TryAutoRemediateAsync(alert, runbooks, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.WasAutoExecuted);
        Assert.Contains("cooldown", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryAutoRemediate_CriticalContainer_ReturnsNeedsConfirmation()
    {
        var svc = CreateService();

        _dockerPlugin.GetContainerStatus(Arg.Any<string>())
            .Returns("Status: running");

        var alert = CreateAlert();
        var runbook = CreateQualifiedRunbook();

        // Persist runbook and mark container as critical
        using (var db = _fixture.DbContextFactory.CreateDbContext())
        {
            db.Runbooks.Add(runbook);
            db.ContainerCriticalities.Add(new ContainerCriticality
            {
                ContainerName = "nginx",
                IsCritical = true,
            });
            db.SaveChanges();
        }

        var result = await svc.TryAutoRemediateAsync(alert, [runbook], CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.WasAutoExecuted);
        Assert.True(result.NeedsConfirmation);
        Assert.Contains("critical", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryAutoRemediate_NonCritical_ExecutesRemediation()
    {
        var svc = CreateService();

        _dockerPlugin.GetContainerStatus(Arg.Any<string>())
            .Returns("Status: running");
        _dockerPlugin.RestartContainer(Arg.Any<string>())
            .Returns("Restarted container 'nginx'.");

        var alert = CreateAlert();
        var runbook = CreateQualifiedRunbook();

        using (var db = _fixture.DbContextFactory.CreateDbContext())
        {
            db.Runbooks.Add(runbook);
            db.SaveChanges();
        }

        var result = await svc.TryAutoRemediateAsync(alert, [runbook], CancellationToken.None);

        // Verify result fields
        Assert.NotNull(result);
        Assert.True(result.WasAutoExecuted);
        Assert.False(result.NeedsConfirmation);
        Assert.NotNull(result.ActionId);
        Assert.True(result.ActionId > 0);
        Assert.Equal("nginx", result.ContainerName);
        Assert.Contains("restart", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nginx", result.Message);
        Assert.Contains("[OK]", result.Message);
        Assert.Contains("80%", result.Message); // 8 success / (8+2) = 80%

        // Verify DB record was persisted correctly
        using (var db = _fixture.DbContextFactory.CreateDbContext())
        {
            var action = db.RemediationActions.Find(result.ActionId);
            Assert.NotNull(action);
            Assert.Equal("nginx", action.ContainerName);
            Assert.Equal("restart", action.ActionType);
            Assert.Equal("Status: running", action.BeforeState);
            Assert.Equal("Status: running", action.AfterState);
            Assert.True(action.Success);
            Assert.Equal(runbook.Id, action.RunbookId);
        }
    }

    // --- ExtractContainerName ---

    [Fact]
    public void ExtractContainerName_PrefersContainer_ThenContainerName_ThenInstance()
    {
        var alert = CreateAlert(new Dictionary<string, string>
        {
            ["container"] = "from_container",
            ["container_name"] = "from_container_name",
            ["instance"] = "from_instance:9090",
        });

        var name = AutoRemediationService.ExtractContainerName(alert);

        Assert.Equal("from_container", name);
    }

    [Fact]
    public void ExtractContainerName_StripsPortFromInstance()
    {
        var alert = CreateAlert(new Dictionary<string, string>
        {
            ["instance"] = "myservice:8080",
        });

        var name = AutoRemediationService.ExtractContainerName(alert);

        Assert.Equal("myservice", name);
    }

    [Fact]
    public void ExtractContainerName_ReturnsNull_WhenNoLabels()
    {
        var alert = CreateAlert(new Dictionary<string, string>());

        var name = AutoRemediationService.ExtractContainerName(alert);

        Assert.Null(name);
    }

    // --- RecordFeedback ---

    [Fact]
    public async Task RecordFeedback_IncrementsCorrectCounter()
    {
        var svc = CreateService();

        Runbook runbook;
        RemediationAction action;
        using (var db = _fixture.DbContextFactory.CreateDbContext())
        {
            runbook = new Runbook
            {
                Name = "Pattern: test",
                TriggerCondition = "test",
                StepsJson = "[]",
                SuccessCount = 5,
                FailureCount = 2
            };
            db.Runbooks.Add(runbook);
            db.SaveChanges();

            action = new RemediationAction
            {
                ContainerName = "nginx",
                ActionType = "restart",
                Trigger = "pattern",
                RunbookId = runbook.Id,
                BeforeState = "running",
            };
            db.RemediationActions.Add(action);
            db.SaveChanges();
        }

        await svc.RecordFeedbackAsync(action.Id, true, CancellationToken.None);

        using (var db = _fixture.DbContextFactory.CreateDbContext())
        {
            var updated = db.Runbooks.Find(runbook.Id)!;
            Assert.Equal(6, updated.SuccessCount);
            Assert.Equal(2, updated.FailureCount);
        }
    }
}
