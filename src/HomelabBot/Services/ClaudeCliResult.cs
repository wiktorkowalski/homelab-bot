using System.Text.Json;

namespace HomelabBot.Services;

internal sealed record ClaudePermissionDenial
{
    public required string ToolName { get; init; }

    public required string Input { get; init; }
}

// The JSON object of claude -p --output-format json, only the fields the bot uses.
internal sealed record ClaudeCliResult
{
    public required string Result { get; init; }

    public string? SessionId { get; init; }

    public bool IsError { get; init; }

    public string? Subtype { get; init; }

    public int? NumTurns { get; init; }

    public long? DurationMs { get; init; }

    public double? TotalCostUsd { get; init; }

    public IReadOnlyList<ClaudePermissionDenial> PermissionDenials { get; init; } = [];

    // claude -p can print a warning line before the JSON, so the last line that starts with '{' counts.
    public static ClaudeCliResult? Parse(string stdout)
    {
        var line = stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
        if (line is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new ClaudeCliResult
            {
                Result = String(root, "result") ?? "",
                SessionId = String(root, "session_id"),
                IsError = root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True,
                Subtype = String(root, "subtype"),
                NumTurns = root.TryGetProperty("num_turns", out var turns) && turns.TryGetInt32(out var t) ? t : null,
                DurationMs = root.TryGetProperty("duration_ms", out var duration) && duration.TryGetInt64(out var d) ? d : null,
                TotalCostUsd = root.TryGetProperty("total_cost_usd", out var cost) && cost.TryGetDouble(out var c) ? c : null,
                PermissionDenials = Denials(root),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // A Bash denial shows its command; other tools show their input as JSON.
    private static List<ClaudePermissionDenial> Denials(JsonElement root)
    {
        if (!root.TryGetProperty("permission_denials", out var denials) || denials.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return denials.EnumerateArray()
            .Where(d => d.ValueKind == JsonValueKind.Object)
            .Select(d =>
            {
                var input = d.TryGetProperty("tool_input", out var i) ? i : default;
                var text = input.ValueKind == JsonValueKind.Object && String(input, "command") is { } command
                    ? command
                    : input.ValueKind == JsonValueKind.Undefined ? "" : input.GetRawText();
                return new ClaudePermissionDenial { ToolName = String(d, "tool_name") ?? "?", Input = text };
            })
            .ToList();
    }
}
