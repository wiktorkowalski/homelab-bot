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
    public void FormatLabels_MoreLabelsThanTheCap_StatesHowManyWereLeftOut()
    {
        var labels = Enumerable.Range(0, PrometheusPlugin.MaxLabelsPerSeries + 3)
            .ToDictionary(i => $"label_{i:D2}", i => "v");
        labels["__name__"] = "container_memory_usage_bytes";

        var output = PrometheusPlugin.FormatLabels(labels);

        Assert.StartsWith("container_memory_usage_bytes{label_00=v, ", output);
        Assert.EndsWith($"label_{PrometheusPlugin.MaxLabelsPerSeries - 1:D2}=v, +3 more labels}}", output);
    }

    [Fact]
    public void FormatQueryResult_MoreSeriesThanTheCap_StatesTheCutInTheFirstLine()
    {
        var total = PrometheusPlugin.MaxSeries + 5;

        var output = PrometheusPlugin.FormatQueryResult("{job=\"node\"}", Rows(total));

        Assert.Equal(PrometheusPlugin.MaxSeries, CountRows(output));
        Assert.StartsWith($"Query: `{{job=\"node\"}}` (showing {PrometheusPlugin.MaxSeries} of {total} series)\n", output);
    }

    [Fact]
    public void FormatQueryResult_SeriesAtTheCap_NoCutNotice()
    {
        var output = PrometheusPlugin.FormatQueryResult("{job=\"node\"}", Rows(PrometheusPlugin.MaxSeries));

        Assert.Equal(PrometheusPlugin.MaxSeries, CountRows(output));
        Assert.DoesNotContain("showing", output);
    }

    private static List<PrometheusResult> Rows(int count) =>
        Enumerable.Range(0, count).Select(i => Row($"metric_{i}", "1")).ToList();

    private static PrometheusResult Row(string name, string value) => new()
    {
        Metric = new Dictionary<string, string> { { "__name__", name } },
        Value = JsonSerializer.Deserialize<JsonElement[]>($"[1700000000, \"{value}\"]"),
    };

    private static int CountRows(string output) =>
        output.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal));
}
