using System.ComponentModel;
using HomelabBot.Data.Entities;
using HomelabBot.Helpers;
using HomelabBot.Services;
using Microsoft.SemanticKernel;

namespace HomelabBot.Plugins;

// The bot's escalation to Claude Code on the host. Kernel only, never on the MCP surface: Claude on
// the host uses this bot's MCP server, and ask_bot → ask_claude would start Claude again.
// [ChangesState] gives the guards: the owner approves each call with a button, scheduled runs never
// see the tool, and an ask_bot turn has no chat to ask in, so the call fails closed.
public sealed class ClaudePlugin(ClaudeRunnerService runner)
{
    [KernelFunction]
    [ChangesState(OwnerOnly = true)]
    [Description(
        "Hands a task to Claude Code (a stronger model) on the homelab host. It has a shell, the host's files and config, " +
        "and the homelab tools. Use it when the work needs many steps across logs, metrics and config, a root-cause analysis " +
        "your tools cannot finish, a shell, or a change on the host that no tool of yours does. Do not use it for a question " +
        "that one or two of your tools answer. The owner approves each call with a button. The call returns at once; the " +
        "result arrives later as a separate message in this chat, with the session id in its footer.")]
    public string AskClaude(
        [Description("The task for Claude, in plain words. Keep it short: at most about 1500 characters.")] string task,
        [Description("The session id from the footer of an earlier Claude result, to continue that session. Omit it to start a new session.")]
        string? sessionId = null)
    {
        if (ToolConfirmationScope.CurrentChat is not { } chat)
        {
            return ToolResult.Failure("This conversation has no Discord chat for the result. It did not run.");
        }

        if (string.IsNullOrWhiteSpace(task))
        {
            return ToolResult.Failure("task is empty. Write what Claude should do.");
        }

        Guid session;
        if (sessionId is null)
        {
            session = Guid.NewGuid();
        }
        else if (!Guid.TryParse(sessionId, out session))
        {
            return ToolResult.Failure($"sessionId '{sessionId}' is not a session id. Copy it from the footer of a Claude result, or omit it.");
        }

        var refusal = runner.TryStart(new ClaudeRunRequest
        {
            Origin = ClaudeRunOrigin.Llm,
            Task = task,
            SessionId = session,
            Resume = sessionId is not null,
            ChannelId = chat.ChannelId,
        });
        if (refusal is not null)
        {
            return ToolResult.Failure(refusal);
        }

        return $"Started Claude session {session}. The result arrives as a separate message in this chat.";
    }
}
