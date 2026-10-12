using HomelabBot.Plugins;
using HomelabBot.Services;

namespace HomelabBot.Tests;

public class LokiToolErrorTests
{
    private const string ParseError = "parse error at line 1, col 12: syntax error: unexpected IDENTIFIER";

    [Fact]
    public async Task QueryLogs_QueryThatLokiRejects_ReturnsTheReasonFromLoki()
    {
        var result = await LokiStub.CreatePlugin(LokiStub.Rejecting(ParseError)).QueryLogs("{job=x} |~ oops");

        Assert.Equal($"Error: Loki rejected the query: {ParseError}", result);
    }

    [Fact]
    public async Task QueryLogs_LongAnswerFromLoki_IsCut()
    {
        var result = await LokiStub.CreatePlugin(LokiStub.Rejecting(new string('x', 5000))).QueryLogs("{job=x}");

        Assert.InRange(result.Length, 400, 600);
        Assert.EndsWith("...", result);
    }

    [Fact]
    public async Task CountErrorsByContainer_ServerFault_KeepsTheStatusInTheAnswer()
    {
        var result = await LokiStub.CreatePlugin(LokiStub.Failing()).CountErrorsByContainer("1h");

        Assert.Equal("Error counting errors: Loki answered 500: no detail", result);
    }

    [Theory]
    [InlineData("30d")]
    [InlineData("soon")]
    public async Task DetectCriticalPatterns_InvalidOrTooLongRange_IsRejectedWithNoRequest(string since)
    {
        var handler = LokiStub.Failing();

        var result = await LokiStub.CreatePlugin(handler).DetectCriticalPatterns(since);

        Assert.Contains("time range", result, StringComparison.OrdinalIgnoreCase);
        Assert.True(TelemetryFunctionFilter.IsFailureText(result));
        Assert.Empty(handler.Queries);
    }

    [Fact]
    public async Task GetErrorCountsByContainerAsync_CancelledToken_SendsNoRequest()
    {
        var handler = LokiStub.Failing();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => LokiStub.CreatePlugin(handler).GetErrorCountsByContainerAsync("1h", ct: cts.Token));

        Assert.Empty(handler.Queries);
    }

    [Theory]
    [InlineData("Error executing query: boom", true)]
    [InlineData("Error: Loki rejected the query: parse error", true)]
    [InlineData("**Error counts by container** (last 1h):", false)]
    [InlineData("No error logs found in the last 1h.", false)]
    [InlineData(null, false)]
    public void TelemetryFilter_FailureText_IsATextThatStartsWithError(string? result, bool expected)
    {
        Assert.Equal(expected, TelemetryFunctionFilter.IsFailureText(result));
    }

    [Fact]
    public void TimeoutMessage_CountsAsAFailureInTelemetry()
    {
        Assert.True(TelemetryFunctionFilter.IsFailureText(LokiPlugin.BuildTimeoutMessage("7d")));
    }
}
