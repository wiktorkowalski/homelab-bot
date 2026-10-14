using DSharpPlus;
using DSharpPlus.Entities;
using HomelabBot.Data.Entities;
using HomelabBot.Models;

namespace HomelabBot.Services;

// The /preferences list: one numbered line and one "Remove" button per preference. Shared by
// the slash command and the button handler, which redraws the list after a removal.
internal static class NotificationPreferencesView
{
    // Discord allows 5 action rows of 5 buttons; four rows are enough for one screen.
    internal const int MaxEntries = 20;
    private const int ButtonsPerRow = 5;
    private const int MaxFactChars = 60;
    private const int MaxTopicChars = 80;

    // Discord rejects a message over 2000 characters, and then neither the list nor its Remove
    // buttons work. Entry lines share this budget; the rest is left for header, notice and footer.
    private const int MaxEntryChars = 1600;
    private const int MaxNoticeChars = 200;

    internal static string Content(List<Knowledge> preferences, string? notice = null)
    {
        var lines = new List<string>();
        if (notice != null)
        {
            lines.Add(ConversationSearchResult.Truncate(notice, MaxNoticeChars));
        }

        if (preferences.Count == 0)
        {
            lines.Add("No notification preferences are stored.");
            return string.Join("\n", lines);
        }

        var entries = EntryLines(preferences);
        lines.Add("**Notification preferences**");
        lines.AddRange(entries);

        if (preferences.Count > entries.Count)
        {
            lines.Add($"Showing the first {entries.Count} of {preferences.Count}. Remove some to see the rest.");
        }

        return string.Join("\n", lines);
    }

    // One button per shown line, so a button never points at a preference the text leaves out.
    internal static List<DiscordActionRowComponent> Components(List<Knowledge> preferences) =>
    [
        .. preferences
            .Take(EntryLines(preferences).Count)
            .Select((p, i) => (DiscordComponent)new DiscordButtonComponent(
                ButtonStyle.Danger, $"{SmartNotificationService.ButtonPrefixRemovePreference}{p.Id}", $"Remove {i + 1}"))
            .Chunk(ButtonsPerRow)
            .Select(row => new DiscordActionRowComponent(row)),
    ];

    private static List<string> EntryLines(List<Knowledge> preferences)
    {
        var lines = new List<string>();
        var used = 0;
        foreach (var (pref, i) in preferences.Take(MaxEntries).Select((p, i) => (p, i)))
        {
            var topic = ConversationSearchResult.Truncate(SmartNotificationService.DescribePreference(pref), MaxTopicChars);
            var fact = ConversationSearchResult.Truncate(pref.Fact.ReplaceLineEndings(" "), MaxFactChars);
            var line = $"{i + 1}. `{topic}` — {fact}";
            if (used + line.Length + 1 > MaxEntryChars)
            {
                break;
            }

            lines.Add(line);
            used += line.Length + 1;
        }

        return lines;
    }
}
