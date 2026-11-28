using System.Diagnostics;

namespace HomelabBot.Services;

public static class TelemetryConstants
{
    // Source of the per-request "chat <model>" spans of the LLM client.
    public const string LlmActivitySourceName = "HomelabBot.Llm";

    // The Discord user of a turn, set on the turn span. Langfuse reads it; it also goes to the Claude API as baggage user.id.
    public const string UserIdTag = "langfuse.user.id";

    public static readonly ActivitySource ChatActivitySource = new("HomelabBot.Chat");

    // The value of the tag on the span or on its nearest ancestor that has one.
    public static string? FindTag(Activity? activity, string key)
    {
        for (var span = activity; span != null; span = span.Parent)
        {
            if (span.GetTagItem(key)?.ToString() is { Length: > 0 } value)
            {
                return value;
            }
        }

        return null;
    }

    // The span of one request to the model.
    public static bool IsLlmSpan(Activity activity) => activity.Source.Name == LlmActivitySourceName;

    // The span Semantic Kernel writes around one tool call.
    public static bool IsToolSpan(Activity activity) =>
        activity.Source.Name.StartsWith("Microsoft.SemanticKernel", StringComparison.Ordinal)
        && activity.GetTagItem("gen_ai.operation.name")?.ToString() == "execute_tool";
}
