using HomelabBot.Data;
using HomelabBot.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomelabBot.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConversationsController : ControllerBase
{
    private readonly IDbContextFactory<HomelabDbContext> _dbFactory;
    private readonly ILogger<ConversationsController> _logger;

    public ConversationsController(IDbContextFactory<HomelabDbContext> dbFactory, ILogger<ConversationsController> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<ConversationDto>>> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Listing conversations Page={Page} PageSize={PageSize}", page, pageSize);

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
        _logger.LogInformation("Getting conversation ThreadId={ThreadId}", threadId);

        if (!ulong.TryParse(threadId, out var tid))
        {
            _logger.LogWarning("Invalid thread ID format ThreadId={ThreadId}", threadId);
            return this.BadRequest("Invalid thread ID");
        }

        await using var db = await this._dbFactory.CreateDbContextAsync(ct);

        var conversation = await db.Conversations
            .Include(c => c.Messages.OrderBy(m => m.Timestamp))
            .FirstOrDefaultAsync(c => c.ThreadId == tid, ct);

        if (conversation is null)
        {
            _logger.LogWarning("Conversation not found ThreadId={ThreadId}", threadId);
            return this.NotFound();
        }

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
