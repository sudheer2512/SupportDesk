using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SupportDesk.Api.Infrastructure;
using SupportDesk.Application.Approvals;
using SupportDesk.Application.Organisation;
using SupportDesk.Application.Platform;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Organisation;

namespace SupportDesk.Api.Controllers.Organisation;

/// <summary>Departments, teams (members, lead, routing configuration), routing preview, skills and role options.</summary>
[Route("api/v1/org")]
public sealed class OrganisationController(DepartmentService departments, TeamService teams, SkillService skills) : DeskControllerBase
{
    [HttpGet("departments")]
    public Task<List<DepartmentDto>> Departments([FromQuery] bool includeArchived = false, CancellationToken ct = default) => departments.ListAsync(includeArchived, ct);

    [HttpGet("departments/{id:guid}")]
    public Task<DepartmentDto> Department(Guid id, CancellationToken ct) => departments.GetAsync(id, ct);

    [HttpPost("departments")]
    public Task<DepartmentDto> CreateDepartment(SaveDepartmentRequest r, CancellationToken ct) => departments.CreateAsync(r, ct);

    [HttpPut("departments/{id:guid}")]
    public Task<DepartmentDto> UpdateDepartment(Guid id, SaveDepartmentRequest r, CancellationToken ct) => departments.UpdateAsync(id, r, ct);

    [HttpPost("departments/{id:guid}/archive")]
    public Task<ArchiveDepartmentResultDto> ArchiveDepartment(Guid id, ArchiveDepartmentRequest r, CancellationToken ct) => departments.ArchiveAsync(id, r, ct);

    [HttpPost("departments/{id:guid}/restore")]
    public Task<DepartmentDto> RestoreDepartment(Guid id, CancellationToken ct) => departments.RestoreAsync(id, ct);

    [HttpGet("teams")]
    public Task<List<TeamDto>> Teams([FromQuery] Guid? departmentId, [FromQuery] bool includeInactive = false, CancellationToken ct = default) =>
        teams.ListAsync(departmentId, includeInactive, ct);

    [HttpGet("teams/{id:guid}")]
    public Task<TeamDto> Team(Guid id, CancellationToken ct) => teams.GetAsync(id, ct);

    [HttpPost("teams")]
    public Task<TeamDto> CreateTeam(SaveTeamRequest r, CancellationToken ct) => teams.CreateAsync(r, ct);

    [HttpPut("teams/{id:guid}")]
    public Task<TeamDto> UpdateTeam(Guid id, SaveTeamRequest r, CancellationToken ct) => teams.UpdateAsync(id, r, ct);

    public sealed record TeamRoutingUpdate(string Mode, bool OnlyOnlineAgents, bool ConsiderShifts, int? MaxOpenTicketsPerAgent, Guid? FallbackTeamId, string? RowVersion);

    [HttpPut("teams/{id:guid}/routing")]
    public Task<TeamDto> UpdateRouting(Guid id, TeamRoutingUpdate r, CancellationToken ct) =>
        teams.UpdateRoutingAsync(id, new TeamRoutingRequest(r.Mode, r.OnlyOnlineAgents, r.ConsiderShifts, r.MaxOpenTicketsPerAgent, r.FallbackTeamId), r.RowVersion, ct);

    [HttpDelete("teams/{id:guid}")]
    public async Task<IActionResult> DeleteTeam(Guid id, CancellationToken ct)
    {
        await teams.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("routing/preview")]
    public Task<RoutingPreviewDto> RoutingPreview(RoutingPreviewRequest r, CancellationToken ct) => teams.PreviewAsync(r, ct);

    [HttpGet("skills")]
    public Task<List<SkillDto>> Skills(CancellationToken ct) => skills.ListAsync(ct);

    [HttpPost("skills")]
    public Task<SkillDto> CreateSkill(SaveSkillRequest r, CancellationToken ct) => skills.SaveAsync(null, r, ct);

    [HttpPut("skills/{id:guid}")]
    public Task<SkillDto> UpdateSkill(Guid id, SaveSkillRequest r, CancellationToken ct) => skills.SaveAsync(id, r, ct);

    [HttpDelete("skills/{id:guid}")]
    public async Task<IActionResult> DeleteSkill(Guid id, CancellationToken ct)
    {
        await skills.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>Read-only role list for pickers (the roles administration API lives under /admin/roles).</summary>
    [HttpGet("role-options")]
    public Task<List<RoleOptionDto>> RoleOptions(CancellationToken ct) => skills.RoleOptionsAsync(ct);
}

/// <summary>Agents: list, invite, profile, access, routing profile, availability, avatar, deactivate/reactivate, plan seats.</summary>
[Route("api/v1/org/agents")]
public sealed class AgentsController(AgentAdminService agents, AgentInvitationService invitations) : DeskControllerBase
{
    [HttpGet]
    public Task<PagedResult<AgentListItemDto>> List([FromQuery] AgentQuery q, CancellationToken ct) => agents.ListAsync(q, ct);

    [HttpGet("seats")]
    public Task<SeatUsageDto> Seats(CancellationToken ct) => agents.SeatsAsync(ct);

    [HttpPost]
    public Task<InviteResultDto> Invite(InviteAgentRequest r, CancellationToken ct) => invitations.InviteAsync(r, ct);

    [HttpGet("{id:guid}")]
    public Task<AgentDetailDto> Get(Guid id, CancellationToken ct) => agents.GetAsync(id, ct);

    [HttpPut("{id:guid}")]
    public Task<AgentDetailDto> UpdateProfile(Guid id, UpdateAgentProfileRequest r, CancellationToken ct) => agents.UpdateProfileAsync(id, r, ct);

    [HttpPut("{id:guid}/access")]
    public Task<AgentDetailDto> UpdateAccess(Guid id, UpdateAgentAccessRequest r, CancellationToken ct) => agents.UpdateAccessAsync(id, r, ct);

    [HttpPut("{id:guid}/routing-profile")]
    public Task<AgentDetailDto> UpdateRoutingProfile(Guid id, UpdateRoutingProfileRequest r, CancellationToken ct) => agents.UpdateRoutingProfileAsync(id, r, ct);

    [HttpPut("{id:guid}/availability")]
    public Task<AgentDetailDto> SetAvailability(Guid id, SetAvailabilityRequest r, CancellationToken ct) => agents.SetAvailabilityAsync(id, r, ct);

    [HttpPost("{id:guid}/deactivate")]
    public Task<DeactivateAgentResultDto> Deactivate(Guid id, DeactivateAgentRequest r, CancellationToken ct) => agents.DeactivateAsync(id, r, ct);

    [HttpPost("{id:guid}/reactivate")]
    public Task<AgentDetailDto> Reactivate(Guid id, CancellationToken ct) => agents.ReactivateAsync(id, ct);

    [HttpPost("{id:guid}/invitation")]
    public Task<InviteResultDto> ResendInvitation(Guid id, CancellationToken ct) => invitations.ResendAsync(id, ct);

    [HttpDelete("{id:guid}/invitation")]
    public Task<AgentDetailDto> RevokeInvitation(Guid id, CancellationToken ct) => invitations.RevokeAsync(id, ct);

    [HttpPost("{id:guid}/avatar")]
    [RequestSizeLimit(3 * 1024 * 1024)]
    public async Task<AgentDetailDto> UploadAvatar(Guid id, IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        return await agents.UploadAvatarAsync(id, stream, file.FileName, file.ContentType, ct);
    }

    [HttpDelete("{id:guid}/avatar")]
    public Task<AgentDetailDto> RemoveAvatar(Guid id, CancellationToken ct) => agents.RemoveAvatarAsync(id, ct);
}

/// <summary>Anonymous endpoints: accepting an agent invitation (sets the password) and avatar images.</summary>
[ApiController]
[Route("api/v1/org/public")]
[AllowAnonymous]
[Produces("application/json")]
public sealed class OrganisationPublicController(AgentInvitationService invitations, AgentAdminService agents, IFileStorage storage) : ControllerBase
{
    [HttpPost("invitations/accept"), EnableRateLimiting(HostingSetup.AuthLimiter)]
    public Task<AcceptInvitationResultDto> Accept(AcceptInvitationRequest r, CancellationToken ct) => invitations.AcceptAsync(r, ct);

    [HttpGet("avatars/{fileId:guid}"), EnableRateLimiting(HostingSetup.PublicLimiter)]
    public async Task<IActionResult> Avatar(Guid fileId, CancellationToken ct)
    {
        var (file, stream) = await agents.OpenAvatarAsync(fileId, storage, ct);
        Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
        Response.Headers.CacheControl = "public, max-age=86400";
        return File(stream, file.ContentType);
    }
}

/// <summary>Shifts and assignments, leave (with approvals), delegation and the team schedule.</summary>
[Route("api/v1/org")]
public sealed class WorkforceController(WorkforceService workforce, ApprovalService approvals) : DeskControllerBase
{
    [HttpGet("shifts")]
    public Task<List<ShiftDto>> Shifts(CancellationToken ct) => workforce.ShiftsAsync(ct);

    [HttpPost("shifts")]
    public Task<ShiftDto> CreateShift(SaveShiftRequest r, CancellationToken ct) => workforce.SaveShiftAsync(null, r, ct);

    [HttpPut("shifts/{id:guid}")]
    public Task<ShiftDto> UpdateShift(Guid id, SaveShiftRequest r, CancellationToken ct) => workforce.SaveShiftAsync(id, r, ct);

    [HttpDelete("shifts/{id:guid}")]
    public async Task<IActionResult> DeleteShift(Guid id, CancellationToken ct)
    {
        await workforce.DeleteShiftAsync(id, ct);
        return NoContent();
    }

    [HttpGet("shift-assignments")]
    public Task<List<ShiftAssignmentDto>> Assignments([FromQuery] Guid? userId, [FromQuery] Guid? shiftId, CancellationToken ct) =>
        workforce.AssignmentsAsync(userId, shiftId, ct);

    [HttpPost("shift-assignments")]
    public Task<List<ShiftAssignmentDto>> Assign(AssignShiftRequest r, CancellationToken ct) => workforce.AssignShiftAsync(r, ct);

    [HttpDelete("shift-assignments/{id:guid}")]
    public async Task<IActionResult> RemoveAssignment(Guid id, CancellationToken ct)
    {
        await workforce.RemoveAssignmentAsync(id, ct);
        return NoContent();
    }

    [HttpGet("leaves")]
    public Task<List<LeaveDto>> Leaves([FromQuery] LeaveQuery q, CancellationToken ct) => workforce.LeavesAsync(q, ct);

    [HttpPost("leaves")]
    public Task<LeaveDto> RequestLeave(CreateLeaveRequest r, CancellationToken ct) => workforce.RequestLeaveAsync(r, ct);

    [HttpPost("leaves/{id:guid}/approve")]
    public Task<LeaveDto> Approve(Guid id, DecideLeaveRequest r, CancellationToken ct) => workforce.DecideLeaveAsync(id, true, r, approvals, ct);

    [HttpPost("leaves/{id:guid}/reject")]
    public Task<LeaveDto> Reject(Guid id, DecideLeaveRequest r, CancellationToken ct) => workforce.DecideLeaveAsync(id, false, r, approvals, ct);

    [HttpPost("leaves/{id:guid}/cancel")]
    public Task<LeaveDto> Cancel(Guid id, CancellationToken ct) => workforce.CancelLeaveAsync(id, ct);

    [HttpGet("delegations")]
    public Task<List<DelegationDto>> Delegations([FromQuery] bool includeEnded = false, CancellationToken ct = default) => workforce.DelegationsAsync(includeEnded, ct);

    [HttpPost("delegations")]
    public Task<DelegationDto> CreateDelegation(SaveDelegationRequest r, CancellationToken ct) => workforce.SaveDelegationAsync(null, r, ct);

    [HttpPut("delegations/{id:guid}")]
    public Task<DelegationDto> UpdateDelegation(Guid id, SaveDelegationRequest r, CancellationToken ct) => workforce.SaveDelegationAsync(id, r, ct);

    [HttpPost("delegations/{id:guid}/end")]
    public Task<DelegationDto> EndDelegation(Guid id, CancellationToken ct) => workforce.EndDelegationAsync(id, ct);

    [HttpGet("schedule")]
    public Task<List<ScheduleEntryDto>> Schedule([FromQuery] DateTime from, [FromQuery] DateTime to, [FromQuery] Guid? departmentId, CancellationToken ct) =>
        workforce.ScheduleAsync(DateTime.SpecifyKind(from, DateTimeKind.Utc), DateTime.SpecifyKind(to, DateTimeKind.Utc), departmentId, ct);
}

/// <summary>Supervisor live monitor snapshot (refreshed by the desk on realtime events).</summary>
[Route("api/v1/org/monitor")]
public sealed class MonitorController(SupervisorMonitorService monitor) : DeskControllerBase
{
    [HttpGet]
    public Task<MonitorSnapshotDto> Snapshot([FromQuery] Guid? departmentId, CancellationToken ct) => monitor.SnapshotAsync(departmentId, ct);
}
