using Anthropic;
using HomelabBot.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace HomelabBot.Tests;

// Calls the real Claude API and costs a few cents per run, so it is skipped unless a key is set:
//   ANTHROPIC_API_KEY=sk-ant-... dotnet test --filter "FullyQualifiedName~ClaudeApiLiveTests"
// It checks what a scripted transport cannot: that the API accepts the requests this bot sends.
public class ClaudeApiLiveTests
{
    private static string? ApiKey =>
        Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } key
            ? key
            : Environment.GetEnvironmentVariable("Bot__AnthropicApiKey");

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
