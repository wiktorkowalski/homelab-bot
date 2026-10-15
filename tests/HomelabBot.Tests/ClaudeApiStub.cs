using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Models.Messages;
using HomelabBot.Configuration;
using HomelabBot.Data;
using HomelabBot.Plugins;
using HomelabBot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace HomelabBot.Tests;

// Canned Messages API responses in the wire format of https://api.anthropic.com/v1/messages.
internal static class ClaudeResponses
{
    public const string Model = "claude-haiku-5-5";
    public const string Signature = "SIG_ABC";

    // Round 1 of a tool loop: an (empty, signed) thinking block followed by one or more tool calls.
    public static string ToolUse(
        int input = 100, int output = 20, int? cacheWrite = 900, int? cacheRead = 0, params string[] tools)
    {
        tools = tools.Length == 0 ? ["Test_Ping"] : tools;
        var calls = string.Join(",", tools.Select((name, i) => ToolCall(i + 1, name, "{}")));
        return ToolUseMessage(calls, input, output, cacheWrite, cacheRead);
    }

    // One tool call that carries arguments, for tools with required parameters.
    public static string ToolUseWithInput(string tool, string inputJson) =>
        ToolUseMessage(ToolCall(1, tool, inputJson), 100, 20, 900, 0);

    public static string Text(
        string text = "pong received", int input = 120, int output = 50, int? cacheWrite = 0, int? cacheRead = 900) =>
        $$"""
        {"id":"msg_text","type":"message","role":"assistant","model":"{{Model}}",
         "content":[{"type":"text","text":{{JsonSerializer.Serialize(text)}}}],
         "stop_reason":"end_turn","stop_sequence":null,
         "usage":{{Usage(input, output, cacheWrite, cacheRead)}}}
        """;

    // One final message with several text blocks, for example around a server tool or a citation.
    public static string TextBlocks(params string[] texts) =>
        $$"""
        {"id":"msg_texts","type":"message","role":"assistant","model":"{{Model}}",
         "content":[{"type":"thinking","thinking":"private reasoning","signature":"{{Signature}}"},
                    {{string.Join(",", texts.Select(t => $$"""{"type":"text","text":{{JsonSerializer.Serialize(t)}}}"""))}}],
         "stop_reason":"end_turn","stop_sequence":null,
         "usage":{{Usage(120, 50, 0, 900)}}}
        """;

    // A tool round where the model writes a line of text before the tool call.
    public static string TextThenToolUse(string text, string tool = "Test_Ping") =>
        $$$"""
        {"id":"msg_text_tool","type":"message","role":"assistant","model":"{{{Model}}}",
         "content":[{"type":"text","text":{{{JsonSerializer.Serialize(text)}}}},
                    {"type":"tool_use","id":"toolu_1","name":"{{{tool}}}","input":{},"caller":{"type":"direct"}}],
         "stop_reason":"tool_use","stop_sequence":null,
         "usage":{{{Usage(100, 20, 900, 0)}}}}
        """;

    // The classifier stopped the model after it had already written a tool call.
    public static string RefusalAfterToolUse(string tool) =>
        $$$"""
        {"id":"msg_refusal_tool","type":"message","role":"assistant","model":"{{{Model}}}",
         "content":[{"type":"thinking","thinking":"","signature":"{{{Signature}}}"},
                    {"type":"tool_use","id":"toolu_1","name":"{{{tool}}}","input":{},"caller":{"type":"direct"}}],
         "stop_reason":"refusal","stop_sequence":null,
         "stop_details":{"type":"refusal","category":"cyber","explanation":"declined by policy"},
         "usage":{{{Usage(30, 12, null, null)}}}}
        """;

    // A null category leaves stop_details out of the response.
    public static string Refusal(string? category = "cyber")
    {
        var details = category is null
            ? ""
            : $$"""
              "stop_details":{"type":"refusal","category":"{{category}}","explanation":"declined by policy"},
              """;

        return $$"""
            {"id":"msg_refusal","type":"message","role":"assistant","model":"{{Model}}",
             "content":[],
             "stop_reason":"refusal","stop_sequence":null,
             {{details}}
             "usage":{{Usage(12, 0, null, null)}}}
            """;
    }

    // Thinking used the whole budget: HTTP 200, no text block.
    public static string EmptyAtMaxTokens() =>
        $$"""
        {"id":"msg_empty","type":"message","role":"assistant","model":"{{Model}}",
         "content":[{"type":"thinking","thinking":"","signature":"{{Signature}}"}],
         "stop_reason":"max_tokens","stop_sequence":null,
         "usage":{{Usage(40, 1024, null, null)}}}
        """;

    public static Message Parse(string json) => JsonSerializer.Deserialize<Message>(json)!;

    private static string ToolCall(int id, string name, string inputJson) =>
        $$$"""{"type":"tool_use","id":"toolu_{{{id}}}","name":"{{{name}}}","input":{{{inputJson}}},"caller":{"type":"direct"}}""";

    private static string ToolUseMessage(string calls, int input, int output, int? cacheWrite, int? cacheRead) =>
        $$"""
        {"id":"msg_tool","type":"message","role":"assistant","model":"{{Model}}",
         "content":[{"type":"thinking","thinking":"","signature":"{{Signature}}"},{{calls}}],
         "stop_reason":"tool_use","stop_sequence":null,
         "usage":{{Usage(input, output, cacheWrite, cacheRead)}}}
        """;

    private static string Usage(int input, int output, int? cacheWrite, int? cacheRead)
    {
        var cache = cacheWrite is null && cacheRead is null
            ? ""
            : $$""","cache_creation_input_tokens":{{cacheWrite ?? 0}},"cache_read_input_tokens":{{cacheRead ?? 0}}""";
        return $$"""{"input_tokens":{{input}},"output_tokens":{{output}}{{cache}}}""";
    }
}

// Answers every request from a script and keeps what was sent.
internal sealed class ScriptedClaudeHandler : HttpMessageHandler
{
    private readonly Queue<Func<Task<HttpResponseMessage>>> _script = new();

    public List<string> RequestBodies { get; } = [];

    public List<JsonObject> Requests => RequestBodies.Select(body => JsonNode.Parse(body)!.AsObject()).ToList();

    public List<Uri?> RequestUris { get; } = [];

    public List<Dictionary<string, string>> RequestHeaders { get; } = [];

    public ScriptedClaudeHandler Enqueue(string json)
    {
        _script.Enqueue(() => Task.FromResult(Json(HttpStatusCode.OK, json)));
        return this;
    }

    // Holds the answer until the test opens the gate; arrived completes when the request is here.
    public ScriptedClaudeHandler EnqueueGated(string json, TaskCompletionSource arrived, Task gate)
    {
        _script.Enqueue(async () =>
        {
            arrived.TrySetResult();
            await gate;
            return Json(HttpStatusCode.OK, json);
        });
        return this;
    }

    public ScriptedClaudeHandler EnqueueStatus(int status, int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            _script.Enqueue(() =>
            {
                var response = Json(
                    (HttpStatusCode)status,
                    $$"""{"type":"error","error":{"type":"api_error","message":"scripted {{status}}"},"request_id":"req_test"}""");

                // Keeps the SDK retry backoff out of the test run time.
                response.Headers.TryAddWithoutValidation("retry-after-ms", "1");
                response.Headers.TryAddWithoutValidation("request-id", "req_test");
                return Task.FromResult(response);
            });
        }

        return this;
    }

    // The caller gives up while this request is on the wire.
    public ScriptedClaudeHandler EnqueueCallerCancel(CancellationTokenSource caller)
    {
        _script.Enqueue(() =>
        {
            caller.Cancel();
            throw new OperationCanceledException(caller.Token);
        });
        return this;
    }

    public ScriptedClaudeHandler EnqueueConnectionFailure(int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            _script.Enqueue(() => throw new HttpRequestException("scripted connection reset"));
        }

        return this;
    }

    // The production client, on this transport.
    public AnthropicClient CreateClient(ILogger? logger = null) =>
        AnthropicChatClientFactory.CreateApiClient("test-key", new HttpClient(this), logger);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
        RequestUris.Add(request.RequestUri);
        RequestHeaders.Add(request.Headers.ToDictionary(
            h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase));

        if (_script.Count == 0)
        {
            throw new InvalidOperationException($"No scripted response left for request {RequestBodies.Count}");
        }

        return await _script.Dequeue()();
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

// The LLM path as KernelService builds it, with the "Test" plugin instead of the homelab plugins.
internal static class ClaudeTestKernel
{
    public static Kernel Build(
        IAnthropicClient client, PingPlugin plugin, ILogger logger, int maxTokens = 16000, ReasoningEffort effort = ReasoningEffort.Medium)
    {
        var chatClient = AnthropicChatClientFactory.Create(client, ClaudeResponses.Model, maxTokens, effort, logger);

        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton(chatClient.AsChatCompletionService());
        builder.Plugins.AddFromObject(plugin, "Test");
        var kernel = builder.Build();
        kernel.FunctionInvocationFilters.Add(new ToolSpanNameFilter());
        kernel.AutoFunctionInvocationFilters.Add(new ToolExceptionFilter(logger));
        return kernel;
    }

    public static ChatHistory NewHistory()
    {
        var history = new ChatHistory();
        history.AddSystemMessage("You are a test bot.");
        history.AddUserMessage("ping please");
        return history;
    }
}

// Collects the ended spans of one trace. A listener is process-wide: other test classes stop
// spans of the same sources on other threads, so only this trace is kept, under a lock.
internal sealed class SpanCollector : IDisposable
{
    private readonly List<Activity> _spans = [];
    private readonly ActivityListener _listener;

    public SpanCollector(ActivityTraceId traceId, Func<ActivitySource, bool> shouldListenTo)
        : this(span => span.TraceId == traceId, shouldListenTo)
    {
    }

    // For spans that start their own trace: keep picks them out from parallel tests.
    public SpanCollector(Func<Activity, bool> keep, Func<ActivitySource, bool> shouldListenTo)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = shouldListenTo,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = span =>
            {
                if (!keep(span))
                {
                    return;
                }

                lock (_spans)
                {
                    _spans.Add(span);
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public List<Activity> Snapshot()
    {
        lock (_spans)
        {
            return [.. _spans];
        }
    }

    public void Dispose() => _listener.Dispose();
}

// A real KernelService with the plugins "Test" and "Other", its own conversation history and
// telemetry, on a SQLite file of its own.
internal sealed class KernelServiceHarness : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"homelab-bot-claude-{Guid.NewGuid():N}.db");

    // morePlugins runs after DbFactory and Conversations exist, so a plugin can be built on them.
    // telemetryDb wraps the database of the telemetry service only, so a test can fail its writes.
    public KernelServiceHarness(
        IAnthropicClient client,
        int maxTokens = 16000,
        Action<IKernelBuilderPlugins, KernelServiceHarness>? morePlugins = null,
        Func<IDbContextFactory<HomelabDbContext>, IDbContextFactory<HomelabDbContext>>? telemetryDb = null,
        bool claudeDelegation = false)
    {
        // A file of its own, not the shared in-memory connection of DatabaseFixture: each test
        // starts from an empty database, and the row asserts expect only that test's rows.
        DbFactory = new DatabaseFixture.TestDbContextFactory(
            new DbContextOptionsBuilder<HomelabDbContext>().UseSqlite($"Data Source={_dbPath}").Options);
        using (var db = DbFactory.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        Conversations = new ConversationService(DbFactory, NullLogger<ConversationService>.Instance);
        Service = new KernelService(
            new BotConfiguration { DiscordToken = "x", AnthropicApiKey = "test-key", MaxTokens = maxTokens },
            client,
            Logger,
            NullLogger<TelemetryFunctionFilter>.Instance,
            Conversations,
            new TelemetryService(telemetryDb?.Invoke(DbFactory) ?? DbFactory, NullLogger<TelemetryService>.Instance),
            plugins =>
            {
                plugins.AddFromObject(Plugin, "Test");
                plugins.AddFromObject(new PingPlugin(), "Other");
                morePlugins?.Invoke(plugins, this);
            },
            claudeDelegation);
    }

    public PingPlugin Plugin { get; } = new();

    public RecordingLogger<KernelService> Logger { get; } = new();

    public DatabaseFixture.TestDbContextFactory DbFactory { get; }

    public KernelService Service { get; }

    public ConversationService Conversations { get; }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }
}

// The caller gives up while the first context is opened: what a cancel during the telemetry
// write at the start of a turn looks like. Later contexts open as usual.
internal sealed class CancelOnFirstContextFactory : IDbContextFactory<HomelabDbContext>
{
    private readonly IDbContextFactory<HomelabDbContext> _inner;
    private readonly CancellationTokenSource _caller;
    private int _created;

    public CancelOnFirstContextFactory(IDbContextFactory<HomelabDbContext> inner, CancellationTokenSource caller)
    {
        _inner = inner;
        _caller = caller;
    }

    public HomelabDbContext CreateDbContext() => _inner.CreateDbContext();

    public Task<HomelabDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _created) == 1)
        {
            _caller.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }

        return _inner.CreateDbContextAsync(cancellationToken);
    }
}

internal sealed class PingPlugin
{
    public int Calls { get; private set; }

    public int RestartCalls { get; private set; }

    public string PingAnswer { get; set; } = "pong";

    [KernelFunction("Ping")]
    [Description("Returns pong.")]
    public string Ping()
    {
        Calls++;
        return PingAnswer;
    }

    [KernelFunction("Restart")]
    [ChangesState]
    [Description("Restarts a container. Has a side effect.")]
    public string Restart()
    {
        RestartCalls++;
        return "container restarted";
    }

    [KernelFunction("Explode")]
    [Description("Always throws.")]
    public string Explode() => throw new InvalidOperationException("docker socket unreachable");

    // What an HttpClient timeout inside a tool looks like: a cancellation the caller did not ask for.
    [KernelFunction("TimeOut")]
    [Description("Always times out.")]
    public string TimeOut() => throw new TaskCanceledException("docker request timed out");
}

internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
