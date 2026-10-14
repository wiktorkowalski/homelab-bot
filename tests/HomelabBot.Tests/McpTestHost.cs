using System.Collections.Concurrent;
using System.Text.Json;
using HomelabBot.Configuration;
using HomelabBot.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace HomelabBot.Tests;

// The MCP endpoint over an in-memory server. Only the Loki tools have their services: Program.cs needs Discord and a database.
internal sealed class McpTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly McpClient _client;

    private McpTestHost(WebApplication app, McpClient client, LogCollector logs)
    {
        _app = app;
        _client = client;
        Logs = logs;
    }

    public LogCollector Logs { get; }

    public static async Task<McpTestHost> StartAsync()
    {
        var logs = new LogCollector();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(logs);

        // No retry policy here: the one valid call of the tests fails fast at the unreachable Loki host.
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton(Options.Create(new LokiConfiguration { Host = "http://loki.invalid" }));
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithHomelabTools();

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri("http://localhost/mcp") },
            app.GetTestClient(),
            ownsHttpClient: true);
        var client = await McpClient.CreateAsync(transport);

        // Only the logs of the tool calls count, not those of the handshake.
        logs.Entries.Clear();
        return new McpTestHost(app, client, logs);
    }

    public async Task<(bool IsError, string Text)> CallToolAsync(string tool, string argumentsJson)
    {
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson);
        var result = await _client.CallToolAsync(tool, arguments);

        return (result.IsError == true, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    public async Task<IList<McpClientTool>> ListToolsAsync() => await _client.ListToolsAsync();

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        await _app.DisposeAsync();
    }

    internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    internal sealed class LogCollector : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Collector(Entries);

        public void Dispose()
        {
        }

        private sealed class Collector(ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }
}
