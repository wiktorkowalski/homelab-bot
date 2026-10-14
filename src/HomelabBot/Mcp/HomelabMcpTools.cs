using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using HomelabBot.Configuration;
using HomelabBot.Helpers;
using HomelabBot.Models;
using HomelabBot.Plugins;
using HomelabBot.Services;
using ModelContextProtocol.Server;

namespace HomelabBot.Mcp;

[McpServerToolType]
public sealed class HomelabMcpTools
{
    private readonly InvestigationService _memoryService;
    private readonly HealthScoreService _healthScore;
    private readonly SummaryDataAggregator _summaryAggregator;
    private readonly ConversationService _conversationService;
    private readonly KernelService _kernelService;
    private readonly ILogger<HomelabMcpTools> _logger;

    public HomelabMcpTools(
        InvestigationService memoryService,
        HealthScoreService healthScore,
        SummaryDataAggregator summaryAggregator,
        ConversationService conversationService,
        KernelService kernelService,
        ILogger<HomelabMcpTools> logger)
    {
        _memoryService = memoryService;
        _healthScore = healthScore;
        _summaryAggregator = summaryAggregator;
        _conversationService = conversationService;
        _kernelService = kernelService;
        _logger = logger;
    }

    [McpServerTool(ReadOnly = true)]
    [Description("Get current health score with breakdown")]
    public async Task<string> GetHealthScore()
    {
        var data = await _summaryAggregator.AggregateAsync();
        var result = _healthScore.CalculateScore(data);
        return $"Health Score: {result.Score}/100 (Alerts: -{result.AlertDeductions}, Containers: -{result.ContainerDeductions}, Pools: -{result.PoolDeductions}, Monitoring: -{result.MonitoringDeductions}, Connectivity: -{result.ConnectivityDeductions})";
    }

    [McpServerTool(ReadOnly = true)]
    [Description("Search past conversations")]
    public async Task<string> SearchConversations(string query)
    {
        var results = await _conversationService.SearchConversationsAsync(query);
        if (results.Count == 0)
        {
            return "No conversations found.";
        }

        return ConversationSearchResult.FormatResults(results);
    }

    [McpServerTool(ReadOnly = true)]
    [Description("List all remediation patterns with their success rates")]
    public async Task<string> ListPatterns()
    {
        var patterns = await _memoryService.ListRunbookPatternsAsync();
        if (patterns.Count == 0)
        {
            return "No remediation patterns found.";
        }

        return string.Join("\n", patterns.Select(p =>
            $"#{p.Id} | {p.TriggerCondition} | {p.SuccessRate:F0}% success | {p.OccurrenceCount}x seen"));
    }

    [ChangesState]
    [McpServerTool(ReadOnly = false, Destructive = true)]
    [Description("Delete a remediation pattern by ID")]
    public async Task<string> DeletePattern(int patternId)
    {
        var deleted = await _memoryService.DeleteRunbookAsync(patternId);
        return deleted ? $"Pattern #{patternId} deleted." : $"Pattern #{patternId} not found.";
    }

    // A chat turn as the owner, on the bot's own model and tools. The turn can write the bot's
    // memory, so the hints say not read-only; [ChangesState] is there because McpToolHintTests
    // requires it on every such tool, and it has no effect on a non-kernel method. A real-world
    // action in the turn needs a Discord button, and this call has no chat to put it in, so it
    // does not run (ToolConfirmationFilter).
    [ChangesState(RequiresConfirmation = false)]
    [McpServerTool(ReadOnly = false, Destructive = false)]
    [Description(
        "Ask HomeLabBot a question or give it a task, as the owner, and get its answer. " +
        "Omit conversationId to start a new conversation; the answer starts with a 'conversationId: <id>' line. " +
        "Pass that id to continue the conversation with its history. " +
        "Actions that change the real world (restart, start, stop, turn on/off, silence, runbooks) need the owner's approval " +
        "with a Discord button and do not run from here.")]
    public async Task<string> AskBot(
        [Description("What to ask or tell the bot.")] string message,
        [Description("The id an earlier ask_bot call returned. Omit it to start a new conversation.")] string? conversationId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            _logger.LogWarning("Rejected ask_bot call: the message is empty");
            return ToolResult.Failure("message is empty. Write what you want to ask the bot.");
        }

        ulong threadId;
        if (conversationId is null)
        {
            threadId = (ulong)Random.Shared.NextInt64(1, long.MaxValue);
        }
        else if (!ulong.TryParse(conversationId, NumberStyles.None, CultureInfo.InvariantCulture, out threadId) || threadId == 0)
        {
            _logger.LogWarning("Rejected ask_bot call: conversationId is not a conversation id");
            return ToolResult.Failure(
                $"conversationId '{conversationId}' is not a conversation id. Pass the id an earlier ask_bot call returned, or omit it to start a new conversation.");
        }

        // KernelService logs a failed turn and returns its error text as the answer.
        _logger.LogInformation(
            "ask_bot turn on thread {ThreadId} ({Kind}, {Chars} chars)",
            threadId, conversationId is null ? "new" : "continued", message.Length);

        // The turn's span must be a root like a Discord turn's, not a child of the HTTP request
        // span that is never exported. Async-local: the request's own span is untouched.
        Activity.Current = null;
        var answer = await _kernelService.ProcessMessageAsync(threadId, message, HomelabOwner.DiscordUserId, ct: ct);
        var reply = $"conversationId: {threadId}\n\n{answer}";
        return KernelService.IsFailedTurnAnswer(answer) ? ToolResult.Failure($"the bot's turn failed.\n{reply}") : reply;
    }
}
