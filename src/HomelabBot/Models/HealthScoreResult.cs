namespace HomelabBot.Models;

public sealed class HealthScoreResult
{
    public required int Score { get; init; }
    public required int AlertDeductions { get; init; }
    public required int ContainerDeductions { get; init; }
    public required int PoolDeductions { get; init; }
    public required int MonitoringDeductions { get; init; }
    public required int ConnectivityDeductions { get; init; }
}
