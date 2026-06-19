using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using HomelabBot.Configuration;
using HomelabBot.Plugins;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace HomelabBot.Services;

public enum TraceType
{
    Chat,
    Discovery,
    Scheduled,
}

public sealed class KernelService
{
    private static readonly ActivitySource ActivitySource = TelemetryConstants.ChatActivitySource;

    private static readonly Regex ThinkingBlockRegex = new(
        @"<(think|thinking|reasoning|reflection)>[\s\S]*?</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Dictionary<TraceType, (string Name, string[] Tags)> TraceConfig = new()
    {
        [TraceType.Chat] = ("Chat", ["homelab", "discord"]),
        [TraceType.Discovery] = ("Discovery", ["homelab", "discord", "investigation"]),
        [TraceType.Scheduled] = ("Scheduled", ["homelab", "scheduled"]),
    };

    private readonly Kernel _kernel;
    private readonly ILogger<KernelService> _logger;
    private readonly ConversationService _conversationService;
    private readonly TelemetryService _telemetryService;
    private readonly IChatCompletionService _chatService;
    private readonly string _modelId;
    private readonly int _maxAttempts;
    private readonly int _maxTokens;

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
        - Use SmartRecallKnowledge(query) for natural language searches when you don't know the exact topic
        - Use RecallKnowledge(topic) when you know the exact topic name
        - Call RememberFact(topic, fact) for each useful discovery - keep facts atomic and specific (e.g., "Pool 'tank' at 50% capacity" not entire status dumps). Call multiple times for multiple facts.
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
        InvestigationPlugin investigationPlugin,
        RunbookPlugin runbookPlugin)
    {
        _logger = logger;
        _conversationService = conversationService;
        _telemetryService = telemetryService;
        _modelId = config.Value.OpenRouterModel;
        _maxAttempts = Math.Max(1, config.Value.MaxResponseAttempts);
        _maxTokens = config.Value.MaxTokens;

        var builder = Kernel.CreateBuilder();

        builder.AddOpenAIChatCompletion(
            modelId: config.Value.OpenRouterModel,
            apiKey: config.Value.OpenRouterApiKey,
            endpoint: new Uri(config.Value.OpenRouterEndpoint));

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
        builder.Plugins.AddFromObject(runbookPlugin, "Runbook");

        _kernel = builder.Build();
        _kernel.FunctionInvocationFilters.Add(new TelemetryFunctionFilter(telemetryService, filterLogger));
        _chatService = _kernel.GetRequiredService<IChatCompletionService>();

        _logger.LogInformation(
            "Kernel initialized with OpenRouter {Model} and {PluginCount} plugins",
            config.Value.OpenRouterModel, _kernel.Plugins.Count);
    }

    public async Task<string> GenerateThreadTitleAsync(
        string userMessage,
        ulong threadId,
        ulong? userId = null,
        CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("Generate Title", ActivityKind.Internal);
        activity?.SetTag("langfuse.trace.name", "Generate Title");
        activity?.SetTag("langfuse.session.id", threadId.ToString());
        activity?.SetTag("langfuse.trace.tags", "[\"internal\"]");
        activity?.SetTag("langfuse.trace.input", userMessage);
        if (userId.HasValue)
        {
            activity?.SetTag("langfuse.user.id", userId.Value.ToString());
        }

        try
        {
            var history = new ChatHistory();
            history.AddSystemMessage("""
                Generate a short thread title (2-6 words) summarizing the user's request.
                Rules:
                - Just output the title, nothing else
                - No quotes, punctuation, or prefixes
                - Summarize the INTENT, not your response
                - Examples: "Router Status Check", "Docker Container Logs", "Wake Up Media Server"
                """);
            history.AddUserMessage(userMessage);

            var response = await _chatService.GetChatMessageContentAsync(history, cancellationToken: ct);
            var title = StripThinkingBlocks(response.Content ?? "").Trim().Trim('"', '\'', '.');
            if (string.IsNullOrEmpty(title) || title.Length < 3)
            {
                title = "Chat";
            }

            // Discord thread name limit is 100 chars
            if (title.Length > 100)
            {
                title = title[..97] + "...";
            }

            activity?.SetTag("langfuse.trace.output", title);
            return title;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Title generation failed, defaulting to 'Chat'");
            return "Chat";
        }
    }

    public Kernel GetKernel() => _kernel;

    public async Task<string> ProcessMessageAsync(
        ulong threadId,
        string userMessage,
        ulong? userId = null,
        TraceType traceType = TraceType.Chat,
        int? maxTokens = null,
        string? systemPromptOverride = null,
        CancellationToken ct = default)
    {
        var (traceName, traceTags) = TraceConfig[traceType];
        var tagsJson = JsonSerializer.Serialize(traceTags);

        // Start root activity with Langfuse attributes
        using var activity = ActivitySource.StartActivity(traceName, ActivityKind.Server);
        activity?.SetTag("langfuse.trace.name", traceName);
        activity?.SetTag("langfuse.session.id", threadId.ToString());
        activity?.SetTag("langfuse.trace.tags", tagsJson);
        activity?.SetTag("langfuse.trace.input", userMessage);
        if (userId.HasValue)
        {
            activity?.SetTag("langfuse.user.id", userId.Value.ToString());
        }

        var sw = Stopwatch.StartNew();
        var systemPrompt = systemPromptOverride ?? SystemPrompt;
        var history = _conversationService.GetOrCreateHistory(threadId, systemPrompt);
        _conversationService.AddUserMessage(threadId, userMessage);

        var historyJson = JsonSerializer.Serialize(history.Select(m => new { m.Role, Content = m.Content }));
        var interaction = await _telemetryService.LogInteractionStartAsync(
            threadId, _modelId, userMessage, historyJson, ct);

        // Set active interaction for tool call logging
        _telemetryService.SetActiveInteraction(interaction.Id);

        var activeSettings = new PromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
            ExtensionData = new Dictionary<string, object>
            {
                ["temperature"] = 0.7,
                ["max_tokens"] = maxTokens ?? _maxTokens,
            },
        };

        try
        {
            var response = await InvokeWithRetryAsync(
                c => _chatService.GetChatMessageContentAsync(history, activeSettings, _kernel, c),
                history,
                _modelId,
                _maxAttempts,
                threadId,
                _logger,
                c: ct);

            sw.Stop();

            if (IsEmptyResponse(response))
            {
                // The retry helper already logged the give-up at Error level (finish reason + tokens).
                const string fallback =
                    "I couldn't generate a response - the model returned nothing. Try asking again or rephrasing.";
                var (emptyPromptTokens, emptyCompletionTokens) = ExtractTokenUsage(response);
                await _telemetryService.LogInteractionCompleteAsync(
                    interaction.Id, fallback, emptyPromptTokens, emptyCompletionTokens, sw.ElapsedMilliseconds, ct);
                activity?.SetTag("langfuse.observation.level", "WARNING");
                activity?.SetTag("langfuse.trace.output", fallback);
                return fallback;
            }

            var responseText = StripThinkingBlocks(response!.Content!);
            _conversationService.AddAssistantMessage(threadId, responseText);

            var (promptTokens, completionTokens) = ExtractTokenUsage(response);

            await _telemetryService.LogInteractionCompleteAsync(
                interaction.Id, responseText, promptTokens, completionTokens, sw.ElapsedMilliseconds, ct);

            // Set trace output
            activity?.SetTag("langfuse.trace.output", responseText);

            return responseText;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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

    // Retries empty/whitespace responses (OpenRouter occasionally returns HTTP 200 with no
    // content - notably reasoning models that spend the whole budget thinking) and transient
    // HTTP failures (408/429/5xx/timeouts). SK auto function invocation appends tool-call and
    // tool-result messages to the shared history in place, so each retry first resets it to the
    // pre-call length - otherwise attempts stack duplicate/partial turns on one another.
    internal static async Task<ChatMessageContent?> InvokeWithRetryAsync(
        Func<CancellationToken, Task<ChatMessageContent>> invoke,
        ChatHistory history,
        string modelId,
        int maxAttempts,
        ulong threadId,
        ILogger logger,
        Func<int, TimeSpan>? backoffDelay = null,
        CancellationToken c = default)
    {
        backoffDelay ??= BackoffDelay;
        maxAttempts = Math.Max(1, maxAttempts);
        var baseline = history.Count;
        ChatMessageContent? response = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (attempt > 1)
            {
                TrimToBaseline(history, baseline);
            }

            try
            {
                response = await invoke(c);

                if (!IsEmptyResponse(response))
                {
                    return response;
                }

                var finishReason = GetFinishReason(response);
                if (attempt < maxAttempts)
                {
                    logger.LogWarning(
                        "Empty response from {Model} for thread {ThreadId} (attempt {Attempt}/{MaxAttempts}, finishReason={FinishReason}); retrying",
                        modelId, threadId, attempt, maxAttempts, finishReason);
                }
                else
                {
                    var (promptTokens, completionTokens) = ExtractTokenUsage(response);
                    logger.LogError(
                        "Exhausted {MaxAttempts} attempts, still empty response from {Model} for thread {ThreadId} (finishReason={FinishReason}, promptTokens={PromptTokens}, completionTokens={CompletionTokens})",
                        maxAttempts, modelId, threadId, finishReason, promptTokens, completionTokens);
                    return response;
                }
            }
            catch (OperationCanceledException) when (c.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpOperationException ex) when (IsTransient(ex))
            {
                if (attempt >= maxAttempts)
                {
                    logger.LogError(
                        ex,
                        "Exhausted {MaxAttempts} attempts on transient {StatusCode} from {Model} for thread {ThreadId}",
                        maxAttempts, ex.StatusCode, modelId, threadId);
                    throw;
                }

                logger.LogWarning(
                    ex,
                    "Transient {StatusCode} from {Model} for thread {ThreadId} (attempt {Attempt}/{MaxAttempts}); retrying",
                    ex.StatusCode, modelId, threadId, attempt, maxAttempts);
            }

            await Task.Delay(backoffDelay(attempt), c);
        }

        return response;
    }

    // SK appends tool-call/result messages to the shared history during a call; drop anything
    // past the pre-call length so a retry starts from a clean state.
    private static void TrimToBaseline(ChatHistory history, int baseline)
    {
        for (var i = history.Count - 1; i >= baseline; i--)
        {
            history.RemoveAt(i);
        }
    }

    // Empty when missing, blank, or only a <think> block (which strips to nothing downstream).
    internal static bool IsEmptyResponse(ChatMessageContent? response)
    {
        if (response is null)
        {
            return true;
        }

        var content = response.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            return true;
        }

        // A response that is nothing but a <think> block collapses to empty once stripped.
        return string.IsNullOrWhiteSpace(StripThinkingBlocks(content));
    }

    // Transport failures (no status) and retryable HTTP codes (408/429/5xx).
    internal static bool IsTransient(HttpOperationException ex)
    {
        if (ex.StatusCode is null)
        {
            // No HTTP response received (connection reset, timeout, DNS, etc.).
            return true;
        }

        var code = (int)ex.StatusCode.Value;
        return code == 408 || code == 429 || code >= 500;
    }

    internal static string GetFinishReason(ChatMessageContent? response)
    {
        if (response?.Metadata is not null
            && response.Metadata.TryGetValue("FinishReason", out var finishReason)
            && finishReason is not null)
        {
            return finishReason.ToString() ?? "unknown";
        }

        return "unknown";
    }

    // Full-jitter exponential backoff: random delay in [0, min(8s, 500ms * 2^(attempt-1))).
    internal static TimeSpan BackoffDelay(int attempt)
    {
        const double baseMs = 500;
        const double capMs = 8000;
        var ceiling = Math.Min(capMs, baseMs * Math.Pow(2, attempt - 1));
        return TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * ceiling);
    }

    internal static (int? PromptTokens, int? CompletionTokens) ExtractTokenUsage(ChatMessageContent? response)
    {
        if (response?.Metadata is null)
        {
            return (null, null);
        }

        if (response.Metadata.TryGetValue("Usage", out var usage)
            && usage is OpenAI.Chat.ChatTokenUsage openAiUsage)
        {
            return (openAiUsage.InputTokenCount, openAiUsage.OutputTokenCount);
        }

        return (null, null);
    }

    internal static string StripThinkingBlocks(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return ThinkingBlockRegex.Replace(text, "").Trim();
    }
}
