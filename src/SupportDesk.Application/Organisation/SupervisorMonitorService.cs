using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Organisation;
using SupportDesk.Domain;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Organisation;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Organisation;

/// <summary>
/// Supervisor live monitor snapshot: agents (availability, shift/leave, load vs capacity, chats, idle time), queue depth
/// per department/team, SLA at-risk/breached tickets and live chats waiting for an agent. Limited to the caller's data
/// scope: supervisors with department scope see only their departments (tickets through the usual visibility rules).
/// The desk refreshes it on realtime events (ticket.changed, sla.alert, agent.availability).
/// </summary>
public sealed class SupervisorMonitorService(DeskDbContext db, RequestContext ctx)
{
    private const int IdleLookbackHours = 12;

    public async Task<MonitorSnapshotDto> SnapshotAsync(Guid? departmentId, CancellationToken ct = default)
    {
        ctx.Require(Permissions.SupervisorMonitor);
        var now = db.Clock.UtcNow;
        var all = ctx.TicketScope == TicketScope.All || ctx.IsSystem;
        var activeDepartments = await db.Departments.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.SortOrder).ThenBy(d => d.Name)
            .Select(d => new IdName(d.Id, d.Name)).ToListAsync(ct);
        var scope = all ? activeDepartments : activeDepartments.Where(d => ctx.DepartmentIds.Contains(d.Id)).ToList();
        if (departmentId is { } only)
        {
            if (scope.All(d => d.Id != only)) throw new NotFoundException("Department", only);
            scope = scope.Where(d => d.Id == only).ToList();
        }
        var deptIds = scope.Select(d => d.Id).ToList();

        // ── Agents ──
        var memberships = await db.AgentDepartments.AsNoTracking().Where(a => deptIds.Contains(a.DepartmentId)).ToListAsync(ct);
        var candidateIds = memberships.Select(m => m.UserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => candidateIds.Contains(u.Id) && u.IsActive && u.Kind != UserKind.LightAgent && u.Kind != UserKind.System)
            .Select(u => new { u.Id, u.DisplayName, u.AvatarUrl }).ToListAsync(ct);
        var userIds = users.Select(u => u.Id).ToList();
        var agents = await db.Agents.AsNoTracking().Where(a => userIds.Contains(a.UserId) && !a.IsLightAgent).ToDictionaryAsync(a => a.UserId, ct);
        var teamsByUser = await db.TeamMembers.AsNoTracking().Where(m => userIds.Contains(m.UserId)).ToListAsync(ct);
        var loads = await db.Tickets.AsNoTracking().Where(t => t.AssigneeId != null && userIds.Contains(t.AssigneeId.Value) && t.StatusCategory < StatusCategory.Resolved)
            .GroupBy(t => t.AssigneeId!.Value)
            .Select(g => new { g.Key, Open = g.Count(), InProgress = g.Count(x => x.StatusCategory == StatusCategory.InProgress), Overdue = g.Count(x => x.DueAt != null && x.DueAt < now) })
            .ToDictionaryAsync(x => x.Key, ct);
        var chats = await db.Conversations.AsNoTracking().Where(c => c.AssignedUserId != null && userIds.Contains(c.AssignedUserId.Value) && c.Status == ConversationStatus.AgentActive)
            .GroupBy(c => c.AssignedUserId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var onLeave = await db.AgentLeaves.AsNoTracking().Where(l => userIds.Contains(l.UserId) && l.Status == LeaveStatus.Approved && l.StartsAt <= now && l.EndsAt > now)
            .Select(l => l.UserId).Distinct().ToListAsync(ct);
        var delegations = await db.AgentDelegations.AsNoTracking().Where(d => userIds.Contains(d.UserId) && d.IsActive && d.StartsAt <= now && d.EndsAt > now)
            .Select(d => new { d.UserId, d.DelegateUserId }).ToListAsync(ct);
        var since = now.AddHours(-IdleLookbackHours);
        var lastActions = await db.TicketEvents.AsNoTracking()
            .Where(e => e.ActorType == ActorType.User && e.ActorId != null && userIds.Contains(e.ActorId.Value) && e.OccurredAt >= since)
            .GroupBy(e => e.ActorId!.Value).Select(g => new { g.Key, Last = g.Max(x => x.OccurredAt) }).ToDictionaryAsync(x => x.Key, x => x.Last, ct);
        var offShift = await OffShiftAsync(userIds, agents.Values.ToList(), now, ct);

        var agentRows = users.Where(u => agents.ContainsKey(u.Id)).Select(u =>
        {
            var a = agents[u.Id];
            var load = loads.GetValueOrDefault(u.Id);
            DateTime? last = lastActions.TryGetValue(u.Id, out var l) ? l : null;
            if (a.AvailabilityChangedAt is { } changed && (last is null || changed > last)) last = changed;
            int? idle = a.Availability == AgentAvailability.Online && last is { } lastAt ? (int)Math.Max(0, (now - lastAt).TotalMinutes) : null;
            return new MonitorAgentDto(u.Id, u.DisplayName, u.AvatarUrl, a.Title, a.Availability.ToString(), a.AvailabilityChangedAt, !offShift.Contains(u.Id),
                onLeave.Contains(u.Id), delegations.FirstOrDefault(d => d.UserId == u.Id)?.DelegateUserId, load?.Open ?? 0, a.MaxOpenTickets, load?.InProgress ?? 0,
                load?.Overdue ?? 0, chats.GetValueOrDefault(u.Id), idle, last,
                memberships.Where(m => m.UserId == u.Id).Select(m => m.DepartmentId).Distinct().ToList(),
                teamsByUser.Where(m => m.UserId == u.Id).Select(m => m.TeamId).ToList());
        }).OrderBy(a => AvailabilityRank(a.Availability)).ThenByDescending(a => a.Capacity == 0 ? 0 : (double)a.OpenTickets / a.Capacity).ThenBy(a => a.Name).ToList();

        // ── Queues (tickets through the caller's visibility) ──
        var open = db.Tickets.AsNoTracking().VisibleTo(ctx, db).Where(t => deptIds.Contains(t.DepartmentId) && t.StatusCategory < StatusCategory.Resolved);
        var grouped = await open.GroupBy(t => new { t.DepartmentId, t.TeamId })
            .Select(g => new
            {
                g.Key.DepartmentId, g.Key.TeamId, Open = g.Count(), Unassigned = g.Count(x => x.AssigneeId == null),
                AtRisk = g.Count(x => x.SlaState == SlaState.AtRisk), Breached = g.Count(x => x.SlaState == SlaState.Breached),
                Oldest = g.Min(x => x.AssigneeId == null ? (DateTime?)x.CreatedAt : null)
            }).ToListAsync(ct);
        var teamIds = grouped.Where(g => g.TeamId != null).Select(g => g.TeamId!.Value).Distinct().ToList();
        var teamNames = await db.Teams.AsNoTracking().Where(t => teamIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var deptNames = scope.ToDictionary(d => d.Id, d => d.Name);
        var queues = grouped.Select(g => new MonitorQueueDto(g.DepartmentId, deptNames.GetValueOrDefault(g.DepartmentId) ?? "", g.TeamId,
                g.TeamId is { } tid ? teamNames.GetValueOrDefault(tid) : null, g.Open, g.Unassigned, g.AtRisk, g.Breached, g.Oldest))
            .OrderBy(q => q.DepartmentName).ThenByDescending(q => q.Unassigned).ThenBy(q => q.TeamName).ToList();

        // ── SLA risk list ──
        var risky = await open.Where(t => t.SlaState == SlaState.AtRisk || t.SlaState == SlaState.Breached)
            .OrderBy(t => t.SlaState == SlaState.Breached ? 0 : 1).ThenBy(t => t.DueAt).Take(50)
            .Select(t => new { t.Id, t.Number, t.Subject, t.Priority, t.SlaState, t.FirstResponseDueAt, t.NextResponseDueAt, t.DueAt, t.AssigneeId, t.DepartmentId })
            .ToListAsync(ct);
        var assigneeNames = await OrgNames.UsersAsync(db, risky.Select(r => r.AssigneeId), ct);
        var slaItems = risky.Select(t =>
        {
            var (milestone, due) = NextDue(t.FirstResponseDueAt, t.NextResponseDueAt, t.DueAt);
            return new MonitorSlaItemDto(t.Id, t.Number, t.Subject, t.Priority.ToString(), t.SlaState.ToString(), milestone, due, t.AssigneeId,
                t.AssigneeId is { } a ? assigneeNames.GetValueOrDefault(a) : null, t.DepartmentId, deptNames.GetValueOrDefault(t.DepartmentId));
        }).ToList();

        // ── Live chats waiting (read-only view of the omnichannel conversations) ──
        var channelAccounts = db.ChannelAccounts.AsNoTracking();
        var waitingQuery = db.Conversations.AsNoTracking().Where(c => c.Status == ConversationStatus.WaitingForAgent);
        if (!all || departmentId is not null)
            waitingQuery = waitingQuery.Where(c => channelAccounts.Any(a => a.Id == c.ChannelAccountId && a.DepartmentId != null && deptIds.Contains(a.DepartmentId.Value)));
        var waitingCount = await waitingQuery.CountAsync(ct);
        var waiting = await waitingQuery.OrderBy(c => c.UpdatedAt).Take(30)
            .Select(c => new
            {
                c.Id, c.Channel, c.VisitorName, c.Subject, c.Status, c.UpdatedAt, c.AssignedUserId, c.TicketId,
                Dept = channelAccounts.Where(a => a.Id == c.ChannelAccountId).Select(a => a.DepartmentId).FirstOrDefault()
            }).ToListAsync(ct);
        var chatRows = waiting.Select(c => new MonitorChatDto(c.Id, c.Channel.ToString(), c.VisitorName, c.Subject, c.Status.ToString(), c.UpdatedAt,
            (int)Math.Max(0, (now - c.UpdatedAt).TotalMinutes), c.Dept, c.AssignedUserId, c.TicketId)).ToList();

        return new MonitorSnapshotDto(now, all && departmentId is null ? "All departments" : string.Join(", ", scope.Select(s => s.Name)), scope, agentRows, queues,
            grouped.Sum(g => g.Open), grouped.Sum(g => g.Unassigned), grouped.Sum(g => g.AtRisk), grouped.Sum(g => g.Breached), slaItems, waitingCount, chatRows,
            ctx.Has(Permissions.AgentsManage) || ctx.Has(Permissions.AdminUsers), ctx.Has(Permissions.TicketsAssign));
    }

    private static (string? Milestone, DateTime? Due) NextDue(DateTime? first, DateTime? next, DateTime? resolution)
    {
        var options = new List<(string, DateTime)>();
        if (first is { } f) options.Add(("FirstResponse", f));
        if (next is { } n) options.Add(("NextResponse", n));
        if (resolution is { } r) options.Add(("Resolution", r));
        if (options.Count == 0) return (null, null);
        var pick = options.OrderBy(o => o.Item2).First();
        return (pick.Item1, pick.Item2);
    }

    private static int AvailabilityRank(string availability) => availability switch { "Online" => 0, "Busy" => 1, "Away" => 2, _ => 3 };

    private async Task<HashSet<Guid>> OffShiftAsync(List<Guid> userIds, List<Agent> agents, DateTime now, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(now);
        var assignments = await db.AgentShiftAssignments.AsNoTracking()
            .Where(a => userIds.Contains(a.UserId) && a.EffectiveFrom <= today && (a.EffectiveTo == null || a.EffectiveTo >= today)).ToListAsync(ct);
        var shiftIds = assignments.Select(a => a.ShiftId).Concat(agents.Where(a => a.DefaultShiftId != null).Select(a => a.DefaultShiftId!.Value)).Distinct().ToList();
        var shifts = await db.Shifts.AsNoTracking().Where(s => shiftIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var result = new HashSet<Guid>();
        foreach (var id in userIds)
        {
            var shiftId = ShiftSchedule.EffectiveAssignment(assignments.Where(a => a.UserId == id), today)?.ShiftId ?? agents.FirstOrDefault(a => a.UserId == id)?.DefaultShiftId;
            if (shiftId is { } sid && shifts.TryGetValue(sid, out var s) && !ShiftSchedule.IsOnShift(s, now)) result.Add(id);
        }
        return result;
    }
}

/// <summary>Pushes committed agent availability / leave / delegation changes to connected desks ("agent.availability").</summary>
public sealed class OrganisationRealtimeHandler(DeskDbContext db, IRealtimePublisher realtime) : IOutboxHandler
{
    public string Name => "org-realtime";
    public int Order => HandlerOrder.Realtime;

    public bool CanHandle(string eventType) => eventType.StartsWith("agent.", StringComparison.Ordinal);

    public async Task HandleAsync(OutboxEvent ev, CancellationToken ct)
    {
        if (ev.TenantId is not { } tenantId) return;
        var p = Json.Deserialize<AgentEventPayload>(ev.PayloadJson);
        if (p is null) return;
        var availability = await db.Agents.AsNoTracking().Where(a => a.UserId == p.UserId).Select(a => (AgentAvailability?)a.Availability).FirstOrDefaultAsync(ct);
        await realtime.ToTenantAsync(tenantId, "agent.availability",
            new { userId = p.UserId, availability = (availability ?? AgentAvailability.Offline).ToString(), reason = ev.Type }, ct);
    }
}
