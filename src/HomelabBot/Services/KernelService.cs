using System.Text.RegularExpressions;
using HomelabBot.Configuration;
using HomelabBot.Plugins;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace HomelabBot.Services;

public sealed class KernelService
{
    private readonly Kernel _kernel;
    private readonly ILogger<KernelService> _logger;
    private readonly ConversationService _conversationService;
    private readonly IChatCompletionService _chatService;

    public const string SystemPrompt = """
        You are HomeLabBot, a chill and helpful assistant for managing a homelab infrastructure.

        Your personality:
        - Conversational and concise - get to the point
        - Casual tone, can swear when the situation warrants it (like when something's really broken)
        - Brief status updates when doing multi-step operations

        Your capabilities:
        - Docker container management (list, start, stop, restart, logs)
        - MikroTik router status and Wake-on-LAN
        - TrueNAS storage health and stats
        - Home Assistant device control
        - Prometheus metrics queries
        - Grafana dashboards
        - Alertmanager alerts
        - Loki log queries
        - Push notifications via Ntfy
        - Prusa 3D printer status

        When executing actions:
        - Tell the user briefly what you're checking ("Checking Docker...")
        - For dangerous operations (stop, restart), request confirmation
        - For long outputs, summarize and offer to attach full details
        - Report errors with full context, not generic messages

        Important: Only perform actions the user explicitly asks for. Don't volunteer to do things.
        """;

    public KernelService(
        IOptions<BotConfiguration> config,
        ILogger<KernelService> logger,
        ConversationService conversationService,
        DockerPlugin dockerPlugin,
        PrometheusPlugin prometheusPlugin,
        AlertmanagerPlugin alertmanagerPlugin,
        LokiPlugin lokiPlugin,
        GrafanaPlugin grafanaPlugin,
        MikroTikPlugin mikrotikPlugin,
        TrueNASPlugin truenasPlugin,
        HomeAssistantPlugin homeAssistantPlugin,
        NtfyPlugin ntfyPlugin)
    {
        _logger = logger;
        _conversationService = conversationService;

        var builder = Kernel.CreateBuilder();

        // Configure OpenRouter as OpenAI-compatible endpoint
        builder.AddOpenAIChatCompletion(
            modelId: config.Value.OpenRouterModel,
            apiKey: config.Value.OpenRouterApiKey,
            endpoint: new Uri(config.Value.OpenRouterEndpoint));

        // Register plugins
        builder.Plugins.AddFromObject(dockerPlugin, "Docker");
        builder.Plugins.AddFromObject(prometheusPlugin, "Prometheus");
        builder.Plugins.AddFromObject(alertmanagerPlugin, "Alertmanager");
        builder.Plugins.AddFromObject(lokiPlugin, "Loki");
        builder.Plugins.AddFromObject(grafanaPlugin, "Grafana");
        builder.Plugins.AddFromObject(mikrotikPlugin, "MikroTik");
        builder.Plugins.AddFromObject(truenasPlugin, "TrueNAS");
        builder.Plugins.AddFromObject(homeAssistantPlugin, "HomeAssistant");
        builder.Plugins.AddFromObject(ntfyPlugin, "Ntfy");

        _kernel = builder.Build();
        _chatService = _kernel.GetRequiredService<IChatCompletionService>();
        _logger.LogInformation("Kernel initialized with model {Model} and {PluginCount} plugins",
            config.Value.OpenRouterModel, 9);
    }

    public async Task<string> GenerateThreadTitleAsync(string userMessage, CancellationToken ct = default)
    {
        try
        {
            var history = new ChatHistory();
            history.AddSystemMessage("Generate a very short thread title (max 5 words) for this conversation. Just the title, no quotes or punctuation.");
            history.AddUserMessage(userMessage);

            var response = await _chatService.GetChatMessageContentAsync(history, cancellationToken: ct);
            var title = StripThinkingBlocks(response.Content ?? "").Trim().Trim('"', '\'');
            if (string.IsNullOrEmpty(title)) title = "Chat";

            // Ensure max length for Discord (100 chars)
            if (title.Length > 50)
            {
                title = title[..47] + "...";
            }

            return title;
        }
        catch
        {
            return "Chat";
        }
    }

    public async Task<string> ProcessMessageAsync(ulong threadId, string userMessage, CancellationToken ct = default)
    {
        var history = _conversationService.GetOrCreateHistory(threadId, SystemPrompt);
        _conversationService.AddUserMessage(threadId, userMessage);

        var chatService = _kernel.GetRequiredService<IChatCompletionService>();

        var settings = new OpenAIPromptExecutionSettings
        {
            Temperature = 0.7,
            MaxTokens = 2048,
            ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions
        };

        try
        {
            var response = await chatService.GetChatMessageContentAsync(
                history,
                settings,
                _kernel,
                ct);

            var responseText = response.Content ?? "I couldn't generate a response.";
            responseText = StripThinkingBlocks(responseText);
            _conversationService.AddAssistantMessage(threadId, responseText);

            return responseText;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message for thread {ThreadId}", threadId);
            return $"Error processing your request: {ex.Message}";
        }
    }

    private static string StripThinkingBlocks(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // Strip various thinking block formats used by different models
        // <think>...</think>, <thinking>...</thinking>, <reasoning>...</reasoning>
        var patterns = new[]
        {
            @"<think>[\s\S]*?</think>",
            @"<thinking>[\s\S]*?</thinking>",
            @"<reasoning>[\s\S]*?</reasoning>",
            @"<reflection>[\s\S]*?</reflection>"
        };

        foreach (var pattern in patterns)
        {
            text = Regex.Replace(text, pattern, "", RegexOptions.IgnoreCase);
        }

        return text.Trim();
    }
}
