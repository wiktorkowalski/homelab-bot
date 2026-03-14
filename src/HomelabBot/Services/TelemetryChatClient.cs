using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace HomelabBot.Services;

/// <summary>
/// M.E.AI middleware that emits OpenTelemetry spans for tool calls made by the LLM.
/// Place between UseOpenTelemetry and UseFunctionInvocation in the pipeline so it
/// captures each round-trip including tool invocations.
/// </summary>
public sealed class TelemetryChatClient : DelegatingChatClient
{
    private static readonly ActivitySource ActivitySource = new("HomelabBot.Chat");
    private readonly TelemetryService _telemetryService;
    private readonly ILogger<TelemetryChatClient> _logger;

    public TelemetryChatClient(
        IChatClient innerClient,
        TelemetryService telemetryService,
        ILogger<TelemetryChatClient> logger)
        : base(innerClient)
    {
        _telemetryService = telemetryService;
        _logger = logger;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken);

        // Emit spans for any tool calls in the response
        EmitToolCallSpans(response);

        return response;
    }

    private void EmitToolCallSpans(ChatResponse response)
    {
        foreach (var message in response.Messages)
        {
            // Log tool calls (LLM → tool)
            foreach (var content in message.Contents.OfType<FunctionCallContent>())
            {
                using var activity = ActivitySource.StartActivity(
                    $"Tool: {content.Name}", ActivityKind.Internal);

                activity?.SetTag("langfuse.observation.type", "SPAN");
                activity?.SetTag("langfuse.observation.name", content.Name);
                activity?.SetTag("gen_ai.tool.name", content.Name);
                activity?.SetTag("gen_ai.tool.call_id", content.CallId);

                if (content.Arguments != null)
                {
                    var argsPreview = System.Text.Json.JsonSerializer.Serialize(content.Arguments);
                    if (argsPreview.Length > 500)
                    {
                        argsPreview = argsPreview[..497] + "...";
                    }

                    activity?.SetTag("langfuse.observation.input", argsPreview);
                }

                // Log to internal telemetry DB
                LogToolCall(content.Name, content.Arguments?.ToString());
            }

            // Log tool results (tool → LLM)
            foreach (var content in message.Contents.OfType<FunctionResultContent>())
            {
                var resultStr = content.Result?.ToString();
                if (resultStr != null && resultStr.Length > 500)
                {
                    resultStr = resultStr[..497] + "...";
                }

                // Find the matching activity if still open, or create a brief one
                using var activity = ActivitySource.StartActivity(
                    $"Tool Result: {content.CallId}", ActivityKind.Internal);

                activity?.SetTag("langfuse.observation.name", $"Result: {content.CallId}");
                activity?.SetTag("langfuse.observation.output", resultStr);
            }
        }
    }

    private void LogToolCall(string? toolName, string? input)
    {
        if (string.IsNullOrEmpty(toolName))
            return;

        // Parse Plugin_Function format
        var parts = toolName.Split('_', 2);
        var plugin = parts.Length == 2 ? parts[0] : "Unknown";
        var function = parts.Length == 2 ? parts[1] : toolName;

        _logger.LogDebug("Anthropic tool call: {Plugin}.{Function}", plugin, function);
    }
}
