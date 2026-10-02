using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HomelabBot.Services;

// Adds Anthropic prompt-cache breakpoints in OpenRouter's format. SK and the OpenAI SDK have no
// cache_control field, so the request body is rewritten here. Format source:
// https://openrouter.ai/docs/features/prompt-caching
internal sealed class OpenRouterPromptCachePolicy : PipelinePolicy
{
    internal const string AnthropicModelPrefix = "anthropic/";

    // SK sends this placeholder when the history holds tool calls but no tools are offered.
    private const string PlaceholderToolName = "NonInvocableTool";

    private readonly ILogger _logger;
    private readonly bool _enabled;

    public OpenRouterPromptCachePolicy(string modelId, ILogger logger)
    {
        _logger = logger;
        _enabled = IsAnthropic(modelId);
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        return ProcessNextAsync(message, pipeline, currentIndex);
    }

    // Returns null when the body must pass through unchanged (skipReason says why, if unexpected).
    // Explicit breakpoints, not top-level automatic caching: OpenRouter documents automatic caching
    // for a named provider list only, but explicit blocks for all Anthropic-compatible providers.
    // - system message: tools render before system, so this caches tools + system prompt, reused
    //   by every round and by other interactions within the TTL;
    // - last message: only when real tools are offered, so the next tool round reads this round's
    //   prefix. A single-round request would pay the 1.25x write for nothing.
    internal static BinaryData? TryAddCacheControl(BinaryData body, out string? skipReason)
    {
        skipReason = null;

        if (!TryParseObject(body, out var request, out skipReason))
        {
            return null;
        }

        // Non-Anthropic models ignore or reject cache_control; not an unexpected shape.
        if (request["model"] is not JsonValue modelValue
            || !modelValue.TryGetValue<string>(out var model)
            || !IsAnthropic(model))
        {
            return null;
        }

        if (request["messages"] is not JsonArray messages || messages.Count == 0)
        {
            skipReason = "no messages";
            return null;
        }

        var systemMarked = HasRole(messages[0], "system") && TryMark(messages[0]!.AsObject());
        if (HasRole(messages[0], "system") && !systemMarked)
        {
            skipReason = "system message content has an unexpected shape";
            return null;
        }

        var lastIndex = messages.Count - 1;
        var tailMarked = lastIndex > 0
            && OffersRealTools(request)
            && messages[lastIndex] is JsonObject last
            && (HasRole(last, "user") || HasRole(last, "tool"))
            && TryMark(last);

        return systemMarked || tailMarked ? BinaryData.FromString(request.ToJsonString()) : null;
    }

    private static bool IsAnthropic(string model) =>
        model.StartsWith(AnthropicModelPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool TryParseObject(
        BinaryData body, [NotNullWhen(true)] out JsonObject? request, out string? skipReason)
    {
        request = null;
        skipReason = null;
        try
        {
            if (JsonNode.Parse(body) is JsonObject parsed)
            {
                request = parsed;
                return true;
            }

            skipReason = "body is not a JSON object";
            return false;
        }
        catch (JsonException)
        {
            skipReason = "body is not JSON";
            return false;
        }
    }

    private static bool HasRole(JsonNode? message, string role) =>
        message is JsonObject obj
        && obj["role"] is JsonValue value
        && value.TryGetValue<string>(out var name)
        && name == role;

    private static bool OffersRealTools(JsonObject request)
    {
        if (request["tool_choice"] is JsonValue choice
            && choice.TryGetValue<string>(out var choiceName)
            && choiceName == "none")
        {
            return false;
        }

        return request["tools"] is JsonArray tools
            && tools.Any(t => t?["function"]?["name"] is JsonValue name
                && name.TryGetValue<string>(out var toolName)
                && toolName != PlaceholderToolName);
    }

    private static bool TryMark(JsonObject message)
    {
        switch (message["content"])
        {
            case JsonValue text when text.TryGetValue<string>(out var value):
                message["content"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = value,
                        ["cache_control"] = Ephemeral(),
                    });
                return true;

            case JsonArray parts when parts.Count > 0 && parts[^1] is JsonObject lastPart:
                lastPart["cache_control"] ??= Ephemeral();
                return true;

            default:
                return false;
        }
    }

    // Default 5-minute TTL: tool rounds of one interaction start seconds apart, so the 2x write
    // price of the 1-hour TTL buys nothing.
    private static JsonObject Ephemeral() => new() { ["type"] = "ephemeral" };

    private void Apply(PipelineMessage message)
    {
        // Configured model decides before the body is buffered and parsed.
        var content = message.Request.Content;
        if (!_enabled
            || content is null
            || !string.Equals(message.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            using var buffer = new MemoryStream();
            content.WriteTo(buffer);
            var rewritten = TryAddCacheControl(new BinaryData(buffer.ToArray()), out var skipReason);
            if (rewritten is not null)
            {
                message.Request.Content = BinaryContent.Create(rewritten);
            }
            else if (skipReason is not null)
            {
                _logger.LogDebug("Prompt cache_control not added: {SkipReason}", skipReason);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
        {
            // Caching is an optimisation; never fail the request over it.
            _logger.LogDebug(ex, "Prompt cache_control not added: request body could not be read");
        }
    }
}
