using System.Diagnostics;
using System.Text.Json.Nodes;
using HomelabBot.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace HomelabBot.Tests;

// What Langfuse receives for one tool interaction: the spans of the real LLM pipeline, passed
// through the enrichment processor as the OpenTelemetry SDK does at span end.
public sealed class LangfuseEnrichmentProcessorTests : IDisposable
{
    private readonly List<Activity> _spans = [];
    private readonly ActivityListener _listener;
    private readonly LangfuseEnrichmentProcessor _processor = new(NullLoggerFactory.Instance);
    private ActivityTraceId _traceId;

    public LangfuseEnrichmentProcessorTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is "HomelabBot.Chat" or TelemetryConstants.LlmActivitySourceName
                || s.Name.StartsWith("Microsoft.SemanticKernel", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                // A listener is process-wide and the processor rewrites tags, so spans of tests
                // that run in parallel must stay untouched.
                if (activity.TraceId != _traceId)
                {
                    return;
                }

                _processor.OnEnd(activity);
                lock (_spans)
                {
                    _spans.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task ToolInteraction_GivesLangfuseTwoGenerationsAndOneToolWithInputOutputAndUsage()
    {
        await RunToolInteractionAsync();

        var mine = Snapshot();
        var generations = mine.Where(s => s.Source.Name == TelemetryConstants.LlmActivitySourceName).ToList();
        Assert.Equal(2, generations.Count);

        // A model attribute is what makes Langfuse file a span as a generation.
        Assert.All(generations, g => Assert.Equal(ClaudeResponses.Model, g.GetTagItem("gen_ai.request.model")));
        Assert.All(generations, g => Assert.Equal("777", g.GetTagItem("langfuse.session.id")));

        // Round 1: system + user in, a tool call out.
        var input1 = Json(generations[0], "langfuse.observation.input").AsArray();
        Assert.Equal(["system", "user"], input1.Select(m => (string?)m!["role"]));
        Assert.Equal("You are a test bot.", (string?)input1[0]!["content"]);
        Assert.Equal("ping please", (string?)input1[1]!["content"]);
        var output1 = Json(generations[0], "langfuse.observation.output");
        Assert.Equal("assistant", (string?)output1["role"]);
        Assert.Equal("Test_Ping", (string?)output1["tool_calls"]![0]!["function"]!["name"]);

        // Round 2: the tool call and its result are part of the prompt, the text is the answer.
        var input2 = Json(generations[1], "langfuse.observation.input").AsArray();
        Assert.Equal(["system", "user", "assistant", "tool"], input2.Select(m => (string?)m!["role"]));
        Assert.Equal("toolu_1", (string?)input2[2]!["tool_calls"]![0]!["id"]);
        Assert.Equal("toolu_1", (string?)input2[3]!["tool_call_id"]);
        Assert.Equal("pong", (string?)input2[3]!["content"]);
        var output2 = Json(generations[1], "langfuse.observation.output");
        Assert.Equal("pong received", (string?)output2["content"]);

        // Keys do not overlap: input is the uncached part only.
        Assert.Equal(
            """{"input":100,"output":20,"cache_read_input_tokens":0,"cache_creation_input_tokens":900}""",
            generations[0].GetTagItem("langfuse.observation.usage_details"));
        Assert.Equal(
            """{"input":120,"output":50,"cache_read_input_tokens":900,"cache_creation_input_tokens":0}""",
            generations[1].GetTagItem("langfuse.observation.usage_details"));

        // The same text is not sent twice, and the tool schemas are not sent at all.
        Assert.All(generations, g => Assert.Null(g.GetTagItem("gen_ai.input.messages")));
        Assert.All(generations, g => Assert.Null(g.GetTagItem("gen_ai.output.messages")));
        Assert.All(generations, g => Assert.Null(g.GetTagItem("gen_ai.tool.definitions")));

        var tool = Assert.Single(mine, s => (string?)s.GetTagItem("gen_ai.operation.name") == "execute_tool");
        Assert.Equal("tool", tool.GetTagItem("langfuse.observation.type"));
        Assert.Equal("pong", tool.GetTagItem("langfuse.observation.output"));
        Assert.Equal("777", tool.GetTagItem("langfuse.session.id"));
    }

    [Fact]
    public void SessionId_ComesFromAnyAncestor_NotOnlyTheParent()
    {
        var source = TelemetryConstants.ChatActivitySource;
        using var root = source.StartActivity("Chat")!;
        _traceId = root.TraceId;
        root.SetTag("langfuse.session.id", "42");
        root.SetTag("langfuse.user.id", "7");
        using var middle = source.StartActivity("SmartRecall")!;
        using var leaf = source.StartActivity("leaf")!;

        leaf.Stop();

        Assert.Equal("42", leaf.GetTagItem("langfuse.session.id"));
        Assert.Equal("7", leaf.GetTagItem("langfuse.user.id"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"role":"user"}""")]
    [InlineData("[]")]
    public void ToChatFormat_UnusableInput_ReturnsNull(string? json)
    {
        Assert.Null(LangfuseEnrichmentProcessor.ToChatFormat(json));
    }

    [Fact]
    public void ToChatFormat_SkipsThinkingParts_AndKeepsStructuredToolResults()
    {
        var chat = LangfuseEnrichmentProcessor.ToChatFormat(
            """
            [{"role":"assistant","parts":[
               {"type":"Microsoft.Extensions.AI.TextReasoningContent","content":{"text":"","protectedData":"SIG"}},
               {"type":"text","content":"checking"},
               {"type":"tool_call","id":"t1","name":"Docker_List","arguments":{"all":true}}]},
             {"role":"tool","parts":[{"type":"tool_call_response","id":"t1","response":{"count":3}}]}]
            """)!;

        Assert.Equal(2, chat.Count);
        Assert.Equal("checking", (string?)chat[0]!["content"]);
        Assert.Equal("""{"all":true}""", (string?)chat[0]!["tool_calls"]![0]!["function"]!["arguments"]);
        Assert.Equal("""{"count":3}""", (string?)chat[1]!["content"]);
        Assert.DoesNotContain("SIG", chat.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void EnrichGeneration_WithoutGenAiAttributes_LeavesTheSpanAlone()
    {
        using var source = new ActivitySource(TelemetryConstants.LlmActivitySourceName);
        using var span = source.StartActivity("chat x")!;

        LangfuseEnrichmentProcessor.EnrichGeneration(span);

        Assert.Null(span.GetTagItem("langfuse.observation.input"));
        Assert.Null(span.GetTagItem("langfuse.observation.output"));
        Assert.Null(span.GetTagItem("langfuse.observation.usage_details"));
    }

    private static JsonNode Json(Activity span, string tag) => JsonNode.Parse((string)span.GetTagItem(tag)!)!;

    private List<Activity> Snapshot()
    {
        lock (_spans)
        {
            return _spans.OrderBy(s => s.StartTimeUtc).ToList();
        }
    }

    private async Task RunToolInteractionAsync()
    {
        var handler = new ScriptedClaudeHandler()
            .Enqueue(ClaudeResponses.ToolUse(input: 100, output: 20, cacheWrite: 900, cacheRead: 0))
            .Enqueue(ClaudeResponses.Text(input: 120, output: 50, cacheWrite: 0, cacheRead: 900));
        var kernel = ClaudeTestKernel.Build(handler.CreateClient(), new PingPlugin(), NullLogger.Instance);

        // The root span KernelService opens for every interaction.
        using var root = TelemetryConstants.ChatActivitySource.StartActivity("Chat", ActivityKind.Server)!;
        _traceId = root.TraceId;
        root.SetTag("langfuse.session.id", "777");

        await kernel.GetRequiredService<IChatCompletionService>().GetChatMessageContentAsync(
            ClaudeTestKernel.NewHistory(),
            new PromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() },
            kernel);
    }
}
