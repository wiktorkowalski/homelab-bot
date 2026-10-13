using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace HomelabBot.Tests;

// Canned Messages API responses in the wire format of https://api.anthropic.com/v1/messages.
internal static class ClaudeResponses
{
    public const string Model = "claude-sonnet-5-5";
    public const string Signature = "SIG_ABC";

    // Round 1 of a tool loop: an (empty, signed) thinking block followed by one or more tool calls.
    public static string ToolUse(
        int input = 100, int output = 20, int? cacheWrite = 900, int? cacheRead = 0, params string[] tools)
    {
        tools = tools.Length == 0 ? ["Test_Ping"] : tools;
        var calls = string.Join(
            ",",
            tools.Select((name, i) => $$$"""{"type":"tool_use","id":"toolu_{{{i + 1}}}","name":"{{{name}}}","input":{},"caller":{"type":"direct"}}"""));

        return $$"""
            {"id":"msg_tool","type":"message","role":"assistant","model":"{{Model}}",
             "content":[{"type":"thinking","thinking":"","signature":"{{Signature}}"},{{calls}}],
             "stop_reason":"tool_use","stop_sequence":null,
             "usage":{{Usage(input, output, cacheWrite, cacheRead)}}}
            """;
    }

    public static string Text(
        string text = "pong received", int input = 120, int output = 50, int? cacheWrite = 0, int? cacheRead = 900) =>
        $$"""
        {"id":"msg_text","type":"message","role":"assistant","model":"{{Model}}",
         "content":[{"type":"text","text":{{JsonSerializer.Serialize(text)}}}],
         "stop_reason":"end_turn","stop_sequence":null,
         "usage":{{Usage(input, output, cacheWrite, cacheRead)}}}
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
    private readonly Queue<Func<HttpResponseMessage>> _script = new();

    public ScriptedClaudeHandler(params string[] responses)
    {
        foreach (var response in responses)
        {
            Enqueue(response);
        }
    }

    public List<string> RequestBodies { get; } = [];

    public List<Uri?> RequestUris { get; } = [];

    public List<Dictionary<string, string>> RequestHeaders { get; } = [];

    public ScriptedClaudeHandler Enqueue(string json)
    {
        _script.Enqueue(() => Json(HttpStatusCode.OK, json));
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
                return response;
            });
        }

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

    public AnthropicClient CreateClient() => new()
    {
        ApiKey = "test-key",
        HttpClient = new HttpClient(this),
    };

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

        return _script.Dequeue()();
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
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
    [Description("Restarts a container. Has a side effect.")]
    public string Restart()
    {
        RestartCalls++;
        return "container restarted";
    }

    [KernelFunction("Explode")]
    [Description("Always throws.")]
    public string Explode() => throw new InvalidOperationException("docker socket unreachable");
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
