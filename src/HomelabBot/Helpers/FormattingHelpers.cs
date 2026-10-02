using System.Globalization;

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

    // Strict variant for tool input: an invalid range must fail loudly, not silently become 1h.
    public static bool TryParseDuration(string? input, TimeSpan max, out TimeSpan duration, out string error)
    {
        duration = TimeSpan.FromHours(1);
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            return true;
        }

        var trimmed = input.Trim();
        var unit = trimmed[^1];
        if (trimmed.Length < 2
            || unit is not ('m' or 'h' or 'd')
            || !int.TryParse(trimmed[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value <= 0)
        {
            error = $"Invalid time range '{input}'. Use a positive whole number with m, h or d (e.g. '30m', '24h', '7d').";
            return false;
        }

        // Compare in minutes so huge values cannot overflow TimeSpan.
        if (value > max.TotalMinutes / UnitMinutes(unit))
        {
            error = $"Time range '{input}' exceeds the maximum of {FormatCompactDuration(max)}.";
            return false;
        }

        duration = TimeSpan.FromMinutes((double)value * UnitMinutes(unit));
        return true;
    }

    // Single-unit form ("90m", "24h", "7d"): valid as a LogQL range and readable in tool output.
    public static string FormatCompactDuration(TimeSpan duration)
    {
        var minutes = (long)duration.TotalMinutes;
        if (minutes > 0 && minutes % 1440 == 0)
        {
            return $"{minutes / 1440}d";
        }

        if (minutes > 0 && minutes % 60 == 0)
        {
            return $"{minutes / 60}h";
        }

        return $"{minutes}m";
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

    private static int UnitMinutes(char unit) => unit switch
    {
        'm' => 1,
        'h' => 60,
        _ => 1440,
    };
}
