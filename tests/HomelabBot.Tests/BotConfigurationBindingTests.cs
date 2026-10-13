using HomelabBot.Configuration;
using HomelabBot.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace HomelabBot.Tests;

// Program.cs binds the "Bot" section with ValidateOnStart: a value the binder cannot read stops
// the bot at startup. The other tests build BotConfiguration by hand and would not notice.
public class BotConfigurationBindingTests
{
    [Fact]
    public void ShippedAppsettings_BindTheClaudeKeys()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .Build()
            .GetSection(BotConfiguration.SectionName)
            .Get<BotConfiguration>()!;

        Assert.Equal("claude-sonnet-5-5", config.AnthropicModel);
        Assert.Equal(ReasoningEffort.Medium, config.AnthropicEffort);
        Assert.Equal("", config.AnthropicApiKey);
    }

    [Theory]
    [InlineData("Low", ReasoningEffort.Low)]
    [InlineData("high", ReasoningEffort.High)]
    [InlineData("ExtraHigh", ReasoningEffort.ExtraHigh)]
    public void EnvironmentStyleKeys_OverrideKeyModelAndEffort(string effort, ReasoningEffort expected)
    {
        var config = Bind(new()
        {
            ["Bot:DiscordToken"] = "x",
            ["Bot:AnthropicApiKey"] = "sk-ant-test",
            ["Bot:AnthropicModel"] = "claude-haiku-5-5",
            ["Bot:AnthropicEffort"] = effort,
        });

        Assert.Equal("sk-ant-test", config.AnthropicApiKey);
        Assert.Equal("claude-haiku-5-5", config.AnthropicModel);
        Assert.Equal(expected, config.AnthropicEffort);
    }

    [Fact]
    public void MissingOptionalKeys_KeepTheDefaults()
    {
        var config = Bind(new() { ["Bot:DiscordToken"] = "x", ["Bot:AnthropicApiKey"] = "k" });

        Assert.Equal("claude-sonnet-5-5", config.AnthropicModel);
        Assert.Equal(ReasoningEffort.Medium, config.AnthropicEffort);
        Assert.Equal(50000, config.MaxTokens);
    }

    [Fact]
    public void ApiClient_HasAFixedTimeoutPerAttempt()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), AnthropicChatClientFactory.CreateApiClient("k").Timeout);
    }

    private static BotConfiguration Bind(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build()
            .GetSection(BotConfiguration.SectionName)
            .Get<BotConfiguration>()!;
}
