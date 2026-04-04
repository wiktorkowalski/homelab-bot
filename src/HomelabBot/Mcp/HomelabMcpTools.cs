using System.ComponentModel;
using HomelabBot.Models;
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

    public HomelabMcpTools(
        InvestigationService memoryService,
        HealthScoreService healthScore,
        SummaryDataAggregator summaryAggregator,
        ConversationService conversationService)
    {
        _memoryService = memoryService;
        _healthScore = healthScore;
        _summaryAggregator = summaryAggregator;
        _conversationService = conversationService;
    }

    [McpServerTool]
    [Description("Get current health score with breakdown")]
    public async Task<string> GetHealthScore()
    {
        var data = await _summaryAggregator.AggregateAsync();
        var result = _healthScore.CalculateScore(data);
        return $"Health Score: {result.Score}/100 (Alerts: -{result.AlertDeductions}, Containers: -{result.ContainerDeductions}, Pools: -{result.PoolDeductions}, Monitoring: -{result.MonitoringDeductions}, Connectivity: -{result.ConnectivityDeductions})";
    }

    [McpServerTool]
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

    [McpServerTool]
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

    [McpServerTool]
    [Description("Delete a remediation pattern by ID")]
    public async Task<string> DeletePattern(int patternId)
    {
        var deleted = await _memoryService.DeleteRunbookAsync(patternId);
        return deleted ? $"Pattern #{patternId} deleted." : $"Pattern #{patternId} not found.";
    }
}
