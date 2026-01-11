using System.Collections.Concurrent;
using HomelabBot.Data;
using HomelabBot.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel.ChatCompletion;

namespace HomelabBot.Services;

public sealed class ConversationService
{
    private readonly ConcurrentDictionary<ulong, ChatHistory> _histories = new();
    private readonly IDbContextFactory<HomelabDbContext> _dbFactory;
    private readonly ILogger<ConversationService> _logger;
    private const int MaxHistoryMessages = 20;

    public ConversationService(
        IDbContextFactory<HomelabDbContext> dbFactory,
        ILogger<ConversationService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<ChatHistory> GetOrCreateHistoryAsync(ulong threadId, string systemPrompt, CancellationToken ct = default)
    {
        if (_histories.TryGetValue(threadId, out var cached))
        {
            return cached;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var conversation = await db.Conversations
            .Include(c => c.Messages.OrderBy(m => m.Timestamp))
            .FirstOrDefaultAsync(c => c.ThreadId == threadId, ct);

        var history = new ChatHistory();
        history.AddSystemMessage(systemPrompt);

        if (conversation is not null)
        {
            foreach (var msg in conversation.Messages)
            {
                if (msg.Role == "user")
                    history.AddUserMessage(msg.Content);
                else if (msg.Role == "assistant")
                    history.AddAssistantMessage(msg.Content);
            }

            _logger.LogDebug("Loaded {Count} messages for thread {ThreadId}", conversation.Messages.Count, threadId);
        }
        else
        {
            conversation = new Conversation
            {
                ThreadId = threadId,
                CreatedAt = DateTime.UtcNow
            };
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync(ct);

            _logger.LogDebug("Created new conversation for thread {ThreadId}", threadId);
        }

        _histories[threadId] = history;
        return history;
    }

    public ChatHistory GetOrCreateHistory(ulong threadId, string systemPrompt)
    {
        return GetOrCreateHistoryAsync(threadId, systemPrompt).GetAwaiter().GetResult();
    }

    public async Task AddUserMessageAsync(ulong threadId, string message, CancellationToken ct = default)
    {
        if (_histories.TryGetValue(threadId, out var history))
        {
            history.AddUserMessage(message);
            TrimHistoryIfNeeded(history);
        }

        await PersistMessageAsync(threadId, "user", message, ct);
    }

    public void AddUserMessage(ulong threadId, string message)
    {
        if (_histories.TryGetValue(threadId, out var history))
        {
            history.AddUserMessage(message);
            TrimHistoryIfNeeded(history);
        }

        _ = PersistMessageAsync(threadId, "user", message);
    }

    public async Task AddAssistantMessageAsync(ulong threadId, string message, CancellationToken ct = default)
    {
        if (_histories.TryGetValue(threadId, out var history))
        {
            history.AddAssistantMessage(message);
            TrimHistoryIfNeeded(history);
        }

        await PersistMessageAsync(threadId, "assistant", message, ct);
    }

    public void AddAssistantMessage(ulong threadId, string message)
    {
        if (_histories.TryGetValue(threadId, out var history))
        {
            history.AddAssistantMessage(message);
            TrimHistoryIfNeeded(history);
        }

        _ = PersistMessageAsync(threadId, "assistant", message);
    }

    public async Task UpdateTitleAsync(ulong threadId, string title, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.ThreadId == threadId, ct);

        if (conversation is not null)
        {
            conversation.Title = title;
            await db.SaveChangesAsync(ct);
        }
    }

    public void ClearHistory(ulong threadId)
    {
        if (_histories.TryRemove(threadId, out _))
        {
            _logger.LogDebug("Cleared conversation history for thread {ThreadId}", threadId);
        }
    }

    private async Task PersistMessageAsync(ulong threadId, string role, string content, CancellationToken ct = default)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.ThreadId == threadId, ct);

            if (conversation is null)
            {
                conversation = new Conversation
                {
                    ThreadId = threadId,
                    CreatedAt = DateTime.UtcNow
                };
                db.Conversations.Add(conversation);
                await db.SaveChangesAsync(ct);
            }

            var msg = new ConversationMessage
            {
                ConversationId = conversation.Id,
                Role = role,
                Content = content,
                Timestamp = DateTime.UtcNow
            };

            db.ConversationMessages.Add(msg);
            conversation.LastMessageAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist message for thread {ThreadId}", threadId);
        }
    }

    private void TrimHistoryIfNeeded(ChatHistory history)
    {
        while (history.Count > MaxHistoryMessages + 1)
        {
            history.RemoveAt(1);
        }
    }
}
