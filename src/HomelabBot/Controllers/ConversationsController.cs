using HomelabBot.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomelabBot.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConversationsController : ControllerBase
{
    private readonly IDbContextFactory<HomelabDbContext> _dbFactory;

    public ConversationsController(IDbContextFactory<HomelabDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<ConversationDto>>> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        await using var db = await this._dbFactory.CreateDbContextAsync(ct);

        var totalCount = await db.Conversations.CountAsync(ct);

        var items = await db.Conversations
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new ConversationDto
            {
                Id = c.Id,
                ThreadId = c.ThreadId.ToString(),
                Title = c.Title,
                CreatedAt = c.CreatedAt,
                LastMessageAt = c.LastMessageAt,
                MessageCount = c.Messages.Count,
            })
            .ToListAsync(ct);

        return this.Ok(new PagedResult<ConversationDto>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
        });
    }

    [HttpGet("{threadId}")]
    public async Task<ActionResult<ConversationDetailDto>> GetByThreadId(
        string threadId,
        CancellationToken ct = default)
    {
        if (!ulong.TryParse(threadId, out var tid))
            return this.BadRequest("Invalid thread ID");

        await using var db = await this._dbFactory.CreateDbContextAsync(ct);

        var conversation = await db.Conversations
            .Include(c => c.Messages.OrderBy(m => m.Timestamp))
            .FirstOrDefaultAsync(c => c.ThreadId == tid, ct);

        if (conversation is null)
            return this.NotFound();

        return this.Ok(new ConversationDetailDto
        {
            Id = conversation.Id,
            ThreadId = conversation.ThreadId.ToString(),
            Title = conversation.Title,
            CreatedAt = conversation.CreatedAt,
            LastMessageAt = conversation.LastMessageAt,
            Messages = conversation.Messages.Select(m => new MessageDto
            {
                Id = m.Id,
                Role = m.Role,
                Content = m.Content,
                Timestamp = m.Timestamp,
            }).ToList(),
        });
    }
}

public record PagedResult<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}

public record ConversationDto
{
    public int Id { get; init; }
    public required string ThreadId { get; init; }
    public string? Title { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastMessageAt { get; init; }
    public int MessageCount { get; init; }
}

public record ConversationDetailDto
{
    public int Id { get; init; }
    public required string ThreadId { get; init; }
    public string? Title { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastMessageAt { get; init; }
    public required IReadOnlyList<MessageDto> Messages { get; init; }
}

public record MessageDto
{
    public int Id { get; init; }
    public required string Role { get; init; }
    public required string Content { get; init; }
    public DateTime Timestamp { get; init; }
}
