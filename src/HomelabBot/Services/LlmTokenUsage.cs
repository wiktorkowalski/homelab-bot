namespace HomelabBot.Services;

// Public because TelemetryService.LogInteractionCompleteAsync is public and takes it.
public sealed record LlmTokenUsage
{
    public static readonly LlmTokenUsage None = new();

    public int? PromptTokens { get; init; }

    public int? CompletionTokens { get; init; }

    public int? CachedPromptTokens { get; init; }

    public int? CacheWriteTokens { get; init; }

    // Billed requests summed; 0 means no per-round usage was captured.
    public int Rounds { get; init; }
}
