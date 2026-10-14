using System.Collections.Concurrent;
using System.Globalization;
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

    // Limits for everything after the system prompt. Tokens are an estimate (chars/4, no tokenizer
    // in the repo); JSON and Polish text count up to twice as high at the API. The base prompt
    // with tool schemas is 16-24k tokens on top. The limit bounds the cost of a request: one
    // large tool result stuck in the window once made requests of ~70k tokens (#120).
    internal static readonly (int Messages, int Tokens) HistoryLimit = (32, 24_000);

    // A trim cuts down to this target, not just back under the limit. The prompt cache matches by
    // prefix and every trim moves the start of the message list: a window kept full drops a turn
    // on every message, so no request could read the cached messages of the one before.
    internal static readonly (int Messages, int Tokens) HistoryTarget = (20, 15_000);

    // One tool result may take ~2k tokens once its turn is over. The model sees the full result
    // during the turn that produced it; later turns only need the gist.
    internal const int MaxToolResultChars = 8_000;

    // A message the bot sent to the owner, stored in the owner's DM thread. The model gets it as a
    // user message with this marker: it is not the owner's text and not the model's own answer.
    internal const string BotMessageRole = "notification";

    // Ceiling on how many keyword-matched conversations get loaded and scored in memory.
    private const int MaxScoredConversations = 200;
    private const int MaxRelevantMessages = 5;
    private const string LikeEscapeChar = "\\";

    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _threadLocks = new();

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
            // The prompt belongs to the caller, not to the history: the daily thread serves the
            // healthcheck and the evaluations, each with its own prompt. Index 0 is never trimmed.
            if (cached[0].Content != systemPrompt)
            {
                cached[0] = new ChatMessageContent(AuthorRole.System, systemPrompt);
                _logger.LogDebug("Replaced the system prompt of thread {ThreadId} for this caller", threadId);
            }

            return cached;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.ThreadId == threadId, ct);

        var history = new ChatHistory();
        history.AddSystemMessage(systemPrompt);

        if (conversation is not null)
        {
            // The owner's DM thread is never reset, so read only the newest rows the window can hold.
            var rows = await db.ConversationMessages
                .AsNoTracking()
                .Where(m => m.ConversationId == conversation.Id)
                .OrderByDescending(m => m.Timestamp)
                .ThenByDescending(m => m.Id)
                .Take(HistoryLimit.Messages)
                .ToListAsync(ct);
            rows.Reverse();

            // The cut can land inside a turn; history must not open with an answer.
            foreach (var msg in rows)
            {
                if (msg.Role is "user" or BotMessageRole)
                {
                    history.AddUserMessage(msg.Content);
                }
                else if (msg.Role == "assistant" && history.Count > 1)
                {
                    history.AddAssistantMessage(msg.Content);
                }
            }

            // A long-lived thread can hold far more than the window; apply the same limits on load.
            TrimHistoryIfNeeded(history);

            _logger.LogInformation(
                "Loaded {Count} messages for thread {ThreadId}, kept {KeptCount} in history",
                rows.Count, threadId, history.Count - 1);
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

    // One writer per thread at a time. KernelService holds it for a whole turn: the tool loop
    // appends to the cached history while it runs, and a message added in between would split a
    // tool call from its result. Not reentrant: nothing that runs inside a turn may take it again.
    public async Task<IDisposable> LockThreadAsync(ulong threadId, CancellationToken ct = default)
    {
        var gate = _threadLocks.GetOrAdd(threadId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new ThreadLock(gate);
    }

    // Adds a message the bot already sent to the owner, so the owner can ask about it. It waits
    // for a running turn on the thread to end, then becomes one user message of its own.
    public async Task RecordBotMessageAsync(ulong threadId, string text, CancellationToken ct = default)
    {
        var record = FormatBotMessage(DateTime.UtcNow, text);

        using (await LockThreadAsync(threadId, ct))
        {
            await AppendAsync(threadId, AuthorRole.User, BotMessageRole, record, ct);
        }

        _logger.LogInformation("Recorded a sent bot message ({Chars} chars) in thread {ThreadId}", record.Length, threadId);
    }

    // The stored text is the text the model sees, so a reload builds the same request bytes.
    internal static string FormatBotMessage(DateTime sentAtUtc, string text)
    {
        var record = $"[Bot message sent to the owner at {sentAtUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC]\n{text}";
        return record.Length > MaxToolResultChars ? Truncate(record, MaxToolResultChars) : record;
    }

    public Task AddUserMessageAsync(ulong threadId, string message, CancellationToken ct = default) =>
        AppendAsync(threadId, AuthorRole.User, "user", message, ct);

    public Task AddAssistantMessageAsync(ulong threadId, string message, CancellationToken ct = default) =>
        AppendAsync(threadId, AuthorRole.Assistant, "assistant", message, ct);

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
        var patterns = keywords.Select(k => $"%{EscapeLike(k)}%").ToList();
        var hitsPerConversation = new Dictionary<int, int>();
        foreach (var pattern in patterns)
        {
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

        // Rank on the conversation rows alone. The owner's DM thread is permanent and grows
        // without end, so loading every message of every candidate would pull it all into memory.
        var candidates = await db.Conversations
            .AsNoTracking()
            .Where(c => candidateIds.Contains(c.Id))
            .ToListAsync(ct);

        var winners = candidates
            .OrderByDescending(c => hitsPerConversation[c.Id])
            .ThenByDescending(c => c.LastMessageAt)
            .Take(limit)
            .ToList();

        // One query per winner, not one join for all: with ConversationId fixed, SQLite walks the
        // (ConversationId, Timestamp) index in time order and stops at the fifth match. A single
        // query with a per-conversation ROW_NUMBER ranked every match in the whole table first.
        var results = new List<ConversationSearchResult>(winners.Count);
        foreach (var conversation in winners)
        {
            var messages = await db.ConversationMessages
                .AsNoTracking()
                .Where(m => m.ConversationId == conversation.Id)
                .Where(m => patterns.Any(p => EF.Functions.Like(m.Content, p, LikeEscapeChar)))
                .OrderBy(m => m.Timestamp)
                .Take(MaxRelevantMessages)
                .ToListAsync(ct);

            results.Add(new ConversationSearchResult
            {
                ConversationId = conversation.Id,
                ThreadId = conversation.ThreadId,
                Title = conversation.Title,
                Date = conversation.LastMessageAt ?? conversation.CreatedAt,
                Score = hitsPerConversation[conversation.Id],
                RelevantMessages = messages,
            });
        }

        _logger.LogDebug(
            "Conversation search: {KeywordCount} keywords, {CandidateCount} candidates, {ResultCount} results",
            keywords.Length, candidateIds.Count, results.Count);

        return results;
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

    private async Task AppendAsync(ulong threadId, AuthorRole role, string storedRole, string message, CancellationToken ct)
    {
        if (_histories.TryGetValue(threadId, out var history))
        {
            history.AddMessage(role, message);
            TrimHistoryIfNeeded(history);
        }

        await PersistMessageAsync(threadId, storedRole, message, ct);
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
        var removed = TrimHistory(history, HistoryLimit, HistoryTarget);

        if (truncatedResults > 0 || removed > 0)
        {
            _logger.LogDebug(
                "Trimmed history: truncated {TruncatedResults} tool results, removed {RemovedMessages} messages, {RemainingMessages} remain",
                truncatedResults, removed, history.Count - 1);
        }
    }

    // The function-invocation loop (KernelService) appends tool results straight into the cached
    // history, so they are capped here, at the next Add* call, before any later request re-sends them.
    // A tool message holds the result as a FunctionResultContent (sent to the model). A history
    // built by hand can also hold it as a TextContent. Both get capped.
    // Rewriting a stored turn is safe for the API because stored turns carry no thinking blocks:
    // the bridge drops them when it converts a response into chat history.
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

    // Once the message count or the approximate token count passes the limit, drops whole turns
    // from the front (never the system prompt at index 0) until both fit the target. A turn starts
    // at a user message; cutting mid-turn would leave history opening with an assistant or an
    // orphaned tool result, which the API rejects. The current turn (from the last user message
    // on) is never dropped, even when it alone exceeds the target.
    internal static int TrimHistory(
        ChatHistory history, (int Messages, int Tokens) limit, (int Messages, int Tokens) target)
    {
        var tokens = 0;
        for (var i = 1; i < history.Count; i++)
        {
            tokens += EstimateTokens(history[i]);
        }

        bool Over((int Messages, int Tokens) cap) => history.Count - 1 > cap.Messages || tokens > cap.Tokens;

        if (!Over(limit))
        {
            return 0;
        }

        var protectedFrom = LastUserIndex(history);
        if (protectedFrom < 1)
        {
            protectedFrom = history.Count - 1;
        }

        var removed = 0;
        while (protectedFrom > 1 && Over(target))
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

        // A tool message can carry the same result as text and as FunctionResultContent; count it once.
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

    // The marker counts toward the cap. A capped result that passed the cap again would be cut
    // once more at the next Add* call, and that changes bytes inside the cached prefix. The room
    // for the marker is measured with the longer of its two numbers in both places.
    private static string Truncate(string text, int maxChars)
    {
        var kept = maxChars - $"\n[truncated: kept {text.Length} of {text.Length} chars]".Length;
        return string.Concat(text.AsSpan(0, kept), $"\n[truncated: kept {kept} of {text.Length} chars]");
    }

    // Auto-invoked results are stored as strings already; ToString covers anything else without
    // the serializer throwing on an odd object graph.
    private static string? ResultText(FunctionResultContent result) => result.Result?.ToString();

    private sealed class ThreadLock : IDisposable
    {
        private SemaphoreSlim? _gate;

        public ThreadLock(SemaphoreSlim gate) => _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
