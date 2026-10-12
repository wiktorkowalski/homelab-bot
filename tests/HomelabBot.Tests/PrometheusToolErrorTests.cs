using System.Net;
using System.Text;
using HomelabBot.Configuration;
using HomelabBot.Plugins;
using HomelabBot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace HomelabBot.Tests;

public class PrometheusToolErrorTests
{
    // Bodies as Prometheus 3 answered them on 2026-10-12.
    private const string ParseErrorBody =
        """{"status":"error","errorType":"bad_data","error":"invalid parameter \"query\": 1:5: parse error: unexpected end of input inside braces"}""";

    private const string ExecutionErrorBody =
        """{"status":"error","errorType":"execution","error":"many-to-many matching not allowed: matching labels must be unique on one side"}""";

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, ParseErrorBody, "invalid parameter \"query\": 1:5: parse error: unexpected end of input inside braces")]
    [InlineData(HttpStatusCode.UnprocessableEntity, ExecutionErrorBody, "many-to-many matching not allowed: matching labels must be unique on one side")]
    public async Task QueryPrometheus_QueryThatPrometheusRejects_ReturnsTheReasonFromPrometheus(
        HttpStatusCode status, string body, string reason)
    {
        var result = await CreatePlugin(status, body).QueryPrometheus("foo{");

        Assert.Equal($"Error: Prometheus rejected the query: {reason}", result);
        Assert.True(TelemetryFunctionFilter.IsFailureText(result));
    }

    [Fact]
    public async Task QueryPrometheus_ServerFault_AnswersWithTheStatusAndNotTheBody()
    {
        var result = await CreatePlugin(HttpStatusCode.InternalServerError, "internal: secret-detail").QueryPrometheus("up");

        Assert.Equal("Error executing query: Prometheus answered 500", result);
    }

    [Fact]
    public async Task QueryPrometheus_EmptyResult_IsNotAFailure()
    {
        var result = await CreatePlugin(
            HttpStatusCode.OK,
            """{"status":"success","data":{"resultType":"vector","result":[]}}""").QueryPrometheus("absent_metric");

        Assert.Equal("Query returned no results.", result);
        Assert.False(TelemetryFunctionFilter.IsFailureText(result));
    }

    [Theory]
    [InlineData("gateway said no", "gateway said no")]
    [InlineData("""{"status":"error"}""", """{"status":"error"}""")]
    [InlineData("  ", "no detail")]
    public void ExtractErrorDetail_BodyWithNoErrorField_IsTheBodyItself(string body, string expected)
    {
        Assert.Equal(expected, PrometheusQueryService.ExtractErrorDetail(body));
    }

    [Fact]
    public void ExtractErrorDetail_LongReason_IsCut()
    {
        var detail = PrometheusQueryService.ExtractErrorDetail($$"""{"error":"{{new string('x', 5000)}}"}""");

        Assert.InRange(detail.Length, 400, 600);
        Assert.EndsWith("...", detail);
    }

    [Fact]
    public async Task GetAvailableMetrics_ServerFault_IsAFailureAndNotAnEmptyList()
    {
        var result = await CreatePlugin(HttpStatusCode.ServiceUnavailable, "").GetAvailableMetrics();

        Assert.Equal("Error getting metrics: Prometheus answered 503", result);
    }

    private static PrometheusPlugin CreatePlugin(HttpStatusCode status, string body)
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("Default").Returns(new HttpClient(new FixedAnswer(status, body)));

        var service = new PrometheusQueryService(
            httpClientFactory,
            Options.Create(new PrometheusConfiguration { Host = "http://prometheus.test" }),
            NullLogger<PrometheusQueryService>.Instance);

        return new PrometheusPlugin(service, NullLogger<PrometheusPlugin>.Instance);
    }

    private sealed class FixedAnswer(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
