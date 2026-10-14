namespace HomelabBot.Services;

internal static class ScheduledPrompts
{
    // Scheduled runs get only tools that read (KernelService.SelectFunctions). Without this line
    // the model looks for a restart tool that is not there.
    internal const string ReadOnlyToolsRule =
        "Your tools only read. Do not try to change anything (no restarts, no silences, no messages): recommend the action in your report instead.";
}
