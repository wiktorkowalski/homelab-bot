using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using OpenTelemetry;

namespace HomelabBot.Services;

/// <summary>
/// Enriches spans for Langfuse. Required because:
/// 1. The LLM spans carry prompt, answer and usage in OpenTelemetry GenAI attributes; Langfuse
///    reads its own langfuse.observation.* attributes on every version, so they are set here.
/// 2. Session/user IDs need propagation for filtering.
/// </summary>
public sealed class LangfuseEnrichmentProcessor : BaseProcessor<Activity>
{
    private const string InputMessages = "gen_ai.input.messages";
    private const string OutputMessages = "gen_ai.output.messages";

    private readonly ILogger<LangfuseEnrichmentProcessor> _logger;

    public LangfuseEnrichmentProcessor(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<LangfuseEnrichmentProcessor>();
    }

    public override void OnEnd(Activity activity)
    {
        try
        {
            PropagateFromAncestor(activity, "langfuse.session.id");
            PropagateFromAncestor(activity, "langfuse.user.id");

            // Our custom traces and spans (Chat, Generate Title, SmartRecall, etc.)
            if (activity.Source.Name == "HomelabBot.Chat")
            {
                // Root trace level
                CopyTag(activity, "langfuse.trace.input", "input.value");
                CopyTag(activity, "langfuse.trace.output", "output.value");

                // Child span level - map to observation for Input/Output preview
                CopyTag(activity, "langfuse.span.input", "langfuse.observation.input");
                CopyTag(activity, "langfuse.span.output", "langfuse.observation.output");
            }
            else if (TelemetryConstants.IsLlmSpan(activity))
            {
                EnrichGeneration(activity);
            }
            else if (TelemetryConstants.IsToolSpan(activity))
            {
                // Semantic Kernel writes these two tags only with its sensitive-diagnostics switch on.
                CopyTag(activity, "gen_ai.tool.call.arguments", "langfuse.observation.input");
                CopyTag(activity, "gen_ai.tool.call.result", "langfuse.observation.output");
                activity.SetTag("langfuse.observation.type", "tool");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enriching span {ActivityName} for Langfuse", activity.DisplayName);
        }

        base.OnEnd(activity);
    }

    // One LLM request. Input and output become chat-format JSON, which Langfuse renders as a
    // conversation. The raw attributes are then removed: they hold the same text a second time.
    internal static void EnrichGeneration(Activity activity)
    {
        if (ToChatFormat(activity.GetTagItem(InputMessages)?.ToString()) is { } input)
        {
            activity.SetTag("langfuse.observation.input", input.ToJsonString());
            activity.SetTag(InputMessages, null);
        }

        if (ToChatFormat(activity.GetTagItem(OutputMessages)?.ToString()) is { } output)
        {
            activity.SetTag("langfuse.observation.output", (output.Count == 1 ? output[0]! : output).ToJsonString());
            activity.SetTag(OutputMessages, null);
        }

        // About 60 tool schemas on every request; Langfuse has no view for them.
        activity.SetTag("gen_ai.tool.definitions", null);

        // gen_ai.usage.input_tokens counts the cached part too. Langfuse prices each key on its
        // own, so the keys must not overlap.
        if (ReadCount(activity, "gen_ai.usage.input_tokens") is { } inputTokens)
        {
            var cacheRead = ReadCount(activity, "gen_ai.usage.cache_read.input_tokens") ?? 0;
            var cacheWrite = ReadCount(activity, AnthropicUsageChatClient.CacheCreationTag) ?? 0;
            var usage = new JsonObject
            {
                ["input"] = Math.Max(0, inputTokens - cacheRead - cacheWrite),
                ["output"] = ReadCount(activity, "gen_ai.usage.output_tokens") ?? 0,
                ["cache_read_input_tokens"] = cacheRead,
                ["cache_creation_input_tokens"] = cacheWrite,
            };
            activity.SetTag("langfuse.observation.usage_details", usage.ToJsonString());
        }
    }

    // From the OpenTelemetry GenAI shape [{role, parts:[{type, ...}]}] to [{role, content, tool_calls}].
    // Null when the value is missing or not in that shape; the caller then leaves the span as it is.
    internal static JsonArray? ToChatFormat(string? genAiMessagesJson)
    {
        if (string.IsNullOrEmpty(genAiMessagesJson))
        {
            return null;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(genAiMessagesJson);
        }
        catch (JsonException)
        {
            return null;
        }

        if (parsed is not JsonArray messages)
        {
            return null;
        }

        var result = new JsonArray();
        foreach (var message in messages.OfType<JsonObject>())
        {
            AppendChatMessages(message, result);
        }

        return result.Count > 0 ? result : null;
    }

    // One GenAI message becomes one chat message, plus one "tool" message per tool result in it.
    private static void AppendChatMessages(JsonObject message, JsonArray result)
    {
        List<string> texts = [];
        var toolCalls = new JsonArray();
        List<JsonObject> toolResults = [];

        // Any other part type is skipped. A thinking part holds no readable text, only a signature.
        foreach (var part in (message["parts"] as JsonArray ?? []).OfType<JsonObject>())
        {
            switch (part["type"]?.GetValue<string>())
            {
                case "text":
                    texts.Add(AsText(part["content"]));
                    break;

                case "tool_call":
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = part["id"]?.DeepClone(),
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = part["name"]?.DeepClone(),
                            ["arguments"] = part["arguments"]?.ToJsonString() ?? "{}",
                        },
                    });
                    break;

                case "tool_call_response":
                    toolResults.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = part["id"]?.DeepClone(),
                        ["content"] = AsText(part["response"]),
                    });
                    break;
            }
        }

        if (texts.Count > 0 || toolCalls.Count > 0)
        {
            var chatMessage = new JsonObject
            {
                ["role"] = message["role"]?.GetValue<string>() ?? "unknown",
                ["content"] = string.Join("\n", texts),
            };
            if (toolCalls.Count > 0)
            {
                chatMessage["tool_calls"] = toolCalls;
            }

            result.Add(chatMessage);
        }

        foreach (var toolResult in toolResults)
        {
            result.Add(toolResult);
        }
    }

    private static string AsText(JsonNode? node) => node switch
    {
        null => "",
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => node.ToJsonString(),
    };

    private static long? ReadCount(Activity activity, string key) =>
        activity.GetTagItem(key) is { } value && long.TryParse(value.ToString(), out var count) ? count : null;

    private static void PropagateFromAncestor(Activity activity, string key)
    {
        if (activity.GetTagItem(key) != null)
        {
            return;
        }

        for (var ancestor = activity.Parent; ancestor != null; ancestor = ancestor.Parent)
        {
            var value = ancestor.GetTagItem(key)?.ToString();
            if (!string.IsNullOrEmpty(value))
            {
                activity.SetTag(key, value);
                return;
            }
        }
    }

    private static void CopyTag(Activity activity, string source, string target)
    {
        var value = activity.GetTagItem(source)?.ToString();
        if (!string.IsNullOrEmpty(value))
        {
            activity.SetTag(target, value);
        }
    }
}
