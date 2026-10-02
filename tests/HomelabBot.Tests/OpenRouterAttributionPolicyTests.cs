using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using HomelabBot.Services;
using OpenAI;
using OpenAI.Chat;

namespace HomelabBot.Tests;

public class OpenRouterAttributionPolicyTests
{
    [Fact]
    public async Task ChatRequest_CarriesAttributionHeaders()
    {
        var handler = new CapturingHandler();
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri("https://openrouter.test/api/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
        };
        options.AddPolicy(new OpenRouterAttributionPolicy(), PipelinePosition.PerCall);
        var client = new OpenAIClient(new ApiKeyCredential("test-key"), options).GetChatClient("test-model");

        await Assert.ThrowsAnyAsync<Exception>(() => client.CompleteChatAsync("hi"));

        Assert.NotNull(handler.Request);
        Assert.Equal("https://github.com/wiktorkowalski/homelab-bot", handler.Request.Headers.GetValues("HTTP-Referer").Single());
        Assert.Equal("homelab-bot", handler.Request.Headers.GetValues("X-Title").Single());
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        }
    }
}
