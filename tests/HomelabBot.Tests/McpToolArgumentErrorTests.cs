using Microsoft.Extensions.Logging;

namespace HomelabBot.Tests;

public class McpToolArgumentErrorTests
{
    private const string Secret = "caller-secret-7f3a";
    private const string QueryLokiShape = "Arguments: query (string, required), limit (integer), since (string).";

    [Theory]
    [InlineData("QueryLoki", $$$"""{"query":"x","limit":"{{{Secret}}}"}""", "'limit' must be integer, got string", QueryLokiShape)]
    [InlineData("QueryLoki", """{"query":"x","limit":1.5}""", "'limit' must be integer, got number", QueryLokiShape)]
    [InlineData("QueryLoki", $$$"""{"query":{"a":"{{{Secret}}}"}}""", "'query' must be string, got object", QueryLokiShape)]
    [InlineData("QueryLoki", """{"query":5,"limit":true}""", "'query' must be string, got integer; 'limit' must be integer, got boolean", QueryLokiShape)]
    [InlineData("QueryLoki", """{"limit":5}""", "required 'query' is missing", QueryLokiShape)]
    [InlineData("QueryLoki", $$$"""{"{{{Secret}}}":"x"}""", "required 'query' is missing", QueryLokiShape)]
    [InlineData("search_logs", """{"searchText":"x","containerName":5}""", "'containerName' must be string or null, got integer", "Arguments: searchText (string, required), since (string), containerName (string or null).")]
    public async Task WrongArgument_NamesTheArgumentAndTheExpectedShape(string tool, string arguments, string expectedProblem, string expectedShape)
    {
        await using var host = await McpTestHost.StartAsync();

        var (isError, text) = await host.CallToolAsync(tool, arguments);

        Assert.True(isError);
        Assert.Equal($"Wrong arguments for '{tool}': {expectedProblem}. {expectedShape}", text);
        Assert.DoesNotContain(Secret, text);

        // The caller's mistake: logged once at Warning with no stack trace, never as an error.
        Assert.DoesNotContain(host.Logs.Entries, entry => entry.Level >= LogLevel.Error);
        Assert.DoesNotContain(host.Logs.Entries, entry => entry.Message.Contains(Secret));
        var rejection = Assert.Single(host.Logs.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal($"Rejected MCP call to {tool}: {expectedProblem}", rejection.Message);
        Assert.Null(rejection.Exception);
    }

    [Fact]
    public async Task NumberAsString_IsAValidArgument()
    {
        await using var host = await McpTestHost.StartAsync();

        // The tool runs and fails at the unreachable Loki host: the arguments passed.
        var (isError, text) = await host.CallToolAsync("QueryLoki", """{"query":"x","limit":"5"}""");

        Assert.False(isError);
        Assert.StartsWith("Error executing query:", text);
    }
}
