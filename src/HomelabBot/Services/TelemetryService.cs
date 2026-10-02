using HomelabBot.Data;
using HomelabBot.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HomelabBot.Services;

public sealed class TelemetryService
{
    private static readonly AsyncLocal<int?> CurrentInteractionId = new();

    private readonly IDbContextFactory<HomelabDbContext> _dbFactory;
    private readonly ILogger<TelemetryService> _logger;

    public TelemetryService(
        IDbContextFactory<HomelabDbContext> dbFactory,
        ILogger<TelemetryService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public int? ActiveInteractionId => CurrentInteractionId.Value;

    public void SetActiveInteraction(int? interactionId) => CurrentInteractionId.Value = interactionId;

    public async Task<LlmInteraction> LogInteractionStartAsync(
        ulong threadId,
        string model,
        string userPrompt,
        string? fullMessagesJson,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var conversation = await db.Conversations
            .FirstOrDefaultAsync(c => c.ThreadId == threadId, ct);

        var interaction = new LlmInteraction
        {
            ConversationId = conversation?.Id,
            ThreadId = threadId,
            Model = model,
            UserPrompt = userPrompt,
            FullMessagesJson = fullMessagesJson,
            Timestamp = DateTime.UtcNow
        };

        db.LlmInteractions.Add(interaction);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "LLM interaction started: {InteractionId} for thread {ThreadId}, model {Model}",
            interaction.Id, threadId, model);

        return interaction;
    }

    public async Task LogInteractionCompleteAsync(
        int interactionId,
        string response,
        LlmTokenUsage usage,
        long latencyMs,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var interaction = await db.LlmInteractions.FindAsync([interactionId], ct);
        if (interaction is null)
        {
            _logger.LogWarning("Interaction {InteractionId} not found for completion", interactionId);
            return;
        }

        interaction.Response = response;
        interaction.Success = true;
        interaction.PromptTokens = usage.PromptTokens;
        interaction.CompletionTokens = usage.CompletionTokens;
        interaction.CachedPromptTokens = usage.CachedPromptTokens;
        interaction.CacheWriteTokens = usage.CacheWriteTokens;
        interaction.LlmRounds = usage.Rounds;
        interaction.LatencyMs = latencyMs;

        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "LLM interaction completed: {InteractionId}, tokens: {PromptTokens}+{CompletionTokens} ({CachedPromptTokens} cached, {CacheWriteTokens} cache-written) over {LlmRounds} rounds, latency: {LatencyMs}ms",
            interactionId,
            usage.PromptTokens,
            usage.CompletionTokens,
            usage.CachedPromptTokens,
            usage.CacheWriteTokens,
            usage.Rounds,
            latencyMs);
    }

    public async Task LogInteractionErrorAsync(
        int interactionId,
        string errorMessage,
        long latencyMs,
        LlmTokenUsage? usage = null,
        CancellationToken ct = default)
    {
        if (!await SaveFailureAsync(interactionId, errorMessage, latencyMs, usage, ct))
        {
            return;
        }

        _logger.LogError(
            "LLM interaction failed: {InteractionId}, error: {Error}, latency: {LatencyMs}ms",
            interactionId, errorMessage, latencyMs);
    }

    // Best effort on purpose: runs after the caller's token is cancelled, so it uses its own
    // token and must never replace the OperationCanceledException the caller rethrows.
    public async Task TryLogInteractionCancelledAsync(int interactionId, long latencyMs, LlmTokenUsage usage)
    {
        try
        {
            if (await SaveFailureAsync(interactionId, "Cancelled", latencyMs, usage, CancellationToken.None))
            {
                _logger.LogInformation(
                    "LLM interaction cancelled: {InteractionId}, tokens: {PromptTokens}+{CompletionTokens} over {LlmRounds} rounds, latency: {LatencyMs}ms",
                    interactionId, usage.PromptTokens, usage.CompletionTokens, usage.Rounds, latencyMs);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record cancelled LLM interaction {InteractionId}", interactionId);
        }
    }

    private async Task<bool> SaveFailureAsync(
        int interactionId,
        string errorMessage,
        long latencyMs,
        LlmTokenUsage? usage,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var interaction = await db.LlmInteractions.FindAsync([interactionId], ct);
        if (interaction is null)
        {
            _logger.LogWarning("Interaction {InteractionId} not found for error logging", interactionId);
            return false;
        }

        interaction.Success = false;
        interaction.ErrorMessage = errorMessage;
        interaction.LatencyMs = latencyMs;

        // Rounds that completed before the failure were still billed.
        if (usage is { Rounds: > 0 })
        {
            interaction.PromptTokens = usage.PromptTokens;
            interaction.CompletionTokens = usage.CompletionTokens;
            interaction.CachedPromptTokens = usage.CachedPromptTokens;
            interaction.CacheWriteTokens = usage.CacheWriteTokens;
            interaction.LlmRounds = usage.Rounds;
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task LogToolCallAsync(
        int interactionId,
        string pluginName,
        string functionName,
        string? argumentsJson,
        string? resultJson,
        bool success,
        string? errorMessage,
        long latencyMs,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var toolCall = new ToolCallLog
        {
            LlmInteractionId = interactionId,
            PluginName = pluginName,
            FunctionName = functionName,
            ArgumentsJson = argumentsJson,
            ResultJson = resultJson,
            Success = success,
            ErrorMessage = errorMessage,
            LatencyMs = latencyMs,
            Timestamp = DateTime.UtcNow
        };

        db.ToolCallLogs.Add(toolCall);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Tool call logged: {Plugin}.{Function}, success: {Success}, latency: {LatencyMs}ms",
            pluginName, functionName, success, latencyMs);
    }
}
