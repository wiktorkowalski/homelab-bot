using System.Net;
using System.Text;
using System.Web;
using HomelabBot.Configuration;
using HomelabBot.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

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
        var handler = new LokiHandler(query => query.StartsWith("{container_name=\"uberprints-server\"}") ? ServerStream : EmptyStreams);

        var result = await CreatePlugin(handler).SearchLogs("failed", "1h", Container);

        Assert.Contains("**server**: print job failed", result);
        Assert.Equal("{container_name=\"uberprints-server\"} |~ \"(?i)failed\"", handler.Queries[^1]);
    }

    [Fact]
    public async Task CountErrorsByContainer_NameMatchesOnlyContainerName_ReturnsCount()
    {
        var handler = new LokiHandler(query => query switch
        {
            _ when query.StartsWith("sum by (compose_service) (count_over_time({container_name=\"uberprints-server\"} ") =>
                """{"status":"success","data":{"resultType":"vector","result":[{"metric":{"compose_service":"server"},"value":[1760000000,"7"]}]}}""",
            "{container_name=\"uberprints-server\"}" => ServerStream,
            _ => EmptyStreams,
        });

        var result = await CreatePlugin(handler).CountErrorsByContainer("1h", Container);

        Assert.Contains("**server**: 7 errors", result);
        Assert.DoesNotContain("No error logs found", result);
    }

    [Fact]
    public async Task CountErrorsByContainer_StreamWithoutComposeService_UsesRequestedName()
    {
        var handler = new LokiHandler(query => query switch
        {
            _ when query.StartsWith("sum by (compose_service) (count_over_time(") =>
                """{"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1760000000,"3"]}]}}""",
            "{container_name=\"uberprints-server\"}" => ServerStream,
            _ => EmptyStreams,
        });

        var result = await CreatePlugin(handler).CountErrorsByContainer("1h", Container);

        Assert.Contains("**uberprints-server**: 3 errors", result);
        Assert.DoesNotContain("unknown", result);
    }

    [Fact]
    public async Task SearchLogs_NameMatchesNothing_ReturnsNoStreamMessage()
    {
        var handler = new LokiHandler(_ => EmptyStreams);

        var result = await CreatePlugin(handler).SearchLogs("failed", "1h", "nope");

        Assert.Equal(LokiPlugin.BuildNoStreamMessage("nope", "1h"), result);
        Assert.DoesNotContain("No logs found containing", result);

        // Every selector is probed; the search itself is not sent.
        Assert.Equal(LokiPlugin.BuildContainerSelectors("nope"), handler.Queries);
    }

    [Fact]
    public async Task CountErrorsByContainer_NameMatchesNothing_ReturnsNoStreamMessage()
    {
        var handler = new LokiHandler(query => query.StartsWith("sum by") ? EmptyVector : EmptyStreams);

        var result = await CreatePlugin(handler).CountErrorsByContainer("6h", "nope");

        Assert.Equal(LokiPlugin.BuildNoStreamMessage("nope", "6h"), result);
        Assert.DoesNotContain("No error logs found", result);
        Assert.Equal(LokiPlugin.BuildContainerSelectors("nope"), handler.Queries);
    }

    [Fact]
    public async Task SearchLogs_ProbeFails_ReturnsErrorNotNoStreamMessage()
    {
        var handler = new LokiHandler(_ => null);

        var result = await CreatePlugin(handler).SearchLogs("failed", "1h", Container);

        Assert.StartsWith("Error searching logs:", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task SearchLogs_NoContainerName_SendsAllServicesQueryWithoutProbe(string? containerName)
    {
        var handler = new LokiHandler(_ => EmptyStreams);

        var result = await CreatePlugin(handler).SearchLogs("failed", "1h", containerName);

        Assert.Equal("No logs found containing 'failed' in the last 1h.", result);
        Assert.Equal(["{compose_service=~\".+\"} |~ \"(?i)failed\""], handler.Queries);
    }

    [Fact]
    public async Task CountErrorsByContainer_NoContainerName_SendsAllServicesQueryWithoutProbe()
    {
        var handler = new LokiHandler(_ => EmptyVector);

        var result = await CreatePlugin(handler).CountErrorsByContainer("1h");

        Assert.Equal("No error logs found in the last 1h.", result);
        var query = Assert.Single(handler.Queries);
        Assert.StartsWith("sum by (compose_service) (count_over_time({compose_service=~\".+\"} |~ ", query);
    }

    private static LokiPlugin CreatePlugin(LokiHandler handler)
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("Default").Returns(new HttpClient(handler));

        return new LokiPlugin(
            httpClientFactory,
            Options.Create(new LokiConfiguration { Host = "http://loki.test" }),
            NullLogger<LokiPlugin>.Instance);
    }

    // Answers each request from its decoded LogQL query; a null answer becomes HTTP 500.
    private sealed class LokiHandler(Func<string, string?> respond) : HttpMessageHandler
    {
        public List<string> Queries { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query)["query"] ?? "";
            Queries.Add(query);

            var body = respond(query);
            var response = body == null
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

            return Task.FromResult(response);
        }
    }
}
