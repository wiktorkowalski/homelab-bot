using System.Globalization;
using System.Text;

namespace HomelabBot.Helpers;

internal static class LogQl
{
    // The "Default" HttpClient has a 10s per-attempt timeout; wider scans time out and retry.
    internal static readonly TimeSpan MaxLookback = TimeSpan.FromDays(7);

    // Matches level tokens, not error words in message text. Bare-word formats are uppercase-only and
    // anchored to the first 40 chars; JSON and logfmt keys cannot be anchored (key order varies).
    internal const string ErrorLevelRegex =
        "(?:"

        // Serilog "[ERR]", *arr "[Error]", "[2026-10-02 13:33:01.412 ERR]".
        + @"^.{0,40}\[(?i:err|eror|error|ftl|fatal|crit|critical|panic)\]"
        + @"|^.{0,40}\b(?:ERR|FTL)\]"

        // *arr / NLog pipe layout "date|Error|Component|msg".
        + @"|^.{0,40}\|(?i:error|fatal)\|"

        // Python / Home Assistant "... ERROR (MainThread)", Postgres "ERROR:" / "FATAL:" / "PANIC:".
        + @"|^.{0,40}\b(?:ERROR|CRITICAL|FATAL|PANIC)\b"

        // .NET console logger "fail: " / "crit: ".
        + @"|^(?:fail|crit): "

        // zigbee2mqtt / winston "[date] error: " and legacy "Zigbee2MQTT:error ".
        + @"|^(?:\[[^\]]{1,40}\] )?error: "
        + @"|^Zigbee2MQTT:error "

        // JSON level fields: string levels, Pino numeric 50/60, Serilog CLEF "@l".
        + @"|""(?:level|lvl|severity)""\s*:\s*""(?i:err|eror|error|fatal|crit|critical|panic)"""
        + @"|""level""\s*:\s*(?:50|60)\b"
        + @"|""@l""\s*:\s*""(?:Error|Fatal)"""

        // logfmt "level=error".
        + @"|(?:^|\s)(?:level|lvl|severity)=""?(?i:err|eror|error|fatal|crit|critical|panic)\b"
        + ")";

    // Loki's default max_entries_limit_per_query.
    internal const int MaxQueryLimit = 5000;

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

    internal static int ClampLimit(int limit) => Math.Clamp(limit, 1, MaxQueryLimit);
}
