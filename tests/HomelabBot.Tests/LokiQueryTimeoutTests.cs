using System.Net;
using HomelabBot.Extensions;
using HomelabBot.Helpers;
using HomelabBot.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace HomelabBot.Tests;

public class LokiQueryTimeoutTests
{
    [Fact]
    public void LokiClient_AttemptTimeout_CoversTheSlowestAllowedScan()
    {
        var options = ResilienceOptions();

        Assert.Equal(LogQl.QueryTimeout, options.AttemptTimeout.Timeout);
        Assert.True(options.TotalRequestTimeout.Timeout > options.AttemptTimeout.Timeout);
    }

    [Fact]
    public async Task LokiClient_Retry_SkipsATimeoutAndKeepsATransientFault()
    {
        var shouldHandle = ResilienceOptions().Retry.ShouldHandle;

        Assert.False(await shouldHandle(RetryArguments(Outcome.FromException<HttpResponseMessage>(new TimeoutRejectedException()))));
        Assert.False(await shouldHandle(RetryArguments(Outcome.FromResult(new HttpResponseMessage(HttpStatusCode.GatewayTimeout)))));
        Assert.False(await shouldHandle(RetryArguments(Outcome.FromResult(new HttpResponseMessage(HttpStatusCode.RequestTimeout)))));
        Assert.True(await shouldHandle(RetryArguments(Outcome.FromException<HttpResponseMessage>(new HttpRequestException("refused")))));
        Assert.True(await shouldHandle(RetryArguments(Outcome.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))));
    }

    [Fact]
    public async Task LokiClient_TimedOutRequest_IsSentOnce()
    {
        // Sending through the real pipeline also proves that Polly accepts the options.
        var handler = LokiStub.TimingOut();
        var services = new ServiceCollection().AddLokiHttpClient();
        services.AddHttpClient(LokiPlugin.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        var client = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient(LokiPlugin.HttpClientName);

        await Assert.ThrowsAsync<TimeoutRejectedException>(() => client.GetAsync("http://loki.test/loki/api/v1/query?query=x"));

        Assert.Single(handler.Queries);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("uberprints-server")]
    public async Task CountErrorsByContainer_Timeout_AsksForAShorterRangeAfterOneRequest(string? containerName)
    {
        var handler = LokiStub.TimingOut();

        var result = await LokiStub.CreatePlugin(handler).CountErrorsByContainer("7d", containerName);

        Assert.Equal(LokiPlugin.BuildTimeoutMessage("7d"), result);
        Assert.Single(handler.Queries);
    }

    [Fact]
    public async Task SearchLogs_Timeout_AsksForAShorterRange()
    {
        var handler = LokiStub.TimingOut();

        var result = await LokiStub.CreatePlugin(handler).SearchLogs("failed", "3d");

        Assert.Equal(LokiPlugin.BuildTimeoutMessage("3d"), result);
    }

    [Fact]
    public async Task QueryLogs_Timeout_AsksForAShorterRange()
    {
        var handler = LokiStub.TimingOut();

        var result = await LokiStub.CreatePlugin(handler).QueryLogs("{job=\"x\"}", since: "7d");

        Assert.Equal(LokiPlugin.BuildTimeoutMessage("7d"), result);
    }

    [Fact]
    public async Task DetectCriticalPatterns_Timeout_AsksForAShorterRange()
    {
        var result = await LokiStub.CreatePlugin(LokiStub.TimingOut()).DetectCriticalPatterns("7d");

        Assert.Equal(LokiPlugin.BuildTimeoutMessage("7d"), result);
    }

    [Fact]
    public async Task GetContainerLogs_Timeout_DoesNotTryTheNextSelector()
    {
        var handler = LokiStub.TimingOut();

        var result = await LokiStub.CreatePlugin(handler).GetContainerLogsFromLoki("uberprints-server", "7d");

        Assert.Equal(LokiPlugin.BuildTimeoutMessage("7d"), result);
        Assert.Single(handler.Queries);
    }

    private static HttpStandardResilienceOptions ResilienceOptions() =>
        new ServiceCollection().AddLokiHttpClient().BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>()
            .Get($"{LokiPlugin.HttpClientName}-standard");

    private static RetryPredicateArguments<HttpResponseMessage> RetryArguments(Outcome<HttpResponseMessage> outcome) =>
        new(ResilienceContextPool.Shared.Get(), outcome, attemptNumber: 0);
}
