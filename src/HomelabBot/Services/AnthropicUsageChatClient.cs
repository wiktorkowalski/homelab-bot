using System.Diagnostics;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;

namespace HomelabBot.Services;

// Reads the native usage of every Claude response. It sits below the function-invocation loop,
// which sends one request per tool round and reports usage only when the whole loop returns: a
// round that was billed before an exception, a cancel or an empty-response retry is counted here.
internal sealed class AnthropicUsageChatClient : DelegatingChatClient
{
    // OpenTelemetry GenAI name for cache writes. Microsoft.Extensions.AI sets the cache-read tag
    // but not this one.
    internal const string CacheCreationTag = "gen_ai.usage.cache_creation.input_tokens";

    private readonly ILogger _logger;

    public AnthropicUsageChatClient(IChatClient innerClient, ILogger logger)
        : base(innerClient)
    {
        _logger = logger;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        Record(response);
        return response;
    }

    private static int ToInt(long value) => (int)Math.Clamp(value, 0, int.MaxValue);

    private void Record(ChatResponse response)
    {
        var scope = TokenUsageScope.Current;

        if (response.RawRepresentation is not Message message)
        {
            scope?.AddMissedRound();
            _logger.LogWarning(
                "Claude response carried no raw message ({RawType}); usage for this round not recorded",
                response.RawRepresentation?.GetType().Name ?? "null");
            return;
        }

        var usage = message.Usage;
        var cacheRead = usage.CacheReadInputTokens;
        var cacheWrite = usage.CacheCreationInputTokens;

        // The API reports input_tokens without the cached part. The telemetry rows count the whole
        // prompt in PromptTokens and the cached part again in its own column, so add them back.
        var promptTokens = usage.InputTokens + (cacheRead ?? 0) + (cacheWrite ?? 0);

        scope?.Add(
            ToInt(promptTokens),
            ToInt(usage.OutputTokens),
            cacheRead is null ? null : ToInt(cacheRead.Value),
            cacheWrite is null ? null : ToInt(cacheWrite.Value));

        if (cacheWrite is not null && Activity.Current is { } activity
            && activity.Source.Name == TelemetryConstants.LlmActivitySourceName)
        {
            activity.SetTag(CacheCreationTag, cacheWrite.Value);
        }

        _logger.LogInformation(
            "Claude round usage for {Model}: {InputTokens} input, {CacheReadTokens} cache read, {CacheWriteTokens} cache write, {OutputTokens} output, stop reason {StopReason}",
            message.Model.Raw(),
            usage.InputTokens,
            cacheRead ?? 0,
            cacheWrite ?? 0,
            usage.OutputTokens,
            message.StopReason?.Raw() ?? "none");
    }
}
