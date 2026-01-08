using System.ComponentModel;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using HomelabBot.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace HomelabBot.Plugins;

public sealed class TrueNASPlugin
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TrueNASPlugin> _logger;
    private readonly string _baseUrl;

    public TrueNASPlugin(
        IHttpClientFactory httpClientFactory,
        IOptions<TrueNASConfiguration> config,
        ILogger<TrueNASPlugin> logger)
    {
        _httpClient = httpClientFactory.CreateClient("Default");
        _logger = logger;
        _baseUrl = config.Value.Host.TrimEnd('/');

        if (!string.IsNullOrEmpty(config.Value.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", config.Value.ApiKey);
        }
    }

    [KernelFunction]
    [Description("Gets the health status of all ZFS pools on TrueNAS.")]
    public async Task<string> GetPoolStatus()
    {
        _logger.LogDebug("Getting TrueNAS pool status...");

        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/api/v2.0/pool");
            response.EnsureSuccessStatusCode();

            var pools = await response.Content.ReadFromJsonAsync<List<TrueNASPool>>();

            if (pools == null || pools.Count == 0)
            {
                return "No ZFS pools found.";
            }

            var sb = new StringBuilder();
            sb.AppendLine("**ZFS Pool Status**\n");

            foreach (var pool in pools)
            {
                var healthEmoji = pool.Healthy == true ? "✅" : "⚠️";
                var statusEmoji = pool.Status switch
                {
                    "ONLINE" => "🟢",
                    "DEGRADED" => "🟡",
                    "FAULTED" => "🔴",
                    "OFFLINE" => "⚫",
                    _ => "⚪"
                };

                sb.AppendLine($"{statusEmoji} **{pool.Name}** - {pool.Status} {healthEmoji}");

                if (pool.Topology?.Data != null)
                {
                    var dataVdevs = pool.Topology.Data.Count;
                    sb.AppendLine($"   Topology: {dataVdevs} vdev(s)");
                }

                // Capacity info
                var allocatedTB = (pool.Allocated ?? 0) / (1024.0 * 1024 * 1024 * 1024);
                var sizeTB = (pool.Size ?? 0) / (1024.0 * 1024 * 1024 * 1024);
                var usedPercent = sizeTB > 0 ? (allocatedTB / sizeTB) * 100 : 0;

                sb.AppendLine($"   Capacity: {allocatedTB:F2} TB / {sizeTB:F2} TB ({usedPercent:F1}% used)");

                if (pool.ScanInfo != null && pool.ScanInfo.State != null)
                {
                    sb.AppendLine($"   Last Scan: {pool.ScanInfo.State}");
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting pool status");
            return $"Error getting pool status: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Gets storage usage for all datasets on TrueNAS.")]
    public async Task<string> GetDatasetUsage()
    {
        _logger.LogDebug("Getting dataset usage...");

        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/api/v2.0/pool/dataset");
            response.EnsureSuccessStatusCode();

            var datasets = await response.Content.ReadFromJsonAsync<List<TrueNASDataset>>();

            if (datasets == null || datasets.Count == 0)
            {
                return "No datasets found.";
            }

            var sb = new StringBuilder();
            sb.AppendLine("**Dataset Usage**\n");

            // Show top-level datasets and their immediate children
            var topLevel = datasets
                .Where(d => d.Name?.Contains('/') == false || d.Name?.Count(c => c == '/') <= 1)
                .OrderBy(d => d.Name)
                .Take(20);

            foreach (var dataset in topLevel)
            {
                var usedGB = (dataset.Used?.Parsed ?? 0) / (1024.0 * 1024 * 1024);
                var availableGB = (dataset.Available?.Parsed ?? 0) / (1024.0 * 1024 * 1024);
                var totalGB = usedGB + availableGB;
                var usedPercent = totalGB > 0 ? (usedGB / totalGB) * 100 : 0;

                var bar = GetUsageBar(usedPercent);
                sb.AppendLine($"**{dataset.Name}**");
                sb.AppendLine($"  {bar} {usedPercent:F1}%");
                sb.AppendLine($"  {FormatSize(usedGB)} used / {FormatSize(totalGB)} total");
                sb.AppendLine();
            }

            if (datasets.Count > 20)
            {
                sb.AppendLine($"... and {datasets.Count - 20} more datasets");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting dataset usage");
            return $"Error getting dataset usage: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Gets general system information from TrueNAS including version and health.")]
    public async Task<string> GetSystemInfo()
    {
        _logger.LogDebug("Getting TrueNAS system info...");

        var sb = new StringBuilder();
        sb.AppendLine("**TrueNAS System Info**\n");

        try
        {
            // System info
            var infoResponse = await _httpClient.GetAsync($"{_baseUrl}/api/v2.0/system/info");
            if (infoResponse.IsSuccessStatusCode)
            {
                var info = await infoResponse.Content.ReadFromJsonAsync<TrueNASSystemInfo>();
                if (info != null)
                {
                    sb.AppendLine($"Hostname: **{info.Hostname}**");
                    sb.AppendLine($"Version: **{info.Version}**");

                    if (info.Uptime != null)
                    {
                        sb.AppendLine($"Uptime: **{info.Uptime}**");
                    }

                    if (info.UptimeSeconds.HasValue)
                    {
                        var uptime = TimeSpan.FromSeconds(info.UptimeSeconds.Value);
                        sb.AppendLine($"Uptime: **{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m**");
                    }
                }
            }

            sb.AppendLine();

            // Alert count
            var alertResponse = await _httpClient.GetAsync($"{_baseUrl}/api/v2.0/alert/list");
            if (alertResponse.IsSuccessStatusCode)
            {
                var alerts = await alertResponse.Content.ReadFromJsonAsync<List<TrueNASAlert>>();
                var activeAlerts = alerts?.Where(a => a.Dismissed != true).ToList() ?? [];

                if (activeAlerts.Count == 0)
                {
                    sb.AppendLine("✅ No active alerts");
                }
                else
                {
                    sb.AppendLine($"⚠️ **{activeAlerts.Count} active alert(s)**");
                    foreach (var alert in activeAlerts.Take(5))
                    {
                        var emoji = alert.Level switch
                        {
                            "CRITICAL" => "🔴",
                            "WARNING" => "🟡",
                            "INFO" => "🔵",
                            _ => "⚪"
                        };
                        sb.AppendLine($"  {emoji} {alert.FormattedText ?? alert.Text}");
                    }
                }
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting system info");
            return $"Error getting system info: {ex.Message}";
        }
    }

    private static string GetUsageBar(double percent)
    {
        var filled = (int)(percent / 10);
        var empty = 10 - filled;
        return "[" + new string('▓', filled) + new string('░', empty) + "]";
    }

    private static string FormatSize(double gb)
    {
        if (gb < 1) return $"{gb * 1024:F0} MB";
        if (gb < 1024) return $"{gb:F1} GB";
        return $"{gb / 1024:F2} TB";
    }

    private sealed class TrueNASPool
    {
        public string? Name { get; set; }
        public string? Status { get; set; }
        public bool? Healthy { get; set; }
        public long? Size { get; set; }
        public long? Allocated { get; set; }
        public TrueNASTopology? Topology { get; set; }
        public TrueNASScan? ScanInfo { get; set; }
    }

    private sealed class TrueNASTopology
    {
        public List<object>? Data { get; set; }
    }

    private sealed class TrueNASScan
    {
        public string? State { get; set; }
    }

    private sealed class TrueNASDataset
    {
        public string? Name { get; set; }
        public TrueNASSize? Used { get; set; }
        public TrueNASSize? Available { get; set; }
    }

    private sealed class TrueNASSize
    {
        public long Parsed { get; set; }
    }

    private sealed class TrueNASSystemInfo
    {
        public string? Hostname { get; set; }
        public string? Version { get; set; }
        public string? Uptime { get; set; }
        public double? UptimeSeconds { get; set; }
    }

    private sealed class TrueNASAlert
    {
        public string? Level { get; set; }
        public string? Text { get; set; }
        public string? FormattedText { get; set; }
        public bool? Dismissed { get; set; }
    }
}
