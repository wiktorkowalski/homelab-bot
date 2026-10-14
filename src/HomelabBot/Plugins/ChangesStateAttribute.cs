using Microsoft.SemanticKernel;

namespace HomelabBot.Plugins;

// Marks a tool that changes something outside the bot: a container, a device, an alert, a
// message to a person. Scheduled runs never get such a tool (KernelService.SelectFunctions).
// The bot's own records (knowledge, investigations) are not marked.
[AttributeUsage(AttributeTargets.Method)]
internal sealed class ChangesStateAttribute : Attribute
{
    public static bool IsOn(KernelFunction function) =>
        function.UnderlyingMethod?.IsDefined(typeof(ChangesStateAttribute), inherit: true) == true;
}
