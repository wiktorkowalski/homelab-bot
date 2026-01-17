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
        int? promptTokens,
        int? completionTokens,
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
        interaction.PromptTokens = promptTokens;
        interaction.CompletionTokens = completionTokens;
        interaction.LatencyMs = latencyMs;

        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "LLM interaction completed: {InteractionId}, tokens: {PromptTokens}+{CompletionTokens}, latency: {LatencyMs}ms",
            interactionId, promptTokens, completionTokens, latencyMs);
    }

    public async Task LogInteractionErrorAsync(
        int interactionId,
        string errorMessage,
        long latencyMs,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var interaction = await db.LlmInteractions.FindAsync([interactionId], ct);
        if (interaction is null)
        {
            _logger.LogWarning("Interaction {InteractionId} not found for error logging", interactionId);
            return;
        }

        interaction.Success = false;
        interaction.ErrorMessage = errorMessage;
        interaction.LatencyMs = latencyMs;

        await db.SaveChangesAsync(ct);

        _logger.LogError(
            "LLM interaction failed: {InteractionId}, error: {Error}, latency: {LatencyMs}ms",
            interactionId, errorMessage, latencyMs);
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
