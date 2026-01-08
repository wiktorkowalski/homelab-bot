using System.Collections.Concurrent;
using Microsoft.SemanticKernel.ChatCompletion;

namespace HomelabBot.Services;

public sealed class ConversationService
{
    private readonly ConcurrentDictionary<ulong, ChatHistory> _histories = new();
    private readonly ILogger<ConversationService> _logger;
    private const int MaxHistoryMessages = 20;

    public ConversationService(ILogger<ConversationService> logger)
    {
        _logger = logger;
    }

    public ChatHistory GetOrCreateHistory(ulong threadId, string systemPrompt)
    {
        return _histories.GetOrAdd(threadId, _ =>
        {
            _logger.LogDebug("Creating new conversation history for thread {ThreadId}", threadId);
            var history = new ChatHistory();
            history.AddSystemMessage(systemPrompt);
            return history;
        });
    }

    public void AddUserMessage(ulong threadId, string message)
    {
        if (_histories.TryGetValue(threadId, out var history))
        {
            history.AddUserMessage(message);
            TrimHistoryIfNeeded(history);
        }
    }

    public void AddAssistantMessage(ulong threadId, string message)
    {
        if (_histories.TryGetValue(threadId, out var history))
        {
            history.AddAssistantMessage(message);
            TrimHistoryIfNeeded(history);
        }
    }

    public void ClearHistory(ulong threadId)
    {
        if (_histories.TryRemove(threadId, out _))
        {
            _logger.LogDebug("Cleared conversation history for thread {ThreadId}", threadId);
        }
    }

    private void TrimHistoryIfNeeded(ChatHistory history)
    {
        // Keep system message + last N messages
        while (history.Count > MaxHistoryMessages + 1)
        {
            history.RemoveAt(1); // Remove oldest non-system message
        }
    }
}
