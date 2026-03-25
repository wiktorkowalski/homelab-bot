using System.ComponentModel;
using HomelabBot.Models;
using HomelabBot.Plugins;
using HomelabBot.Services;
using ModelContextProtocol.Server;

namespace HomelabBot.Mcp;

[McpServerToolType]
public sealed class HomelabMcpTools
{
    private readonly DockerPlugin _docker;
    private readonly PrometheusPlugin _prometheus;
    private readonly AlertmanagerPlugin _alertmanager;
    private readonly TrueNASPlugin _truenas;
    private readonly MikroTikPlugin _mikrotik;
    private readonly KnowledgePlugin _knowledge;
    private readonly LokiPlugin _loki;
    private readonly HealthScoreService _healthScore;
    private readonly SummaryDataAggregator _summaryAggregator;
    private readonly ConversationService _conversationService;

    public HomelabMcpTools(
        DockerPlugin docker,
        PrometheusPlugin prometheus,
        AlertmanagerPlugin alertmanager,
        TrueNASPlugin truenas,
        MikroTikPlugin mikrotik,
        KnowledgePlugin knowledge,
        LokiPlugin loki,
        HealthScoreService healthScore,
        SummaryDataAggregator summaryAggregator,
        ConversationService conversationService)
    {
        _docker = docker;
        _prometheus = prometheus;
        _alertmanager = alertmanager;
        _truenas = truenas;
        _mikrotik = mikrotik;
        _knowledge = knowledge;
        _loki = loki;
        _healthScore = healthScore;
        _summaryAggregator = summaryAggregator;
        _conversationService = conversationService;
    }

    [McpServerTool]
    [Description("List all Docker containers with their status")]
    public Task<string> ListContainers() => _docker.ListContainers();

    [McpServerTool]
    [Description("Get detailed status for a specific container")]
    public Task<string> GetContainerStatus(string containerName) => _docker.GetContainerStatus(containerName);

    [McpServerTool]
    [Description("Get recent logs from a container via Loki (time-based)")]
    public Task<string> GetContainerLogs(string containerName, string since = "1h") => _loki.GetContainerLogs(containerName, since);

    [McpServerTool]
    [Description("Execute a PromQL query against Prometheus")]
    public Task<string> QueryPrometheus(string query) => _prometheus.QueryPrometheus(query);

    [McpServerTool]
    [Description("Get CPU, memory, disk stats for the node")]
    public Task<string> GetNodeStats() => _prometheus.GetNodeStats();

    [McpServerTool]
    [Description("Get currently firing alerts from Alertmanager")]
    public Task<string> GetAlerts() => _alertmanager.GetActiveAlerts();

    [McpServerTool]
    [Description("Get ZFS pool health status from TrueNAS")]
    public Task<string> GetPoolStatus() => _truenas.GetPoolStatus();

    [McpServerTool]
    [Description("Get MikroTik router status including CPU, memory, temperature")]
    public Task<string> GetRouterStatus() => _mikrotik.GetRouterStatus();

    [McpServerTool]
    [Description("Search stored knowledge about the homelab")]
    public Task<string> SearchKnowledge(string query) => _knowledge.RecallKnowledge(query);

    [McpServerTool]
    [Description("Search logs across all containers, optionally filtered by container name")]
    public Task<string> SearchLogs(string searchText, string since = "1h", string? containerName = null)
        => _loki.SearchLogs(searchText, since, containerName);

    [McpServerTool]
    [Description("Count error log lines per container, optionally filtered to a specific container")]
    public Task<string> CountErrors(string since = "1h", string? containerName = null)
        => _loki.CountErrorsByContainer(since, containerName);

    [McpServerTool]
    [Description("Execute a raw LogQL query against Loki")]
    public Task<string> QueryLoki(string query, int limit = 100)
        => _loki.QueryLogs(query, limit);

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
}
