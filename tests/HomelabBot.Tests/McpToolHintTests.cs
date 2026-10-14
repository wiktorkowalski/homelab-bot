using System.Reflection;
using HomelabBot.Plugins;
using HomelabBot.Services;
using ModelContextProtocol.Server;

namespace HomelabBot.Tests;

public class McpToolHintTests
{
    // Reads the hints a client gets: a null hint means the attribute never set it, and the client guesses.
    private static List<(string Name, bool ChangesState, bool? ReadOnly, bool? Destructive)> McpTools() =>
        typeof(KernelService).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.IsDefined(typeof(McpServerToolAttribute)))
            .Select(m =>
            {
                var hints = McpServerTool.Create(m, _ => throw new InvalidOperationException("Only the metadata is read.")).ProtocolTool.Annotations;
                return ($"{m.DeclaringType!.Name}.{m.Name}", m.IsDefined(typeof(ChangesStateAttribute)), hints?.ReadOnlyHint, hints?.DestructiveHint);
            })
            .ToList();

    [Fact]
    public void ScanFindsTheToolsOfEveryToolType()
    {
        var names = McpTools().Select(t => t.Name).ToList();

        Assert.Contains("LokiPlugin.QueryLogs", names);
        Assert.Contains("HomelabMcpTools.DeletePattern", names);
    }

    [Fact]
    public void ToolWithoutTheMark_IsReadOnly()
    {
        var wrong = McpTools()
            .Where(t => !t.ChangesState && t.ReadOnly != true)
            .Select(t => t.Name);

        Assert.Empty(wrong);
    }

    // Unset, the SDK sends no hints and a client may run the tool without asking.
    [Fact]
    public void MarkedTools_AreNotReadOnlyAndCarryAChosenDestructiveHint()
    {
        Dictionary<string, bool> expected = new()
        {
            ["HomeAssistantPlugin.TurnOff"] = true,
            ["HomeAssistantPlugin.TurnOn"] = true,
            ["HomelabMcpTools.AskBot"] = false,
            ["HomelabMcpTools.DeletePattern"] = true,
            ["NtfyPlugin.SendNotification"] = false,
        };

        var marked = McpTools().Where(t => t.ChangesState).ToList();

        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), marked.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.All(marked, t =>
        {
            Assert.False(t.ReadOnly);
            Assert.Equal(expected[t.Name], t.Destructive);
        });
    }
}
