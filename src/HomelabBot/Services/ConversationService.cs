using System.Collections.Concurrent;
using HomelabBot.Data;
using HomelabBot.Data.Entities;
using HomelabBot.Models;
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
                {
                    history.AddUserMessage(msg.Content);
                }
                else if (msg.Role == "assistant")
                {
                    history.AddAssistantMessage(msg.Content);
                }
            }

            _logger.LogInformation("Loaded {Count} messages for thread {ThreadId}", conversation.Messages.Count, threadId);
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

            _logger.LogInformation("Created new conversation for thread {ThreadId}", threadId);
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

    public async Task<List<ConversationSearchResult>> SearchConversationsAsync(
        string query, int limit = 5, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var keywords = query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(k => k.Length > 1)
            .ToArray();

        if (keywords.Length == 0)
        {
            return [];
        }

        var conversations = await db.Conversations
            .AsNoTracking()
            .Include(c => c.Messages)
            .Where(c => c.Messages.Count > 0)
            .OrderByDescending(c => c.LastMessageAt)
            .Take(100)
            .ToListAsync(ct);

        return conversations
            .Select(c =>
            {
                var score = keywords.Count(k =>
                    c.Messages.Any(m => m.Content.Contains(k, StringComparison.OrdinalIgnoreCase)));
                return (Conversation: c, Score: score);
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Conversation.LastMessageAt)
            .Take(limit)
            .Select(x => new ConversationSearchResult
            {
                ConversationId = x.Conversation.Id,
                ThreadId = x.Conversation.ThreadId,
                Title = x.Conversation.Title,
                Date = x.Conversation.LastMessageAt ?? x.Conversation.CreatedAt,
                Score = x.Score,
                RelevantMessages = x.Conversation.Messages
                    .Where(m => keywords.Any(k => m.Content.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(m => m.Timestamp)
                    .Take(5)
                    .ToList(),
            })
            .ToList();
    }

    public void ClearHistory(ulong threadId)
    {
        if (_histories.TryRemove(threadId, out _))
        {
            _logger.LogInformation("Cleared conversation history for thread {ThreadId}", threadId);
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

            // Auto-set title from first user message
            if (role == "user" && string.IsNullOrEmpty(conversation.Title))
            {
                conversation.Title = content.Length > 100 ? content[..97] + "..." : content;
            }

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
