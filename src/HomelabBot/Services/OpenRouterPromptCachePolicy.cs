using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HomelabBot.Services;

// Adds Anthropic prompt-cache breakpoints in OpenRouter's format. SK and the OpenAI SDK have no
// cache_control field, so the request body is rewritten here. Format source:
// https://openrouter.ai/docs/features/prompt-caching
internal sealed class OpenRouterPromptCachePolicy : PipelinePolicy
{
    private const string AnthropicModelPrefix = "anthropic/";

    private readonly ILogger _logger;

    public OpenRouterPromptCachePolicy(ILogger logger)
    {
        _logger = logger;
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

    // Returns null when the body must pass through unchanged. Two breakpoints:
    // - the system message: tools render before system, so this caches tools + system prompt;
    // - top-level automatic caching: moves with the growing tool-round history, so each round
    //   reads the previous round's prefix.
    internal static BinaryData? TryAddCacheControl(BinaryData body, out string? skipReason)
    {
        skipReason = null;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            skipReason = "body is not JSON";
            return null;
        }

        if (root is not JsonObject request)
        {
            skipReason = "body is not a JSON object";
            return null;
        }

        // Non-Anthropic models ignore or reject cache_control; not an unexpected shape.
        if (!IsAnthropicModel(request))
        {
            return null;
        }

        if (request["messages"] is not JsonArray messages)
        {
            skipReason = "no messages array";
            return null;
        }

        if (messages.Count > 0
            && messages[0] is JsonObject first
            && first["role"] is JsonValue role
            && role.TryGetValue<string>(out var roleName)
            && roleName == "system")
        {
            if (!TryMarkSystemMessage(first))
            {
                skipReason = "system message content has an unexpected shape";
                return null;
            }
        }

        request["cache_control"] ??= Ephemeral();

        return BinaryData.FromString(request.ToJsonString());
    }

    private static bool IsAnthropicModel(JsonObject request) =>
        request["model"] is JsonValue modelValue
        && modelValue.TryGetValue<string>(out var model)
        && model.StartsWith(AnthropicModelPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool TryMarkSystemMessage(JsonObject systemMessage)
    {
        switch (systemMessage["content"])
        {
            case JsonValue text when text.TryGetValue<string>(out var value):
                systemMessage["content"] = new JsonArray(
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
        var content = message.Request.Content;
        if (content is null || !string.Equals(message.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
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
