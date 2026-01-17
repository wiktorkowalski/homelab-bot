using HomelabBot.Services;
using Microsoft.AspNetCore.Mvc;

namespace HomelabBot.Controllers;

[ApiController]
[Route("api/[controller]")]
public class InvestigationsController : ControllerBase
{
    private readonly MemoryService _memoryService;

    public InvestigationsController(MemoryService memoryService)
    {
        _memoryService = memoryService;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<InvestigationDto>>> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool? resolved = null,
        CancellationToken ct = default)
    {
        var (items, totalCount) = await _memoryService.GetInvestigationsAsync(page, pageSize, resolved);

        return Ok(new PagedResult<InvestigationDto>
        {
            Items = items.Select(i => new InvestigationDto
            {
                Id = i.Id,
                ThreadId = i.ThreadId.ToString(),
                Trigger = i.Trigger,
                StartedAt = i.StartedAt,
                Resolved = i.Resolved,
                Resolution = i.Resolution,
                StepCount = i.Steps.Count
            }).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        });
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<InvestigationDetailDto>> GetById(int id)
    {
        var item = await _memoryService.GetInvestigationByIdAsync(id);

        if (item is null)
            return NotFound();

        return Ok(new InvestigationDetailDto
        {
            Id = item.Id,
            ThreadId = item.ThreadId.ToString(),
            Trigger = item.Trigger,
            StartedAt = item.StartedAt,
            Resolved = item.Resolved,
            Resolution = item.Resolution,
            Steps = item.Steps.Select(s => new InvestigationStepDto
            {
                Id = s.Id,
                Action = s.Action,
                Plugin = s.Plugin,
                ResultSummary = s.ResultSummary,
                Timestamp = s.Timestamp
            }).ToList()
        });
    }

    [HttpPost]
    public async Task<ActionResult<InvestigationDetailDto>> Create(
        [FromBody] CreateInvestigationRequest request)
    {
        var investigation = await _memoryService.StartInvestigationAsync(
            request.ThreadId ?? 0,
            request.Trigger);

        return CreatedAtAction(nameof(GetById), new { id = investigation.Id }, new InvestigationDetailDto
        {
            Id = investigation.Id,
            ThreadId = investigation.ThreadId.ToString(),
            Trigger = investigation.Trigger,
            StartedAt = investigation.StartedAt,
            Resolved = investigation.Resolved,
            Resolution = investigation.Resolution,
            Steps = []
        });
    }

    [HttpPost("{id:int}/steps")]
    public async Task<ActionResult<InvestigationStepDto>> AddStep(
        int id,
        [FromBody] CreateStepRequest request)
    {
        var investigation = await _memoryService.GetInvestigationByIdAsync(id);
        if (investigation is null)
            return NotFound();

        if (investigation.Resolved)
            return BadRequest("Cannot add steps to a resolved investigation");

        await _memoryService.RecordStepAsync(id, request.Action, request.Plugin, request.ResultSummary);

        // Get updated investigation to return the new step
        var updated = await _memoryService.GetInvestigationByIdAsync(id);
        var newStep = updated!.Steps.Last();

        return Ok(new InvestigationStepDto
        {
            Id = newStep.Id,
            Action = newStep.Action,
            Plugin = newStep.Plugin,
            ResultSummary = newStep.ResultSummary,
            Timestamp = newStep.Timestamp
        });
    }

    [HttpPost("{id:int}/resolve")]
    public async Task<ActionResult<InvestigationDetailDto>> Resolve(
        int id,
        [FromBody] ResolveInvestigationRequest request)
    {
        var investigation = await _memoryService.ResolveInvestigationAsync(id, request.Resolution);

        if (investigation is null)
            return NotFound();

        return Ok(new InvestigationDetailDto
        {
            Id = investigation.Id,
            ThreadId = investigation.ThreadId.ToString(),
            Trigger = investigation.Trigger,
            StartedAt = investigation.StartedAt,
            Resolved = investigation.Resolved,
            Resolution = investigation.Resolution,
            Steps = investigation.Steps.Select(s => new InvestigationStepDto
            {
                Id = s.Id,
                Action = s.Action,
                Plugin = s.Plugin,
                ResultSummary = s.ResultSummary,
                Timestamp = s.Timestamp
            }).ToList()
        });
    }

    [HttpGet("search")]
    public async Task<ActionResult<List<InvestigationDto>>> Search(
        [FromQuery] string symptom,
        [FromQuery] int limit = 5)
    {
        var items = await _memoryService.SearchPastIncidentsAsync(symptom, limit);

        return Ok(items.Select(i => new InvestigationDto
        {
            Id = i.Id,
            ThreadId = i.ThreadId.ToString(),
            Trigger = i.Trigger,
            StartedAt = i.StartedAt,
            Resolved = i.Resolved,
            Resolution = i.Resolution,
            StepCount = i.Steps.Count
        }).ToList());
    }
}

public record InvestigationDto
{
    public int Id { get; init; }
    public required string ThreadId { get; init; }
    public required string Trigger { get; init; }
    public DateTime StartedAt { get; init; }
    public bool Resolved { get; init; }
    public string? Resolution { get; init; }
    public int StepCount { get; init; }
}

public record InvestigationDetailDto
{
    public int Id { get; init; }
    public required string ThreadId { get; init; }
    public required string Trigger { get; init; }
    public DateTime StartedAt { get; init; }
    public bool Resolved { get; init; }
    public string? Resolution { get; init; }
    public required IReadOnlyList<InvestigationStepDto> Steps { get; init; }
}

public record InvestigationStepDto
{
    public int Id { get; init; }
    public required string Action { get; init; }
    public string? Plugin { get; init; }
    public string? ResultSummary { get; init; }
    public DateTime Timestamp { get; init; }
}

public record CreateInvestigationRequest
{
    public ulong? ThreadId { get; init; }
    public required string Trigger { get; init; }
}

public record CreateStepRequest
{
    public required string Action { get; init; }
    public string? Plugin { get; init; }
    public string? ResultSummary { get; init; }
}

public record ResolveInvestigationRequest
{
    public required string Resolution { get; init; }
}
