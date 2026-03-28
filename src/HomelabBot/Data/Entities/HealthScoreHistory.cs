namespace HomelabBot.Data.Entities;

public sealed class HealthScoreHistory
{
    public int Id { get; set; }

    public int Score { get; set; }

    public int AlertDeductions { get; set; }

    public int ContainerDeductions { get; set; }

    public int PoolDeductions { get; set; }

    public int MonitoringDeductions { get; set; }

    public int ConnectivityDeductions { get; set; }

    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}
