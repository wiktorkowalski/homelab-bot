namespace HomelabBot.Services;

public static class MessageSplitService
{
    private const int DefaultMaxLength = 1950; // 50 char safety margin for Discord's 2000 limit

    public static bool IsLongMessage(string content, int threshold = DefaultMaxLength)
        => content.Length > threshold;

    public static List<string> SplitIntoSections(string content, int maxLength = DefaultMaxLength)
    {
        if (content.Length <= maxLength)
            return [content];

        var chunks = new List<string>();
        var remaining = content;

        while (remaining.Length > maxLength)
        {
            var splitIndex = FindBestSplitPoint(remaining, maxLength);
            chunks.Add(remaining[..splitIndex].TrimEnd());
            remaining = remaining[splitIndex..].TrimStart();
        }

        if (!string.IsNullOrWhiteSpace(remaining))
            chunks.Add(remaining);

        return chunks;
    }

    private static int FindBestSplitPoint(string text, int maxLength)
    {
        // Priority 1: Split on section boundaries (double newline, headers, dividers)
        var sectionSplitters = new[] { "\n\n", "\n**", "\n##", "\n---" };
        foreach (var splitter in sectionSplitters)
        {
            var idx = text.LastIndexOf(splitter, maxLength - 1, StringComparison.Ordinal);
            if (idx > maxLength / 4) // Don't split too early
                return idx;
        }

        // Priority 2: Single newline
        var newlineIdx = text.LastIndexOf('\n', maxLength - 1);
        if (newlineIdx > maxLength / 4)
            return newlineIdx;

        // Priority 3: Space
        var spaceIdx = text.LastIndexOf(' ', maxLength - 1);
        if (spaceIdx > maxLength / 4)
            return spaceIdx;

        // Fallback: hard split
        return maxLength;
    }
}
