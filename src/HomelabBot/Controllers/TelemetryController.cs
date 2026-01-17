using HomelabBot.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomelabBot.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TelemetryController : ControllerBase
{
    private readonly IDbContextFactory<HomelabDbContext> _dbFactory;

    public TelemetryController(IDbContextFactory<HomelabDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<LlmInteractionDto>>> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? threadId = null,
        [FromQuery] bool? success = null,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var query = db.LlmInteractions.AsQueryable();

        if (!string.IsNullOrEmpty(threadId) && ulong.TryParse(threadId, out var tid))
            query = query.Where(i => i.ThreadId == tid);

        if (success.HasValue)
            query = query.Where(i => i.Success == success.Value);

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(i => i.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(i => new LlmInteractionDto
            {
                Id = i.Id,
                ThreadId = i.ThreadId.HasValue ? i.ThreadId.Value.ToString() : null,
                Model = i.Model,
                UserPrompt = i.UserPrompt.Length > 100 ? i.UserPrompt.Substring(0, 100) + "..." : i.UserPrompt,
                Success = i.Success,
                PromptTokens = i.PromptTokens,
                CompletionTokens = i.CompletionTokens,
                LatencyMs = i.LatencyMs,
                Timestamp = i.Timestamp,
                ToolCallCount = i.ToolCalls.Count
            })
            .ToListAsync(ct);

        return Ok(new PagedResult<LlmInteractionDto>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        });
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<LlmInteractionDetailDto>> GetById(int id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var item = await db.LlmInteractions
            .Include(i => i.ToolCalls.OrderBy(t => t.Timestamp))
            .FirstOrDefaultAsync(i => i.Id == id, ct);

        if (item is null)
            return NotFound();

        return Ok(new LlmInteractionDetailDto
        {
            Id = item.Id,
            ConversationId = item.ConversationId,
            ThreadId = item.ThreadId?.ToString(),
            Model = item.Model,
            UserPrompt = item.UserPrompt,
            FullMessagesJson = item.FullMessagesJson,
            Response = item.Response,
            ErrorMessage = item.ErrorMessage,
            Success = item.Success,
            PromptTokens = item.PromptTokens,
            CompletionTokens = item.CompletionTokens,
            LatencyMs = item.LatencyMs,
            Timestamp = item.Timestamp,
            ToolCalls = item.ToolCalls.Select(t => new ToolCallDto
            {
                Id = t.Id,
                PluginName = t.PluginName,
                FunctionName = t.FunctionName,
                ArgumentsJson = t.ArgumentsJson,
                ResultJson = t.ResultJson,
                Success = t.Success,
                ErrorMessage = t.ErrorMessage,
                LatencyMs = t.LatencyMs,
                Timestamp = t.Timestamp
            }).ToList()
        });
    }

    [HttpGet("stats")]
    public async Task<ActionResult<TelemetryStatsDto>> GetStats(
        [FromQuery] int days = 7,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var since = DateTime.UtcNow.AddDays(-days);

        var interactions = await db.LlmInteractions
            .Where(i => i.Timestamp >= since)
            .ToListAsync(ct);

        var totalInteractions = interactions.Count;
        var successfulInteractions = interactions.Count(i => i.Success);
        var totalPromptTokens = interactions.Sum(i => i.PromptTokens ?? 0);
        var totalCompletionTokens = interactions.Sum(i => i.CompletionTokens ?? 0);
        var avgLatency = interactions.Count > 0 ? interactions.Average(i => i.LatencyMs) : 0;

        var toolCalls = await db.ToolCallLogs
            .Where(t => t.Timestamp >= since)
            .ToListAsync(ct);

        var totalToolCalls = toolCalls.Count;
        var successfulToolCalls = toolCalls.Count(t => t.Success);

        return Ok(new TelemetryStatsDto
        {
            Days = days,
            TotalInteractions = totalInteractions,
            SuccessfulInteractions = successfulInteractions,
            FailedInteractions = totalInteractions - successfulInteractions,
            TotalPromptTokens = totalPromptTokens,
            TotalCompletionTokens = totalCompletionTokens,
            TotalTokens = totalPromptTokens + totalCompletionTokens,
            AverageLatencyMs = avgLatency,
            TotalToolCalls = totalToolCalls,
            SuccessfulToolCalls = successfulToolCalls,
            FailedToolCalls = totalToolCalls - successfulToolCalls
        });
    }
}

public record LlmInteractionDto
{
    public int Id { get; init; }
    public string? ThreadId { get; init; }
    public required string Model { get; init; }
    public required string UserPrompt { get; init; }
    public bool Success { get; init; }
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    public long LatencyMs { get; init; }
    public DateTime Timestamp { get; init; }
    public int ToolCallCount { get; init; }
}

public record LlmInteractionDetailDto
{
    public int Id { get; init; }
    public int? ConversationId { get; init; }
    public string? ThreadId { get; init; }
    public required string Model { get; init; }
    public required string UserPrompt { get; init; }
    public string? FullMessagesJson { get; init; }
    public string? Response { get; init; }
    public string? ErrorMessage { get; init; }
    public bool Success { get; init; }
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    public long LatencyMs { get; init; }
    public DateTime Timestamp { get; init; }
    public required IReadOnlyList<ToolCallDto> ToolCalls { get; init; }
}

public record ToolCallDto
{
    public int Id { get; init; }
    public required string PluginName { get; init; }
    public required string FunctionName { get; init; }
    public string? ArgumentsJson { get; init; }
    public string? ResultJson { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public long LatencyMs { get; init; }
    public DateTime Timestamp { get; init; }
}

public record TelemetryStatsDto
{
    public int Days { get; init; }
    public int TotalInteractions { get; init; }
    public int SuccessfulInteractions { get; init; }
    public int FailedInteractions { get; init; }
    public int TotalPromptTokens { get; init; }
    public int TotalCompletionTokens { get; init; }
    public int TotalTokens { get; init; }
    public double AverageLatencyMs { get; init; }
    public int TotalToolCalls { get; init; }
    public int SuccessfulToolCalls { get; init; }
    public int FailedToolCalls { get; init; }
}
