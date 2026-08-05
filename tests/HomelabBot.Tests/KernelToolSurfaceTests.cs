using HomelabBot.Services;
using Microsoft.SemanticKernel;

namespace HomelabBot.Tests;

public class KernelToolSurfaceTests
{
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
    public void InvestigationPlugins_HaveNoDuplicates()
    {
        var plugins = NotificationPrompts.InvestigationPlugins;

        Assert.Equal(plugins.Length, plugins.Distinct(StringComparer.Ordinal).Count());
    }
}
