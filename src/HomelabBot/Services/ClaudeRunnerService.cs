using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HomelabBot.Configuration;
using HomelabBot.Data;
using HomelabBot.Data.Entities;
using HomelabBot.Helpers;
using HomelabBot.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public sealed record ClaudeRunRequest
{
    public required ClaudeRunOrigin Origin { get; init; }

    public required string Task { get; init; }

    public required Guid SessionId { get; init; }

    public bool Resume { get; init; }

    // Null is the owner DM.
    public ulong? ChannelId { get; init; }
}

// Where a message of a guild thread or an owner DM goes.
public enum ClaudeRoute
{
    Bot,
    Claude,
    Ignore,
}

// The Discord side of ClaudeRunnerService (DiscordBotService).
public interface IClaudeChat
{
    Task WaitForReadyAsync(CancellationToken ct = default);

    // Sends the text, split to Discord's limit. In the owner DM (channelId null) recordText goes into
    // the DM history. Returns the ids of the sent messages, empty when nothing was sent.
    Task<IReadOnlyList<ulong>> SendClaudeMessageAsync(ulong? channelId, string text, string recordText);
}

// The one run slot of the bot, held until disposed.
public sealed class ClaudeRunSlot : IDisposable
{
    private SemaphoreSlim? _gate;

    internal ClaudeRunSlot(SemaphoreSlim gate)
    {
        _gate = gate;
    }

    public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
}

// Runs claude -p on the host over SSH (IClaudeProcessRunner) and posts the result to the chat of the run.
// One run at a time for the whole bot. At startup it marks runs that a restart killed.
public sealed class ClaudeRunnerService : BackgroundService
{
    internal const string BusyText = "A Claude run is in progress. Wait for it to end, then try again.";
    internal const string DisabledText = "Claude runs are disabled (ClaudeRunner:Enabled is false).";

    private const int MaxDetailChars = 1500;
    private const int MaxDenialInputChars = 200;
    private const int MaxLoggedStderrChars = 500;

    // claude -p reports the subscription limit in its result text. An API rate limit is a different cause.
    private static readonly Regex UsageLimit = new(@"usage limit|hit your (?:\w+ )?limit|limit (?:will )?resets?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ClaudeRunnerConfiguration _config;
    private readonly IDbContextFactory<HomelabDbContext> _dbFactory;
    private readonly IClaudeProcessRunner _process;
    private readonly Lazy<IClaudeChat> _chat;
    private readonly ILogger _logger;
    private readonly Func<ClaudeRunOrigin, TimeSpan> _timeoutFor;

    // Runs that started before this process can only be leftovers of an earlier one.
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;

    public ClaudeRunnerService(
        IOptions<ClaudeRunnerConfiguration> config,
        IDbContextFactory<HomelabDbContext> dbFactory,
        IClaudeProcessRunner process,
        IServiceProvider services,
        ILogger<ClaudeRunnerService> logger)
        : this(config.Value, dbFactory, process, new Lazy<IClaudeChat>(services.GetRequiredService<IClaudeChat>), logger, timeoutFor: null)
    {
    }

    // Tests pass a fake process, a fake chat and short timeouts.
    internal ClaudeRunnerService(
        ClaudeRunnerConfiguration config,
        IDbContextFactory<HomelabDbContext> dbFactory,
        IClaudeProcessRunner process,
        Lazy<IClaudeChat> chat,
        ILogger logger,
        Func<ClaudeRunOrigin, TimeSpan>? timeoutFor)
    {
        _config = config;
        _dbFactory = dbFactory;
        _process = process;
        _chat = chat;
        _logger = logger;
        _timeoutFor = timeoutFor ?? (origin => TimeoutFor(config, origin));
    }

    public bool Enabled => _config.Enabled;

    internal static TimeSpan TimeoutFor(ClaudeRunnerConfiguration config, ClaudeRunOrigin origin) =>
        TimeSpan.FromMinutes(origin == ClaudeRunOrigin.Owner ? config.OwnerTimeoutMinutes : config.LlmTimeoutMinutes);

    // Try, never wait: two runs on one session id would both succeed and write to the same session.
    public ClaudeRunSlot? TryReserve() => _gate.Wait(0) ? new ClaudeRunSlot(_gate) : null;

    // Starts the run in the background; the slot is released when it ends. The task is for tests.
    public Task LaunchAsync(ClaudeRunSlot slot, ClaudeRunRequest request) => Task.Run(() => RunAsync(slot, request));

    // Null when the run started, else the text for the owner.
    public string? TryStart(ClaudeRunRequest request)
    {
        if (!Enabled)
        {
            return DisabledText;
        }

        var slot = TryReserve();
        if (slot is null)
        {
            _logger.LogInformation("Claude run of session {SessionId} refused: another run is active", request.SessionId);
            return BusyText;
        }

        _ = LaunchAsync(slot, request);
        return null;
    }

    // A thread or DM message that belongs to a Claude session goes to Claude, and only the owner talks there.
    public async Task<(ClaudeRoute Route, Guid SessionId)> RouteAsync(ulong? discordId, ulong authorId, CancellationToken ct = default)
    {
        if (discordId is not { } id)
        {
            return (ClaudeRoute.Bot, Guid.Empty);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var sessionId = await db.ClaudeSessionLinks
            .Where(l => l.DiscordId == id)
            .Select(l => (Guid?)l.SessionId)
            .FirstOrDefaultAsync(ct);

        if (sessionId is null)
        {
            return (ClaudeRoute.Bot, Guid.Empty);
        }

        return (authorId == HomelabOwner.DiscordUserId ? ClaudeRoute.Claude : ClaudeRoute.Ignore, sessionId.Value);
    }

    public async Task LinkAsync(IEnumerable<ulong> discordIds, Guid sessionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.ClaudeSessionLinks.AddRange(discordIds.Select(id => new ClaudeSessionLink { DiscordId = id, SessionId = sessionId }));
        await db.SaveChangesAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var interrupted = await MarkInterruptedRunsAsync(stoppingToken);
            if (interrupted.Count == 0)
            {
                return;
            }

            await _chat.Value.WaitForReadyAsync(stoppingToken);
            foreach (var run in interrupted)
            {
                await NotifyInterruptedAsync(run);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not handle Claude runs interrupted by a restart");
        }
    }

    // A restart kills a running ssh; Claude itself can restart the homelab-bot container.
    internal async Task<IReadOnlyList<ClaudeRun>> MarkInterruptedRunsAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var runs = await db.ClaudeRuns
            .Where(r => r.State == ClaudeRunState.Running && r.StartedAt < _startedAtUtc)
            .ToListAsync(ct);

        foreach (var run in runs)
        {
            run.State = ClaudeRunState.Interrupted;
            run.EndedAt = DateTime.UtcNow;
            _logger.LogWarning(
                "Claude run {RunId} of session {SessionId} was interrupted by a restart; marked as interrupted", run.Id, run.SessionId);
        }

        await db.SaveChangesAsync(ct);
        return runs;
    }

    internal async Task NotifyInterruptedAsync(ClaudeRun run)
    {
        var how = run.ChannelId is null
            ? "Reply to this message to continue it."
            : run.Origin == ClaudeRunOrigin.Owner
                ? "Write in this thread to continue it."
                : $"Ask the bot to continue session `{run.SessionId}`.";
        var text = $"⚠️ The Claude run of session `{run.SessionId}` was interrupted: the bot restarted while it ran. " +
                   $"The session can be resumed; Claude knows that its work was cut off. {how}";

        try
        {
            await SendAndLinkAsync(run.ChannelId, run.SessionId, text, text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not tell the owner that Claude run {RunId} was interrupted", run.Id);
        }
    }

    private async Task RunAsync(ClaudeRunSlot slot, ClaudeRunRequest request)
    {
        using var heldSlot = slot;
        try
        {
            var runId = await StoreStartAsync(request);
            _logger.LogInformation(
                "Claude run {RunId} started: origin {Origin}, session {SessionId}, {Mode}",
                runId, request.Origin, request.SessionId, request.Resume ? "resume" : "new");

            var stopwatch = Stopwatch.StartNew();
            var outcome = await RunProcessAsync(request);
            var durationMs = stopwatch.ElapsedMilliseconds;

            // A bookkeeping failure must not lose a result the owner may have waited 30 minutes for.
            try
            {
                await StoreEndAsync(runId, outcome, durationMs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not store the end of Claude run {RunId}", runId);
            }

            _logger.LogInformation(
                "Claude run {RunId} ended: {State}, origin {Origin}, session {SessionId}, {DurationMs} ms, cost {CostUsd} USD, error {IsError}",
                runId, outcome.State, request.Origin, request.SessionId, durationMs, outcome.Cli?.TotalCostUsd, outcome.State != ClaudeRunState.Completed);

            await PostAsync(request, outcome, durationMs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Claude run of session {SessionId} failed", request.SessionId);
        }
    }

    private async Task<ClaudeRunOutcome> RunProcessAsync(ClaudeRunRequest request)
    {
        var timeout = _timeoutFor(request.Origin);
        using var cts = new CancellationTokenSource(timeout);
        ClaudeProcessResult process;
        try
        {
            process = await _process.RunAsync($"{(request.Resume ? "resume" : "new")} {request.SessionId}", request.Task, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Claude run of session {SessionId} hit its time limit of {Timeout}; ssh was killed", request.SessionId, timeout);
            var text = $"⏱️ The Claude run was stopped at its time limit of {FormattingHelpers.FormatDuration(timeout)} and returned no output. " +
                       "The session can be resumed: Claude knows that its command was interrupted.";
            return Outcome(ClaudeRunState.TimedOut, text, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not run ssh for Claude session {SessionId}", request.SessionId);
            return Outcome(ClaudeRunState.Failed, $"❌ The Claude run could not start: {ClaudeOutputRedactor.Redact(ex.Message)}", null);
        }

        return Evaluate(process, request.SessionId);
    }

    internal ClaudeRunOutcome Evaluate(ClaudeProcessResult process, Guid sessionId)
    {
        if (process.ExitCode != 0)
        {
            _logger.LogWarning(
                "ssh for Claude session {SessionId} exited with code {ExitCode}: {Stderr}",
                sessionId, process.ExitCode, ConversationSearchResult.Truncate(ClaudeOutputRedactor.Redact(process.Stderr.Trim()), MaxLoggedStderrChars));
        }

        var cli = ClaudeCliResult.Parse(process.Stdout);
        if (cli is null)
        {
            _logger.LogWarning(
                "Claude session {SessionId} returned no JSON result ({StdoutChars} chars of stdout, exit code {ExitCode})",
                sessionId, process.Stdout.Length, process.ExitCode);
            var detail = ConversationSearchResult.Truncate(ClaudeOutputRedactor.Redact($"{process.Stderr}\n{process.Stdout}".Trim()), MaxDetailChars);
            if (UsageLimit.IsMatch(detail))
            {
                return Outcome(ClaudeRunState.UsageLimit, UsageLimitText(detail), null);
            }

            var shown = detail.Length == 0 ? "" : $"\n```\n{detail.Replace('`', '\'')}\n```";
            return Outcome(ClaudeRunState.Failed, $"❌ The Claude run failed (ssh exit code {process.ExitCode}) and returned no result.{shown}", null);
        }

        if (cli.SessionId is { } reported && reported != sessionId.ToString())
        {
            _logger.LogWarning("Claude session {SessionId} reported session id {ReportedSessionId}", sessionId, reported);
        }

        var result = ClaudeOutputRedactor.Redact(cli.Result);
        if (cli.IsError)
        {
            return UsageLimit.IsMatch(cli.Result)
                ? Outcome(ClaudeRunState.UsageLimit, UsageLimitText(result), cli)
                : Outcome(ClaudeRunState.Failed, $"❌ The Claude run ended with an error ({cli.Subtype ?? "unknown"}).\n{result}", cli);
        }

        return Outcome(ClaudeRunState.Completed, string.IsNullOrWhiteSpace(result) ? "(Claude returned an empty result.)" : result, cli);
    }

    // The runs share the owner's subscription with his terminal sessions; a retry would only fail again.
    private static string UsageLimitText(string detail) =>
        $"🚫 The Claude subscription usage limit is reached, so the run did not finish. It was not retried; try again after the limit resets.\n{detail}";

    private static ClaudeRunOutcome Outcome(ClaudeRunState state, string body, ClaudeCliResult? cli) =>
        new() { State = state, Body = body, Cli = cli };

    internal static string Footer(ClaudeCliResult? cli, long measuredMs, Guid sessionId)
    {
        List<string> parts = [FormattingHelpers.FormatDuration(TimeSpan.FromMilliseconds(cli?.DurationMs ?? measuredMs))];
        if (cli?.NumTurns is { } turns)
        {
            parts.Add($"{turns} turns");
        }

        // The notional API price of the run, not a bill: the subscription pays.
        if (cli?.TotalCostUsd is { } cost)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"${cost:0.00} API list price, not billed"));
        }

        parts.Add($"session `{sessionId}`");
        var footer = $"-# {string.Join(" · ", parts)}";

        foreach (var denial in cli?.PermissionDenials ?? [])
        {
            var input = ConversationSearchResult.Truncate(ClaudeOutputRedactor.Redact(denial.Input), MaxDenialInputChars).Replace('`', '\'').ReplaceLineEndings(" ");
            footer += $"\n🚫 Denied: {denial.ToolName} `{input}`";
        }

        return footer;
    }

    // The DM history keeps a capped copy; Discord gets the full text.
    internal static string CapForRecord(string text, int maxChars) =>
        text.Length <= maxChars
            ? text
            : $"{text[..maxChars]}\n[Cut at {maxChars} characters for this history; the Discord message has the full text.]";

    private async Task PostAsync(ClaudeRunRequest request, ClaudeRunOutcome outcome, long durationMs)
    {
        var footer = Footer(outcome.Cli, durationMs, request.SessionId);
        var hint = request.ChannelId is null ? "\n-# Reply to this message to continue the session." : "";
        var text = $"{outcome.Body}\n\n{footer}{hint}";
        var record = $"{CapForRecord(outcome.Body, _config.RecordedResultMaxChars)}\n\n{footer}";

        try
        {
            await SendAndLinkAsync(request.ChannelId, request.SessionId, text, record);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not post the result of Claude session {SessionId}", request.SessionId);
        }
    }

    // A message in the owner DM continues its session when the owner replies to it.
    private async Task SendAndLinkAsync(ulong? channelId, Guid sessionId, string text, string record)
    {
        var ids = await _chat.Value.SendClaudeMessageAsync(channelId, text, record);
        if (channelId is null)
        {
            await LinkAsync(ids, sessionId);
        }
    }

    private async Task<int> StoreStartAsync(ClaudeRunRequest request)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var run = new ClaudeRun
        {
            Origin = request.Origin,
            Task = request.Task,
            SessionId = request.SessionId,
            Resume = request.Resume,
            ChannelId = request.ChannelId,
            StartedAt = DateTime.UtcNow,
        };
        db.ClaudeRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private async Task StoreEndAsync(int runId, ClaudeRunOutcome outcome, long durationMs)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var run = await db.ClaudeRuns.SingleAsync(r => r.Id == runId);
        run.State = outcome.State;
        run.EndedAt = DateTime.UtcNow;
        run.DurationMs = durationMs;
        run.NumTurns = outcome.Cli?.NumTurns;
        run.TotalCostUsd = outcome.Cli?.TotalCostUsd;
        run.IsError = outcome.Cli?.IsError ?? true;
        run.PermissionDenialsJson = outcome.Cli?.PermissionDenials is { Count: > 0 } denials ? JsonSerializer.Serialize(denials) : null;
        await db.SaveChangesAsync();
    }
}

internal sealed record ClaudeRunOutcome
{
    public required ClaudeRunState State { get; init; }

    public required string Body { get; init; }

    public ClaudeCliResult? Cli { get; init; }
}
