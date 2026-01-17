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
    public long LatencyMs { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public List<ToolCallLog> ToolCalls { get; set; } = [];
}
