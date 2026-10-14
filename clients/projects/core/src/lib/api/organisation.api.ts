import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from '../http/api-client';
import { Guid, PagedResult } from '../models/common';
import {
  AcceptInvitationRequest,
  AcceptInvitationResultDto,
  AgentDetailDto,
  AgentListItemDto,
  AgentQuery,
  ArchiveDepartmentRequest,
  ArchiveDepartmentResultDto,
  AssignShiftRequest,
  CreateLeaveRequest,
  DeactivateAgentRequest,
  DeactivateAgentResultDto,
  DelegationDto,
  DepartmentDto,
  InviteAgentRequest,
  InviteResultDto,
  LeaveDto,
  LeaveQuery,
  MonitorSnapshotDto,
  RoleOptionDto,
  RoutingPreviewDto,
  RoutingPreviewRequest,
  SaveDelegationRequest,
  SaveDepartmentRequest,
  SaveShiftRequest,
  SaveSkillRequest,
  SaveTeamRequest,
  ScheduleEntryDto,
  SeatUsageDto,
  ShiftAssignmentDto,
  ShiftDto,
  SkillDto,
  TeamDto,
  TeamRoutingRequest,
  UpdateAgentAccessRequest,
  UpdateAgentProfileRequest,
  UpdateRoutingProfileRequest,
} from '../models/organisation';

const id = (value: Guid) => encodeURIComponent(value);

/**
 * Organisation structure (`/org`): departments (archive with reassignment), teams (members, lead, routing
 * configuration), the routing preview, the skills catalogue and role options for pickers.
 * Reading needs an organisation/supervision permission; structure changes need admin.organisation.
 */
@Injectable({ providedIn: 'root' })
export class OrganisationApi {
  private readonly api = inject(ApiClient);

  departments(includeArchived = false): Observable<DepartmentDto[]> {
    return this.api.get<DepartmentDto[]>('org/departments', { params: { includeArchived: includeArchived || null } });
  }

  department(departmentId: Guid): Observable<DepartmentDto> {
    return this.api.get<DepartmentDto>(`org/departments/${id(departmentId)}`);
  }

  saveDepartment(departmentId: Guid | null, request: SaveDepartmentRequest): Observable<DepartmentDto> {
    return departmentId
      ? this.api.put<DepartmentDto>(`org/departments/${id(departmentId)}`, request, { silent: true })
      : this.api.post<DepartmentDto>('org/departments', request, { silent: true });
  }

  archiveDepartment(departmentId: Guid, request: ArchiveDepartmentRequest): Observable<ArchiveDepartmentResultDto> {
    return this.api.post<ArchiveDepartmentResultDto>(`org/departments/${id(departmentId)}/archive`, request, { silent: true });
  }

  restoreDepartment(departmentId: Guid): Observable<DepartmentDto> {
    return this.api.post<DepartmentDto>(`org/departments/${id(departmentId)}/restore`, {});
  }

  teams(departmentId: Guid | null = null, includeInactive = false): Observable<TeamDto[]> {
    return this.api.get<TeamDto[]>('org/teams', { params: { departmentId, includeInactive: includeInactive || null } });
  }

  team(teamId: Guid): Observable<TeamDto> {
    return this.api.get<TeamDto>(`org/teams/${id(teamId)}`);
  }

  saveTeam(teamId: Guid | null, request: SaveTeamRequest): Observable<TeamDto> {
    return teamId
      ? this.api.put<TeamDto>(`org/teams/${id(teamId)}`, request, { silent: true })
      : this.api.post<TeamDto>('org/teams', request, { silent: true });
  }

  saveRouting(teamId: Guid, routing: TeamRoutingRequest, rowVersion: string): Observable<TeamDto> {
    return this.api.put<TeamDto>(`org/teams/${id(teamId)}/routing`, { ...routing, rowVersion }, { silent: true });
  }

  deleteTeam(teamId: Guid): Observable<void> {
    return this.api.delete<void>(`org/teams/${id(teamId)}`, { silent: true });
  }

  routingPreview(request: RoutingPreviewRequest): Observable<RoutingPreviewDto> {
    return this.api.post<RoutingPreviewDto>('org/routing/preview', request, { silent: true });
  }

  skills(): Observable<SkillDto[]> {
    return this.api.get<SkillDto[]>('org/skills');
  }

  saveSkill(skillId: Guid | null, request: SaveSkillRequest): Observable<SkillDto> {
    return skillId ? this.api.put<SkillDto>(`org/skills/${id(skillId)}`, request, { silent: true }) : this.api.post<SkillDto>('org/skills', request, { silent: true });
  }

  deleteSkill(skillId: Guid): Observable<void> {
    return this.api.delete<void>(`org/skills/${id(skillId)}`);
  }

  /** Read-only role list for pickers. */
  roleOptions(): Observable<RoleOptionDto[]> {
    return this.api.get<RoleOptionDto[]>('org/role-options');
  }
}

/**
 * Agent administration (`/org/agents`): keyset-paged list, invitations (with the demo code in demo mode), profile,
 * access (roles, departments, teams, light agent), routing profile, availability, avatar, deactivation and
 * reactivation, plan seat usage. The anonymous `acceptInvitation` sets the invitee's password.
 */
@Injectable({ providedIn: 'root' })
export class AgentsAdminApi {
  private readonly api = inject(ApiClient);

  list(query: AgentQuery = {}): Observable<PagedResult<AgentListItemDto>> {
    return this.api.get<PagedResult<AgentListItemDto>>('org/agents', { params: query });
  }

  seats(): Observable<SeatUsageDto> {
    return this.api.get<SeatUsageDto>('org/agents/seats');
  }

  get(userId: Guid): Observable<AgentDetailDto> {
    return this.api.get<AgentDetailDto>(`org/agents/${id(userId)}`);
  }

  invite(request: InviteAgentRequest): Observable<InviteResultDto> {
    return this.api.post<InviteResultDto>('org/agents', request, { silent: true });
  }

  resendInvitation(userId: Guid): Observable<InviteResultDto> {
    return this.api.post<InviteResultDto>(`org/agents/${id(userId)}/invitation`, {}, { silent: true });
  }

  revokeInvitation(userId: Guid): Observable<AgentDetailDto> {
    return this.api.delete<AgentDetailDto>(`org/agents/${id(userId)}/invitation`, { silent: true });
  }

  updateProfile(userId: Guid, request: UpdateAgentProfileRequest): Observable<AgentDetailDto> {
    return this.api.put<AgentDetailDto>(`org/agents/${id(userId)}`, request, { silent: true });
  }

  updateAccess(userId: Guid, request: UpdateAgentAccessRequest): Observable<AgentDetailDto> {
    return this.api.put<AgentDetailDto>(`org/agents/${id(userId)}/access`, request, { silent: true });
  }

  updateRoutingProfile(userId: Guid, request: UpdateRoutingProfileRequest): Observable<AgentDetailDto> {
    return this.api.put<AgentDetailDto>(`org/agents/${id(userId)}/routing-profile`, request, { silent: true });
  }

  setAvailability(userId: Guid, availability: string): Observable<AgentDetailDto> {
    return this.api.put<AgentDetailDto>(`org/agents/${id(userId)}/availability`, { availability }, { silent: true });
  }

  deactivate(userId: Guid, request: DeactivateAgentRequest): Observable<DeactivateAgentResultDto> {
    return this.api.post<DeactivateAgentResultDto>(`org/agents/${id(userId)}/deactivate`, request, { silent: true });
  }

  reactivate(userId: Guid): Observable<AgentDetailDto> {
    return this.api.post<AgentDetailDto>(`org/agents/${id(userId)}/reactivate`, {}, { silent: true });
  }

  uploadAvatar(userId: Guid, file: File): Observable<AgentDetailDto> {
    const form = new FormData();
    form.append('file', file, file.name);
    return this.api.post<AgentDetailDto>(`org/agents/${id(userId)}/avatar`, form, { silent: true });
  }

  removeAvatar(userId: Guid): Observable<AgentDetailDto> {
    return this.api.delete<AgentDetailDto>(`org/agents/${id(userId)}/avatar`, { silent: true });
  }

  /** Anonymous: accept an invitation with the e-mailed code and choose a password. */
  acceptInvitation(request: AcceptInvitationRequest): Observable<AcceptInvitationResultDto> {
    return this.api.post<AcceptInvitationResultDto>('org/public/invitations/accept', request, { silent: true, skipAuth: true });
  }
}

/**
 * Workforce (`/org/shifts|shift-assignments|leaves|delegations|schedule`): shift patterns and assignments, leave
 * requests (approved through the Approvals inbox or by managers), out-of-office delegation and the team schedule.
 */
@Injectable({ providedIn: 'root' })
export class WorkforceApi {
  private readonly api = inject(ApiClient);

  shifts(): Observable<ShiftDto[]> {
    return this.api.get<ShiftDto[]>('org/shifts');
  }

  saveShift(shiftId: Guid | null, request: SaveShiftRequest): Observable<ShiftDto> {
    return shiftId ? this.api.put<ShiftDto>(`org/shifts/${id(shiftId)}`, request, { silent: true }) : this.api.post<ShiftDto>('org/shifts', request, { silent: true });
  }

  deleteShift(shiftId: Guid): Observable<void> {
    return this.api.delete<void>(`org/shifts/${id(shiftId)}`);
  }

  assignments(userId: Guid | null = null, shiftId: Guid | null = null): Observable<ShiftAssignmentDto[]> {
    return this.api.get<ShiftAssignmentDto[]>('org/shift-assignments', { params: { userId, shiftId } });
  }

  assignShift(request: AssignShiftRequest): Observable<ShiftAssignmentDto[]> {
    return this.api.post<ShiftAssignmentDto[]>('org/shift-assignments', request, { silent: true });
  }

  removeAssignment(assignmentId: Guid): Observable<void> {
    return this.api.delete<void>(`org/shift-assignments/${id(assignmentId)}`);
  }

  leaves(query: LeaveQuery = {}): Observable<LeaveDto[]> {
    return this.api.get<LeaveDto[]>('org/leaves', { params: query });
  }

  requestLeave(request: CreateLeaveRequest): Observable<LeaveDto> {
    return this.api.post<LeaveDto>('org/leaves', request, { silent: true });
  }

  approveLeave(leaveId: Guid, note: string | null): Observable<LeaveDto> {
    return this.api.post<LeaveDto>(`org/leaves/${id(leaveId)}/approve`, { note }, { silent: true });
  }

  rejectLeave(leaveId: Guid, note: string | null): Observable<LeaveDto> {
    return this.api.post<LeaveDto>(`org/leaves/${id(leaveId)}/reject`, { note }, { silent: true });
  }

  cancelLeave(leaveId: Guid): Observable<LeaveDto> {
    return this.api.post<LeaveDto>(`org/leaves/${id(leaveId)}/cancel`, {}, { silent: true });
  }

  delegations(includeEnded = false): Observable<DelegationDto[]> {
    return this.api.get<DelegationDto[]>('org/delegations', { params: { includeEnded: includeEnded || null } });
  }

  saveDelegation(delegationId: Guid | null, request: SaveDelegationRequest): Observable<DelegationDto> {
    return delegationId
      ? this.api.put<DelegationDto>(`org/delegations/${id(delegationId)}`, request, { silent: true })
      : this.api.post<DelegationDto>('org/delegations', request, { silent: true });
  }

  endDelegation(delegationId: Guid): Observable<DelegationDto> {
    return this.api.post<DelegationDto>(`org/delegations/${id(delegationId)}/end`, {}, { silent: true });
  }

  schedule(from: Date, to: Date, departmentId: Guid | null = null): Observable<ScheduleEntryDto[]> {
    return this.api.get<ScheduleEntryDto[]>('org/schedule', { params: { from, to, departmentId } });
  }
}

/** Supervisor live monitor snapshot (`GET /org/monitor`), limited to the caller's data scope. */
@Injectable({ providedIn: 'root' })
export class SupervisorMonitorApi {
  private readonly api = inject(ApiClient);

  snapshot(departmentId: Guid | null = null): Observable<MonitorSnapshotDto> {
    return this.api.get<MonitorSnapshotDto>('org/monitor', { params: { departmentId }, silent: true });
  }
}
