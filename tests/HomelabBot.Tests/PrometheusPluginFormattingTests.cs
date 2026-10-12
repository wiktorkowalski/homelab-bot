using System.Text.Json;
using HomelabBot.Models.Prometheus;
using HomelabBot.Plugins;

namespace HomelabBot.Tests;

public class PrometheusPluginFormattingTests
{
    [Fact]
    public void FormatLabels_NameOnly_ReturnsName()
    {
        var labels = new Dictionary<string, string>
        {
            { "__name__", "node_load1" },
        };

        Assert.Equal("node_load1", PrometheusPlugin.FormatLabels(labels));
    }

    [Fact]
    public void FormatLabels_NameAndLabels_PrintsEveryLabelSortedByKey()
    {
        var labels = new Dictionary<string, string>
        {
            { "mountpoint", "/" },
            { "job", "node" },
            { "__name__", "node_filesystem_avail_bytes" },
            { "instance", "ubuntu:9100" },
            { "fstype", "ext4" },
            { "device", "/dev/sda1" },
        };

        Assert.Equal(
            "node_filesystem_avail_bytes{device=/dev/sda1, fstype=ext4, instance=ubuntu:9100, job=node, mountpoint=/}",
            PrometheusPlugin.FormatLabels(labels));
    }

    [Fact]
    public void FormatLabels_NoName_ReturnsBracedLabels()
    {
        var labels = new Dictionary<string, string>
        {
            { "a", "b" },
        };

        Assert.Equal("{a=b}", PrometheusPlugin.FormatLabels(labels));
    }

    [Fact]
    public void FormatLabels_NullOrEmpty_ReturnsEmptyBraces()
    {
        Assert.Equal("{}", PrometheusPlugin.FormatLabels(new Dictionary<string, string>()));
        Assert.Equal("{}", PrometheusPlugin.FormatLabels(null));
    }

    [Fact]
    public void FormatQueryResult_CountByName_KeepsMetricName()
    {
        var results = new List<PrometheusResult>
        {
            Row("up", "12"),
            Row("node_load1", "1"),
        };

        var output = PrometheusPlugin.FormatQueryResult("count by (__name__) ({job=\"node\"})", results);

        Assert.Contains("- up: **12**", output);
        Assert.Contains("- node_load1: **1**", output);
        Assert.DoesNotContain("{}", output);
    }

    [Fact]
    public void FormatQueryResult_MoreThan20Series_ShowsTruncationLine()
    {
        var results = Enumerable.Range(0, 25).Select(i => Row($"metric_{i}", "1")).ToList();

        var output = PrometheusPlugin.FormatQueryResult("{job=\"node\"}", results);

        Assert.Equal(20, CountRows(output));
        Assert.Contains("showing 20 of 25 series", output);
    }

    [Fact]
    public void FormatQueryResult_Exactly20Series_NoTruncationLine()
    {
        var results = Enumerable.Range(0, 20).Select(i => Row($"metric_{i}", "1")).ToList();

        var output = PrometheusPlugin.FormatQueryResult("{job=\"node\"}", results);

        Assert.Equal(20, CountRows(output));
        Assert.DoesNotContain("showing", output);
    }

    private static PrometheusResult Row(string name, string value) => new()
    {
        Metric = new Dictionary<string, string> { { "__name__", name } },
        Value = JsonSerializer.Deserialize<JsonElement[]>($"[1700000000, \"{value}\"]"),
    };

    private static int CountRows(string output) =>
        output.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal));
}
