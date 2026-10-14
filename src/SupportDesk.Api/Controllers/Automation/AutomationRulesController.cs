using Microsoft.AspNetCore.Mvc;
using SupportDesk.Application.Automation;
using SupportDesk.Contracts.Automation;
using SupportDesk.Contracts.Common;

namespace SupportDesk.Api.Controllers.Automation;

/// <summary>Automation rules: designer catalog, CRUD, versions, activation/order, validation, simulation, conflicts, execution history.</summary>
[Route("api/v1/automation")]
public sealed class AutomationRulesController(RuleService rules) : DeskControllerBase
{
    [HttpGet("catalog")]
    public AutomationCatalogDto Catalog() => rules.Catalog();

    [HttpGet("lookups")]
    public Task<AutomationLookupsDto> Lookups(CancellationToken ct) => rules.LookupsAsync(ct);

    // ───── Rules ─────

    [HttpGet("rules")]
    public Task<List<RuleSummaryDto>> List([FromQuery] string? trigger, [FromQuery] bool? active, CancellationToken ct) => rules.ListAsync(trigger, active, ct);

    [HttpPost("rules")]
    public async Task<ActionResult<RuleDetailDto>> Create(SaveRuleRequest request, CancellationToken ct)
    {
        var rule = await rules.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = rule.Rule.Id }, rule);
    }

    [HttpGet("rules/{id:guid}")]
    public Task<RuleDetailDto> Get(Guid id, CancellationToken ct) => rules.GetAsync(id, ct);

    [HttpPut("rules/{id:guid}")]
    public Task<RuleDetailDto> Update(Guid id, SaveRuleRequest request, CancellationToken ct) => rules.UpdateAsync(id, request, ct);

    [HttpDelete("rules/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await rules.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpGet("rules/{id:guid}/versions/{versionId:guid}")]
    public Task<RuleVersionDto> Version(Guid id, Guid versionId, CancellationToken ct) => rules.GetVersionAsync(id, versionId, ct);

    [HttpPost("rules/{id:guid}/versions/{versionId:guid}/publish")]
    public Task<RuleDetailDto> Publish(Guid id, Guid versionId, [FromBody] PublishVersionRequest? request, CancellationToken ct) =>
        rules.PublishAsync(id, versionId, request?.ChangeNote, ct);

    /// <summary>Publishes an older version's definition again as a new version.</summary>
    [HttpPost("rules/{id:guid}/versions/{versionId:guid}/rollback")]
    public Task<RuleDetailDto> Rollback(Guid id, Guid versionId, [FromBody] PublishVersionRequest? request, CancellationToken ct) =>
        rules.RollbackAsync(id, versionId, request?.ChangeNote, ct);

    [HttpPost("rules/{id:guid}/activate")]
    public Task<RuleDetailDto> Activate(Guid id, CancellationToken ct) => rules.SetActiveAsync(id, true, ct);

    [HttpPost("rules/{id:guid}/deactivate")]
    public Task<RuleDetailDto> Deactivate(Guid id, CancellationToken ct) => rules.SetActiveAsync(id, false, ct);

    [HttpPost("rules/reorder")]
    public Task<List<RuleSummaryDto>> Reorder(ReorderRulesRequest request, CancellationToken ct) => rules.ReorderAsync(request, ct);

    /// <summary>Issues a new inbound webhook secret (returned once).</summary>
    [HttpPost("rules/{id:guid}/rotate-webhook-secret")]
    public Task<RuleDetailDto> RotateWebhookSecret(Guid id, CancellationToken ct) => rules.RotateWebhookSecretAsync(id, ct);

    [HttpPost("rules/validate")]
    public Task<ValidationResultDto> Validate(ValidateRuleRequest request, CancellationToken ct) => rules.ValidateAsync(request, ct);

    /// <summary>Dry run of a saved rule (latest or given version, or an unsaved definition) — no side effects.</summary>
    [HttpPost("rules/{id:guid}/simulate")]
    public Task<SimulationResultDto> Simulate(Guid id, SimulateRuleRequest request, CancellationToken ct) => rules.SimulateAsync(id, request, ct);

    /// <summary>Dry run of an unsaved definition (designer preview).</summary>
    [HttpPost("rules/simulate")]
    public Task<SimulationResultDto> SimulateDraft(SimulateRuleRequest request, CancellationToken ct) => rules.SimulateAsync(null, request, ct);

    [HttpGet("rules/{id:guid}/conflicts")]
    public Task<List<RuleConflictDto>> RuleConflicts(Guid id, CancellationToken ct) => rules.ConflictsForRuleAsync(id, ct);

    [HttpGet("conflicts")]
    public Task<List<RuleConflictDto>> Conflicts([FromQuery] string? trigger, CancellationToken ct) => rules.ConflictsAsync(trigger, ct);

    // ───── Execution history ─────

    [HttpGet("executions")]
    public Task<PageResult<RuleExecutionDto>> Executions([FromQuery] ExecutionQuery query, CancellationToken ct) => rules.ExecutionsAsync(query, ct);

    [HttpGet("executions/{id:guid}")]
    public Task<RuleExecutionDto> Execution(Guid id, CancellationToken ct) => rules.ExecutionAsync(id, ct);

    [HttpPost("executions/{id:guid}/retry")]
    public Task<RuleExecutionDto> Retry(Guid id, CancellationToken ct) => rules.RetryAsync(id, ct);
}
