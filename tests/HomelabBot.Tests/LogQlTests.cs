using System.Text.RegularExpressions;
using HomelabBot.Helpers;
using HomelabBot.Plugins;

namespace HomelabBot.Tests;

public class LogQlTests
{
    [Theory]
    [InlineData("[Error", @"\[Error")]
    [InlineData("a.b*c?", @"a\.b\*c\?")]
    [InlineData("(x|y){2}^$", @"\(x\|y\)\{2\}\^\$")]
    [InlineData(@"C:\path", @"C:\\path")]
    [InlineData("plain text #1", "plain text #1")]
    public void EscapeRegex_EscapesOnlyRe2MetaChars(string input, string expected)
    {
        Assert.Equal(expected, LogQl.EscapeRegex(input));
    }

    [Theory]
    [InlineData("[Error", "[Error")]
    [InlineData("a.b*c+(d)", "a.b*c+(d)")]
    [InlineData("say \"hi\"", "say \"hi\"")]
    [InlineData(@"back\slash", @"back\slash")]
    public void EscapeRegex_ResultMatchesInputLiterally(string input, string line)
    {
        Assert.Matches(new Regex(LogQl.EscapeRegex(input)), line);
    }

    [Theory]
    [InlineData("abc", "\"abc\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"a\b", "\"a\\\\b\"")]
    [InlineData("line1\nline2", "\"line1\\nline2\"")]
    public void QuoteString_EscapesForLogQlStringLiteral(string input, string expected)
    {
        Assert.Equal(expected, LogQl.QuoteString(input));
    }

    [Fact]
    public void BuildSearchQuery_BracketIsRegexEscapedAndStringEscaped()
    {
        var query = LokiPlugin.BuildSearchQuery("[Error", null);

        // LogQL string "\\[" unquotes to regex \[ which matches a literal '['.
        Assert.Equal("{compose_service=~\".+\"} |~ \"(?i)\\\\[Error\"", query);
    }

    [Fact]
    public void BuildSearchQuery_QuoteAndBackslashAreEscaped()
    {
        var query = LokiPlugin.BuildSearchQuery("path \"C:\\x\"", "web");

        Assert.Equal("{compose_service=\"web\"} |~ \"(?i)path \\\"C:\\\\\\\\x\\\"\"", query);
    }

    [Fact]
    public void BuildServiceSelector_EscapesContainerName()
    {
        Assert.Equal("{compose_service=\"we\\\"b\"}", LokiPlugin.BuildServiceSelector("we\"b"));
    }

    [Fact]
    public void BuildContainerSelectors_EscapesNameForStringAndRegex()
    {
        var selectors = LokiPlugin.BuildContainerSelectors("my.app\"x");

        Assert.Equal(
            [
                "{compose_service=\"my.app\\\"x\"}",
                "{container_name=\"my.app\\\"x\"}",
                "{container_name=~\".*my\\\\.app\\\"x.*\"}",
                "{compose_service=~\".*my\\\\.app\\\"x.*\"}",
            ],
            selectors);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(100, 100)]
    [InlineData(5000, 5000)]
    [InlineData(100000, 5000)]
    public void ClampLimit_KeepsLimitWithinLokiBounds(int input, int expected)
    {
        Assert.Equal(expected, LogQl.ClampLimit(input));
    }

    [Theory]
    [InlineData(null, 60)]
    [InlineData("", 60)]
    [InlineData("30m", 30)]
    [InlineData("1h", 60)]
    [InlineData("24h", 1440)]
    [InlineData("7d", 10080)]
    [InlineData(" 2h ", 120)]
    [InlineData("168h", 10080)]
    [InlineData("10080m", 10080)]
    public void TryParseDuration_ValidInputs(string? input, int expectedMinutes)
    {
        Assert.True(FormattingHelpers.TryParseDuration(input, LogQl.MaxLookback, out var duration, out var error));
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), duration);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1w")]
    [InlineData("h")]
    [InlineData("0h")]
    [InlineData("-1h")]
    [InlineData("1.5h")]
    [InlineData("1H")]
    [InlineData("8d")]
    [InlineData("169h")]
    [InlineData("10081m")]
    [InlineData("2147483647d")]
    [InlineData("99999999999d")]
    public void TryParseDuration_InvalidOrTooLong_ReturnsError(string input)
    {
        Assert.False(FormattingHelpers.TryParseDuration(input, LogQl.MaxLookback, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData(30, "30m")]
    [InlineData(90, "90m")]
    [InlineData(60, "1h")]
    [InlineData(1440, "1d")]
    [InlineData(2880, "2d")]
    [InlineData(1500, "25h")]
    public void FormatCompactDuration_UsesLargestExactUnit(int minutes, string expected)
    {
        Assert.Equal(expected, FormattingHelpers.FormatCompactDuration(TimeSpan.FromMinutes(minutes)));
    }

    [Theory]
    [InlineData("13:37:05 [ERR] HomelabBot.Services.Foo: boom")]
    [InlineData("13:37:05 [FTL] HomelabBot.Program: host crashed")]
    [InlineData("[2026-10-02 13:33:01.412 ERR] MinecraftServerBot.Services.X failed")]
    [InlineData("[v5.0] [Error] RssSyncService: sync failed")]
    [InlineData("2026-10-02 13:33:01.4|Error|RssSyncService|sync failed")]
    [InlineData("2026-10-02 13:00:00.123 ERROR (MainThread) [homeassistant.components.mqtt] lost connection")]
    [InlineData("2026-10-02 13:00:00.123 CRITICAL (MainThread) [homeassistant] boom")]
    [InlineData("ERROR:root:something broke")]
    [InlineData("2026-10-02 13:00:00.123 UTC [61] ERROR:  relation \"x\" does not exist")]
    [InlineData("2026-10-02 13:00:00.123 UTC [61] FATAL:  password authentication failed")]
    [InlineData("2026-10-02 13:00:00.123 UTC [61] PANIC:  could not write to file")]
    [InlineData("fail: Microsoft.AspNetCore.Server.Kestrel[13]")]
    [InlineData("crit: Microsoft.Hosting.Lifetime[0]")]
    [InlineData("[2026-10-02 13:00:00] error: \tz2m: MQTT failed to connect")]
    [InlineData("error: z2m: Adapter disconnected")]
    [InlineData("Zigbee2MQTT:error 2026-10-02 13:00:00: Failed to ping")]
    [InlineData("{\"level\":\"error\",\"message\":\"scrape failed\"}")]
    [InlineData("{\"time\":1,\"level\": \"FATAL\",\"msg\":\"x\"}")]
    [InlineData("{\"level\":50,\"time\":1,\"msg\":\"request failed\"}")]
    [InlineData("{\"level\":60,\"time\":1,\"msg\":\"crash\"}")]
    [InlineData("{\"@t\":\"2026-10-02T13:00:00Z\",\"@mt\":\"x\",\"@l\":\"Error\"}")]
    [InlineData("{\"@t\":\"2026-10-02T13:00:00Z\",\"@l\":\"Fatal\"}")]
    [InlineData("ts=2026-10-02T13:00:00Z level=error msg=\"x\"")]
    [InlineData("level=error ts=2026-10-02T13:37:08Z caller=errors.go:26")]
    [InlineData("time=\"x\" level=\"critical\" msg=\"x\"")]
    [InlineData("t=x lvl=eror msg=x")]
    public void ErrorLevelRegex_MatchesErrorLevelLines(string line)
    {
        Assert.Matches(new Regex(LogQl.ErrorLevelRegex), line);
    }

    [Theory]
    [InlineData("13:37:05 [INF] HomelabBot.Services.ContagionTrackerService: critical dependency check ok")]
    [InlineData("13:37:05 [INF] HomelabBot.Plugins.LokiPlugin: Counting errors by container")]
    [InlineData("13:37:05 [WRN] X: request failed, retrying after error")]
    [InlineData("13:37:05 [INF] HomelabBot.Plugins.LokiPlugin: search for [Error] returned 3 lines")]
    [InlineData("13:37:05 [INF] HomelabBot.Services.SomeLongServiceName: got ERROR from upstream")]
    [InlineData("[2026-10-02 13:33:01.412 WRN] MinecraftServerBot.Services.DiscordBotService Discord socket error")]
    [InlineData("{\"level\":\"info\",\"message\":\"Error while scraping\"}")]
    [InlineData("{\"level\":30,\"msg\":\"error count 50\"}")]
    [InlineData("{\"@l\":\"Information\",\"@mt\":\"Error budget ok\"}")]
    [InlineData("level=info msg=\"error rate ok\"")]
    [InlineData("level=warn msg=x")]
    [InlineData("info: Microsoft.Hosting.Lifetime[0] failed to fail: nothing")]
    [InlineData("2026-10-02 13:00:00.123 INFO (MainThread) [homeassistant] error handler registered")]
    [InlineData("2026-10-02 13:00:00.123 UTC [61] LOG:  checkpoint complete, no error")]
    [InlineData("[2026-10-02 13:00:00] info: \tz2m: error count reset")]
    public void ErrorLevelRegex_IgnoresLowerLevelLinesWithErrorWords(string line)
    {
        Assert.DoesNotMatch(new Regex(LogQl.ErrorLevelRegex), line);
    }

    [Fact]
    public void BuildErrorLevelCountQuery_EmbedsQuotedLevelRegex()
    {
        var query = LokiPlugin.BuildErrorLevelCountQuery("24h", "homelab-bot");

        Assert.StartsWith("sum by (compose_service) (count_over_time({compose_service=\"homelab-bot\"} |~ \"(?:^.{0,40}\\\\[(?i:err", query);
        Assert.EndsWith(" [24h]))", query);
    }
}
