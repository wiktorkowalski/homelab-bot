using System.Collections.Concurrent;
using HomelabBot.Data;
using HomelabBot.Data.Entities;
using HomelabBot.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace HomelabBot.Services;

public sealed class ConversationService
{
    private readonly ConcurrentDictionary<ulong, ChatHistory> _histories = new();
    private readonly IDbContextFactory<HomelabDbContext> _dbFactory;
    private readonly ILogger<ConversationService> _logger;
    internal const int MaxHistoryMessages = 20;

    // Approximate token budget for everything after the system prompt (chars/4, no tokenizer in
    // the repo). The base prompt with tool schemas is already 16-24k tokens; 15k of history keeps
    // a request well under the ~70k seen when one large tool result stuck in the window (#120).
    internal const int MaxHistoryTokens = 15_000;

    // One tool result may take ~2k tokens once its turn is over. The model sees the full result
    // during the turn that produced it; later turns only need the gist.
    internal const int MaxToolResultChars = 8_000;

    // Ceiling on how many keyword-matched conversations get loaded and scored in memory.
    private const int MaxScoredConversations = 200;
    private const string LikeEscapeChar = "\\";

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

            // A long-lived thread can hold far more than the window; apply the same limits on load.
            TrimHistoryIfNeeded(history);

            _logger.LogInformation(
                "Loaded {Count} messages for thread {ThreadId}, kept {KeptCount} in history",
                conversation.Messages.Count, threadId, history.Count - 1);
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

        // Match in SQL first. Scanning the newest N conversations and scoring them in memory made
        // every older conversation unfindable, and one busy source (alert investigations) could
        // fill that window on its own.
        var hitsPerConversation = new Dictionary<int, int>();
        foreach (var keyword in keywords)
        {
            var pattern = $"%{EscapeLike(keyword)}%";
            var ids = await db.ConversationMessages
                .AsNoTracking()
                .Where(m => EF.Functions.Like(m.Content, pattern, LikeEscapeChar))
                .Select(m => m.ConversationId)
                .Distinct()
                .ToListAsync(ct);

            foreach (var id in ids)
            {
                hitsPerConversation[id] = hitsPerConversation.GetValueOrDefault(id) + 1;
            }
        }

        if (hitsPerConversation.Count == 0)
        {
            return [];
        }

        // Cap by how many keywords each conversation matched, not by recency. One common word
        // ("on", "docker") matches most of the table, and a recency cap would then serve the
        // newest rows regardless of relevance — the very failure this replaces.
        // Id is autoincrement, so it stands in for recency and breaks ties toward newer
        // conversations. Without it a single-keyword query keeps whichever rows SQLite happened
        // to return first, which is the oldest ones.
        var candidateIds = hitsPerConversation
            .OrderByDescending(kv => kv.Value)
            .ThenByDescending(kv => kv.Key)
            .Take(MaxScoredConversations)
            .Select(kv => kv.Key)
            .ToList();

        var conversations = await db.Conversations
            .AsNoTracking()
            .Include(c => c.Messages)
            .Where(c => candidateIds.Contains(c.Id))
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

    // LIKE wildcards in a user's query would otherwise match far more than they typed.
    internal static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

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
        var truncatedResults = TruncateToolResults(history, MaxToolResultChars);
        var removed = TrimHistory(history, MaxHistoryMessages, MaxHistoryTokens);

        if (truncatedResults > 0 || removed > 0)
        {
            _logger.LogDebug(
                "Trimmed history: truncated {TruncatedResults} tool results, removed {RemovedMessages} messages, {RemainingMessages} remain",
                truncatedResults, removed, history.Count - 1);
        }
    }

    // SK auto function invocation (KernelService) appends tool results straight into the cached
    // history, so they are capped here, at the next Add* call, before any later request re-sends them.
    // SK builds each tool message with the result twice: a TextContent (message.Content, also
    // serialized into telemetry) and a FunctionResultContent (sent to the model). Both get capped.
    internal static int TruncateToolResults(ChatHistory history, int maxChars)
    {
        var truncated = 0;

        foreach (var message in history)
        {
            if (message.Role != AuthorRole.Tool)
            {
                continue;
            }

            var changed = false;
            for (var i = 0; i < message.Items.Count; i++)
            {
                switch (message.Items[i])
                {
                    case TextContent text when text.Text is { } body && body.Length > maxChars:
                        text.Text = Truncate(body, maxChars);
                        changed = true;
                        break;

                    // Result is get-only, so swap in a new item with the same call id; the id pairs
                    // it with the assistant's tool call and must survive.
                    case FunctionResultContent result when ResultText(result) is { } body && body.Length > maxChars:
                        message.Items[i] = new FunctionResultContent(
                            result.FunctionName, result.PluginName, result.CallId, Truncate(body, maxChars));
                        changed = true;
                        break;
                }
            }

            if (changed)
            {
                truncated++;
            }
        }

        return truncated;
    }

    // Drops whole turns from the front (never the system prompt at index 0) until the message-count
    // and approximate token limits hold. A turn starts at a user message; cutting mid-turn would
    // leave history opening with an assistant or an orphaned tool result, which the API rejects.
    // The current turn (from the last user message on) is never dropped, even when it alone
    // exceeds the budget.
    internal static int TrimHistory(ChatHistory history, int maxMessages, int maxTokens)
    {
        var tokens = 0;
        for (var i = 1; i < history.Count; i++)
        {
            tokens += EstimateTokens(history[i]);
        }

        var protectedFrom = LastUserIndex(history);
        if (protectedFrom < 1)
        {
            protectedFrom = history.Count - 1;
        }

        var removed = 0;
        while (protectedFrom > 1 && (history.Count - 1 > maxMessages || tokens > maxTokens))
        {
            do
            {
                tokens -= EstimateTokens(history[1]);
                history.RemoveAt(1);
                protectedFrom--;
                removed++;
            }
            while (protectedFrom > 1 && history[1].Role != AuthorRole.User);
        }

        return removed;
    }

    internal static int EstimateTokens(ChatMessageContent message)
    {
        var textChars = 0;
        var resultChars = 0;
        var callChars = 0;
        var hasText = false;

        foreach (var item in message.Items)
        {
            switch (item)
            {
                case TextContent text:
                    textChars += text.Text?.Length ?? 0;
                    hasText = true;
                    break;
                case FunctionResultContent result:
                    resultChars += ResultText(result)?.Length ?? 0;
                    break;
                case FunctionCallContent call:
                    callChars += call.FunctionName.Length;
                    if (call.Arguments is not null)
                    {
                        foreach (var arg in call.Arguments)
                        {
                            callChars += arg.Key.Length + (arg.Value?.ToString()?.Length ?? 0);
                        }
                    }

                    break;
            }
        }

        // An SK tool message carries the same result as text and as FunctionResultContent; count it once.
        return (callChars + (hasText ? textChars : resultChars)) / 4;
    }

    private static int LastUserIndex(ChatHistory history)
    {
        for (var i = history.Count - 1; i >= 1; i--)
        {
            if (history[i].Role == AuthorRole.User)
            {
                return i;
            }
        }

        return -1;
    }

    private static string Truncate(string text, int maxChars) =>
        $"{text[..maxChars]}\n[truncated: kept {maxChars} of {text.Length} chars]";

    // SK stores auto-invoked results as strings already; ToString covers anything else without
    // the serializer throwing on an odd object graph.
    private static string? ResultText(FunctionResultContent result) => result.Result?.ToString();
}
