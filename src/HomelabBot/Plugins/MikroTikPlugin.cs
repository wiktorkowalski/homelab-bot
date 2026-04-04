using System.ComponentModel;
using System.Text;
using HomelabBot.Configuration;
using HomelabBot.Models.Prometheus;
using HomelabBot.Services;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using ModelContextProtocol.Server;

namespace HomelabBot.Plugins;

[McpServerToolType]
public sealed class MikroTikPlugin
{
    private readonly PrometheusQueryService _prometheus;
    private readonly ILogger<MikroTikPlugin> _logger;
    private readonly MikroTikConfiguration _config;

    public MikroTikPlugin(
        PrometheusQueryService prometheus,
        IOptions<MikroTikConfiguration> config,
        ILogger<MikroTikPlugin> logger)
    {
        _prometheus = prometheus;
        _logger = logger;
        _config = config.Value;
    }

    [KernelFunction]
    [McpServerTool]
    [Description("Gets the current status of the MikroTik router including uptime, CPU, memory usage, and temperature.")]
    public async Task<string> GetRouterStatus()
    {
        _logger.LogDebug("Getting MikroTik router status...");

        var sb = new StringBuilder();
        sb.AppendLine("**MikroTik Router Status**\n");

        var metrics = await GetRouterMetricsAsync();

        if (metrics.Uptime > TimeSpan.Zero)
        {
            sb.AppendLine($"Uptime: **{metrics.Uptime.Days}d {metrics.Uptime.Hours}h {metrics.Uptime.Minutes}m**");
        }

        sb.AppendLine($"CPU Load: **{metrics.CpuLoad:F0}%**");

        if (metrics.MemoryTotal > 0)
        {
            var memUsed = metrics.MemoryTotal - metrics.MemoryFree;
            sb.AppendLine($"Memory: **{metrics.MemoryPercent:F1}%** used ({FormatBytes(memUsed)} / {FormatBytes(metrics.MemoryTotal)})");
        }

        if (metrics.Temperature > 0)
        {
            sb.AppendLine($"CPU Temperature: **{metrics.Temperature:F1}°C**");
        }

        var diskFree = await _prometheus.QueryScalarAsync("mktxp_system_free_hdd_space") ?? 0;
        var diskTotal = await _prometheus.QueryScalarAsync("mktxp_system_total_hdd_space") ?? 0;
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
            var txResults = await _prometheus.QueryMultipleAsync("rate(mktxp_interface_tx_byte[5m])");
            var rxResults = await _prometheus.QueryMultipleAsync("rate(mktxp_interface_rx_byte[5m])");

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
            var results = await _prometheus.QueryMultipleAsync("mktxp_wifi_client_signal");

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
            var results = await _prometheus.QueryMultipleAsync("mktxp_dhcp_lease_info");

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
            for (int i = 0; i < 6; i++)
            {
                packet[i] = 0xFF;
            }

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

    internal async Task<RouterMetrics> GetRouterMetricsAsync(CancellationToken ct = default)
    {
        var cpuLoad = await _prometheus.QueryScalarAsync("mktxp_system_cpu_load", ct) ?? 0;
        var memTotal = await _prometheus.QueryScalarAsync("mktxp_system_total_memory", ct) ?? 0;
        var memFree = await _prometheus.QueryScalarAsync("mktxp_system_free_memory", ct) ?? 0;
        var temp = await _prometheus.QueryScalarAsync("mktxp_system_cpu_temperature", ct) ?? 0;
        var uptimeSeconds = await _prometheus.QueryScalarAsync("mktxp_system_uptime", ct) ?? 0;

        var memPercent = memTotal > 0 ? ((memTotal - memFree) / memTotal) * 100 : 0;

        return new RouterMetrics
        {
            CpuLoad = cpuLoad,
            MemoryPercent = memPercent,
            MemoryTotal = memTotal,
            MemoryFree = memFree,
            Temperature = temp,
            Uptime = TimeSpan.FromSeconds(uptimeSeconds)
        };
    }

    private static string FormatBytes(double bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes:F0} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024:F1} KB";
        }

        if (bytes < 1024 * 1024 * 1024)
        {
            return $"{bytes / (1024 * 1024):F1} MB";
        }

        return $"{bytes / (1024 * 1024 * 1024):F2} GB";
    }

    private static string FormatBitsPerSecond(double bps)
    {
        if (bps < 1000)
        {
            return $"{bps:F0} bps";
        }

        if (bps < 1_000_000)
        {
            return $"{bps / 1000:F1} Kbps";
        }

        if (bps < 1_000_000_000)
        {
            return $"{bps / 1_000_000:F1} Mbps";
        }

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
}

public sealed class RouterMetrics
{
    public double CpuLoad { get; init; }

    public double MemoryPercent { get; init; }

    public double MemoryTotal { get; init; }

    public double MemoryFree { get; init; }

    public double Temperature { get; init; }

    public TimeSpan Uptime { get; init; }
}
