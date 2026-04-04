using HomelabBot.Models;
using HomelabBot.Services;
using Microsoft.AspNetCore.Mvc;

namespace HomelabBot.Controllers;

[ApiController]
[Route("api/[controller]")]
public class InvestigationsController : ControllerBase
{
    private readonly InvestigationService _memoryService;
    private readonly IncidentSimilarityService _similarityService;
    private readonly ILogger<InvestigationsController> _logger;

    public InvestigationsController(InvestigationService memoryService, IncidentSimilarityService similarityService, ILogger<InvestigationsController> logger)
    {
        _memoryService = memoryService;
        _similarityService = similarityService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<InvestigationDto>>> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool? resolved = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Listing investigations Page={Page} PageSize={PageSize} Resolved={Resolved}", page, pageSize, resolved);

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
        {
            _logger.LogWarning("Investigation not found Id={Id}", id);
            return NotFound();
        }

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
        _logger.LogInformation("Creating investigation Trigger={Trigger}", request.Trigger);

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
        _logger.LogInformation("Adding step to investigation Id={Id}", id);

        var investigation = await _memoryService.GetInvestigationByIdAsync(id);
        if (investigation is null)
        {
            _logger.LogWarning("Investigation not found Id={Id}", id);
            return NotFound();
        }

        if (investigation.Resolved)
        {
            _logger.LogWarning("Cannot add step to resolved investigation Id={Id}", id);
            return BadRequest("Cannot add steps to a resolved investigation");
        }

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
        _logger.LogInformation("Resolving investigation Id={Id}", id);

        var investigation = await _memoryService.ResolveInvestigationAsync(id, request.Resolution);

        if (investigation is null)
        {
            _logger.LogWarning("Investigation not found Id={Id}", id);
            return NotFound();
        }

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
        _logger.LogInformation("Searching investigations Symptom={Symptom} Limit={Limit}", symptom, limit);

        var items = await _similarityService.FindSimilarAsync(symptom, limit: limit);
        if (items.Count == 0)
        {
            return Ok(new List<InvestigationDto>());
        }

        var ids = items.Select(i => i.InvestigationId).ToList();
        var lookup = await _memoryService.GetInvestigationsByIdsAsync(ids);

        return Ok(items.Select(i =>
        {
            lookup.TryGetValue(i.InvestigationId, out var inv);
            return new InvestigationDto
            {
                Id = i.InvestigationId,
                ThreadId = inv?.ThreadId.ToString() ?? "",
                Trigger = i.Trigger ?? "",
                StartedAt = i.OccurredAt,
                Resolved = inv?.Resolved ?? true,
                Resolution = i.Resolution,
                StepCount = inv?.Steps.Count ?? 0
            };
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
