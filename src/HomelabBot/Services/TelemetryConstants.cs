using System.Diagnostics;

namespace HomelabBot.Services;

public static class TelemetryConstants
{
    // Source of the per-request "chat <model>" spans of the LLM client.
    public const string LlmActivitySourceName = "HomelabBot.Llm";

    public static readonly ActivitySource ChatActivitySource = new("HomelabBot.Chat");

    // The span of one request to the model.
    public static bool IsLlmSpan(Activity activity) => activity.Source.Name == LlmActivitySourceName;

    // The span Semantic Kernel writes around one tool call.
    public static bool IsToolSpan(Activity activity) =>
        activity.Source.Name.StartsWith("Microsoft.SemanticKernel", StringComparison.Ordinal)
        && activity.GetTagItem("gen_ai.operation.name")?.ToString() == "execute_tool";
}
