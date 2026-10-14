using Microsoft.AspNetCore.Mvc;
using SupportDesk.Application.Automation;
using SupportDesk.Contracts.Automation;

namespace SupportDesk.Api.Controllers.Automation;

/// <summary>Blueprint (state-transition) workflow administration.</summary>
[Route("api/v1/automation/blueprints")]
public sealed class BlueprintsController(BlueprintService blueprints) : DeskControllerBase
{
    [HttpGet]
    public Task<List<BlueprintSummaryDto>> List(CancellationToken ct) => blueprints.ListAsync(ct);

    [HttpPost]
    public async Task<ActionResult<BlueprintDetailDto>> Create(SaveBlueprintRequest request, CancellationToken ct)
    {
        var bp = await blueprints.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = bp.Blueprint.Id }, bp);
    }

    [HttpGet("{id:guid}")]
    public Task<BlueprintDetailDto> Get(Guid id, CancellationToken ct) => blueprints.GetAsync(id, ct);

    [HttpPut("{id:guid}")]
    public Task<BlueprintDetailDto> Update(Guid id, SaveBlueprintRequest request, CancellationToken ct) => blueprints.UpdateAsync(id, request, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await blueprints.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/versions/{versionId:guid}/publish")]
    public Task<BlueprintDetailDto> Publish(Guid id, Guid versionId, [FromBody] PublishVersionRequest? request, CancellationToken ct) =>
        blueprints.PublishAsync(id, versionId, request?.ChangeNote, ct);

    [HttpPost("{id:guid}/activate")]
    public Task<BlueprintDetailDto> Activate(Guid id, CancellationToken ct) => blueprints.SetActiveAsync(id, true, ct);

    [HttpPost("{id:guid}/deactivate")]
    public Task<BlueprintDetailDto> Deactivate(Guid id, CancellationToken ct) => blueprints.SetActiveAsync(id, false, ct);

    [HttpPost("validate")]
    public Task<ValidationResultDto> Validate(ValidateBlueprintRequest request, CancellationToken ct) => blueprints.ValidateAsync(request, ct);
}

/// <summary>A ticket's blueprint: current state, transitions available to the caller, transition execution.</summary>
[Route("api/v1/tickets/{id:guid}/blueprint")]
public sealed class TicketBlueprintController(BlueprintService blueprints) : DeskControllerBase
{
    /// <summary>204 when the ticket does not follow a blueprint.</summary>
    [HttpGet]
    public Task<TicketBlueprintDto?> Get(Guid id, CancellationToken ct) => blueprints.GetTicketBlueprintAsync(id, ct);

    /// <summary>200 when completed; 202 when the transition awaits approval.</summary>
    [HttpPost("transitions/{key}")]
    public async Task<ActionResult<TransitionResultDto>> Transition(Guid id, string key, [FromBody] ExecuteTransitionRequest? request, CancellationToken ct)
    {
        var result = await blueprints.ExecuteTransitionAsync(id, key, request ?? new ExecuteTransitionRequest(), ct);
        return result.Status == "pending_approval" ? Accepted(result) : Ok(result);
    }

    /// <summary>Takes the ticket off its blueprint (admin.automation).</summary>
    [HttpDelete]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        await blueprints.RemoveFromBlueprintAsync(id, ct);
        return NoContent();
    }
}
