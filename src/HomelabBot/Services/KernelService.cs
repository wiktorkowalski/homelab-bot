using System.Diagnostics;
using System.Text.Json;
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
    private static readonly ActivitySource ActivitySource = new("HomelabBot.Chat");

    private readonly Kernel _kernel;
    private readonly ILogger<KernelService> _logger;
    private readonly ConversationService _conversationService;
    private readonly TelemetryService _telemetryService;
    private readonly IChatCompletionService _chatService;
    private readonly string _modelId;

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
        - Knowledge & memory system

        When executing actions:
        - Tell the user briefly what you're checking ("Checking Docker...")
        - For dangerous operations (stop, restart), request confirmation
        - For long outputs, summarize and offer to attach full details
        - Report errors with full context, not generic messages

        Learning & Memory:
        - Call RecallKnowledge(topic) BEFORE taking action to check what you already know
        - Call RememberFact(topic, fact) when you discover something useful
        - Call LearnCorrection() when user corrects you
        - Use StoreAlias() when user tells you friendly names for devices/containers
        - Use ResolveAlias() to translate user-friendly names to technical identifiers

        Investigation & Troubleshooting:
        When diagnosing issues (something broken, slow, not working):
        1. Call StartInvestigation(threadId, symptom) - this checks for similar past issues
        2. Check the most likely cause first based on past patterns
        3. Call RecordStep() after each diagnostic check
        4. Cross-reference related services (if Docker issue, check Loki logs too)
        5. Call ResolveInvestigation(threadId, resolution) when fixed - this saves the pattern

        When user mentions devices by name (like "my PC", "media server"):
        - First try ResolveAlias() to get the actual identifier
        - If no alias exists, ask them for the technical name and store it

        Important: Only perform actions the user explicitly asks for. Don't volunteer to do things.
        """;

    public KernelService(
        IOptions<BotConfiguration> config,
        ILogger<KernelService> logger,
        ILogger<TelemetryFunctionFilter> filterLogger,
        ConversationService conversationService,
        TelemetryService telemetryService,
        DockerPlugin dockerPlugin,
        PrometheusPlugin prometheusPlugin,
        AlertmanagerPlugin alertmanagerPlugin,
        LokiPlugin lokiPlugin,
        GrafanaPlugin grafanaPlugin,
        MikroTikPlugin mikrotikPlugin,
        TrueNASPlugin truenasPlugin,
        HomeAssistantPlugin homeAssistantPlugin,
        NtfyPlugin ntfyPlugin,
        KnowledgePlugin knowledgePlugin,
        InvestigationPlugin investigationPlugin)
    {
        _logger = logger;
        _conversationService = conversationService;
        _telemetryService = telemetryService;
        _modelId = config.Value.OpenRouterModel;

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
        builder.Plugins.AddFromObject(knowledgePlugin, "Knowledge");
        builder.Plugins.AddFromObject(investigationPlugin, "Investigation");

        _kernel = builder.Build();

        // Add telemetry filter for tool call logging
        _kernel.FunctionInvocationFilters.Add(new TelemetryFunctionFilter(telemetryService, filterLogger));

        _chatService = _kernel.GetRequiredService<IChatCompletionService>();
        _logger.LogInformation("Kernel initialized with model {Model} and {PluginCount} plugins",
            config.Value.OpenRouterModel, 11);
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

    public async Task<string> ProcessMessageAsync(
        ulong threadId,
        string userMessage,
        ulong? userId = null,
        CancellationToken ct = default)
    {
        // Start root activity with Langfuse attributes
        using var activity = ActivitySource.StartActivity("HomeLabBot Chat", ActivityKind.Server);
        activity?.SetTag("langfuse.trace.name", "HomeLabBot Chat");
        activity?.SetTag("langfuse.session.id", threadId.ToString());
        activity?.SetTag("langfuse.trace.tags", "[\"homelab\", \"discord\"]");
        activity?.SetTag("langfuse.trace.input", userMessage);
        if (userId.HasValue)
        {
            activity?.SetTag("langfuse.user.id", userId.Value.ToString());
        }

        var sw = Stopwatch.StartNew();
        var history = _conversationService.GetOrCreateHistory(threadId, SystemPrompt);
        _conversationService.AddUserMessage(threadId, userMessage);

        var historyJson = JsonSerializer.Serialize(history.Select(m => new { m.Role, Content = m.Content }));
        var interaction = await _telemetryService.LogInteractionStartAsync(
            threadId, _modelId, userMessage, historyJson, ct);

        // Set active interaction for tool call logging
        _telemetryService.SetActiveInteraction(interaction.Id);

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

            sw.Stop();
            var responseText = response.Content ?? "I couldn't generate a response.";
            responseText = StripThinkingBlocks(responseText);
            _conversationService.AddAssistantMessage(threadId, responseText);

            int? promptTokens = null;
            int? completionTokens = null;
            if (response.Metadata?.TryGetValue("Usage", out var usage) == true && usage is not null)
            {
                var usageDict = usage as IDictionary<string, object>;
                if (usageDict?.TryGetValue("PromptTokens", out var pt) == true)
                    promptTokens = Convert.ToInt32(pt);
                if (usageDict?.TryGetValue("CompletionTokens", out var cpt) == true)
                    completionTokens = Convert.ToInt32(cpt);
            }

            await _telemetryService.LogInteractionCompleteAsync(
                interaction.Id, responseText, promptTokens, completionTokens, sw.ElapsedMilliseconds, ct);

            // Set trace output
            activity?.SetTag("langfuse.trace.output", responseText);

            return responseText;
        }
        catch (Exception ex)
        {
            sw.Stop();
            await _telemetryService.LogInteractionErrorAsync(interaction.Id, ex.Message, sw.ElapsedMilliseconds, ct);
            _logger.LogError(ex, "Error processing message for thread {ThreadId}", threadId);
            activity?.SetTag("langfuse.observation.level", "ERROR");
            activity?.SetTag("langfuse.observation.status_message", ex.Message);
            return $"Error processing your request: {ex.Message}";
        }
        finally
        {
            _telemetryService.SetActiveInteraction(null);
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
