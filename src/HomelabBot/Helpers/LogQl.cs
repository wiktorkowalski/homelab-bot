using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HomelabBot.Helpers;

internal static class LogQl
{
    private static readonly Regex LookbackPattern = new(@"^(\d{1,6})([mhd])$", RegexOptions.Compiled);

    // Loki's default max_query_length is 721h; 30d stays inside it.
    internal static readonly TimeSpan MaxLookback = TimeSpan.FromDays(30);

    // Level tokens only (Serilog [ERR], *arr [Error], JSON "level", logfmt level=), so error words in INF text don't count.
    internal const string ErrorLevelRegex =
        "(?i)(\\[(err|eror|error|ftl|fatal|crit|critical|panic)\\]"
        + "|\\b(err|ftl)\\]"
        + "|\"(level|lvl|severity)\"\\s*:\\s*\"(err|eror|error|fatal|crit|critical|panic)\""
        + "|\\b(level|lvl|severity)=\"?(err|eror|error|fatal|crit|critical|panic)\\b)";

    // Mirrors Go's regexp.QuoteMeta so the escape set matches Loki's RE2 engine, not .NET's.
    internal static string EscapeRegex(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (@"\.+*?()|[]{}^$".Contains(c))
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    internal static string QuoteString(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\':
                    sb.Append(@"\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\n':
                    sb.Append(@"\n");
                    break;
                case '\r':
                    sb.Append(@"\r");
                    break;
                case '\t':
                    sb.Append(@"\t");
                    break;
                default:
                    if (char.IsControl(c))
                    {
                        sb.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

    internal static string ContainsIgnoreCaseFilter(string text) =>
        $"|~ {QuoteString("(?i)" + EscapeRegex(text))}";

    internal static bool TryParseLookback(string? input, out TimeSpan lookback, out string error)
    {
        lookback = default;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            lookback = TimeSpan.FromHours(1);
            return true;
        }

        var match = LookbackPattern.Match(input.Trim());
        if (!match.Success
            || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value <= 0)
        {
            error = $"Invalid time range '{input}'. Use a positive number with m, h or d (e.g. '30m', '24h', '7d').";
            return false;
        }

        var maxValue = match.Groups[2].Value switch
        {
            "m" => MaxLookback.TotalMinutes,
            "h" => MaxLookback.TotalHours,
            _ => MaxLookback.TotalDays,
        };

        if (value > maxValue)
        {
            error = $"Time range '{input}' exceeds the maximum of {(int)MaxLookback.TotalDays}d.";
            return false;
        }

        lookback = match.Groups[2].Value switch
        {
            "m" => TimeSpan.FromMinutes(value),
            "h" => TimeSpan.FromHours(value),
            _ => TimeSpan.FromDays(value),
        };
        return true;
    }
}
