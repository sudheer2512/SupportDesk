using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Approvals;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Organisation;
using SupportDesk.Domain;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Organisation;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Organisation;

/// <summary>
/// Workforce management: shift patterns and assignments, leave (requests go through the Approvals inbox; managers
/// record approved leave directly), out-of-office delegation (tickets and approvals of the absent agent go to the
/// delegate within the window) and a combined schedule for calendars. Routing skips agents on approved leave,
/// delegated away or off shift (teams that consider shifts).
/// </summary>
public sealed class WorkforceService(DeskDbContext db, RequestContext ctx, IAuditWriter audit, IEventPublisher events, IJobQueue jobs, AgentAdminService agents)
{
    public const string LeaveApprovalType = "agent_leave";

    // ───────────────────────────── Shifts ─────────────────────────────

    public async Task<List<ShiftDto>> ShiftsAsync(CancellationToken ct = default)
    {
        ctx.RequireAny(OrgPermissions.ReadStructure);
        var today = DateOnly.FromDateTime(db.Clock.UtcNow);
        var counts = await db.AgentShiftAssignments.AsNoTracking().Where(a => a.EffectiveFrom <= today && (a.EffectiveTo == null || a.EffectiveTo >= today))
            .GroupBy(a => a.ShiftId).Select(g => new { g.Key, Count = g.Select(x => x.UserId).Distinct().Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var rows = await db.Shifts.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
        return rows.Select(s => MapShift(s, counts.GetValueOrDefault(s.Id))).ToList();
    }

    public async Task<ShiftDto> SaveShiftAsync(Guid? id, SaveShiftRequest r, CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.AgentsManage, Permissions.AdminOrganisation);
        var shift = id is { } existing ? await db.Shifts.FirstOrDefaultAsync(s => s.Id == existing, ct) ?? throw new NotFoundException("Shift", existing) : new Shift();
        if (id is not null) OrgRowVersion.Apply(db, shift, r.RowVersion, "shift");
        var name = (r.Name ?? "").Trim();
        if (name.Length is < 2 or > 80) throw new ValidationException(new Dictionary<string, string> { ["name"] = "Enter a name of 2–80 characters." });
        WeeklyHours.EnsureTimeZone(r.TimeZone);
        var json = WeeklyHours.ToJson(r.WeeklyHours);
        if (WeeklyHours.TotalHours(json) <= 0) throw new ValidationException(new Dictionary<string, string> { ["weeklyHours"] = "Add at least one working interval." });
        if (r.Color is { Length: > 0 } c && !System.Text.RegularExpressions.Regex.IsMatch(c, "^#[0-9a-fA-F]{6}$"))
            throw new ValidationException(new Dictionary<string, string> { ["color"] = "Use a #rrggbb colour." });
        shift.Name = name;
        shift.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
        shift.TimeZone = r.TimeZone;
        shift.Color = string.IsNullOrWhiteSpace(r.Color) ? null : r.Color;
        shift.IsActive = r.IsActive;
        shift.WeeklyHoursJson = json;
        if (id is null) db.Shifts.Add(shift);
        audit.Write(OrgAuditActions.ShiftChanged, "Shift", shift.Id, $"Shift '{shift.Name}' {(id is null ? "created" : "updated")}");
        await db.SaveChangesAsync(ct);
        return (await ShiftsAsync(ct)).First(s => s.Id == shift.Id);
    }

    public async Task DeleteShiftAsync(Guid id, CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.AgentsManage, Permissions.AdminOrganisation);
        var shift = await db.Shifts.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException("Shift", id);
        foreach (var a in await db.Agents.Where(a => a.DefaultShiftId == id).ToListAsync(ct)) a.DefaultShiftId = null;
        db.Shifts.Remove(shift); // assignments cascade
        audit.Write(OrgAuditActions.ShiftChanged, "Shift", id, $"Shift '{shift.Name}' deleted");
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<ShiftAssignmentDto>> AssignmentsAsync(Guid? userId, Guid? shiftId, CancellationToken ct = default)
    {
        ctx.RequireAny(OrgPermissions.ReadStructure);
        var q = db.AgentShiftAssignments.AsNoTracking().AsQueryable();
        if (userId is { } u) q = q.Where(a => a.UserId == u);
        if (shiftId is { } s) q = q.Where(a => a.ShiftId == s);
        var rows = await q.OrderByDescending(a => a.EffectiveFrom).Take(500).ToListAsync(ct);
        var shifts = await db.Shifts.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var names = await OrgNames.UsersAsync(db, rows.Select(r => (Guid?)r.UserId), ct);
        var now = db.Clock.UtcNow;
        return rows.Select(a => new ShiftAssignmentDto(a.Id, a.UserId, names.GetValueOrDefault(a.UserId) ?? "Unknown", a.ShiftId,
            shifts.GetValueOrDefault(a.ShiftId)?.Name ?? "Unknown", a.EffectiveFrom, a.EffectiveTo, ShiftSchedule.IsOnShift(shifts.GetValueOrDefault(a.ShiftId), now))).ToList();
    }

    /// <summary>Assigns a shift to agents from a date. An agent's open-ended assignment that overlaps is closed the day before.</summary>
    public async Task<List<ShiftAssignmentDto>> AssignShiftAsync(AssignShiftRequest r, CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.AgentsManage, Permissions.AdminOrganisation);
        if (!await db.Shifts.AnyAsync(s => s.Id == r.ShiftId, ct)) throw new NotFoundException("Shift", r.ShiftId);
        if (r.EffectiveTo is { } to && to < r.EffectiveFrom) throw new ValidationException(new Dictionary<string, string> { ["effectiveTo"] = "The end date is before the start date." });
        var userIds = r.UserIds.Distinct().ToList();
        if (userIds.Count == 0) throw new ValidationException(new Dictionary<string, string> { ["userIds"] = "Choose at least one agent." });
        foreach (var uid in userIds) await agents.RequireManageAgentAsync(uid, ct);
        if (await db.Agents.CountAsync(a => userIds.Contains(a.UserId), ct) != userIds.Count)
            throw new ValidationException(new Dictionary<string, string> { ["userIds"] = "Unknown agent." });
        var created = new List<Guid>();
        foreach (var uid in userIds)
        {
            var overlapping = await db.AgentShiftAssignments.Where(a => a.UserId == uid && (a.EffectiveTo == null || a.EffectiveTo >= r.EffectiveFrom) && a.EffectiveFrom <= (r.EffectiveTo ?? DateOnly.MaxValue)).ToListAsync(ct);
            foreach (var o in overlapping)
            {
                if (o.EffectiveFrom >= r.EffectiveFrom) db.AgentShiftAssignments.Remove(o);
                else o.EffectiveTo = r.EffectiveFrom.AddDays(-1);
            }
            var a = new AgentShiftAssignment { UserId = uid, ShiftId = r.ShiftId, EffectiveFrom = r.EffectiveFrom, EffectiveTo = r.EffectiveTo };
            db.AgentShiftAssignments.Add(a);
            created.Add(a.Id);
        }
        audit.Write(OrgAuditActions.ShiftChanged, "Shift", r.ShiftId, $"Shift assigned to {userIds.Count} agent(s) from {r.EffectiveFrom:yyyy-MM-dd}");
        await db.SaveChangesAsync(ct);
        return (await AssignmentsAsync(null, r.ShiftId, ct)).Where(a => created.Contains(a.Id)).ToList();
    }

    public async Task RemoveAssignmentAsync(Guid id, CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.AgentsManage, Permissions.AdminOrganisation);
        var a = await db.AgentShiftAssignments.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Shift assignment", id);
        await agents.RequireManageAgentAsync(a.UserId, ct);
        db.AgentShiftAssignments.Remove(a);
        await db.SaveChangesAsync(ct);
    }

    private static ShiftDto MapShift(Shift s, int assigned) =>
        new(s.Id, s.Name, s.Description, s.TimeZone, s.Color, s.IsActive, WeeklyHours.FromJson(s.WeeklyHoursJson), WeeklyHours.TotalHours(s.WeeklyHoursJson), assigned,
            OrgRowVersion.Encode(s.RowVersion));

    // ───────────────────────────── Leave ─────────────────────────────

    public async Task<List<LeaveDto>> LeavesAsync(LeaveQuery q, CancellationToken ct = default)
    {
        var manager = ctx.Has(Permissions.AgentsManage) || ctx.Has(Permissions.AdminUsers);
        var rows = db.AgentLeaves.AsNoTracking().AsQueryable();
        if (!manager) rows = rows.Where(l => l.UserId == ctx.UserId);
        else if (!SeesAll())
        {
            var depts = ctx.DepartmentIds.ToList();
            var me = ctx.UserId;
            rows = rows.Where(l => l.UserId == me || db.AgentDepartments.Any(a => a.UserId == l.UserId && depts.Contains(a.DepartmentId)));
        }
        if (q.UserId is { } u) rows = rows.Where(l => l.UserId == u);
        if (q.DepartmentId is { } d) rows = rows.Where(l => db.AgentDepartments.Any(a => a.UserId == l.UserId && a.DepartmentId == d));
        if (Enum.TryParse<LeaveStatus>(q.Status, true, out var st)) rows = rows.Where(l => l.Status == st);
        if (q.From is { } from) rows = rows.Where(l => l.EndsAt > from);
        if (q.To is { } to) rows = rows.Where(l => l.StartsAt < to);
        var list = await rows.OrderByDescending(l => l.StartsAt).Take(500).ToListAsync(ct);
        return await MapLeavesAsync(list, ct);
    }

    public async Task<LeaveDto> RequestLeaveAsync(CreateLeaveRequest r, CancellationToken ct = default)
    {
        ctx.RequireAgent();
        var userId = r.UserId ?? ctx.UserId ?? throw new ForbiddenException("Agent access required.");
        var forOther = userId != ctx.UserId;
        if (forOther) await agents.RequireManageAgentAsync(userId, ct);
        var errors = new Dictionary<string, string>();
        if (r.EndsAt <= r.StartsAt) errors["endsAt"] = "The leave must end after it starts.";
        if ((r.EndsAt - r.StartsAt).TotalDays > 366) errors["endsAt"] = "Leave may span at most one year.";
        if (!Enum.TryParse<LeaveType>(r.Type, true, out var type) || !Enum.IsDefined(type)) errors["type"] = "Unknown leave type.";
        if (r.Reason is { Length: > 1000 }) errors["reason"] = "The reason is too long.";
        if (errors.Count > 0) throw new ValidationException(errors);
        var start = DateTime.SpecifyKind(r.StartsAt, DateTimeKind.Utc);
        var end = DateTime.SpecifyKind(r.EndsAt, DateTimeKind.Utc);
        if (await db.AgentLeaves.AnyAsync(l => l.UserId == userId && (l.Status == LeaveStatus.Requested || l.Status == LeaveStatus.Approved) && l.StartsAt < end && l.EndsAt > start, ct))
            throw new ConflictException("leave_overlap", "This overlaps another leave request.");
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, ct) ?? throw new NotFoundException("Agent", userId);

        var leave = new AgentLeave { UserId = userId, StartsAt = start, EndsAt = end, Type = type, Reason = string.IsNullOrWhiteSpace(r.Reason) ? null : r.Reason.Trim() };
        db.AgentLeaves.Add(leave);
        if (forOther)
        {
            // A manager recording leave for someone approves it at the same time.
            leave.Status = LeaveStatus.Approved;
            leave.ApprovedById = ctx.UserId;
            leave.DecidedAt = db.Clock.UtcNow;
            audit.Write(OrgAuditActions.LeaveDecided, "AgentLeave", leave.Id, $"Leave recorded and approved for {user.DisplayName}");
        }
        else
        {
            var approver = await ApproverForAsync(userId, ct);
            var approval = new ApprovalRequest
            {
                Type = LeaveApprovalType, EntityType = "AgentLeave", EntityId = leave.Id, TicketId = null, RequestedById = userId, ApproverUserId = approver,
                Reason = $"{type} leave {start:yyyy-MM-dd HH:mm} – {end:yyyy-MM-dd HH:mm} UTC" + (leave.Reason is null ? "" : $": {leave.Reason}"),
                PayloadJson = Json.Serialize(new { leaveId = leave.Id, userId, startsAt = start, endsAt = end, type = type.ToString() }),
                ExpiresAt = start > db.Clock.UtcNow ? start : db.Clock.UtcNow.AddDays(7)
            };
            db.ApprovalRequests.Add(approval);
            leave.ApprovalRequestId = approval.Id;
            events.Publish(EventTypes.ApprovalRequested, "ApprovalRequest", approval.Id, new EntityEventPayload(approval.Id, LeaveApprovalType));
        }
        events.Publish(OrgEvents.LeaveChanged, "AgentLeave", leave.Id, new AgentEventPayload(userId, null, leave.Status.ToString()));
        await db.SaveChangesAsync(ct);
        return (await MapLeavesAsync([leave], ct))[0];
    }

    /// <summary>Approve/reject a leave. With an approval request the decision goes through the Approvals service (same rules as the inbox).</summary>
    public async Task<LeaveDto> DecideLeaveAsync(Guid id, bool approve, DecideLeaveRequest r, ApprovalService approvals, CancellationToken ct = default)
    {
        var leave = await db.AgentLeaves.FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw new NotFoundException("Leave", id);
        if (leave.Status != LeaveStatus.Requested) throw new ConflictException("already_decided", $"This leave is already {leave.Status.ToString().ToLowerInvariant()}.");
        if (leave.ApprovalRequestId is { } approvalId)
        {
            // Same authorisation as the Approvals inbox (named approver or approvals.decide; never your own request).
            await approvals.DecideAsync(approvalId, new DecideApprovalRequest(approve, r.Note), ct);
            db.ChangeTracker.Clear();
            var current = await db.AgentLeaves.FirstAsync(l => l.Id == id, ct);
            if (current.Status == LeaveStatus.Requested)
            {
                await ApplyDecisionAsync(current, approve, ctx.UserId, r.Note, ct);
                await db.SaveChangesAsync(ct);
            }
        }
        else
        {
            await agents.RequireManageAgentAsync(leave.UserId, ct);
            if (leave.UserId == ctx.UserId) throw new ForbiddenException("You cannot decide your own leave.");
            await ApplyDecisionAsync(leave, approve, ctx.UserId, r.Note, ct);
            await db.SaveChangesAsync(ct);
        }
        var fresh = await db.AgentLeaves.AsNoTracking().FirstAsync(l => l.Id == id, ct);
        return (await MapLeavesAsync([fresh], ct))[0];
    }

    public async Task<LeaveDto> CancelLeaveAsync(Guid id, CancellationToken ct = default)
    {
        var leave = await db.AgentLeaves.FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw new NotFoundException("Leave", id);
        if (leave.UserId != ctx.UserId) await agents.RequireManageAgentAsync(leave.UserId, ct);
        if (leave.Status is LeaveStatus.Cancelled or LeaveStatus.Rejected) throw new ConflictException("already_closed", "The leave is no longer active.");
        if (leave.EndsAt <= db.Clock.UtcNow) throw new ConflictException("leave_past", "Past leave cannot be cancelled.");
        leave.Status = LeaveStatus.Cancelled;
        if (leave.ApprovalRequestId is { } aid && await db.ApprovalRequests.FirstOrDefaultAsync(a => a.Id == aid && a.Status == ApprovalStatus.Pending, ct) is { } approval)
        {
            approval.Status = ApprovalStatus.Cancelled;
            approval.DecidedAt = db.Clock.UtcNow;
            approval.DecidedById = ctx.UserId;
        }
        events.Publish(OrgEvents.LeaveChanged, "AgentLeave", leave.Id, new AgentEventPayload(leave.UserId, null, "Cancelled"));
        audit.Write(OrgAuditActions.LeaveDecided, "AgentLeave", leave.Id, "Leave cancelled");
        await db.SaveChangesAsync(ct);
        return (await MapLeavesAsync([leave], ct))[0];
    }

    internal async Task ApplyDecisionAsync(AgentLeave leave, bool approve, Guid? decidedBy, string? note, CancellationToken ct)
    {
        if (leave.Status != LeaveStatus.Requested) return;
        leave.Status = approve ? LeaveStatus.Approved : LeaveStatus.Rejected;
        leave.ApprovedById = decidedBy;
        leave.DecidedAt = db.Clock.UtcNow;
        leave.DecisionNote = note;
        events.Publish(OrgEvents.LeaveChanged, "AgentLeave", leave.Id, new AgentEventPayload(leave.UserId, null, leave.Status.ToString()));
        audit.Write(OrgAuditActions.LeaveDecided, "AgentLeave", leave.Id, $"Leave {(approve ? "approved" : "rejected")}", new { note });
        await Task.CompletedTask;
    }

    /// <summary>Reports-to manager, else a supervisor of one of the agent's departments, else nobody (any approvals.decide holder).</summary>
    private async Task<Guid?> ApproverForAsync(Guid userId, CancellationToken ct)
    {
        var reportsTo = await db.Agents.AsNoTracking().Where(a => a.UserId == userId).Select(a => a.ReportsToUserId).FirstOrDefaultAsync(ct);
        if (reportsTo is { } r && await db.Users.AnyAsync(u => u.Id == r && u.IsActive, ct)) return r;
        var depts = await db.AgentDepartments.AsNoTracking().Where(a => a.UserId == userId).Select(a => a.DepartmentId).ToListAsync(ct);
        return await db.AgentDepartments.AsNoTracking()
            .Where(a => depts.Contains(a.DepartmentId) && a.IsSupervisor && a.UserId != userId && db.Users.Any(u => u.Id == a.UserId && u.IsActive))
            .OrderBy(a => db.Users.Any(u => u.Id == a.UserId && u.Kind == UserKind.Administrator)) // team supervisors before administrators
            .ThenBy(a => a.UserId).Select(a => (Guid?)a.UserId).FirstOrDefaultAsync(ct);
    }

    private async Task<List<LeaveDto>> MapLeavesAsync(List<AgentLeave> rows, CancellationToken ct)
    {
        var names = await OrgNames.UsersAsync(db, rows.Select(r => (Guid?)r.UserId).Concat(rows.Select(r => r.ApprovedById)), ct);
        var approvalIds = rows.Where(r => r.ApprovalRequestId != null).Select(r => r.ApprovalRequestId!.Value).ToList();
        var approvers = await db.ApprovalRequests.AsNoTracking().Where(a => approvalIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.ApproverUserId, ct);
        var manager = ctx.Has(Permissions.AgentsManage) || ctx.Has(Permissions.AdminUsers);
        var now = db.Clock.UtcNow;
        return rows.Select(l =>
        {
            var namedApprover = l.ApprovalRequestId is { } aid && approvers.GetValueOrDefault(aid) == ctx.UserId;
            var canDecide = l.Status == LeaveStatus.Requested && l.UserId != ctx.UserId && (namedApprover || manager && ctx.Has(Permissions.ApprovalsDecide) || manager && l.ApprovalRequestId is null);
            var canCancel = l.Status is LeaveStatus.Requested or LeaveStatus.Approved && l.EndsAt > now && (l.UserId == ctx.UserId || manager);
            return new LeaveDto(l.Id, l.UserId, names.GetValueOrDefault(l.UserId) ?? "Unknown", l.StartsAt, l.EndsAt, l.Type.ToString(), l.Status.ToString(), l.Reason,
                l.ApprovedById, l.ApprovedById is { } a ? names.GetValueOrDefault(a) : null, l.DecidedAt, l.DecisionNote, l.ApprovalRequestId, l.CreatedAt, canDecide, canCancel);
        }).ToList();
    }

    // ───────────────────────────── Delegation ─────────────────────────────

    public async Task<List<DelegationDto>> DelegationsAsync(bool includeEnded, CancellationToken ct = default)
    {
        var manager = ctx.Has(Permissions.AgentsManage) || ctx.Has(Permissions.AdminUsers);
        var q = db.AgentDelegations.AsNoTracking().AsQueryable();
        var me = ctx.UserId;
        if (!manager) q = q.Where(d => d.UserId == me || d.DelegateUserId == me);
        else if (!SeesAll())
        {
            var depts = ctx.DepartmentIds.ToList();
            q = q.Where(d => d.UserId == me || d.DelegateUserId == me || db.AgentDepartments.Any(a => a.UserId == d.UserId && depts.Contains(a.DepartmentId)));
        }
        var now = db.Clock.UtcNow;
        if (!includeEnded) q = q.Where(d => d.IsActive && d.EndsAt > now);
        var rows = await q.OrderByDescending(d => d.StartsAt).Take(300).ToListAsync(ct);
        return await MapDelegationsAsync(rows, ct);
    }

    public async Task<DelegationDto> SaveDelegationAsync(Guid? id, SaveDelegationRequest r, CancellationToken ct = default)
    {
        ctx.RequireAgent();
        var userId = r.UserId ?? ctx.UserId ?? throw new ForbiddenException("Agent access required.");
        if (userId != ctx.UserId) await agents.RequireManageAgentAsync(userId, ct);
        var errors = new Dictionary<string, string>();
        if (r.DelegateUserId == userId) errors["delegateUserId"] = "Choose someone else as the delegate.";
        else if (!await db.Users.AnyAsync(u => u.Id == r.DelegateUserId && u.IsActive && u.PasswordHash != null, ct)) errors["delegateUserId"] = "Choose an active agent.";
        if (r.EndsAt <= r.StartsAt) errors["endsAt"] = "The delegation must end after it starts.";
        if (r.ReassignOpenTickets && await db.Agents.AnyAsync(a => a.UserId == r.DelegateUserId && a.IsLightAgent, ct))
            errors["reassignOpenTickets"] = "Light agents cannot own tickets.";
        if (errors.Count > 0) throw new ValidationException(errors);
        var start = DateTime.SpecifyKind(r.StartsAt, DateTimeKind.Utc);
        var end = DateTime.SpecifyKind(r.EndsAt, DateTimeKind.Utc);
        if (await db.AgentDelegations.AnyAsync(d => d.Id != id && d.UserId == userId && d.IsActive && d.StartsAt < end && d.EndsAt > start, ct))
            throw new ConflictException("delegation_overlap", "Another delegation for this agent overlaps the period.");
        if (await db.AgentDelegations.AnyAsync(d => d.UserId == r.DelegateUserId && d.IsActive && d.StartsAt < end && d.EndsAt > start, ct))
            throw new ConflictException("delegate_away", "The delegate is away (delegated) during this period.");

        var del = id is { } existing ? await db.AgentDelegations.FirstOrDefaultAsync(d => d.Id == existing, ct) ?? throw new NotFoundException("Delegation", existing) : new AgentDelegation();
        if (id is not null && del.UserId != userId) throw new ValidationException(new Dictionary<string, string> { ["userId"] = "The absent agent cannot be changed." });
        del.UserId = userId;
        del.DelegateUserId = r.DelegateUserId;
        del.StartsAt = start;
        del.EndsAt = end;
        del.Reason = string.IsNullOrWhiteSpace(r.Reason) ? null : r.Reason.Trim();
        del.ReassignOpenTickets = r.ReassignOpenTickets;
        del.IncludeApprovals = r.IncludeApprovals;
        del.IsActive = true;
        if (id is null) db.AgentDelegations.Add(del);
        // Applies now if the window is open, else when it opens (durable, idempotent job).
        jobs.Enqueue(DelegationStartJobHandler.JobType, new DelegationStartPayload(del.Id), runAt: start > db.Clock.UtcNow ? start : null,
            idempotencyKey: $"org.delegation-start:{del.Id:N}:{start.Ticks}:{r.DelegateUserId:N}");
        events.Publish(OrgEvents.DelegationChanged, "AgentDelegation", del.Id, new AgentEventPayload(userId, null, "Saved"));
        audit.Write(OrgAuditActions.DelegationChanged, "AgentDelegation", del.Id, $"Delegation {(id is null ? "created" : "updated")}",
            new { userId, r.DelegateUserId, start, end, r.ReassignOpenTickets, r.IncludeApprovals });
        await db.SaveChangesAsync(ct);
        return (await MapDelegationsAsync([del], ct))[0];
    }

    public async Task<DelegationDto> EndDelegationAsync(Guid id, CancellationToken ct = default)
    {
        var del = await db.AgentDelegations.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Delegation", id);
        if (del.UserId != ctx.UserId) await agents.RequireManageAgentAsync(del.UserId, ct);
        var now = db.Clock.UtcNow;
        if (del.EndsAt > now) del.EndsAt = del.StartsAt > now ? del.StartsAt : now;
        del.IsActive = false;
        events.Publish(OrgEvents.DelegationChanged, "AgentDelegation", del.Id, new AgentEventPayload(del.UserId, null, "Ended"));
        audit.Write(OrgAuditActions.DelegationChanged, "AgentDelegation", del.Id, "Delegation ended");
        await db.SaveChangesAsync(ct);
        return (await MapDelegationsAsync([del], ct))[0];
    }

    private async Task<List<DelegationDto>> MapDelegationsAsync(List<AgentDelegation> rows, CancellationToken ct)
    {
        var names = await OrgNames.UsersAsync(db, rows.Select(r => (Guid?)r.UserId).Concat(rows.Select(r => (Guid?)r.DelegateUserId)), ct);
        var now = db.Clock.UtcNow;
        return rows.Select(d => new DelegationDto(d.Id, d.UserId, names.GetValueOrDefault(d.UserId) ?? "Unknown", d.DelegateUserId,
            names.GetValueOrDefault(d.DelegateUserId) ?? "Unknown", d.StartsAt, d.EndsAt, d.Reason, d.ReassignOpenTickets, d.IncludeApprovals, d.IsActive,
            !d.IsActive || d.EndsAt <= now ? "Ended" : d.StartsAt > now ? "Scheduled" : "Active")).ToList();
    }

    // ───────────────────────────── Schedule ─────────────────────────────

    /// <summary>Leave, delegations and holidays of the default calendar in a window (for the team calendar view).</summary>
    public async Task<List<ScheduleEntryDto>> ScheduleAsync(DateTime from, DateTime to, Guid? departmentId, CancellationToken ct = default)
    {
        ctx.RequireAny(OrgPermissions.ReadStructure);
        if (to <= from || (to - from).TotalDays > 120) throw new ValidationException(new Dictionary<string, string> { ["to"] = "Use a window of up to 120 days." });
        var leaves = await LeavesAsync(new LeaveQuery { From = from, To = to, DepartmentId = departmentId }, ct);
        var result = leaves.Where(l => l.Status is "Approved" or "Requested")
            .Select(l => new ScheduleEntryDto("leave", l.Id, l.UserId, $"{l.UserName} — {l.Type}{(l.Status == "Requested" ? " (requested)" : "")}", l.StartsAt, l.EndsAt, l.Status, false))
            .ToList();
        var dels = await DelegationsAsync(true, ct);
        result.AddRange(dels.Where(d => d.IsActive && d.StartsAt < to && d.EndsAt > from)
            .Select(d => new ScheduleEntryDto("delegation", d.Id, d.UserId, $"{d.UserName} → {d.DelegateName}", d.StartsAt, d.EndsAt, d.State, false)));
        var cal = await db.BusinessCalendars.AsNoTracking().Where(c => c.IsDefault).Select(c => new { c.Id, c.TimeZone }).FirstOrDefaultAsync(ct);
        if (cal is not null)
        {
            var holidays = await db.CalendarHolidays.AsNoTracking().Where(h => h.CalendarId == cal.Id).ToListAsync(ct);
            for (var day = DateOnly.FromDateTime(from); day <= DateOnly.FromDateTime(to); day = day.AddDays(1))
                foreach (var h in holidays.Where(h => h.Recurring ? h.Date.Month == day.Month && h.Date.Day == day.Day : h.Date == day))
                {
                    var start = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                    result.Add(new ScheduleEntryDto("holiday", h.Id, null, h.Name, start, start.AddDays(1), null, true));
                }
        }
        return result.OrderBy(e => e.StartsAt).ToList();
    }

    private bool SeesAll() => ctx.IsSystem || ctx.Has(Permissions.AdminUsers) || ctx.TicketScope == TicketScope.All;
}

/// <summary>Approvals inbox integration: approving an "agent_leave" request approves the leave.</summary>
public sealed class LeaveApprovalAction(DeskDbContext db, WorkforceService workforce, RequestContext ctx) : IApprovalAction
{
    public string Type => WorkforceService.LeaveApprovalType;

    public async Task ApplyAsync(ApprovalRequest request, CancellationToken ct)
    {
        var leave = await db.AgentLeaves.FirstOrDefaultAsync(l => l.Id == request.EntityId, ct);
        if (leave is not null) await workforce.ApplyDecisionAsync(leave, approve: true, ctx.UserId, request.DecisionNote, ct);
    }
}

/// <summary>
/// Keeps leave in sync with approval decisions made anywhere (rejections included) and routes new approval requests
/// addressed to an agent who is delegated away to the delegate (approvals follow delegation).
/// </summary>
public sealed class OrganisationApprovalHandler(DeskDbContext db, WorkforceService workforce, ILogger<OrganisationApprovalHandler> logger) : IOutboxHandler
{
    public string Name => "org-approvals";
    public int Order => HandlerOrder.Automation - 5;

    public bool CanHandle(string eventType) => eventType is EventTypes.ApprovalRequested or EventTypes.ApprovalDecided;

    public async Task HandleAsync(OutboxEvent ev, CancellationToken ct)
    {
        var p = Json.Deserialize<EntityEventPayload>(ev.PayloadJson);
        if (p is null) return;
        var approval = await db.ApprovalRequests.FirstOrDefaultAsync(a => a.Id == p.Id, ct);
        if (approval is null) return;
        if (ev.Type == EventTypes.ApprovalRequested)
        {
            if (approval.Status != ApprovalStatus.Pending || approval.ApproverUserId is not { } approver) return;
            var now = db.Clock.UtcNow;
            var delegation = await db.AgentDelegations.AsNoTracking()
                .Where(d => d.UserId == approver && d.IsActive && d.IncludeApprovals && d.StartsAt <= now && d.EndsAt > now)
                .OrderByDescending(d => d.StartsAt).FirstOrDefaultAsync(ct);
            if (delegation is null || delegation.DelegateUserId == approval.RequestedById) return;
            approval.ApproverUserId = delegation.DelegateUserId;
            logger.LogInformation("Approval {Approval} routed from {From} to delegate {To}", approval.Id, approver, delegation.DelegateUserId);
            await db.SaveChangesAsync(ct);
            return;
        }
        if (approval.Type != WorkforceService.LeaveApprovalType || approval.Status is not (ApprovalStatus.Approved or ApprovalStatus.Rejected)) return;
        var leave = await db.AgentLeaves.FirstOrDefaultAsync(l => l.Id == approval.EntityId, ct);
        if (leave is null || leave.Status != LeaveStatus.Requested) return;
        await workforce.ApplyDecisionAsync(leave, approval.Status == ApprovalStatus.Approved, approval.DecidedById, approval.DecisionNote, ct);
        await db.SaveChangesAsync(ct);
    }
}

public sealed record DelegationStartPayload(Guid DelegationId);

/// <summary>
/// When a delegation window opens: pending approvals addressed to the absent agent move to the delegate and, if
/// requested, the absent agent's open tickets are reassigned to the delegate (where they belong to the department).
/// </summary>
public sealed class DelegationStartJobHandler(DeskDbContext db, TicketService tickets, ILogger<DelegationStartJobHandler> logger) : JobHandler<DelegationStartPayload>
{
    public const string JobType = "org.delegation-start";
    public override string Type => JobType;

    protected override async Task ExecuteAsync(DelegationStartPayload p, BackgroundJob job, CancellationToken ct)
    {
        var del = await db.AgentDelegations.AsNoTracking().FirstOrDefaultAsync(d => d.Id == p.DelegationId, ct);
        var now = db.Clock.UtcNow;
        if (del is null || !del.IsActive || del.EndsAt <= now) return;
        if (del.StartsAt > now) return; // re-saved with a later start; the newer job handles it

        if (del.IncludeApprovals)
        {
            var pending = await db.ApprovalRequests.Where(a => a.ApproverUserId == del.UserId && a.Status == ApprovalStatus.Pending && a.RequestedById != del.DelegateUserId)
                .ToListAsync(ct);
            foreach (var a in pending) a.ApproverUserId = del.DelegateUserId;
            if (pending.Count > 0) await db.SaveChangesAsync(ct);
        }
        if (!del.ReassignOpenTickets) return;
        var open = await db.Tickets.AsNoTracking().Where(t => t.AssigneeId == del.UserId && t.StatusCategory < StatusCategory.Resolved)
            .OrderBy(t => t.Id).Select(t => t.Id).Take(500).ToListAsync(ct);
        foreach (var id in open)
        {
            try
            {
                var t = await db.Tickets.Include(x => x.Tags).FirstOrDefaultAsync(x => x.Id == id && x.AssigneeId == del.UserId, ct);
                if (t is null || !await db.AgentDepartments.AnyAsync(d => d.UserId == del.DelegateUserId && d.DepartmentId == t.DepartmentId, ct)) continue;
                await tickets.AssignCoreAsync(t, del.DelegateUserId, null, "Out-of-office delegation", ct);
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is DbUpdateConcurrencyException or DomainException)
            {
                logger.LogInformation("Delegation could not move ticket {Ticket}: {Message}", id, ex.Message);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }
    }
}
