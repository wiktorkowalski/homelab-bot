using System.Diagnostics;
using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;

namespace HomelabBot.Services;

internal static class AnthropicChatClientFactory
{
    // Per attempt; the SDK makes up to three. Without it the SDK derives the limit from max_tokens
    // and, because thinking is on, from its streaming rule: about 23 minutes per attempt at the
    // default 50000 tokens, so a hung request would block a Discord turn for over an hour.
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);

    // The base URL is fixed: without it the SDK takes ANTHROPIC_BASE_URL from the process
    // environment, and one stray line in the server .env would move every request, key included,
    // to another host. The SDK keeps its own HttpClient in production; tests pass a scripted one.
    // The attempt log rides on Handlers, so the SDK HttpClient, its timeout and its retry stay as
    // they are. A handler binds to one client: build a new one on every call, never share it.
    public static AnthropicClient CreateApiClient(string apiKey, HttpClient? httpClient = null, ILogger? logger = null)
    {
        var client = new AnthropicClient
        {
            ApiKey = apiKey,
            BaseUrl = EnvironmentUrl.Production,
            Timeout = RequestTimeout,
            Handlers = [new AnthropicAttemptLogHandler(logger ?? NullLogger.Instance)],
        };

        return httpClient is null ? client : (AnthropicClient)client.WithOptions(options => options with { HttpClient = httpClient });
    }

    // Outermost first. The function-invocation loop is on top, so the clients below it run once
    // per tool round: one span, one usage record and one shaped request per billed call.
    public static IChatClient Create(
        IAnthropicClient client, string modelId, int maxTokens, ReasoningEffort effort, ILogger logger) =>
        client.AsIChatClient(modelId, maxTokens)
            .AsBuilder()
            .UseKernelFunctionInvocation()
            .Use(inner => new LoopSpanShield(inner))
            .Use(inner => new AnthropicRefusalChatClient(inner))
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
