using HomelabBot.Data;
using HomelabBot.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomelabBot.Controllers;

[ApiController]
[Route("api/[controller]")]
public class KnowledgeController : ControllerBase
{
    private readonly IDbContextFactory<HomelabDbContext> _dbFactory;

    public KnowledgeController(IDbContextFactory<HomelabDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<KnowledgeDto>>> GetAll(
        [FromQuery] string? topic = null,
        [FromQuery] bool? isValid = null,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var query = db.Knowledge.AsQueryable();

        if (!string.IsNullOrEmpty(topic))
            query = query.Where(k => k.Topic.Contains(topic));

        if (isValid.HasValue)
            query = query.Where(k => k.IsValid == isValid.Value);

        var items = await query
            .OrderByDescending(k => k.CreatedAt)
            .Take(100)
            .Select(k => new KnowledgeDto
            {
                Id = k.Id,
                Topic = k.Topic,
                Fact = k.Fact,
                Context = k.Context,
                Confidence = k.Confidence,
                Source = k.Source,
                IsValid = k.IsValid,
                LastVerified = k.LastVerified,
                CreatedAt = k.CreatedAt,
                LastUsed = k.LastUsed
            })
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<KnowledgeDto>> GetById(int id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var item = await db.Knowledge.FindAsync([id], ct);
        if (item is null)
            return NotFound();

        return Ok(new KnowledgeDto
        {
            Id = item.Id,
            Topic = item.Topic,
            Fact = item.Fact,
            Context = item.Context,
            Confidence = item.Confidence,
            Source = item.Source,
            IsValid = item.IsValid,
            LastVerified = item.LastVerified,
            CreatedAt = item.CreatedAt,
            LastUsed = item.LastUsed
        });
    }

    [HttpPost]
    public async Task<ActionResult<KnowledgeDto>> Create(
        [FromBody] CreateKnowledgeRequest request,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var item = new Knowledge
        {
            Topic = request.Topic,
            Fact = request.Fact,
            Context = request.Context,
            Confidence = request.Confidence ?? 0.8,
            Source = request.Source ?? "manual",
            IsValid = true,
            CreatedAt = DateTime.UtcNow
        };

        db.Knowledge.Add(item);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetById), new { id = item.Id }, new KnowledgeDto
        {
            Id = item.Id,
            Topic = item.Topic,
            Fact = item.Fact,
            Context = item.Context,
            Confidence = item.Confidence,
            Source = item.Source,
            IsValid = item.IsValid,
            CreatedAt = item.CreatedAt
        });
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<KnowledgeDto>> Update(
        int id,
        [FromBody] UpdateKnowledgeRequest request,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var item = await db.Knowledge.FindAsync([id], ct);
        if (item is null)
            return NotFound();

        if (request.Topic is not null) item.Topic = request.Topic;
        if (request.Fact is not null) item.Fact = request.Fact;
        if (request.Context is not null) item.Context = request.Context;
        if (request.Confidence.HasValue) item.Confidence = request.Confidence.Value;
        if (request.IsValid.HasValue) item.IsValid = request.IsValid.Value;

        await db.SaveChangesAsync(ct);

        return Ok(new KnowledgeDto
        {
            Id = item.Id,
            Topic = item.Topic,
            Fact = item.Fact,
            Context = item.Context,
            Confidence = item.Confidence,
            Source = item.Source,
            IsValid = item.IsValid,
            LastVerified = item.LastVerified,
            CreatedAt = item.CreatedAt,
            LastUsed = item.LastUsed
        });
    }

    [HttpDelete("{id:int}")]
    public async Task<ActionResult> Delete(int id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var item = await db.Knowledge.FindAsync([id], ct);
        if (item is null)
            return NotFound();

        item.IsValid = false;
        await db.SaveChangesAsync(ct);

        return NoContent();
    }
}

public record KnowledgeDto
{
    public int Id { get; init; }
    public required string Topic { get; init; }
    public required string Fact { get; init; }
    public string? Context { get; init; }
    public double Confidence { get; init; }
    public required string Source { get; init; }
    public bool IsValid { get; init; }
    public DateTime? LastVerified { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastUsed { get; init; }
}

public record CreateKnowledgeRequest
{
    public required string Topic { get; init; }
    public required string Fact { get; init; }
    public string? Context { get; init; }
    public double? Confidence { get; init; }
    public string? Source { get; init; }
}

public record UpdateKnowledgeRequest
{
    public string? Topic { get; init; }
    public string? Fact { get; init; }
    public string? Context { get; init; }
    public double? Confidence { get; init; }
    public bool? IsValid { get; init; }
}
