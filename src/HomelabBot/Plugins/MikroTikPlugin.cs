using System.ComponentModel;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HomelabBot.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace HomelabBot.Plugins;

public sealed class MikroTikPlugin
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<MikroTikPlugin> _logger;
    private readonly string _prometheusUrl;
    private readonly MikroTikConfiguration _config;

    public MikroTikPlugin(
        IHttpClientFactory httpClientFactory,
        IOptions<MikroTikConfiguration> config,
        IOptions<PrometheusConfiguration> prometheusConfig,
        ILogger<MikroTikPlugin> logger)
    {
        _httpClient = httpClientFactory.CreateClient("Default");
        _logger = logger;
        _config = config.Value;
        _prometheusUrl = prometheusConfig.Value.Host.TrimEnd('/');
    }

    [KernelFunction]
    [Description("Gets the current status of the MikroTik router including uptime, CPU, memory usage, and temperature.")]
    public async Task<string> GetRouterStatus()
    {
        _logger.LogDebug("Getting MikroTik router status...");

        var sb = new StringBuilder();
        sb.AppendLine("**MikroTik Router Status**\n");

        // Uptime
        var uptimeSeconds = await QueryPrometheusValue("mktxp_system_uptime");
        if (uptimeSeconds > 0)
        {
            var uptime = TimeSpan.FromSeconds(uptimeSeconds);
            sb.AppendLine($"Uptime: **{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m**");
        }

        // CPU
        var cpuLoad = await QueryPrometheusValue("mktxp_system_cpu_load");
        sb.AppendLine($"CPU Load: **{cpuLoad:F0}%**");

        // Memory
        var memTotal = await QueryPrometheusValue("mktxp_system_total_memory");
        var memFree = await QueryPrometheusValue("mktxp_system_free_memory");
        if (memTotal > 0)
        {
            var memUsedPercent = ((memTotal - memFree) / memTotal) * 100;
            sb.AppendLine($"Memory: **{memUsedPercent:F1}%** used ({FormatBytes(memTotal - memFree)} / {FormatBytes(memTotal)})");
        }

        // Temperature
        var temp = await QueryPrometheusValue("mktxp_system_cpu_temperature");
        if (temp > 0)
        {
            sb.AppendLine($"CPU Temperature: **{temp:F1}°C**");
        }

        // Free disk space
        var diskFree = await QueryPrometheusValue("mktxp_system_free_hdd_space");
        var diskTotal = await QueryPrometheusValue("mktxp_system_total_hdd_space");
        if (diskTotal > 0)
        {
            var diskUsedPercent = ((diskTotal - diskFree) / diskTotal) * 100;
            sb.AppendLine($"Storage: **{diskUsedPercent:F1}%** used");
        }

        return sb.ToString();
    }

    [KernelFunction]
    [Description("Gets network interface statistics from the MikroTik router including bandwidth usage.")]
    public async Task<string> GetInterfaceStats()
    {
        _logger.LogDebug("Getting interface stats...");

        try
        {
            // Query all interface metrics
            var txQuery = "rate(mktxp_interface_tx_byte[5m])";
            var rxQuery = "rate(mktxp_interface_rx_byte[5m])";

            var txResults = await QueryPrometheusMultiple(txQuery);
            var rxResults = await QueryPrometheusMultiple(rxQuery);

            if (txResults.Count == 0)
            {
                return "No interface statistics available.";
            }

            var sb = new StringBuilder();
            sb.AppendLine("**Network Interfaces**\n");

            var interfaces = txResults
                .Select(t => t.Labels.GetValueOrDefault("name") ?? "unknown")
                .Distinct()
                .Where(n => !n.StartsWith("lo"))
                .OrderBy(n => n);

            foreach (var iface in interfaces)
            {
                var tx = txResults.FirstOrDefault(r => r.Labels.GetValueOrDefault("name") == iface);
                var rx = rxResults.FirstOrDefault(r => r.Labels.GetValueOrDefault("name") == iface);

                var txRate = tx?.Value ?? 0;
                var rxRate = rx?.Value ?? 0;

                // Only show active interfaces
                if (txRate > 100 || rxRate > 100)
                {
                    sb.AppendLine($"**{iface}**");
                    sb.AppendLine($"  ↑ {FormatBitsPerSecond(txRate * 8)} | ↓ {FormatBitsPerSecond(rxRate * 8)}");
                }
            }

            if (sb.Length < 30)
            {
                sb.AppendLine("All interfaces idle.");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting interface stats");
            return $"Error getting interface stats: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Lists connected WiFi clients with their signal strength.")]
    public async Task<string> GetWifiClients()
    {
        _logger.LogDebug("Getting WiFi clients...");

        try
        {
            var signalQuery = "mktxp_wifi_client_signal";
            var results = await QueryPrometheusMultiple(signalQuery);

            if (results.Count == 0)
            {
                return "No WiFi clients connected or metrics not available.";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"**WiFi Clients ({results.Count})**\n");

            foreach (var client in results.OrderByDescending(r => r.Value))
            {
                var mac = client.Labels.GetValueOrDefault("mac_address") ?? "unknown";
                var signal = client.Value;
                var signalBars = GetSignalBars(signal);

                sb.AppendLine($"{signalBars} {mac} ({signal:F0} dBm)");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting WiFi clients");
            return $"Error getting WiFi clients: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Lists DHCP leases (connected devices) from the MikroTik router.")]
    public async Task<string> GetDhcpLeases()
    {
        _logger.LogDebug("Getting DHCP leases...");

        try
        {
            // MKTXP exports DHCP lease info
            var leaseQuery = "mktxp_dhcp_lease_info";
            var results = await QueryPrometheusMultiple(leaseQuery);

            if (results.Count == 0)
            {
                return "No DHCP lease information available. MKTXP may need DHCP export enabled.";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"**DHCP Leases ({results.Count})**\n");

            foreach (var lease in results.OrderBy(r => r.Labels.GetValueOrDefault("address")))
            {
                var hostname = lease.Labels.GetValueOrDefault("host_name") ?? "unknown";
                var address = lease.Labels.GetValueOrDefault("address") ?? "N/A";
                var mac = lease.Labels.GetValueOrDefault("mac_address") ?? "N/A";
                var status = lease.Labels.GetValueOrDefault("status") ?? "active";

                var emoji = status == "bound" ? "🟢" : "⚪";
                sb.AppendLine($"{emoji} **{hostname}** - {address}");
                sb.AppendLine($"   MAC: {mac}");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting DHCP leases");
            return $"Error getting DHCP leases: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Sends a Wake-on-LAN magic packet to wake up a device. Requires the target device's MAC address.")]
    public async Task<string> WakeOnLan([Description("MAC address of the device to wake (e.g., AA:BB:CC:DD:EE:FF)")] string macAddress)
    {
        _logger.LogInformation("Sending WoL packet to {MacAddress}...", macAddress);

        // Validate MAC format
        var cleanMac = macAddress.Replace(":", "").Replace("-", "").ToUpperInvariant();
        if (cleanMac.Length != 12 || !cleanMac.All(c => "0123456789ABCDEF".Contains(c)))
        {
            return "Invalid MAC address format. Use AA:BB:CC:DD:EE:FF or AA-BB-CC-DD-EE-FF.";
        }

        try
        {
            // Send WoL via UDP broadcast
            var macBytes = new byte[6];
            for (int i = 0; i < 6; i++)
            {
                macBytes[i] = Convert.ToByte(cleanMac.Substring(i * 2, 2), 16);
            }

            // Build magic packet: 6 bytes of 0xFF followed by MAC repeated 16 times
            var packet = new byte[102];
            for (int i = 0; i < 6; i++) packet[i] = 0xFF;
            for (int i = 0; i < 16; i++)
            {
                Array.Copy(macBytes, 0, packet, 6 + (i * 6), 6);
            }

            using var client = new System.Net.Sockets.UdpClient();
            client.EnableBroadcast = true;
            await client.SendAsync(packet, packet.Length, "255.255.255.255", 9);

            _logger.LogInformation("WoL packet sent to {MacAddress}", macAddress);
            return $"Wake-on-LAN packet sent to **{macAddress}**. The device should wake up in a few seconds if WoL is enabled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending WoL packet to {MacAddress}", macAddress);
            return $"Error sending WoL packet: {ex.Message}";
        }
    }

    private async Task<double> QueryPrometheusValue(string metric)
    {
        try
        {
            var encodedQuery = Uri.EscapeDataString(metric);
            var response = await _httpClient.GetAsync($"{_prometheusUrl}/api/v1/query?query={encodedQuery}");
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<PrometheusResponse>();
            if (result?.Data?.Result?.FirstOrDefault()?.Value?.Length > 1)
            {
                if (double.TryParse(result.Data.Result[0].Value![1].ToString(), out var value))
                {
                    return value;
                }
            }

            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private async Task<List<MetricResult>> QueryPrometheusMultiple(string query)
    {
        var results = new List<MetricResult>();

        try
        {
            var encodedQuery = Uri.EscapeDataString(query);
            var response = await _httpClient.GetAsync($"{_prometheusUrl}/api/v1/query?query={encodedQuery}");
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<PrometheusResponse>();
            if (result?.Data?.Result != null)
            {
                foreach (var item in result.Data.Result)
                {
                    var value = 0.0;
                    if (item.Value?.Length > 1)
                    {
                        double.TryParse(item.Value[1].ToString(), out value);
                    }

                    results.Add(new MetricResult
                    {
                        Labels = item.Metric ?? [],
                        Value = value
                    });
                }
            }
        }
        catch
        {
            // Return empty list on error
        }

        return results;
    }

    private static string FormatBytes(double bytes)
    {
        if (bytes < 1024) return $"{bytes:F0} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024 * 1024):F1} MB";
        return $"{bytes / (1024 * 1024 * 1024):F2} GB";
    }

    private static string FormatBitsPerSecond(double bps)
    {
        if (bps < 1000) return $"{bps:F0} bps";
        if (bps < 1_000_000) return $"{bps / 1000:F1} Kbps";
        if (bps < 1_000_000_000) return $"{bps / 1_000_000:F1} Mbps";
        return $"{bps / 1_000_000_000:F2} Gbps";
    }

    private static string GetSignalBars(double signalDbm)
    {
        return signalDbm switch
        {
            > -50 => "📶▓▓▓▓",
            > -60 => "📶▓▓▓░",
            > -70 => "📶▓▓░░",
            > -80 => "📶▓░░░",
            _ => "📶░░░░"
        };
    }

    private sealed class MetricResult
    {
        public Dictionary<string, string> Labels { get; set; } = [];
        public double Value { get; set; }
    }

    private sealed class PrometheusResponse
    {
        public string Status { get; set; } = "";
        public PrometheusData? Data { get; set; }
    }

    private sealed class PrometheusData
    {
        public string ResultType { get; set; } = "";
        public List<PrometheusResult>? Result { get; set; }
    }

    private sealed class PrometheusResult
    {
        public Dictionary<string, string>? Metric { get; set; }
        public JsonElement[]? Value { get; set; }
    }
}
