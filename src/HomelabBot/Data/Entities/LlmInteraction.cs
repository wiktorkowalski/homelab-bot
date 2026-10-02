namespace HomelabBot.Data.Entities;

public sealed class LlmInteraction
{
    public int Id { get; set; }

    public int? ConversationId { get; set; }

    public Conversation? Conversation { get; set; }

    public ulong? ThreadId { get; set; }

    public required string Model { get; set; }

    public required string UserPrompt { get; set; }

    public string? FullMessagesJson { get; set; }

    public string? Response { get; set; }

    public string? ErrorMessage { get; set; }

    public bool Success { get; set; }

    public int? PromptTokens { get; set; }

    public int? CompletionTokens { get; set; }

    // Subset of PromptTokens served from the provider's prompt cache.
    public int? CachedPromptTokens { get; set; }

    // Subset of PromptTokens written to the provider's prompt cache (billed at a premium).
    public int? CacheWriteTokens { get; set; }

    // Billed LLM requests during the interaction (tool rounds, retries, nested tool LLM calls).
    // 0 means usage came from the final response only; null means not recorded.
    public int? LlmRounds { get; set; }

    public long LatencyMs { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public List<ToolCallLog> ToolCalls { get; set; } = [];
}
