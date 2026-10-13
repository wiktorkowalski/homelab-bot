using HomelabBot.Helpers;
using Microsoft.SemanticKernel;

namespace HomelabBot.Services;

// Turns an exception from a model-requested tool into the "Error: ..." answer that plugins use
// for their own faults. Without it the function-invocation loop tells the model only
// "Error: Function failed." and, after three failing rounds in a row, throws to the caller.
internal sealed class ToolExceptionFilter : IAutoFunctionInvocationFilter
{
    private readonly ILogger _logger;

    public ToolExceptionFilter(ILogger logger)
    {
        _logger = logger;
    }

    public async Task OnAutoFunctionInvocationAsync(
        AutoFunctionInvocationContext context, Func<AutoFunctionInvocationContext, Task> next)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            // Cancellation is not let through: a tool's own timeout is an OperationCanceledException
            // too, and nothing here can tell it from a cancel of the caller. When the caller did
            // cancel, the next LLM request throws on the same token, so the turn still ends.
            _logger.LogWarning(
                ex, "Tool {Plugin}.{Function} threw; the model gets the error text", context.Function.PluginName, context.Function.Name);

            // Semantic Kernel wraps a cancelled tool; the reason is in the inner exception.
            var reason = ex.InnerException is { } inner ? $"{ex.Message} ({inner.Message})" : ex.Message;
            context.Result = new FunctionResult(context.Function, ToolResult.Failure(reason));
        }
    }
}
