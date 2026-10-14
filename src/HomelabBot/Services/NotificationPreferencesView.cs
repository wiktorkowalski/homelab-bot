using DSharpPlus;
using DSharpPlus.Entities;
using HomelabBot.Data.Entities;
using HomelabBot.Models;

namespace HomelabBot.Services;

// The /preferences list: one numbered line and one "Remove" button per preference. Shared by
// the slash command and the button handler, which redraws the list after a removal.
internal static class NotificationPreferencesView
{
    // Discord allows 5 action rows of 5 buttons. Four rows keep the text under 2000 characters.
    internal const int MaxEntries = 20;
    private const int ButtonsPerRow = 5;
    private const int MaxFactChars = 60;

    internal static string Content(List<Knowledge> preferences, string? notice = null)
    {
        var lines = new List<string>();
        if (notice != null)
        {
            lines.Add(notice);
        }

        if (preferences.Count == 0)
        {
            lines.Add("No notification preferences are stored.");
            return string.Join("\n", lines);
        }

        lines.Add("**Notification preferences**");
        lines.AddRange(preferences
            .Take(MaxEntries)
            .Select((p, i) =>
                $"{i + 1}. `{SmartNotificationService.DescribePreference(p)}` — {ConversationSearchResult.Truncate(p.Fact.ReplaceLineEndings(" "), MaxFactChars)}"));

        if (preferences.Count > MaxEntries)
        {
            lines.Add($"Showing the first {MaxEntries} of {preferences.Count}. Remove some to see the rest.");
        }

        return string.Join("\n", lines);
    }

    internal static List<DiscordActionRowComponent> Components(List<Knowledge> preferences) =>
    [
        .. preferences
            .Take(MaxEntries)
            .Select((p, i) => (DiscordComponent)new DiscordButtonComponent(
                ButtonStyle.Danger, $"{SmartNotificationService.ButtonPrefixRemovePreference}{p.Id}", $"Remove {i + 1}"))
            .Chunk(ButtonsPerRow)
            .Select(row => new DiscordActionRowComponent(row)),
    ];
}
