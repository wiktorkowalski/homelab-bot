using HomelabBot.Plugins;

namespace HomelabBot.Tests;

public class LokiContainerResolutionTests
{
    private const string Container = "uberprints-server";
    private const string EmptyStreams = """{"status":"success","data":{"resultType":"streams","result":[]}}""";
    private const string EmptyVector = """{"status":"success","data":{"resultType":"vector","result":[]}}""";

    // The container has compose_service="server", so only the container_name selector finds it.
    private const string ServerStream =
        """{"status":"success","data":{"resultType":"streams","result":[{"stream":{"compose_service":"server","container_name":"uberprints-server"},"values":[["1760000000000000000","print job failed"]]}]}}""";

    [Fact]
    public async Task SearchLogs_NameMatchesOnlyContainerName_ReturnsLogLine()
    {
        var handler = new LokiStub.Handler(query => query.StartsWith("{container_name=\"uberprints-server\"}") ? ServerStream : EmptyStreams);

        var result = await LokiStub.CreatePlugin(handler).SearchLogs("failed", "1h", Container);

        Assert.Contains("**server**: print job failed", result);
        Assert.Equal("{container_name=\"uberprints-server\"} |~ \"(?i)failed\"", handler.Queries[^1]);
    }

    [Fact]
    public async Task CountErrorsByContainer_NameMatchesOnlyContainerName_ReturnsCount()
    {
        var handler = new LokiStub.Handler(query => query switch
        {
            _ when query.StartsWith("sum by (compose_service, container_name) (count_over_time({container_name=\"uberprints-server\"} ") =>
                """{"status":"success","data":{"resultType":"vector","result":[{"metric":{"compose_service":"server","container_name":"uberprints-server"},"value":[1760000000,"7"]}]}}""",
            "{container_name=\"uberprints-server\"}" => ServerStream,
            _ => EmptyStreams,
        });

        var result = await LokiStub.CreatePlugin(handler).CountErrorsByContainer("1h", Container);

        Assert.Contains("**server**: 7 errors", result);
        Assert.DoesNotContain("No error logs found", result);
    }

    [Fact]
    public async Task CountErrorsByContainer_StreamsWithoutComposeService_ShowUnderTheirContainerNames()
    {
        // The regex selector matches two plain docker run containers.
        var handler = new LokiStub.Handler(query => query switch
        {
            _ when query.StartsWith("sum by (compose_service, container_name) (count_over_time(") =>
                """{"status":"success","data":{"resultType":"vector","result":[{"metric":{"container_name":"web-1"},"value":[1760000000,"3"]},{"metric":{"container_name":"old-web"},"value":[1760000000,"2"]}]}}""",
            "{container_name=~\".*web.*\"}" => ServerStream,
            _ => EmptyStreams,
        });

        var result = await LokiStub.CreatePlugin(handler).CountErrorsByContainer("1h", "web");

        Assert.Contains("**web-1**: 3 errors", result);
        Assert.Contains("**old-web**: 2 errors", result);
        Assert.DoesNotContain("unknown", result);
    }

    [Fact]
    public async Task CountErrorsByContainer_ReplicasOfOneService_AddUpInOneRow()
    {
        var handler = new LokiStub.Handler(query => query switch
        {
            _ when query.StartsWith("sum by (compose_service, container_name) (count_over_time(") =>
                """{"status":"success","data":{"resultType":"vector","result":[{"metric":{"compose_service":"server","container_name":"server-1"},"value":[1760000000,"4"]},{"metric":{"compose_service":"server","container_name":"server-2"},"value":[1760000000,"1"]}]}}""",
            "{compose_service=\"server\"}" => ServerStream,
            _ => EmptyStreams,
        });

        var result = await LokiStub.CreatePlugin(handler).CountErrorsByContainer("1h", "server");

        Assert.Contains("**server**: 5 errors", result);
        Assert.Contains("Total: 5 errors across 1 containers", result);
    }

    [Fact]
    public async Task SearchLogs_NameMatchesNothing_ReturnsNoStreamMessage()
    {
        var handler = new LokiStub.Handler(_ => EmptyStreams);

        var result = await LokiStub.CreatePlugin(handler).SearchLogs("failed", "1h", "nope");

        Assert.Equal(LokiPlugin.BuildNoStreamMessage("nope", "1h"), result);

        // Every selector is probed; the search itself is not sent.
        Assert.Equal(LokiPlugin.BuildContainerSelectors("nope"), handler.Queries);
    }

    [Fact]
    public async Task CountErrorsByContainer_NameMatchesNothing_ReturnsNoStreamMessage()
    {
        var handler = new LokiStub.Handler(_ => EmptyStreams);

        var result = await LokiStub.CreatePlugin(handler).CountErrorsByContainer("6h", "nope");

        Assert.Equal(LokiPlugin.BuildNoStreamMessage("nope", "6h"), result);
        Assert.Equal(LokiPlugin.BuildContainerSelectors("nope"), handler.Queries);
    }

    [Fact]
    public async Task SearchLogs_ProbeFails_ReturnsErrorNotNoStreamMessage()
    {
        var handler = new LokiStub.Handler(_ => null);

        var result = await LokiStub.CreatePlugin(handler).SearchLogs("failed", "1h", Container);

        // The first failed probe ends the call: no later selector is tried.
        Assert.StartsWith("Error searching logs:", result);
        Assert.Single(handler.Queries);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task SearchLogs_NoContainerName_SendsAllServicesQueryWithoutProbe(string? containerName)
    {
        var handler = new LokiStub.Handler(_ => EmptyStreams);

        var result = await LokiStub.CreatePlugin(handler).SearchLogs("failed", "1h", containerName);

        Assert.Equal("No logs found containing 'failed' in the last 1h.", result);
        Assert.Equal(["{compose_service=~\".+\"} |~ \"(?i)failed\""], handler.Queries);
    }

    [Fact]
    public async Task CountErrorsByContainer_NoContainerName_SendsAllServicesQueryWithoutProbe()
    {
        var handler = new LokiStub.Handler(_ => EmptyVector);

        var result = await LokiStub.CreatePlugin(handler).CountErrorsByContainer("1h");

        Assert.Equal("No error logs found in the last 1h.", result);
        var query = Assert.Single(handler.Queries);
        Assert.StartsWith("sum by (compose_service) (count_over_time({compose_service=~\".+\"} |~ ", query);
    }
}
