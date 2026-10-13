using System.Diagnostics;
using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;

namespace HomelabBot.Services;

internal static class AnthropicChatClientFactory
{
    // Outermost first. The function-invocation loop is on top, so the three clients below it
    // run once per tool round: one span, one usage record and one shaped request per billed call.
    public static IChatClient Create(
        IAnthropicClient client, string modelId, int maxTokens, ReasoningEffort effort, ILogger logger) =>
        client.AsIChatClient(modelId, maxTokens)
            .AsBuilder()
            .UseKernelFunctionInvocation()
            .Use(inner => new LoopSpanShield(inner))
            .UseOpenTelemetry(sourceName: TelemetryConstants.LlmActivitySourceName, configure: telemetry => telemetry.EnableSensitiveData = true)
            .Use(inner => new AnthropicUsageChatClient(inner, logger))
            .Use(inner => new AnthropicRequestChatClient(inner, effort))
            .Build();

    // The function-invocation loop asks the clients below it for an ActivitySource. When it gets
    // one it adds an "orchestrate_tools" span that repeats the token counts of all rounds, and an
    // "execute_tool" span around the one Semantic Kernel already writes for each tool. Hiding the
    // source keeps the trace as it was: one chat span per round, one tool span per call.
    private sealed class LoopSpanShield : DelegatingChatClient
    {
        public LoopSpanShield(IChatClient innerClient)
            : base(innerClient)
        {
        }

        public override object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ActivitySource) ? null : base.GetService(serviceType, serviceKey);
    }
}
