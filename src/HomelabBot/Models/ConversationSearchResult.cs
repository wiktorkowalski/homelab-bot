using System.Text;
using HomelabBot.Data.Entities;

namespace HomelabBot.Models;

public sealed class ConversationSearchResult
{
    public required int ConversationId { get; init; }
    public required ulong ThreadId { get; init; }
    public string? Title { get; init; }
    public required DateTime Date { get; init; }
    public required int Score { get; init; }
    public required List<ConversationMessage> RelevantMessages { get; init; }

    public string TimeAgo
    {
        get
        {
            var age = DateTime.UtcNow - Date;
            return age.TotalDays > 1 ? $"{(int)age.TotalDays}d ago" : $"{(int)age.TotalHours}h ago";
        }
    }

    public string DisplayTitle => Title ?? "Untitled";

    public static string FormatResults(
        IReadOnlyList<ConversationSearchResult> results, int previewMaxLength = 150)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} relevant conversation(s):\n");

        foreach (var r in results)
        {
            sb.AppendLine($"**[{r.TimeAgo}] {r.DisplayTitle}**");

            foreach (var msg in r.RelevantMessages)
            {
                var preview = Truncate(msg.Content, previewMaxLength);
                sb.AppendLine($"  {msg.Role}: {preview}");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string Truncate(string text, int maxLength)
    {
        if (maxLength < 4 || text.Length <= maxLength)
            return text;

        return text[.. (maxLength - 3)] + "...";
    }
}
