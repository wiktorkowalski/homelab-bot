using System.Text;
using System.Text.Json;
using HomelabBot.Configuration;
using HomelabBot.Data;
using HomelabBot.Data.Entities;
using HomelabBot.Helpers;
using HomelabBot.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public sealed class WarRoomService
{
    private readonly IDbContextFactory<HomelabDbContext> _dbFactory;
    private readonly DiscordBotService _discordService;
    private readonly WarRoomConfiguration _config;
    private readonly ILogger<WarRoomService> _logger;

    public WarRoomService(
        IDbContextFactory<HomelabDbContext> dbFactory,
        DiscordBotService discordService,
        IOptions<WarRoomConfiguration> config,
        ILogger<WarRoomService> logger)
    {
        _dbFactory = dbFactory;
        _discordService = discordService;
        _config = config.Value;
        _logger = logger;
    }

    public bool ShouldOpenWarRoom(string severity)
    {
        return _config.Enabled
               && _config.ChannelId != 0
               && _config.TriggerSeverities.Contains(severity, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<WarRoom?> OpenWarRoomAsync(
        string trigger, string severity, CancellationToken ct = default)
    {
        if (!ShouldOpenWarRoom(severity))
        {
            return null;
        }

        var threadName = $"War Room: {ConversationSearchResult.Truncate(trigger, 80)}";
        var initialMessage = $"**War Room opened** for: {trigger}\nSeverity: {severity}";

        var threadResult = await _discordService.CreateThreadInChannelAsync(
            _config.ChannelId, threadName, initialMessage);

        if (threadResult == null)
        {
            _logger.LogWarning("Failed to create War Room thread for '{Trigger}'", trigger);
            return null;
        }

        var (threadId, messageId) = threadResult.Value;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var warRoom = new WarRoom
        {
            DiscordThreadId = threadId,
            StatusMessageId = messageId,
            Trigger = trigger,
            Severity = severity,
            TimelineJson = JsonSerializer.Serialize(new List<TimelineEvent>
            {
                new() { Event = "War Room opened", Timestamp = DateTime.UtcNow }
            })
        };

        db.WarRooms.Add(warRoom);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Opened War Room #{Id} for '{Trigger}' (thread: {ThreadId})",
            warRoom.Id, trigger, threadId);

        return warRoom;
    }

    public async Task LogEventAsync(int warRoomId, string eventText, CancellationToken ct = default)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var warRoom = await db.WarRooms.FindAsync([warRoomId], ct);
            if (warRoom == null || warRoom.Status != WarRoomStatus.Active)
            {
                return;
            }

            var timeline = JsonSerializer.Deserialize<List<TimelineEvent>>(warRoom.TimelineJson) ?? [];
            timeline.Add(new TimelineEvent { Event = eventText, Timestamp = DateTime.UtcNow });
            warRoom.TimelineJson = JsonSerializer.Serialize(timeline);
            await db.SaveChangesAsync(ct);

            var timeStr = DateTime.UtcNow.ToString("HH:mm");
            await _discordService.SendToThreadAsync(warRoom.DiscordThreadId, $"[{timeStr}] {eventText}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to log event to War Room {Id}", warRoomId);
        }
    }

    public async Task<WarRoom?> ResolveAsync(int warRoomId, string resolution, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var warRoom = await db.WarRooms.FindAsync([warRoomId], ct);
        if (warRoom == null || warRoom.Status != WarRoomStatus.Active)
        {
            return null;
        }

        warRoom.Status = WarRoomStatus.Resolved;
        warRoom.Resolution = resolution;
        warRoom.ResolvedAt = DateTime.UtcNow;
        warRoom.Mttr = warRoom.ResolvedAt.Value - warRoom.CreatedAt;

        var timeline = JsonSerializer.Deserialize<List<TimelineEvent>>(warRoom.TimelineJson) ?? [];
        timeline.Add(new TimelineEvent { Event = $"Resolved: {resolution}", Timestamp = DateTime.UtcNow });
        warRoom.TimelineJson = JsonSerializer.Serialize(timeline);

        // Generate post-mortem summary
        warRoom.PostMortemSummary = GeneratePostMortem(warRoom, timeline);

        await db.SaveChangesAsync(ct);

        // Post summary to thread
        await _discordService.SendToThreadAsync(warRoom.DiscordThreadId, warRoom.PostMortemSummary);

        _logger.LogInformation("Resolved War Room #{Id}, MTTR: {Mttr}", warRoomId, warRoom.Mttr);
        return warRoom;
    }

    public async Task<WarRoom?> GetActiveWarRoomAsync(string? containerName = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var query = db.WarRooms
            .Where(w => w.Status == WarRoomStatus.Active)
            .OrderByDescending(w => w.CreatedAt);

        if (containerName != null)
        {
            return await query.FirstOrDefaultAsync(
                w => w.Trigger.Contains(containerName), ct);
        }

        return await query.FirstOrDefaultAsync(ct);
    }

    private static string GeneratePostMortem(WarRoom warRoom, List<TimelineEvent> timeline)
    {
        var sb = new StringBuilder();
        sb.AppendLine("**Post-Mortem Summary**");
        sb.AppendLine($"Trigger: {warRoom.Trigger}");
        sb.AppendLine($"Severity: {warRoom.Severity}");
        sb.AppendLine($"Duration: {FormattingHelpers.FormatDuration(warRoom.Mttr ?? TimeSpan.Zero)}");
        sb.AppendLine($"Resolution: {warRoom.Resolution}");
        sb.AppendLine();
        sb.AppendLine("**Timeline:**");

        foreach (var entry in timeline)
        {
            sb.AppendLine($"- [{entry.Timestamp:HH:mm:ss}] {entry.Event}");
        }

        return sb.ToString();
    }

    internal sealed class TimelineEvent
    {
        public required string Event { get; init; }

        public DateTime Timestamp { get; init; }
    }
}
