using System.ClientModel.Primitives;
using System.Text.Json;

namespace HomelabBot.Services;

// Reads `usage` from every chat response so tool rounds are counted, not only the final one.
// A pipeline policy sees each HTTP round; SK's returned message only carries the last round.
internal sealed class OpenRouterUsagePolicy : PipelinePolicy
{
    private readonly ILogger _logger;

    public OpenRouterUsagePolicy(ILogger logger)
    {
        _logger = logger;
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        ProcessNext(message, pipeline, currentIndex);
        Record(message);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        Record(message);
    }

    internal static bool TryParseUsage(
        BinaryData body,
        out int promptTokens,
        out int completionTokens,
        out int? cachedPromptTokens,
        out int? cacheWriteTokens)
    {
        promptTokens = 0;
        completionTokens = 0;
        cachedPromptTokens = null;
        cacheWriteTokens = null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            promptTokens = ReadInt(usage, "prompt_tokens") ?? 0;
            completionTokens = ReadInt(usage, "completion_tokens") ?? 0;

            if (usage.TryGetProperty("prompt_tokens_details", out var details)
                && details.ValueKind == JsonValueKind.Object)
            {
                cachedPromptTokens = ReadInt(details, "cached_tokens");
                cacheWriteTokens = ReadInt(details, "cache_write_tokens");
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int? ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)
            ? n
            : null;

    private void Record(PipelineMessage message)
    {
        var scope = TokenUsageScope.Current;
        var response = message.Response;

        // Outside a scope (title generation) nobody consumes the numbers, so skip parsing. Error
        // responses are not billed.
        if (scope is null || response is null || response.IsError)
        {
            return;
        }

        // Unbuffered (streaming) bodies cannot be read without consuming the caller's stream.
        if (!message.BufferResponse)
        {
            scope.AddMissedRound();
            _logger.LogDebug("OpenRouter response is streamed; usage for this round not recorded");
            return;
        }

        BinaryData body;
        try
        {
            body = response.Content;
        }
        catch (InvalidOperationException ex)
        {
            scope.AddMissedRound();
            _logger.LogDebug(ex, "OpenRouter response body not buffered; usage for this round not recorded");
            return;
        }

        if (!TryParseUsage(body, out var prompt, out var completion, out var cached, out var cacheWrite))
        {
            scope.AddMissedRound();
            _logger.LogDebug("OpenRouter response had no readable usage; round not recorded");
            return;
        }

        scope.Add(prompt, completion, cached, cacheWrite);

        _logger.LogDebug(
            "OpenRouter round usage: {PromptTokens} prompt ({CachedTokens} cached, {CacheWriteTokens} written), {CompletionTokens} completion",
            prompt, cached, cacheWrite, completion);
    }
}
