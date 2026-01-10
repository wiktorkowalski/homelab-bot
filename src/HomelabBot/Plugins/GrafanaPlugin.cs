using System.ComponentModel;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using HomelabBot.Configuration;
using HomelabBot.Services;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace HomelabBot.Plugins;

public sealed class GrafanaPlugin
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GrafanaPlugin> _logger;
    private readonly string _baseUrl;
    private readonly string _externalUrl;

    public GrafanaPlugin(
        IHttpClientFactory httpClientFactory,
        IOptions<GrafanaConfiguration> config,
        UrlService urlService,
        ILogger<GrafanaPlugin> logger)
    {
        _httpClient = httpClientFactory.CreateClient("Default");
        _logger = logger;
        _baseUrl = config.Value.Host.TrimEnd('/');
        _externalUrl = urlService.GetExternalUrl("grafana", _baseUrl);

        if (!string.IsNullOrEmpty(config.Value.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", config.Value.ApiKey);
        }
    }

    [KernelFunction]
    [Description("Lists all available Grafana dashboards with their UIDs and titles.")]
    public async Task<string> ListDashboards()
    {
        _logger.LogDebug("Listing Grafana dashboards...");

        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/api/search?type=dash-db");
            response.EnsureSuccessStatusCode();

            var dashboards = await response.Content.ReadFromJsonAsync<List<GrafanaDashboard>>();

            if (dashboards == null || dashboards.Count == 0)
            {
                return "No dashboards found.";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"**Grafana Dashboards ({dashboards.Count})**\n");

            var grouped = dashboards
                .GroupBy(d => d.FolderTitle ?? "General")
                .OrderBy(g => g.Key);

            foreach (var folder in grouped)
            {
                sb.AppendLine($"**{folder.Key}/**");
                foreach (var dashboard in folder.OrderBy(d => d.Title))
                {
                    sb.AppendLine($"  - {dashboard.Title} (uid: `{dashboard.Uid}`)");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing dashboards");
            return $"Error listing dashboards: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Gets information about a specific dashboard including its panels.")]
    public async Task<string> GetDashboardInfo([Description("Dashboard UID (from ListDashboards)")] string uid)
    {
        _logger.LogDebug("Getting dashboard info for {Uid}...", uid);

        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/api/dashboards/uid/{uid}");
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<GrafanaDashboardResponse>();

            if (result?.Dashboard == null)
            {
                return $"Dashboard '{uid}' not found.";
            }

            var dashboard = result.Dashboard;
            var sb = new StringBuilder();

            sb.AppendLine($"**{dashboard.Title}**");
            sb.AppendLine($"- UID: `{dashboard.Uid}`");
            sb.AppendLine($"- Folder: {result.Meta?.FolderTitle ?? "General"}");

            if (dashboard.Panels != null && dashboard.Panels.Count > 0)
            {
                sb.AppendLine($"\n**Panels ({dashboard.Panels.Count})**");

                foreach (var panel in dashboard.Panels.Where(p => p.Type != "row"))
                {
                    var emoji = panel.Type switch
                    {
                        "graph" or "timeseries" => "📈",
                        "stat" or "singlestat" => "🔢",
                        "gauge" => "🎯",
                        "table" => "📋",
                        "text" => "📝",
                        "logs" => "📜",
                        _ => "📊"
                    };

                    sb.AppendLine($"{emoji} {panel.Title} (id: {panel.Id}, type: {panel.Type})");
                }
            }

            // Generate dashboard URL
            sb.AppendLine($"\n🔗 URL: {_externalUrl}/d/{uid}");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting dashboard {Uid}", uid);
            return $"Error getting dashboard: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Renders a dashboard or specific panel as a PNG image. Returns the image URL.")]
    public async Task<string> RenderDashboardScreenshot(
        [Description("Dashboard UID")] string uid,
        [Description("Optional panel ID to render only that panel")] int? panelId = null,
        [Description("Time range like '1h', '6h', '24h', '7d' (default 1h)")] string timeRange = "1h")
    {
        _logger.LogDebug("Rendering dashboard screenshot for {Uid}...", uid);

        var duration = ParseDuration(timeRange);
        var from = DateTimeOffset.UtcNow.Subtract(duration).ToUnixTimeMilliseconds();
        var to = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var renderUrl = $"{_externalUrl}/render/d-solo/{uid}";
        if (panelId.HasValue)
        {
            renderUrl += $"?panelId={panelId}";
        }
        else
        {
            renderUrl = $"{_externalUrl}/render/d/{uid}";
        }

        renderUrl += $"&from={from}&to={to}&width=800&height=400&tz=UTC";

        // Note: Grafana rendering requires the image renderer plugin
        // Return the URL for now - actual image fetching would need more setup
        return $"Dashboard screenshot URL: {renderUrl}\n\nNote: Direct rendering requires Grafana Image Renderer plugin. You can view the dashboard at: {_externalUrl}/d/{uid}?from={from}&to={to}";
    }

    [KernelFunction]
    [Description("Gets the current health status of Grafana.")]
    public async Task<string> GetGrafanaHealth()
    {
        _logger.LogDebug("Checking Grafana health...");

        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/api/health");
            response.EnsureSuccessStatusCode();

            var health = await response.Content.ReadFromJsonAsync<GrafanaHealth>();

            var sb = new StringBuilder();
            sb.AppendLine("**Grafana Health**\n");
            sb.AppendLine($"- Database: {(health?.Database == "ok" ? "✅" : "❌")} {health?.Database}");
            sb.AppendLine($"- Version: {health?.Version ?? "unknown"}");
            sb.AppendLine($"- Commit: {health?.Commit?[..8] ?? "unknown"}");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking Grafana health");
            return $"Error checking health: {ex.Message}";
        }
    }

    private static TimeSpan ParseDuration(string duration)
    {
        if (string.IsNullOrWhiteSpace(duration))
        {
            return TimeSpan.FromHours(1);
        }

        var unit = duration[^1];
        if (!int.TryParse(duration[..^1], out var value))
        {
            return TimeSpan.FromHours(1);
        }

        return unit switch
        {
            'm' => TimeSpan.FromMinutes(value),
            'h' => TimeSpan.FromHours(value),
            'd' => TimeSpan.FromDays(value),
            _ => TimeSpan.FromHours(1)
        };
    }

    private sealed class GrafanaDashboard
    {
        public int Id { get; set; }
        public string? Uid { get; set; }
        public string? Title { get; set; }
        public string? FolderTitle { get; set; }
        public string? Type { get; set; }
    }

    private sealed class GrafanaDashboardResponse
    {
        public DashboardDetail? Dashboard { get; set; }
        public DashboardMeta? Meta { get; set; }
    }

    private sealed class DashboardDetail
    {
        public int Id { get; set; }
        public string? Uid { get; set; }
        public string? Title { get; set; }
        public List<DashboardPanel>? Panels { get; set; }
    }

    private sealed class DashboardPanel
    {
        public int Id { get; set; }
        public string? Title { get; set; }
        public string? Type { get; set; }
    }

    private sealed class DashboardMeta
    {
        public string? FolderTitle { get; set; }
    }

    private sealed class GrafanaHealth
    {
        public string? Database { get; set; }
        public string? Version { get; set; }
        public string? Commit { get; set; }
    }
}
