using System.Reflection;
using Microsoft.SemanticKernel;

namespace HomelabBot.Plugins;

// Marks a tool that changes something outside the run: a container, a device, an alert, a message
// to a person, or what later runs act on (runbooks start on their own when an alert matches;
// knowledge feeds every prompt and the notification preferences). Scheduled runs never get such a
// tool (KernelService.SelectFunctions), and a runbook step that calls one is Risky. An MCP tool with
// the mark sends ReadOnly = false, every other MCP tool ReadOnly = true (McpToolHintTests). Investigation
// records are not marked: the alert investigation prompt asks for them.
[AttributeUsage(AttributeTargets.Method)]
internal sealed class ChangesStateAttribute : Attribute
{
    public static IReadOnlySet<string> FunctionNames { get; } = typeof(ChangesStateAttribute).Assembly.GetTypes()
        .Where(t => t.Namespace == typeof(ChangesStateAttribute).Namespace)
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        .Where(m => m.IsDefined(typeof(KernelFunctionAttribute)) && m.IsDefined(typeof(ChangesStateAttribute)))
        .Select(m => m.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // In a chat turn the owner approves the call with a Discord button first (ToolConfirmationFilter).
    // On by default, so a new tool asks unless it opts out. Opt out only for writes with no effect
    // outside the bot's own memory or the owner's own notifications: a button per fact would make
    // the memory tools unusable.
    public bool RequiresConfirmation { get; set; } = true;

    // Only the owner may ask for it: a turn of another user gets a refusal before any button.
    public bool OwnerOnly { get; set; }

    public static bool IsOn(KernelFunction function) =>
        function.UnderlyingMethod?.IsDefined(typeof(ChangesStateAttribute), inherit: true) == true;

    public static bool NeedsConfirmation(KernelFunction function) =>
        function.UnderlyingMethod?.GetCustomAttribute<ChangesStateAttribute>(inherit: true) is { RequiresConfirmation: true };

    public static bool IsOwnerOnly(KernelFunction function) =>
        function.UnderlyingMethod?.GetCustomAttribute<ChangesStateAttribute>(inherit: true) is { OwnerOnly: true };
}
