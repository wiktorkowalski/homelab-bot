using Anthropic;
using HomelabBot.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace HomelabBot.Tests;

// Calls the real Claude API and costs a few cents per run, so it is skipped unless a key is set:
// Bot__AnthropicApiKey in the repo .env file, or
//   ANTHROPIC_API_KEY=sk-ant-... dotnet test --filter "FullyQualifiedName~ClaudeApiLiveTests"
// It checks what a scripted transport cannot: that the API accepts the requests this bot sends.
public class ClaudeApiLiveTests
{
    private const string ConfigKey = "Bot__AnthropicApiKey";

    // The process environment first, then the developer's .env file (found by walking up from
    // the test output directory, the same file the bot loads). The file is read without setting
    // environment variables, so the other secrets in it stay out of the test process.
    private static readonly Lazy<string?> LazyApiKey = new(() =>
        Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } key
            ? key
            : Environment.GetEnvironmentVariable(ConfigKey) is { Length: > 0 } configured
                ? configured
                : DotNetEnv.Env.NoEnvVars().TraversePath().Load()
                    .Where(pair => pair.Key == ConfigKey && pair.Value.Length > 0)
                    .Select(pair => pair.Value)
                    .FirstOrDefault());

    private static string? ApiKey => LazyApiKey.Value;

    [LiveFact]
    public async Task ToolLoop_IsAcceptedByTheApi_AndRoundTwoReadsTheCache()
    {
        var logger = new RecordingLogger<ClaudeApiLiveTests>();
        var plugin = new PingPlugin();
        var kernel = ClaudeTestKernel.Build(AnthropicChatClientFactory.CreateApiClient(ApiKey!), plugin, logger, maxTokens: 4096);

        // Long enough to pass the minimum cacheable prompt length of the model.
        var history = new ChatHistory();
        history.AddSystemMessage(
            "You are a test bot for a homelab. " + string.Concat(Enumerable.Repeat("Answer in one short sentence. ", 600)));
        history.AddUserMessage("Call the Ping tool once. Then tell me the exact word it returned.");

        using var scope = TokenUsageScope.Begin();
        var response = await kernel.GetRequiredService<IChatCompletionService>().GetChatMessageContentAsync(
            history, new PromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() }, kernel);
        var usage = scope.Snapshot();

        Assert.Equal(1, plugin.Calls);
        Assert.Contains("pong", response.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("end_turn", KernelService.GetStopReason(response));
        Assert.Equal(2, usage.Rounds);
        Assert.True(usage.CachedPromptTokens > 0, $"no cache read in round 2: {string.Join(" | ", logger.Entries.Select(e => e.Message))}");

        // A second turn on the stored history: tool turn without its thinking block, then no tools.
        history.Add(response);
        history.AddUserMessage("Now answer without tools: which tool did you call?");
        var second = await kernel.GetRequiredService<IChatCompletionService>().GetChatMessageContentAsync(
            history, new PromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.None(functions: []) }, kernel);

        Assert.Contains("ping", second.Content, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class LiveFactAttribute : FactAttribute
    {
        public LiveFactAttribute()
        {
            if (string.IsNullOrEmpty(ApiKey))
            {
                Skip = "Set ANTHROPIC_API_KEY to run the tests against the real Claude API.";
            }
        }
    }
}
