using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomelabBot.Commands;
using HomelabBot.Configuration;
using HomelabBot.Data.Entities;
using HomelabBot.Plugins;
using HomelabBot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace HomelabBot.Tests;

public class ClaudeCliResultTests
{
    private const string Json =
        """{"type":"result","subtype":"success","is_error":false,"duration_ms":4200,"num_turns":3,"result":"OK","session_id":"s-1","total_cost_usd":0.125,"permission_denials":[]}""";

    [Fact]
    public void Parse_ReadsTheFieldsTheBotUses()
    {
        var result = ClaudeCliResult.Parse(Json + "\n");

        Assert.NotNull(result);
        Assert.Equal("OK", result.Result);
        Assert.Equal("s-1", result.SessionId);
        Assert.False(result.IsError);
        Assert.Equal("success", result.Subtype);
        Assert.Equal(3, result.NumTurns);
        Assert.Equal(4200, result.DurationMs);
        Assert.Equal(0.125, result.TotalCostUsd);
        Assert.Empty(result.PermissionDenials);
    }

    // claude -p prints a warning line before the JSON when stdin is empty.
    [Fact]
    public void Parse_SkipsAWarningLineBeforeTheJson()
    {
        var result = ClaudeCliResult.Parse("Warning: no stdin data received in 3s, proceeding without it.\n" + Json);

        Assert.Equal("OK", result?.Result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Error: something broke\n")]
    [InlineData("{not json")]
    public void Parse_NoJsonObject_IsNull(string stdout)
    {
        Assert.Null(ClaudeCliResult.Parse(stdout));
    }

    [Fact]
    public void Parse_Denials_ShowABashCommand_AndOtherInputAsJson()
    {
        var result = ClaudeCliResult.Parse(
            """{"result":"x","permission_denials":[{"tool_name":"Bash","tool_input":{"command":"rm -rf /srv"}},{"tool_name":"Write","tool_input":{"file_path":"/etc/x"}}]}""");

        Assert.NotNull(result);
        Assert.Equal(
            [new ClaudePermissionDenial { ToolName = "Bash", Input = "rm -rf /srv" }, new ClaudePermissionDenial { ToolName = "Write", Input = """{"file_path":"/etc/x"}""" }],
            result.PermissionDenials);
    }
}

public class ClaudeOutputRedactorTests
{
    // Split literals: a whole fake token in the source trips GitHub push protection.
    [Theory]
    [InlineData("token sk-ant-api03-AbCdEfGhIjKlMnOpQrSt end", "token [redacted] end")]
    [InlineData("ghp_" + "a1B2c3D4e5F6g7H8i9J0k1L2m3N4o5P6q7R8", "[redacted]")]
    [InlineData("pat github_pat_" + "11ABCDEFG0123456789_abcdefghijklmnop", "pat [redacted]")]
    [InlineData("slack xoxb-" + "1234567890-abcdefghij", "slack [redacted]")]
    [InlineData("jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U", "jwt [redacted]")]
    [InlineData("bot MTA5ODc2NTQzMjEwOTg3NjU0" + ".GaBcDe." + "abcdefghijklmnopqrstuvwxyz0123456789", "bot [redacted]")]
    [InlineData("DB_PASSWORD=hunter2", "DB_PASSWORD=[redacted]")]
    [InlineData("api_key: abc123", "api_key: [redacted]")]
    [InlineData("  export GITHUB_TOKEN=xyz", "  export GITHUB_TOKEN=[redacted]")]
    [InlineData("SECRET_KEY=sk-ant-api03-AbCdEfGhIjKlMnOpQrSt", "SECRET_KEY=[redacted]")]
    [InlineData("docker run -e POSTGRES_PASSWORD=s3cret image", "docker run -e POSTGRES_PASSWORD=[redacted] image")]
    [InlineData("Authorization: Bearer abcdef123456", "Authorization: Bearer [redacted]")]
    [InlineData("curl -H 'Authorization: Basic dXNlcjpwYXNz' x", "curl -H 'Authorization: Basic [redacted]' x")]
    [InlineData("postgres://admin:s3cret@db:5432/app", "postgres://[redacted]@db:5432/app")]
    [InlineData("before\n-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaA\n-----END OPENSSH PRIVATE KEY-----\nafter", "before\n[redacted]\nafter")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIEow cut here", "[redacted]")]
    public void Redact_ReplacesEachSecretShape(string input, string expected)
    {
        Assert.Equal(expected, ClaudeOutputRedactor.Redact(input));
    }

    [Fact]
    public void Redact_LeavesNormalTextAlone()
    {
        const string text = "Disk tank is 81% full.\nBasic configuration is fine; primary_key=id stays.\nKey findings below.";

        Assert.Equal(text, ClaudeOutputRedactor.Redact(text));
    }
}

public sealed class ClaudeRunnerServiceTests : IDisposable
{
    private const ulong ThreadId = 777;
    private const ulong OtherUser = 42;

    private readonly KernelServiceHarness _harness = new(new ScriptedClaudeHandler().CreateClient());
    private readonly FakeClaudeProcess _process = new();
    private readonly FakeClaudeChat _chat = new();
    private readonly RecordingLogger<ClaudeRunnerService> _logger = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void Timeouts_AreThirtyMinutesForTheOwner_AndTenForTheLlm()
    {
        var config = new ClaudeRunnerConfiguration();

        Assert.Equal(TimeSpan.FromMinutes(30), ClaudeRunnerService.TimeoutFor(config, ClaudeRunOrigin.Owner));
        Assert.Equal(TimeSpan.FromMinutes(10), ClaudeRunnerService.TimeoutFor(config, ClaudeRunOrigin.Llm));
    }

    [Fact]
    public void SshArguments_AreTheCommandOfTheIssue()
    {
        var config = new ClaudeRunnerConfiguration { KeyPath = "/app/ssh/key", KnownHostsPath = "/app/ssh/kh" };

        Assert.Equal(
            ["-i", "/app/ssh/key", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "UserKnownHostsFile=/app/ssh/kh", "ubuntu@host.docker.internal", "new 0a1b"],
            SshClaudeProcessRunner.Arguments(config, "new 0a1b"));
    }

    [Fact]
    public void Lock_IsTriedNeverWaited_AndFreeAgainAfterRelease()
    {
        var runner = CreateRunner();

        var first = runner.TryReserve();
        Assert.NotNull(first);
        Assert.Null(runner.TryReserve());

        first.Dispose();
        first.Dispose();
        using var again = runner.TryReserve();
        Assert.NotNull(again);
        Assert.Null(runner.TryReserve());
    }

    [Fact]
    public async Task SecondRun_WhileOneIsActive_IsRefused_AndDoesNotStart()
    {
        var gate = new TaskCompletionSource<ClaudeProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _process.Respond = _ => gate.Task;
        var runner = CreateRunner();

        Assert.Null(runner.TryStart(Request(ClaudeRunOrigin.Owner, ThreadId)));
        Assert.Equal(ClaudeRunnerService.BusyText, runner.TryStart(Request(ClaudeRunOrigin.Owner, ThreadId)));

        gate.SetResult(FakeClaudeProcess.Result("done"));
        await _chat.WaitForSendsAsync(1);
        await WaitUntilAsync(() => runner.TryReserve() is { } slot && Release(slot));
        Assert.Single(_process.Calls);
    }

    [Fact]
    public void Disabled_RefusesEveryStart()
    {
        var runner = CreateRunner(new ClaudeRunnerConfiguration { Enabled = false });

        Assert.Equal(ClaudeRunnerService.DisabledText, runner.TryStart(Request(ClaudeRunOrigin.Owner, ThreadId)));
        Assert.Empty(_process.Calls);
    }

    [Fact]
    public async Task OwnerRun_PostsResultWithFooter_ToItsThread_AndStoresTheRun()
    {
        _process.Respond = _ => Task.FromResult(FakeClaudeProcess.Result(
            "All good.", turns: 4, cost: 0.4, denials: """[{"tool_name":"Bash","tool_input":{"command":"sudo reboot"}}]"""));
        var runner = CreateRunner();
        var request = Request(ClaudeRunOrigin.Owner, ThreadId);

        await runner.LaunchAsync(runner.TryReserve()!, request);

        Assert.Equal([($"new {request.SessionId}", "check the disks")], _process.Calls);
        var sent = Assert.Single(_chat.Sent);
        Assert.Equal(ThreadId, sent.ChannelId);
        Assert.StartsWith("All good.\n\n-# 4s · 4 turns · $0.40 API list price, not billed · session `", sent.Text, StringComparison.Ordinal);
        Assert.Contains("🚫 Denied: Bash `sudo reboot`", sent.Text, StringComparison.Ordinal);

        await using var db = _harness.DbFactory.CreateDbContext();
        var run = await db.ClaudeRuns.SingleAsync();
        Assert.Equal(ClaudeRunState.Completed, run.State);
        Assert.Equal(ClaudeRunOrigin.Owner, run.Origin);
        Assert.Equal(ThreadId, run.ChannelId);
        Assert.Equal(4, run.NumTurns);
        Assert.Equal(0.4, run.TotalCostUsd);
        Assert.False(run.IsError);
        Assert.Contains("sudo reboot", run.PermissionDenialsJson, StringComparison.Ordinal);
        Assert.NotNull(run.EndedAt);
        Assert.Empty(db.ClaudeSessionLinks);
    }

    [Fact]
    public async Task DmRun_RecordsACappedCopy_AndLinksEachMessageToTheSession()
    {
        var longResult = new string('x', 120);
        _process.Respond = _ => Task.FromResult(FakeClaudeProcess.Result(longResult));
        var runner = CreateRunner(new ClaudeRunnerConfiguration { Enabled = true, RecordedResultMaxChars = 50 });
        var request = Request(ClaudeRunOrigin.Llm, channelId: null);

        await runner.LaunchAsync(runner.TryReserve()!, request);

        var sent = Assert.Single(_chat.Sent);
        Assert.Null(sent.ChannelId);
        Assert.StartsWith(longResult + "\n\n-# ", sent.Text, StringComparison.Ordinal);
        Assert.StartsWith(new string('x', 50) + "\n[Cut at 50 characters", sent.Record, StringComparison.Ordinal);
        Assert.Contains($"session `{request.SessionId}`", sent.Record, StringComparison.Ordinal);

        var (route, sessionId) = await runner.RouteAsync(sent.Ids.Single(), HomelabOwner.DiscordUserId);
        Assert.Equal(ClaudeRoute.Claude, route);
        Assert.Equal(request.SessionId, sessionId);
    }

    [Fact]
    public async Task Timeout_KillsTheProcess_TellsTheChat_AndStoresTimedOut()
    {
        var cancelled = false;
        _process.Respond = async ct =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                throw;
            }

            return FakeClaudeProcess.Result("never");
        };
        var runner = CreateRunner(timeoutFor: o => o == ClaudeRunOrigin.Llm ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromMinutes(5));

        await runner.LaunchAsync(runner.TryReserve()!, Request(ClaudeRunOrigin.Llm, ThreadId));

        Assert.True(cancelled);
        Assert.Contains("time limit", Assert.Single(_chat.Sent).Text, StringComparison.Ordinal);
        Assert.Contains("can be resumed", _chat.Sent.Single().Text, StringComparison.Ordinal);
        await using var db = _harness.DbFactory.CreateDbContext();
        Assert.Equal(ClaudeRunState.TimedOut, (await db.ClaudeRuns.SingleAsync()).State);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("time limit", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_NonZeroExitWithoutJson_IsAFailure_ThatShowsStderr()
    {
        var runner = CreateRunner();

        var outcome = runner.Evaluate(new ClaudeProcessResult { ExitCode = 255, Stderr = "Permission denied (publickey)." }, Guid.NewGuid());

        Assert.Equal(ClaudeRunState.Failed, outcome.State);
        Assert.Contains("ssh exit code 255", outcome.Body, StringComparison.Ordinal);
        Assert.Contains("Permission denied (publickey).", outcome.Body, StringComparison.Ordinal);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("exited with code 255", StringComparison.Ordinal));
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("no JSON result", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_EmptyOutput_IsAFailure()
    {
        var outcome = CreateRunner().Evaluate(new ClaudeProcessResult(), Guid.NewGuid());

        Assert.Equal(ClaudeRunState.Failed, outcome.State);
        Assert.Contains("returned no result", outcome.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_UsageLimit_IsSaid_AndNotRetried()
    {
        var process = new ClaudeProcessResult
        {
            ExitCode = 1,
            Stdout = """{"result":"Claude AI usage limit reached|1760540400","is_error":true,"subtype":"error_during_execution"}""",
        };

        var outcome = CreateRunner().Evaluate(process, Guid.NewGuid());

        Assert.Equal(ClaudeRunState.UsageLimit, outcome.State);
        Assert.Contains("usage limit is reached", outcome.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_RedactsTheResult()
    {
        var outcome = CreateRunner().Evaluate(FakeClaudeProcess.Result("the key is DB_PASSWORD=hunter2"), Guid.NewGuid());

        Assert.Equal("the key is DB_PASSWORD=[redacted]", outcome.Body);
    }

    [Fact]
    public async Task Startup_MarksRunsWithNoEnd_AsInterrupted_AndTellsTheirChat()
    {
        var oldSession = Guid.NewGuid();
        await using (var db = _harness.DbFactory.CreateDbContext())
        {
            db.ClaudeRuns.Add(new ClaudeRun { Task = "old", SessionId = oldSession, StartedAt = DateTime.UtcNow.AddMinutes(-3) });
            db.ClaudeRuns.Add(new ClaudeRun { Task = "done", SessionId = Guid.NewGuid(), StartedAt = DateTime.UtcNow.AddMinutes(-9), State = ClaudeRunState.Completed });
            await db.SaveChangesAsync();
        }

        var runner = CreateRunner();
        await using (var db = _harness.DbFactory.CreateDbContext())
        {
            db.ClaudeRuns.Add(new ClaudeRun { Task = "new", SessionId = Guid.NewGuid(), StartedAt = DateTime.UtcNow.AddSeconds(1) });
            await db.SaveChangesAsync();
        }

        var interrupted = await runner.MarkInterruptedRunsAsync(CancellationToken.None);
        await runner.NotifyInterruptedAsync(Assert.Single(interrupted));

        await using var check = _harness.DbFactory.CreateDbContext();
        var states = await check.ClaudeRuns.OrderBy(r => r.Id).Select(r => r.State).ToListAsync();
        Assert.Equal([ClaudeRunState.Interrupted, ClaudeRunState.Completed, ClaudeRunState.Running], states);
        var sent = Assert.Single(_chat.Sent);
        Assert.Contains("interrupted", sent.Text, StringComparison.Ordinal);
        Assert.Equal((ClaudeRoute.Claude, oldSession), await runner.RouteAsync(sent.Ids.Single(), HomelabOwner.DiscordUserId));
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("interrupted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Routing_ThreadWithASession_GoesToClaude_ForTheOwnerOnly()
    {
        var runner = CreateRunner();
        var session = Guid.NewGuid();
        await runner.LinkAsync([ThreadId], session);

        Assert.Equal((ClaudeRoute.Claude, session), await runner.RouteAsync(ThreadId, HomelabOwner.DiscordUserId));
        Assert.Equal(ClaudeRoute.Ignore, (await runner.RouteAsync(ThreadId, OtherUser)).Route);
        Assert.Equal(ClaudeRoute.Bot, (await runner.RouteAsync(ThreadId + 1, HomelabOwner.DiscordUserId)).Route);
    }

    // A plain DM has no replied message; a Reply to any other message has no link.
    [Fact]
    public async Task Routing_PlainDm_AndReplyToAnUnmappedMessage_GoToTheBot()
    {
        var runner = CreateRunner();
        await runner.LinkAsync([5001], Guid.NewGuid());

        Assert.Equal(ClaudeRoute.Bot, (await runner.RouteAsync(null, HomelabOwner.DiscordUserId)).Route);
        Assert.Equal(ClaudeRoute.Bot, (await runner.RouteAsync(5002, HomelabOwner.DiscordUserId)).Route);
        Assert.Equal(ClaudeRoute.Claude, (await runner.RouteAsync(5001, HomelabOwner.DiscordUserId)).Route);
    }

    [Fact]
    public void Command_IsForTheOwnerOnly_AndNotInsideAThread()
    {
        Assert.Equal("Only the owner can run Claude.", HomeLabCommands.ClaudeCommandRefusal(OtherUser, enabled: true, inThread: false));
        Assert.NotNull(HomeLabCommands.ClaudeCommandRefusal(HomelabOwner.DiscordUserId, enabled: true, inThread: true));
        Assert.Null(HomeLabCommands.ClaudeCommandRefusal(HomelabOwner.DiscordUserId, enabled: true, inThread: false));
        Assert.Equal(ClaudeRunnerService.DisabledText, HomeLabCommands.ClaudeCommandRefusal(HomelabOwner.DiscordUserId, enabled: false, inThread: false));
    }

    internal static ClaudeRunRequest Request(ClaudeRunOrigin origin, ulong? channelId) =>
        new() { Origin = origin, Task = "check the disks", SessionId = Guid.NewGuid(), ChannelId = channelId };

    private static bool Release(ClaudeRunSlot slot)
    {
        slot.Dispose();
        return true;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private ClaudeRunnerService CreateRunner(ClaudeRunnerConfiguration? config = null, Func<ClaudeRunOrigin, TimeSpan>? timeoutFor = null) =>
        new(config ?? new ClaudeRunnerConfiguration { Enabled = true }, _harness.DbFactory, _process, new Lazy<IClaudeChat>(_chat), _logger, timeoutFor);
}

// ask_claude in the bot's kernel: the guards of [ChangesState] and the asynchronous result.
public sealed class AskClaudeToolTests : IDisposable
{
    private const ulong ThreadId = 6161;

    private readonly ScriptedClaudeHandler _handler = new();
    private readonly FakeClaudeProcess _process = new();
    private readonly FakeClaudeChat _chat = new();
    private readonly List<string> _asked = [];
    private readonly KernelServiceHarness _harness;

    public AskClaudeToolTests()
    {
        _harness = new KernelServiceHarness(
            _handler.CreateClient(),
            morePlugins: (plugins, h) => plugins.AddFromObject(
                new ClaudePlugin(new ClaudeRunnerService(
                    new ClaudeRunnerConfiguration { Enabled = true, RecordedResultMaxChars = 20 },
                    h.DbFactory,
                    _process,
                    new Lazy<IClaudeChat>(_chat),
                    new RecordingLogger<ClaudeRunnerService>(),
                    timeoutFor: null)),
                "Claude"),
            claudeDelegation: true);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void AskClaude_ChangesState_NeedsConfirmation_AndIsForTheOwnerOnly()
    {
        var method = typeof(ClaudePlugin).GetMethod(nameof(ClaudePlugin.AskClaude))!;

        var mark = method.GetCustomAttribute<ChangesStateAttribute>();
        Assert.NotNull(mark);
        Assert.True(mark.RequiresConfirmation);
        Assert.True(mark.OwnerOnly);
        Assert.Contains("1500 characters", method.GetParameters()[0].GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()!.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AskClaude_IsInTheKernel_ButNotOnTheReadOnlySurface()
    {
        var kernel = _harness.Service.GetKernel();
        var tool = kernel.Plugins.GetFunction("Claude", "AskClaude");

        Assert.Contains(tool, KernelService.SelectFunctions(kernel, ["Claude", "Test"]));
        Assert.DoesNotContain(KernelService.SelectFunctions(kernel, ["Claude", "Test"], readOnly: true), f => f.Name == "AskClaude");
    }

    // Claude on the host uses this MCP server: ask_bot → ask_claude must not start Claude again.
    [Fact]
    public async Task AskClaude_IsNotOnTheMcpSurface()
    {
        await using var host = await McpTestHost.StartAsync();

        var names = (await host.ListToolsAsync()).Select(t => t.Name).ToList();

        Assert.Contains("ask_bot", names);
        Assert.DoesNotContain(names, n => n.Contains("claude", StringComparison.OrdinalIgnoreCase));
    }

    // The ask_bot path: a turn with no chat to ask in.
    [Fact]
    public async Task NoConfirmationScope_DoesNotStartARun()
    {
        ScriptAskClaude("could not delegate");

        await _harness.Service.ProcessMessageAsync(ThreadId, "delegate the disk analysis");

        Assert.Equal(ToolConfirmationFilter.NoChannelResult, ToolResultSentInRound(1));
        Assert.Contains("Delegating to Claude Code", _handler.Requests[0]["system"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Empty(_process.Calls);
        await using var db = _harness.DbFactory.CreateDbContext();
        Assert.Empty(db.ClaudeRuns);
        Assert.Contains(
            _harness.Logger.Entries,
            e => e.Message.Contains("Tool Claude.AskClaude needs confirmation, but the turn has no chat to ask in; it did not run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OtherUser_IsRefused_WithoutAButton()
    {
        ScriptAskClaude("not for you");
        using var scope = AnswerApproved(new TurnChat { UserId = 42, ChannelId = ThreadId });

        await _harness.Service.ProcessMessageAsync(ThreadId, "delegate the disk analysis", 42);

        Assert.Equal(ToolConfirmationFilter.OwnerOnlyResult, ToolResultSentInRound(1));
        Assert.Empty(_asked);
        Assert.Empty(_process.Calls);
    }

    [Fact]
    public async Task Approved_ReturnsAtOnce_AndTheResultArrivesLaterInTheTurnsChat()
    {
        var gate = new TaskCompletionSource<ClaudeProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _process.Respond = _ => gate.Task;
        ScriptAskClaude("Started; the result comes as a separate message.");
        using var scope = AnswerApproved(new TurnChat { UserId = HomelabOwner.DiscordUserId });

        var answer = await _harness.Service.ProcessMessageAsync(HomelabOwner.DmThreadId, "delegate the disk analysis", HomelabOwner.DiscordUserId);

        Assert.Equal("Started; the result comes as a separate message.", answer);
        Assert.StartsWith("Started Claude session ", ToolResultSentInRound(1), StringComparison.Ordinal);
        Assert.Single(_asked);
        Assert.Empty(_chat.Sent);

        gate.SetResult(FakeClaudeProcess.Result("The pool tank is degraded: disk sdb has 120 reallocated sectors."));
        await _chat.WaitForSendsAsync(1);

        var sent = _chat.Sent.Single();
        Assert.Null(sent.ChannelId);
        Assert.StartsWith("The pool tank is degraded: disk sdb has 120 reallocated sectors.", sent.Text, StringComparison.Ordinal);
        Assert.StartsWith("The pool tank is deg\n[Cut at 20 characters", sent.Record, StringComparison.Ordinal);
        await WaitForRunAsync(ClaudeRunOrigin.Llm);
    }

    private async Task WaitForRunAsync(ClaudeRunOrigin origin)
    {
        for (var i = 0; i < 200; i++)
        {
            await using var db = _harness.DbFactory.CreateDbContext();
            if (await db.ClaudeRuns.SingleOrDefaultAsync(r => r.State == ClaudeRunState.Completed) is { } run)
            {
                Assert.Equal(origin, run.Origin);
                Assert.Equal("disk analysis", run.Task);
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("The run was not stored as completed.");
    }

    private ToolConfirmationScope AnswerApproved(TurnChat chat) =>
        ToolConfirmationScope.Begin(
            (action, _) =>
            {
                _asked.Add(action);
                return Task.FromResult(ToolConfirmationDecision.Approved);
            },
            chat);

    private void ScriptAskClaude(string finalText) =>
        _handler
            .Enqueue(ClaudeResponses.ToolUseWithInput("Claude_AskClaude", """{"task":"disk analysis"}"""))
            .Enqueue(ClaudeResponses.Text(finalText));

    private string? ToolResultSentInRound(int round)
    {
        var lastMessage = _handler.Requests[round]["messages"]!.AsArray()[^1]!;
        var result = lastMessage["content"]!.AsArray().OfType<JsonObject>().Single(b => (string?)b["type"] == "tool_result");
        return (string?)result["content"];
    }
}

internal sealed class FakeClaudeProcess : IClaudeProcessRunner
{
    private readonly ConcurrentQueue<(string Command, string Stdin)> _calls = new();

    public Func<CancellationToken, Task<ClaudeProcessResult>> Respond { get; set; } = _ => Task.FromResult(Result("done"));

    public List<(string Command, string Stdin)> Calls => [.. _calls];

    public static ClaudeProcessResult Result(string result, int turns = 1, double cost = 0.01, string denials = "[]") =>
        new()
        {
            Stdout = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["type"] = "result",
                ["subtype"] = "success",
                ["is_error"] = false,
                ["duration_ms"] = 4000,
                ["num_turns"] = turns,
                ["result"] = result,
                ["total_cost_usd"] = cost,
                ["permission_denials"] = JsonDocument.Parse(denials).RootElement,
            }),
        };

    public Task<ClaudeProcessResult> RunAsync(string remoteCommand, string stdin, CancellationToken ct)
    {
        _calls.Enqueue((remoteCommand, stdin));
        return Respond(ct);
    }
}

internal sealed class FakeClaudeChat : IClaudeChat
{
    private readonly ConcurrentQueue<(ulong? ChannelId, string Text, string Record, IReadOnlyList<ulong> Ids)> _sent = new();
    private long _nextId = 9000;

    public List<(ulong? ChannelId, string Text, string Record, IReadOnlyList<ulong> Ids)> Sent => [.. _sent];

    public Task WaitForReadyAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<ulong>> SendClaudeMessageAsync(ulong? channelId, string text, string recordText)
    {
        IReadOnlyList<ulong> ids = [(ulong)Interlocked.Increment(ref _nextId)];
        _sent.Enqueue((channelId, text, recordText, ids));
        return Task.FromResult(ids);
    }

    public async Task WaitForSendsAsync(int count)
    {
        for (var i = 0; i < 300 && _sent.Count < count; i++)
        {
            await Task.Delay(10);
        }

        Assert.True(_sent.Count >= count, $"Expected {count} sends, got {_sent.Count}.");
    }
}
