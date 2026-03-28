using HomelabBot.Models;
using HomelabBot.Services;

namespace HomelabBot.Tests;

public class ContagionTrackerServiceTests
{
    [Fact]
    public void FormatBlastRadius_EmptyAffected_ReturnsEmpty()
    {
        var report = new BlastRadiusReport
        {
            SourceService = "nginx",
            SourceAnomaly = "high CPU",
            AffectedServices = []
        };

        var result = ContagionTrackerService.FormatBlastRadius(report);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void FormatBlastRadius_WithAffected_FormatsCorrectly()
    {
        var report = new BlastRadiusReport
        {
            SourceService = "postgres",
            SourceAnomaly = "high CPU",
            RiskLevel = "high",
            AffectedServices =
            [
                new AffectedService
                {
                    Name = "grafana",
                    Relationship = "same network (monitoring)",
                    CurrentStatus = "running"
                },
                new AffectedService
                {
                    Name = "prometheus",
                    Relationship = "same network (monitoring)",
                    CurrentStatus = "running"
                }
            ]
        };

        var result = ContagionTrackerService.FormatBlastRadius(report);

        Assert.Contains("2 services", result);
        Assert.Contains("grafana", result);
        Assert.Contains("prometheus", result);
        Assert.Contains("same network (monitoring)", result);
    }

    [Fact]
    public void FormatBlastRadius_CriticalRisk_ShowsRedEmoji()
    {
        var report = new BlastRadiusReport
        {
            SourceService = "dns",
            SourceAnomaly = "down",
            RiskLevel = "critical",
            AffectedServices = Enumerable.Range(1, 6).Select(i =>
                new AffectedService
                {
                    Name = $"service-{i}",
                    Relationship = "same network",
                    CurrentStatus = "running"
                }).ToList()
        };

        var result = ContagionTrackerService.FormatBlastRadius(report);

        Assert.StartsWith("\U0001f534", result.Trim());
    }

    [Fact]
    public void BlastRadiusReport_DefaultRiskLevel()
    {
        var report = new BlastRadiusReport
        {
            SourceService = "test",
            SourceAnomaly = "test"
        };

        Assert.Equal("low", report.RiskLevel);
        Assert.Empty(report.AffectedServices);
    }

    [Fact]
    public void ContainerNetworkInfo_Properties()
    {
        var info = new ContainerNetworkInfo
        {
            Name = "nginx",
            State = "running",
            Image = "nginx:latest",
            Networks = ["web", "monitoring"],
            Ports = ["80/tcp", "443/tcp"],
            Labels = new Dictionary<string, string>
            {
                ["com.docker.compose.project"] = "homelab"
            }
        };

        Assert.Equal("nginx", info.Name);
        Assert.Equal(2, info.Networks.Count);
        Assert.Single(info.Labels);
    }
}
