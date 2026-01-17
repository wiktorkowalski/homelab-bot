namespace HomelabBot.Data.Entities;

public sealed class ToolCallLog
{
    public int Id { get; set; }
    public int LlmInteractionId { get; set; }
    public LlmInteraction LlmInteraction { get; set; } = null!;
    public required string PluginName { get; set; }
    public required string FunctionName { get; set; }
    public string? ArgumentsJson { get; set; }
    public string? ResultJson { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public long LatencyMs { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
