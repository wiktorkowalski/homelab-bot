using System.Diagnostics;

namespace HomelabBot.Services;

public static class TelemetryConstants
{
    // Source of the per-request "chat <model>" spans of the LLM client.
    public const string LlmActivitySourceName = "HomelabBot.Llm";

    public static readonly ActivitySource ChatActivitySource = new("HomelabBot.Chat");
}
