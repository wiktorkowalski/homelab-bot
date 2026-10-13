using System.Diagnostics;
using Microsoft.SemanticKernel;

namespace HomelabBot.Services;

// Semantic Kernel names the tool span after the function alone ("ListContainers"); the model and
// the generation spans use the plugin-qualified name. A function filter runs inside that span.
internal sealed class ToolSpanNameFilter : IFunctionInvocationFilter
{
    public Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        if (Activity.Current is { } activity && TelemetryConstants.IsToolSpan(activity))
        {
            // As an AIFunction the kernel function carries the name it is offered to the model with.
            var toolName = ((Microsoft.Extensions.AI.AIFunction)context.Function).Name;
            activity.DisplayName = $"execute_tool {toolName}";

            // Langfuse takes the name of the observation from this tag, not from the span name.
            activity.SetTag("gen_ai.tool.name", toolName);
        }

        return next(context);
    }
}
