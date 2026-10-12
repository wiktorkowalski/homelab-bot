using System.Net;
using System.Text;
using System.Web;
using HomelabBot.Configuration;
using HomelabBot.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Polly.Timeout;

namespace HomelabBot.Tests;

// A LokiPlugin over a scripted Loki.
internal static class LokiStub
{
    public static LokiPlugin CreatePlugin(HttpMessageHandler handler)
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(LokiPlugin.HttpClientName).Returns(new HttpClient(handler));

        return new LokiPlugin(
            httpClientFactory,
            Options.Create(new LokiConfiguration { Host = "http://loki.test" }),
            NullLogger<LokiPlugin>.Instance);
    }

    // What the resilience pipeline of the Loki client throws when an attempt runs out of time.
    public static Handler TimingOut() => new(_ => throw new TimeoutRejectedException());

    // Answers each request from its decoded LogQL query; a null answer becomes HTTP 500.
    internal sealed class Handler(Func<string, string?> respond) : HttpMessageHandler
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
