using System.Reflection;
using HomelabBot.Data.Entities;
using HomelabBot.Models;
using HomelabBot.Plugins;
using HomelabBot.Services;
using Microsoft.SemanticKernel;

namespace HomelabBot.Tests;

public class KernelToolSurfaceTests
{
    private static Kernel CreateKernelWithPingPlugin()
    {
        var builder = Kernel.CreateBuilder();
        builder.Plugins.AddFromObject(new PingPlugin(), "Test");
        return builder.Build();
    }

    private static IEnumerable<MethodInfo> PluginTools() =>
        typeof(KernelService).Assembly.GetTypes()
            .Where(t => t.Namespace == "HomelabBot.Plugins")
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.IsDefined(typeof(KernelFunctionAttribute)));

    private static Kernel CreateKernel()
    {
        var builder = Kernel.CreateBuilder();

        builder.Plugins.Add(KernelPluginFactory.CreateFromFunctions("Docker", [
            KernelFunctionFactory.CreateFromMethod(() => "containers", "ListContainers"),
            KernelFunctionFactory.CreateFromMethod(() => "status", "GetContainerStatus"),
        ]));

        builder.Plugins.Add(KernelPluginFactory.CreateFromFunctions("Loki", [
            KernelFunctionFactory.CreateFromMethod(() => "logs", "SearchLogs"),
        ]));

        builder.Plugins.Add(KernelPluginFactory.CreateFromFunctions("HomeAssistant", [
            KernelFunctionFactory.CreateFromMethod(() => "on", "TurnOn"),
            KernelFunctionFactory.CreateFromMethod(() => "off", "TurnOff"),
        ]));

        return builder.Build();
    }

    [Fact]
    public void SelectFunctions_KeepsOnlyAllowedPlugins()
    {
        var selected = KernelService.SelectFunctions(CreateKernel(), ["Docker", "Loki"]);

        Assert.Equal(3, selected.Count);
        Assert.DoesNotContain(selected, f => f.Name is "TurnOn" or "TurnOff");
    }

    [Fact]
    public void SelectFunctions_UnknownPluginName_FallsBackToEverything()
    {
        var selected = KernelService.SelectFunctions(CreateKernel(), ["Dockerr"]);

        // Better a full tool surface than a model with no tools at all.
        Assert.Equal(5, selected.Count);
    }

    [Fact]
    public void SelectFunctions_IsCaseSensitive_SoTyposDoNotSilentlyPass()
    {
        var selected = KernelService.SelectFunctions(CreateKernel(), ["docker"]);

        Assert.Equal(5, selected.Count);
    }

    [Fact]
    public void SelectFunctions_PartiallyWrongAllowList_KeepsTheNamesThatMatch()
    {
        var selected = KernelService.SelectFunctions(CreateKernel(), ["Docker", "Lokii"]);

        // The bad name drops Loki's tools silently — the Error log is what surfaces it.
        Assert.Equal(2, selected.Count);
        Assert.DoesNotContain(selected, f => f.Name == "SearchLogs");
    }

    [Fact]
    public void FunctionChoiceBehaviorNone_OnlySuppressesSchemasWithAnEmptyList()
    {
        var context = new FunctionChoiceBehaviorConfigurationContext([]) { Kernel = CreateKernel() };

        var bare = FunctionChoiceBehavior.None().GetConfiguration(context);
        var empty = FunctionChoiceBehavior.None(functions: []).GetConfiguration(context);

        // The bare form still advertises every function — it only sets tool_choice=none. Pinned
        // here because ProcessMessageAsync relies on the empty-list form to send no schemas.
        Assert.Equal(5, bare.Functions?.Count);
        Assert.Null(empty.Functions);
    }

    [Fact]
    public void SelectFunctions_ReadOnly_DropsToolsThatChangeState()
    {
        var selected = KernelService.SelectFunctions(CreateKernelWithPingPlugin(), ["Test"], readOnly: true);

        Assert.Contains(selected, f => f.Name == "Ping");
        Assert.DoesNotContain(selected, f => f.Name == "Restart");
    }

    [Fact]
    public void SelectFunctions_ReadOnly_AlsoFiltersTheFallbackOfAWrongAllowList()
    {
        var selected = KernelService.SelectFunctions(CreateKernelWithPingPlugin(), ["Tset"], readOnly: true);

        Assert.Contains(selected, f => f.Name == "Ping");
        Assert.DoesNotContain(selected, f => f.Name == "Restart");
    }

    [Fact]
    public void SelectFunctions_ReadOnly_WithNoAllowList_KeepsEveryReadTool()
    {
        var selected = KernelService.SelectFunctions(CreateKernelWithPingPlugin(), [], readOnly: true);

        Assert.Contains(selected, f => f.Name == "Ping");
        Assert.DoesNotContain(selected, f => f.Name == "Restart");
    }

    // A matching runbook runs on its own unless it is Risky, so every marked tool makes it Risky.
    [Theory]
    [InlineData("RestartContainer", TrustLevel.Risky)]
    [InlineData("SendNotification", TrustLevel.Risky)]
    [InlineData("ExecuteRunbook", TrustLevel.Risky)]
    [InlineData("RememberFact", TrustLevel.Risky)]
    [InlineData("ListContainers", TrustLevel.ReadOnly)]
    public void RunbookTrustLevel_FollowsTheMarks(string function, TrustLevel expected)
    {
        var step = new RunbookStep { Description = "step", PluginName = "Any", FunctionName = function };

        Assert.Equal(expected, RunbookPlugin.DetermineTrustLevel([step]));
    }

    // Runbooks stored before the marks keep an old level; the steps raise it, a stricter stored level holds.
    [Theory]
    [InlineData(TrustLevel.ReadOnly, "SendNotification", TrustLevel.Risky)]
    [InlineData(TrustLevel.Conservative, "RestartContainer", TrustLevel.Risky)]
    [InlineData(TrustLevel.Risky, "ListContainers", TrustLevel.Risky)]
    [InlineData(TrustLevel.Conservative, "ListContainers", TrustLevel.Conservative)]
    [InlineData(TrustLevel.ReadOnly, "ListContainers", TrustLevel.ReadOnly)]
    public void RunbookEffectiveTrustLevel_IsTheStricterOfStoredAndSteps(TrustLevel stored, string function, TrustLevel expected)
    {
        var runbook = new Runbook
        {
            Name = "test",
            TriggerCondition = "alertname=Test",
            StepsJson = $$"""[{"Order":1,"Description":"step","PluginName":"Any","FunctionName":"{{function}}"}]""",
            TrustLevel = stored,
        };

        Assert.Equal(expected, RunbookPlugin.EffectiveTrustLevel(runbook));
    }

    // Pattern runbooks are stored with no steps; nothing in them changes state.
    [Fact]
    public void RunbookEffectiveTrustLevel_NoSteps_KeepsTheStoredLevel()
    {
        var runbook = new Runbook { Name = "pattern", TriggerCondition = "nginx down", StepsJson = "[]" };

        Assert.Equal(TrustLevel.ReadOnly, RunbookPlugin.EffectiveTrustLevel(runbook));
    }

    // The gate cannot read the steps, so it stays closed.
    [Fact]
    public void RunbookEffectiveTrustLevel_UnreadableSteps_IsRisky()
    {
        var runbook = new Runbook { Name = "broken", TriggerCondition = "nginx down", StepsJson = "{not json" };

        Assert.Equal(TrustLevel.Risky, RunbookPlugin.EffectiveTrustLevel(runbook));
    }

    // Removing a mark by mistake would give scheduled runs that tool again.
    [Fact]
    public void ToolsThatChangeState_AreMarked()
    {
        string[] expected =
        [
            "AlertmanagerPlugin.SilenceAlert", "DockerPlugin.RestartContainer", "DockerPlugin.StartContainer",
            "DockerPlugin.StopContainer", "HomeAssistantPlugin.TriggerAutomation", "HomeAssistantPlugin.TurnOff",
            "HomeAssistantPlugin.TurnOn", "KnowledgePlugin.InvalidateKnowledge", "KnowledgePlugin.LearnCorrection",
            "KnowledgePlugin.RememberFact", "KnowledgePlugin.StoreAlias", "MikroTikPlugin.WakeOnLan",
            "NtfyPlugin.SendNotification", "NtfyPlugin.SendNotificationWithActions", "RunbookPlugin.CreateRunbook",
            "RunbookPlugin.ExecuteRunbook",
        ];

        var marked = PluginTools()
            .Where(m => m.IsDefined(typeof(ChangesStateAttribute)))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .Order(StringComparer.Ordinal);

        Assert.Equal(expected, marked);
    }

    // The owner approves these with a button in a chat turn. Memory writes and the owner's own
    // push notifications run without one.
    [Fact]
    public void ToolsWithARealWorldEffect_NeedConfirmation()
    {
        string[] expected =
        [
            "AlertmanagerPlugin.SilenceAlert", "DockerPlugin.RestartContainer", "DockerPlugin.StartContainer",
            "DockerPlugin.StopContainer", "HomeAssistantPlugin.TriggerAutomation", "HomeAssistantPlugin.TurnOff",
            "HomeAssistantPlugin.TurnOn", "MikroTikPlugin.WakeOnLan", "RunbookPlugin.CreateRunbook",
            "RunbookPlugin.ExecuteRunbook",
        ];

        var confirmed = PluginTools()
            .Where(m => m.GetCustomAttribute<ChangesStateAttribute>() is { RequiresConfirmation: true })
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .Order(StringComparer.Ordinal);

        Assert.Equal(expected, confirmed);
    }

    // A new tool named like an action must be marked, or carry a name that does not look like one.
    [Fact]
    public void ToolsNamedLikeAnAction_AreMarked()
    {
        string[] actionVerbs =
        [
            "Start", "Stop", "Restart", "Silence", "Wake", "Turn", "Trigger", "Send", "Execute", "Delete", "Set", "Kill",
            "Remove", "Create", "Remember", "Store", "Learn", "Invalidate", "Update", "Save",
        ];

        var unmarked = PluginTools()
            .Where(m => actionVerbs.Any(v => m.Name.StartsWith(v, StringComparison.Ordinal)))
            .Where(m => !m.IsDefined(typeof(ChangesStateAttribute)))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .ToList();

        // Starts a record of the bot's own, nothing outside it.
        Assert.Equal(["InvestigationPlugin.StartInvestigation"], unmarked);
    }

    [Fact]
    public void InvestigationPlugins_HaveNoDuplicates()
    {
        var plugins = NotificationPrompts.InvestigationPlugins;

        Assert.Equal(plugins.Length, plugins.Distinct(StringComparer.Ordinal).Count());
    }
}
