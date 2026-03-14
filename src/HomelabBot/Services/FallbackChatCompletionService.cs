using System.Runtime.CompilerServices;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace HomelabBot.Services;

/// <summary>
/// Wraps a primary IChatCompletionService with a fallback.
/// On primary failure (except cancellation), retries with the fallback using
/// proper OpenAI settings including tool calling.
/// </summary>
public sealed class FallbackChatCompletionService : IChatCompletionService
{
    private readonly IChatCompletionService _primary;
    private readonly IChatCompletionService _fallback;
    private readonly ILogger _logger;

    public FallbackChatCompletionService(
        IChatCompletionService primary,
        IChatCompletionService fallback,
        ILogger logger)
    {
        _primary = primary;
        _fallback = fallback;
        _logger = logger;
    }

    public IReadOnlyDictionary<string, object?> Attributes => _primary.Attributes;

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _primary.GetChatMessageContentsAsync(
                chatHistory, executionSettings, kernel, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Primary LLM failed, falling back to OpenRouter");

            // Build OpenAI-specific settings with tool calling for fallback
            var fallbackSettings = new OpenAIPromptExecutionSettings
            {
                Temperature = 0.7,
                MaxTokens = 2048,
                ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
            };

            return await _fallback.GetChatMessageContentsAsync(
                chatHistory, fallbackSettings, kernel, cancellationToken);
        }
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var results = await GetChatMessageContentsAsync(
            chatHistory, executionSettings, kernel, cancellationToken);

        foreach (var result in results)
        {
            yield return new StreamingChatMessageContent(result.Role, result.Content);
        }
    }
}
