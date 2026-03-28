using System.Text;
using HomelabBot.Models;
using HomelabBot.Plugins;

namespace HomelabBot.Services;

public sealed class ContagionTrackerService
{
    private readonly DockerPlugin _dockerPlugin;
    private readonly ILogger<ContagionTrackerService> _logger;

    private List<ContainerNetworkInfo>? _cachedMap;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public ContagionTrackerService(
        DockerPlugin dockerPlugin,
        ILogger<ContagionTrackerService> logger)
    {
        _dockerPlugin = dockerPlugin;
        _logger = logger;
    }

    public async Task<BlastRadiusReport> AnalyzeBlastRadiusAsync(
        string sourceContainer, string anomalyType, CancellationToken ct = default)
    {
        var containerMap = await GetContainerMapAsync();

        var source = containerMap.FirstOrDefault(
            c => c.Name.Equals(sourceContainer, StringComparison.OrdinalIgnoreCase));

        if (source == null)
        {
            return new BlastRadiusReport
            {
                SourceService = sourceContainer,
                SourceAnomaly = anomalyType,
                RiskLevel = "unknown"
            };
        }

        var affected = new List<AffectedService>();

        foreach (var container in containerMap)
        {
            if (container.Name.Equals(sourceContainer, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relationship = FindRelationship(source, container);
            if (relationship == null)
            {
                continue;
            }

            affected.Add(new AffectedService
            {
                Name = container.Name,
                Relationship = relationship,
                CurrentStatus = container.State
            });
        }

        var riskLevel = affected.Count switch
        {
            0 => "low",
            <= 2 => "medium",
            <= 5 => "high",
            _ => "critical"
        };

        _logger.LogDebug("Blast radius for {Container}: {Count} affected services ({Risk})",
            sourceContainer, affected.Count, riskLevel);

        return new BlastRadiusReport
        {
            SourceService = sourceContainer,
            SourceAnomaly = anomalyType,
            AffectedServices = affected,
            RiskLevel = riskLevel
        };
    }

    public static string FormatBlastRadius(BlastRadiusReport report)
    {
        if (report.AffectedServices.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        var riskEmoji = report.RiskLevel switch
        {
            "critical" => "🔴",
            "high" => "🟠",
            "medium" => "🟡",
            _ => "🟢"
        };

        sb.AppendLine($"{riskEmoji} **Blast Radius**: {report.AffectedServices.Count} services potentially affected");

        foreach (var service in report.AffectedServices)
        {
            var statusEmoji = service.CurrentStatus == "running" ? "🟢" : "🔴";
            sb.AppendLine($"  {statusEmoji} {service.Name} — {service.Relationship} ({service.CurrentStatus})");
        }

        return sb.ToString();
    }

    private async Task<List<ContainerNetworkInfo>> GetContainerMapAsync()
    {
        if (_cachedMap != null && DateTime.UtcNow < _cacheExpiry)
        {
            return _cachedMap;
        }

        try
        {
            _cachedMap = await _dockerPlugin.GetContainerNetworkMapAsync();
            _cacheExpiry = DateTime.UtcNow.Add(CacheDuration);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh container network map");
            _cachedMap ??= [];
        }

        return _cachedMap;
    }

    private static string? FindRelationship(ContainerNetworkInfo source, ContainerNetworkInfo other)
    {
        // Same Docker network
        var sharedNetworks = source.Networks
            .Intersect(other.Networks, StringComparer.OrdinalIgnoreCase)
            .Where(n => !n.Equals("bridge", StringComparison.OrdinalIgnoreCase)
                     && !n.Equals("host", StringComparison.OrdinalIgnoreCase)
                     && !n.Equals("none", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (sharedNetworks.Count > 0)
        {
            return $"same network ({string.Join(", ", sharedNetworks)})";
        }

        // Compose project relationship (same compose project)
        if (source.Labels.TryGetValue("com.docker.compose.project", out var sourceProject) &&
            other.Labels.TryGetValue("com.docker.compose.project", out var otherProject) &&
            sourceProject.Equals(otherProject, StringComparison.OrdinalIgnoreCase))
        {
            return $"same compose project ({sourceProject})";
        }

        // depends_on relationship
        if (other.Labels.TryGetValue("com.docker.compose.depends_on", out var dependsOn) &&
            dependsOn.Contains(source.Name, StringComparison.OrdinalIgnoreCase))
        {
            return "depends on source";
        }

        return null;
    }
}
