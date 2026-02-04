namespace HomelabBot.Models;

public sealed class DailySummaryData
{
    public List<AlertSummary> Alerts { get; init; } = [];
    public List<ContainerStatus> Containers { get; init; } = [];
    public List<PoolStatus> Pools { get; init; } = [];
    public RouterStatus? Router { get; init; }
    public MonitoringStatus? Monitoring { get; init; }
    public int HealthScore { get; init; }
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;
}

public sealed class AlertSummary
{
    public required string Name { get; init; }
    public required string Severity { get; init; }
    public string? Instance { get; init; }
}

public sealed class ContainerStatus
{
    public required string Name { get; init; }
    public required string State { get; init; }
    public string? Health { get; init; }
}

public sealed class PoolStatus
{
    public required string Name { get; init; }
    public required string Health { get; init; }
    public double UsedPercent { get; init; }
}

public sealed class RouterStatus
{
    public double CpuPercent { get; init; }
    public double MemoryPercent { get; init; }
    public TimeSpan Uptime { get; init; }
}

public sealed class MonitoringStatus
{
    public int TotalTargets { get; init; }
    public int UpTargets { get; init; }
    public int DownTargets { get; init; }
}
