using Microsoft.AspNetCore.Mvc;
using SupportDesk.Application.Ai.Admin;
using SupportDesk.Contracts.Ai;
using SupportDesk.Contracts.Common;

namespace SupportDesk.Api.Controllers.Ai;

/// <summary>AI administration (admin.ai): configuration, prompt templates, usage &amp; cost, execution log, provider test, agents.</summary>
[Route("api/v1/admin/ai")]
public sealed class AiAdminController(AiAdminService admin) : DeskControllerBase
{
    [HttpGet("config")]
    public Task<AiConfigurationDto> GetConfig(CancellationToken ct) => admin.GetConfigAsync(ct);

    [HttpPut("config")]
    public Task<AiConfigurationDto> UpdateConfig(UpdateAiConfigurationRequest request, CancellationToken ct) => admin.UpdateConfigAsync(request, ct);

    [HttpPost("test-provider")]
    public Task<TestProviderResultDto> TestProvider([FromBody] TestProviderRequest? request, CancellationToken ct) =>
        admin.TestProviderAsync(request ?? new TestProviderRequest(), ct);

    [HttpGet("prompts")]
    public Task<List<PromptTemplateDto>> Prompts(CancellationToken ct) => admin.ListPromptsAsync(ct);

    /// <summary>Saves a tenant override as a new active version.</summary>
    [HttpPut("prompts/{feature}")]
    public Task<PromptTemplateDto> SavePrompt(string feature, SavePromptTemplateRequest request, CancellationToken ct) => admin.SavePromptAsync(feature, request, ct);

    /// <summary>Activates a saved version (0 = built-in default).</summary>
    [HttpPost("prompts/{feature}/versions/{version:int}/activate")]
    public Task<PromptTemplateDto> ActivatePrompt(string feature, int version, CancellationToken ct) => admin.ActivatePromptAsync(feature, version, ct);

    [HttpDelete("prompts/{feature}")]
    public Task<PromptTemplateDto> ResetPrompt(string feature, CancellationToken ct) => admin.ActivatePromptAsync(feature, 0, ct);

    [HttpGet("usage")]
    public Task<AiUsageSummaryDto> Usage([FromQuery] int months = 6, CancellationToken ct = default) => admin.UsageAsync(months, ct);

    [HttpGet("executions")]
    public Task<PageResult<AiExecutionDto>> Executions([FromQuery] string? feature, [FromQuery] string? status, [FromQuery] string? outcome,
        [FromQuery] Guid? entityId, [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        CancellationToken ct = default) =>
        admin.ExecutionsAsync(feature, status, outcome, entityId, from, to, page, pageSize, ct);

    [HttpGet("executions/{id:guid}")]
    public Task<AiExecutionDto> Execution(Guid id, CancellationToken ct) => admin.ExecutionAsync(id, ct);

    [HttpGet("agents")]
    public Task<List<AiAgentDto>> Agents(CancellationToken ct) => admin.ListAgentsAsync(ct);

    [HttpPost("agents")]
    public Task<AiAgentDto> CreateAgent(SaveAiAgentRequest request, CancellationToken ct) => admin.SaveAgentAsync(null, request, ct);

    [HttpPut("agents/{id:guid}")]
    public Task<AiAgentDto> UpdateAgent(Guid id, SaveAiAgentRequest request, CancellationToken ct) => admin.SaveAgentAsync(id, request, ct);

    [HttpDelete("agents/{id:guid}")]
    public async Task<IActionResult> DeleteAgent(Guid id, CancellationToken ct)
    {
        await admin.DeleteAgentAsync(id, ct);
        return NoContent();
    }

    /// <summary>Indexes missing knowledge, removes stale chunks and re-embeds chunks of an old embedding model now.</summary>
    [HttpPost("reindex")]
    public Task<ReindexResultDto> Reindex(CancellationToken ct) => admin.ReindexAsync(ct);
}
