using HomelabBot.Helpers;
using HomelabBot.Plugins;
using Microsoft.SemanticKernel;

namespace HomelabBot.Services;

// Holds a model-requested tool that changes the real world until the user of the turn approves it
// with a button. Declined, unanswered and unaskable calls do not run; the model gets a text that
// says so and tells the user. Only model calls pass here: a runbook's own steps (kernel.InvokeAsync)
// run under the one approval of ExecuteRunbook.
internal sealed class ToolConfirmationFilter : IAutoFunctionInvocationFilter
{
    internal const string DeclinedResult = "The owner declined this action; it did not run.";

    // The argument text is for a person to read in one glance, not a full dump.
    private const int MaxArgumentChars = 300;

    internal static readonly string TimedOutResult =
        $"No confirmation within {ConfirmationService.Timeout.TotalMinutes:0} minutes; the action did not run.";

    internal static readonly string NoChannelResult = ToolResult.Failure(
        "This action needs the owner's approval with a Discord button, and this conversation has no Discord chat to ask in. " +
        "It did not run. The owner can ask for it in a DM or a bot thread.");

    private readonly ILogger _logger;

    public ToolConfirmationFilter(ILogger logger)
    {
        _logger = logger;
    }

    public async Task OnAutoFunctionInvocationAsync(
        AutoFunctionInvocationContext context, Func<AutoFunctionInvocationContext, Task> next)
    {
        if (!ChangesStateAttribute.NeedsConfirmation(context.Function))
        {
            await next(context);
            return;
        }

        var plugin = context.Function.PluginName;
        var function = context.Function.Name;
        var ask = ToolConfirmationScope.Current;
        if (ask is null)
        {
            _logger.LogWarning(
                "Tool {Plugin}.{Function} needs confirmation, but the turn has no chat to ask in; it did not run", plugin, function);
            context.Result = new FunctionResult(context.Function, NoChannelResult);
            return;
        }

        _logger.LogInformation("Asking the user to confirm tool {Plugin}.{Function}", plugin, function);
        var decision = await ask(Describe(context), context.CancellationToken);
        _logger.LogInformation("Confirmation of tool {Plugin}.{Function}: {Decision}", plugin, function, decision);

        if (decision == ToolConfirmationDecision.Approved)
        {
            await next(context);
            return;
        }

        context.Result = new FunctionResult(
            context.Function, decision == ToolConfirmationDecision.Declined ? DeclinedResult : TimedOutResult);
    }

    internal static string Describe(AutoFunctionInvocationContext context)
    {
        var arguments = context.Arguments is { } args ? string.Join(", ", args.Select(a => $"{a.Key}: {a.Value}")) : "";
        if (arguments.Length > MaxArgumentChars)
        {
            arguments = $"{arguments[..MaxArgumentChars]}...";
        }

        return $"{context.Function.PluginName}.{context.Function.Name}({arguments})";
    }
}
