namespace HomelabBot.Helpers;

public static class FormattingHelpers
{
    public static string FormatBytes(double bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes:F0} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024:F1} KB";
        }

        if (bytes < 1024 * 1024 * 1024)
        {
            return $"{bytes / (1024 * 1024):F1} MB";
        }

        return $"{bytes / (1024 * 1024 * 1024):F2} GB";
    }

    public static TimeSpan ParseDuration(string duration, TimeSpan? fallback = null)
    {
        var defaultValue = fallback ?? TimeSpan.FromHours(1);

        if (string.IsNullOrWhiteSpace(duration))
        {
            return defaultValue;
        }

        var unit = duration[^1];
        if (!int.TryParse(duration[..^1], out var value))
        {
            return defaultValue;
        }

        return unit switch
        {
            'm' => TimeSpan.FromMinutes(value),
            'h' => TimeSpan.FromHours(value),
            'd' => TimeSpan.FromDays(value),
            _ => defaultValue
        };
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays}d {duration.Hours}h";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        }

        return $"{(int)duration.TotalSeconds}s";
    }
}
