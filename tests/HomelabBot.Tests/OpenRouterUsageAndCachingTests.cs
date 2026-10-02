using System.ClientModel;
using System.ClientModel.Primitives;
using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using HomelabBot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI;

namespace HomelabBot.Tests;

public class OpenRouterUsageAndCachingTests
{
    private const string AnthropicModel = "anthropic/claude-sonnet-5.5";

    [Fact]
    public async Task ToolLoop_SumsUsageOfEveryRound_AndMarksEveryRequestForCaching()
    {
        var handler = new ScriptedHandler(
            ToolCallResponse(promptTokens: 1000, completionTokens: 20, cachedTokens: 0, cacheWriteTokens: 900),
            TextResponse(promptTokens: 1100, completionTokens: 50, cachedTokens: 900, cacheWriteTokens: 0));
        var kernel = BuildKernel(handler);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        var history = new ChatHistory();
        history.AddSystemMessage("You are a test bot.");
        history.AddUserMessage("ping please");
        var settings = new OpenAIPromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() };

        using var scope = TokenUsageScope.Begin();
        var response = await chat.GetChatMessageContentAsync(history, settings, kernel);
        var usage = scope.Snapshot();

        Assert.Equal("pong received", response.Content);
        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Equal(2, usage.Rounds);
        Assert.Equal(2100, usage.PromptTokens);
        Assert.Equal(70, usage.CompletionTokens);
        Assert.Equal(900, usage.CachedPromptTokens);
        Assert.Equal(900, usage.CacheWriteTokens);

        foreach (var body in handler.RequestBodies)
        {
            var request = JsonNode.Parse(body)!.AsObject();
            Assert.Equal("ephemeral", (string?)request["cache_control"]?["type"]);
            var systemContent = request["messages"]![0]!["content"]!.AsArray();
            Assert.Equal("You are a test bot.", (string?)systemContent[^1]!["text"]);
            Assert.Equal("ephemeral", (string?)systemContent[^1]!["cache_control"]?["type"]);
        }
    }

    [Fact]
    public async Task Usage_WithoutCacheDetails_LeavesCacheFieldsNull()
    {
        var handler = new ScriptedHandler(TextResponse(promptTokens: 10, completionTokens: 5, cachedTokens: null, cacheWriteTokens: null));
        var chat = BuildKernel(handler).GetRequiredService<IChatCompletionService>();

        using var scope = TokenUsageScope.Begin();
        await chat.GetChatMessageContentAsync("hi");
        var usage = scope.Snapshot();

        Assert.Equal(1, usage.Rounds);
        Assert.Equal(10, usage.PromptTokens);
        Assert.Null(usage.CachedPromptTokens);
        Assert.Null(usage.CacheWriteTokens);
    }

    [Fact]
    public void TryAddCacheControl_StringSystemContent_BecomesMarkedTextPart()
    {
        var body = BinaryData.FromString(
            """{"model":"anthropic/claude-sonnet-5.5","messages":[{"role":"system","content":"sys"},{"role":"user","content":"hi"}]}""");

        var result = OpenRouterPromptCachePolicy.TryAddCacheControl(body, out var skipReason);

        Assert.Null(skipReason);
        var request = JsonNode.Parse(result!.ToString())!.AsObject();
        Assert.Equal("ephemeral", (string?)request["cache_control"]?["type"]);
        var part = request["messages"]![0]!["content"]!.AsArray().Single()!;
        Assert.Equal("text", (string?)part["type"]);
        Assert.Equal("sys", (string?)part["text"]);
        Assert.Equal("ephemeral", (string?)part["cache_control"]?["type"]);
        Assert.Equal("hi", (string?)request["messages"]![1]!["content"]);
    }

    [Fact]
    public void TryAddCacheControl_ArraySystemContent_MarksLastPart()
    {
        var body = BinaryData.FromString(
            """{"model":"anthropic/claude-sonnet-5.5","messages":[{"role":"system","content":[{"type":"text","text":"a"},{"type":"text","text":"b"}]}]}""");

        var result = OpenRouterPromptCachePolicy.TryAddCacheControl(body, out _);

        var parts = JsonNode.Parse(result!.ToString())!["messages"]![0]!["content"]!.AsArray();
        Assert.Null(parts[0]!["cache_control"]);
        Assert.Equal("ephemeral", (string?)parts[1]!["cache_control"]?["type"]);
    }

    [Fact]
    public void TryAddCacheControl_NoSystemMessage_AddsTopLevelOnly()
    {
        var body = BinaryData.FromString("""{"model":"anthropic/claude-sonnet-5.5","messages":[{"role":"user","content":"hi"}]}""");

        var result = OpenRouterPromptCachePolicy.TryAddCacheControl(body, out _);

        var request = JsonNode.Parse(result!.ToString())!;
        Assert.Equal("ephemeral", (string?)request["cache_control"]?["type"]);
        Assert.Equal("hi", (string?)request["messages"]![0]!["content"]);
    }

    [Fact]
    public void TryAddCacheControl_NonAnthropicModel_PassesThroughSilently()
    {
        var body = BinaryData.FromString("""{"model":"openai/gpt-5","messages":[{"role":"system","content":"sys"}]}""");

        Assert.Null(OpenRouterPromptCachePolicy.TryAddCacheControl(body, out var skipReason));
        Assert.Null(skipReason);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"model":"anthropic/claude-sonnet-5.5"}""")]
    [InlineData("""{"model":"anthropic/claude-sonnet-5.5","messages":[{"role":"system","content":42}]}""")]
    public void TryAddCacheControl_UnexpectedShape_PassesThroughWithReason(string json)
    {
        Assert.Null(OpenRouterPromptCachePolicy.TryAddCacheControl(BinaryData.FromString(json), out var skipReason));
        Assert.NotNull(skipReason);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"id":"x"}""")]
    [InlineData("""{"usage":null}""")]
    public void TryParseUsage_NoUsage_ReturnsFalse(string json)
    {
        Assert.False(OpenRouterUsagePolicy.TryParseUsage(BinaryData.FromString(json), out _, out _, out _, out _));
    }

    private static Kernel BuildKernel(ScriptedHandler handler)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri("https://openrouter.test/api/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
        };
        options.AddPolicy(new OpenRouterPromptCachePolicy(NullLogger.Instance), PipelinePosition.PerCall);
        options.AddPolicy(new OpenRouterUsagePolicy(NullLogger.Instance), PipelinePosition.PerCall);
        var client = new OpenAIClient(new ApiKeyCredential("test-key"), options);

        var builder = Kernel.CreateBuilder();
        builder.AddOpenAIChatCompletion(AnthropicModel, client);
        builder.Plugins.AddFromObject(new PingPlugin(), "Test");
        return builder.Build();
    }

    private static string ToolCallResponse(int promptTokens, int completionTokens, int? cachedTokens, int? cacheWriteTokens) =>
        $$$"""
        {"id":"r1","object":"chat.completion","created":1,"model":"{{{AnthropicModel}}}",
         "choices":[{"index":0,"finish_reason":"tool_calls","message":{"role":"assistant","content":null,
           "tool_calls":[{"id":"call_1","type":"function","function":{"name":"Test-Ping","arguments":"{}"}}]}}],
         "usage":{{{Usage(promptTokens, completionTokens, cachedTokens, cacheWriteTokens)}}}}
        """;

    private static string TextResponse(int promptTokens, int completionTokens, int? cachedTokens, int? cacheWriteTokens) =>
        $$$"""
        {"id":"r2","object":"chat.completion","created":1,"model":"{{{AnthropicModel}}}",
         "choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"pong received"}}],
         "usage":{{{Usage(promptTokens, completionTokens, cachedTokens, cacheWriteTokens)}}}}
        """;

    private static string Usage(int promptTokens, int completionTokens, int? cachedTokens, int? cacheWriteTokens)
    {
        var details = cachedTokens is null && cacheWriteTokens is null
            ? ""
            : $$""","prompt_tokens_details":{"cached_tokens":{{cachedTokens ?? 0}},"cache_write_tokens":{{cacheWriteTokens ?? 0}}}""";
        return $$"""{"prompt_tokens":{{promptTokens}},"completion_tokens":{{completionTokens}},"total_tokens":{{promptTokens + completionTokens}}{{details}}}""";
    }

    private sealed class PingPlugin
    {
        [KernelFunction("Ping")]
        [Description("Returns pong.")]
        public string Ping() => "pong";
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;

        public ScriptedHandler(params string[] responses)
        {
            _responses = new Queue<string>(responses);
        }

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json"),
            };
        }
    }
}
