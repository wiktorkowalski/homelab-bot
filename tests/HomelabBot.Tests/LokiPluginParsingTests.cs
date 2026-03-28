using HomelabBot.Plugins;

namespace HomelabBot.Tests;

public class LokiPluginParsingTests
{
    [Theory]
    [InlineData("30m", 30, 0, 0)]
    [InlineData("2h", 0, 2, 0)]
    [InlineData("7d", 0, 0, 7)]
    public void ParseDuration_ValidInputs(string input, int minutes, int hours, int days)
    {
        var expected = new TimeSpan(days, hours, minutes, 0);
        var result = LokiPlugin.ParseDuration(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    public void ParseDuration_InvalidOrEmpty_ReturnsOneHour(string? input)
    {
        var result = LokiPlugin.ParseDuration(input!);
        Assert.Equal(TimeSpan.FromHours(1), result);
    }

    [Theory]
    [InlineData("30m", "30m")]
    [InlineData("120m", "2h")]
    [InlineData("2880m", "2d")]
    public void NormalizeDuration_ConvertsToAppropriateUnit(string input, string expected)
    {
        var result = LokiPlugin.NormalizeDuration(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ParseNanoseconds_ValidTimestamp_ConvertsCorrectly()
    {
        // 1_700_000_000_000_000_000 ns = 1_700_000_000_000 ms = 2023-11-14T22:13:20Z
        var ns = "1700000000000000000";
        var result = LokiPlugin.ParseNanoseconds(ns);
        var expected = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000).DateTime;
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("")]
    public void ParseNanoseconds_InvalidString_ReturnsFallback(string input)
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var result = LokiPlugin.ParseNanoseconds(input);
        var after = DateTime.UtcNow.AddSeconds(1);

        Assert.InRange(result, before, after);
    }

    [Fact]
    public void FormatLabels_PrefersContainerName_FallsToJob_ThenUnknown()
    {
        // Prefers container_name
        var labels1 = new Dictionary<string, string>
        {
            { "container_name", "traefik" },
            { "job", "docker" },
        };
        Assert.Equal("traefik", LokiPlugin.FormatLabels(labels1));

        // Falls back to job
        var labels2 = new Dictionary<string, string>
        {
            { "job", "docker" },
        };
        Assert.Equal("docker", LokiPlugin.FormatLabels(labels2));

        // Null or empty returns "{}"
        Assert.Equal("{}", LokiPlugin.FormatLabels(new Dictionary<string, string>()));
        Assert.Equal("{}", LokiPlugin.FormatLabels(null));

        // Unknown when labels present but no container_name or job
        var labels3 = new Dictionary<string, string>
        {
            { "host", "server1" },
        };
        Assert.Equal("unknown", LokiPlugin.FormatLabels(labels3));
    }
}
