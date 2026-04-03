namespace HomelabBot.Services;

internal static class NotificationPrompts
{
    internal const string InvestigationSystem = """
        You are a homelab infrastructure analyst investigating a potential issue.
        Use the available tools to investigate root causes — query logs, metrics, container states, and any relevant data.
        Be thorough but concise. Your goal is to determine if this issue is actionable and worth notifying the owner about.
        """;

    internal const string EndOfCycleLearning = """
        Review the conversation above from today's notification cycle.
        Extract notification preference updates based on the owner's responses:

        1. Issues the owner dismissed as unimportant (e.g., "this is fine", "don't worry about this", ignored after investigating)
        2. Issues the owner explicitly wants to always be notified about
        3. Any other notification preferences expressed

        For each preference, output a line in this exact format:
        SUPPRESS: <issue type> — <reason>
        ALWAYS: <issue type> — <reason>

        If no clear preferences were expressed, output:
        NO_UPDATES

        Be conservative — only extract preferences that were clearly expressed or strongly implied by the conversation.
        """;

    internal const string TagNotify = "[NOTIFY]";
    internal const string TagSilent = "[SILENT]";
    internal const string TagNoUpdates = "NO_UPDATES";
    internal const int MaxTokens = 4096;

    internal static string BuildDecisionOnlyPrompt(string summary, string rawData, string preferences)
    {
        return $"""
            An automated healthcheck produced the following report:

            {rawData}

            {(string.IsNullOrWhiteSpace(preferences) ? "" : $"""
            The owner has these notification preferences:
            {preferences}

            Respect suppressed issue types unless the situation has clearly escalated beyond what was previously dismissed.
            """)}

            Based on this report, decide if the owner should be notified.
            Do NOT use any tools — the investigation is already complete.

            Notify if: there are actionable findings, degraded services, warnings, or anything the owner should act on.
            Stay silent if: everything is healthy, no actionable findings, all-clear status.

            Respond with ONLY one of these tags on its own line:
            [NOTIFY] — if the owner should be notified (the report above will be sent as-is)
            [SILENT] — if this is not worth a notification
            """;
    }

    internal static string BuildInvestigationPrompt(string summary, string rawData, string preferences)
    {
        return $"""
            An automated monitor detected the following:

            {summary}

            Raw data:
            {rawData}

            {(string.IsNullOrWhiteSpace(preferences) ? "" : $"""
            The owner has these notification preferences:
            {preferences}

            Respect suppressed issue types unless the situation has clearly escalated beyond what was previously dismissed.
            """)}

            INSTRUCTIONS:
            1. Use your tools to investigate this finding. Check related logs, metrics, and container states.
            2. Determine the root cause or likely explanation.
            3. Decide if this is worth notifying the owner about:
               - YES if: actionable problem, degraded service, data loss risk, something the owner should know
               - NO if: transient blip that resolved, known benign behavior, matches a suppressed preference
            4. End your response with exactly one of these tags on its own line:
               [NOTIFY] — if the owner should be notified
               [SILENT] — if this is not worth a notification

            If [NOTIFY], write a concise report (under 1500 chars) with:
            - What was detected
            - What you found during investigation (root cause / context)
            - Recommended action if any
            Use emoji severity indicators (🔴 critical, 🟡 warning, 🔵 info).
            """;
    }
}
