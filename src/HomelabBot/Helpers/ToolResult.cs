namespace HomelabBot.Helpers;

internal static class ToolResult
{
    private const string ErrorPrefix = "Error";

    // Plugins catch their faults and answer with text that starts with "Error": a failed tool call throws no exception.
    public static string Failure(string message) => $"{ErrorPrefix}: {message}";

    public static bool IsFailure(string? result) =>
        result?.StartsWith(ErrorPrefix, StringComparison.Ordinal) == true;
}
