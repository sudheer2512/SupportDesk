using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Organisation;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Sla;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Sla;

public sealed record SlaTargetSnapshot(TicketPriority Priority, int? FirstResponseMinutes, int? NextResponseMinutes, int? ResolutionMinutes, bool BusinessHours, int WarningPercent);
public sealed record SlaEscalationSnapshot(SlaMilestoneKind Milestone, int Level, int MinutesAfterBreach, EscalationAction Action, string NotifyJson, Guid? ReassignToUserId, Guid? ReassignToTeamId,
    int? AtPercent = null);
public sealed record SlaPolicySnapshot(Guid PolicyId, string Name, int Version, IReadOnlyList<SlaTargetSnapshot> Targets, IReadOnlyList<SlaEscalationSnapshot> Escalations,
    IReadOnlyList<Guid> PauseStatusIds, Guid? CalendarId, string CalendarSource);
public sealed record CalendarSnapshot(string TimeZone, bool Is24x7, string WeeklyJson, IReadOnlyList<DateOnly> Holidays, IReadOnlyList<string> RecurringHolidays);

/// <summary>Immutable copies of the policy and calendar used by an SLA instance (reproducible calculations).</summary>
public static class SlaSnapshot
{
    public static SlaPolicySnapshot Policy(string json) => Json.Deserialize<SlaPolicySnapshot>(json)!;

    public static WorkingCalendar Calendar(string json)
    {
        var c = Json.Deserialize<CalendarSnapshot>(json);
        if (c is null || c.Is24x7) return WorkingCalendar.AlwaysOpen;
        return new WorkingCalendar(c.TimeZone, false, WorkingCalendar.ParseWeekly(c.WeeklyJson), c.Holidays,
            c.RecurringHolidays.Select(s => (int.Parse(s[..2]), int.Parse(s[3..]))));
    }

    public static async Task<CalendarSnapshot> CaptureCalendarAsync(DeskDbContext db, Guid? calendarId, CancellationToken ct)
    {
        if (calendarId is null) return new CalendarSnapshot("UTC", true, "{}", [], []);
        var cal = await db.BusinessCalendars.AsNoTracking().FirstOrDefaultAsync(c => c.Id == calendarId, ct);
        if (cal is null || cal.Is24x7) return new CalendarSnapshot(cal?.TimeZone ?? "UTC", true, "{}", [], []);
        var holidays = await db.CalendarHolidays.AsNoTracking().Where(h => h.CalendarId == calendarId).ToListAsync(ct);
        return new CalendarSnapshot(cal.TimeZone, false, cal.WeeklyHoursJson,
            holidays.Where(h => !h.Recurring).Select(h => h.Date).ToList(),
            holidays.Where(h => h.Recurring).Select(h => $"{h.Date.Month:00}-{h.Date.Day:00}").ToList());
    }
}

/// <summary>Elapsed/due arithmetic for one milestone (pauses excluded, business hours honoured).</summary>
public static class SlaClock
{
    public static double Elapsed(SlaMilestone m, DateTime now, WorkingCalendar calendar)
    {
        var elapsed = m.ElapsedMinutesBeforeSegment;
        if (m.SegmentStartedAt is { } start)
        {
            var end = m.CompletedAt is { } done && done < now ? done : now;
            elapsed += calendar.WorkingMinutesBetween(start, end);
        }
        return elapsed;
    }

    /// <summary>Recomputes DueAt/WarningAt from the current running segment (no-op while paused).</summary>
    public static void Recompute(SlaMilestone m, WorkingCalendar calendar, int warningPercent)
    {
        if (m.SegmentStartedAt is not { } start) return;
        var remaining = Math.Max(0, m.TargetMinutes - m.ElapsedMinutesBeforeSegment);
        m.DueAt = calendar.AddWorkingMinutes(start, remaining);
        var warnMinutes = m.TargetMinutes * warningPercent / 100.0 - m.ElapsedMinutesBeforeSegment;
        m.WarningAt = warnMinutes <= 0 ? start : calendar.AddWorkingMinutes(start, warnMinutes);
    }
}

/// <summary>
/// SLA engine. Policy selection: contract SLA → first matching policy by evaluation order → default policy.
/// Calendar: contract support hours → policy calendar → department calendar → 24x7. Clocks: first response,
/// next response (one cycle per customer message after the first response) and resolution. Statuses flagged
/// PausesSla (or listed on the policy) pause resolution/next-response clocks. Changes of priority, policy inputs or
/// contract re-target running milestones while keeping the elapsed time. Every change writes an SlaEvent.
/// </summary>
public sealed class SlaEngine(DeskDbContext db) : ISlaEngine
{
    public async Task ApplyAsync(Ticket ticket, CancellationToken ct)
    {
        var (policy, calendarId, source) = await SelectPolicyAsync(ticket, ct);
        if (policy is null)
        {
            ticket.SlaPolicyId = null;
            ticket.SlaState = SlaState.None;
            return;
        }
        var target = policy.Targets.FirstOrDefault(t => t.Priority == ticket.Priority);
        if (target is null)
        {
            ticket.SlaPolicyId = policy.Id;
            ticket.SlaState = SlaState.None;
            return;
        }
        var snapshot = SnapshotOf(policy, calendarId, source);
        var calSnapshot = await SlaSnapshot.CaptureCalendarAsync(db, calendarId, ct);
        var calendar = SlaSnapshot.Calendar(Json.Serialize(calSnapshot));
        var start = ticket.CreatedAt == default ? db.Clock.UtcNow : ticket.CreatedAt;
        var instance = new SlaInstance
        {
            TicketId = ticket.Id, PolicyId = policy.Id, PolicyVersion = policy.Version, PolicySnapshotJson = Json.Serialize(snapshot),
            CalendarSnapshotJson = Json.Serialize(calSnapshot), Priority = ticket.Priority, StartedAt = start, Status = SlaInstanceStatus.Active, UpdatedAt = db.Clock.UtcNow
        };
        db.SlaInstances.Add(instance);
        var paused = await IsPausedStatusAsync(ticket.StatusId, snapshot, ct);
        if (target.FirstResponseMinutes is { } fr && ticket.FirstResponseAt is null)
            instance.Milestones.Add(NewMilestone(instance, ticket, SlaMilestoneKind.FirstResponse, 1, fr, target, start, calendar, paused: false));
        if (target.ResolutionMinutes is { } res)
            instance.Milestones.Add(NewMilestone(instance, ticket, SlaMilestoneKind.Resolution, 1, res, target, start, calendar, paused));
        if (paused) { instance.Status = SlaInstanceStatus.Paused; instance.PausedSince = start; }
        ticket.SlaPolicyId = policy.Id;
        AddEvent(instance, null, SlaEventTypes.Started, null, new { policy = policy.Name, priority = ticket.Priority.ToString(), calendar = source });
        Denormalise(ticket, instance, calendar);
    }

    public async Task OnTicketChangedAsync(Ticket ticket, IReadOnlyList<FieldChange> changes, CancellationToken ct)
    {
        var instance = await ActiveInstanceAsync(ticket.Id, ct);
        var (policy, calendarId, source) = await SelectPolicyAsync(ticket, ct);
        if (instance is null)
        {
            if (policy is not null && ticket.StatusCategory.IsOpen()) await ApplyAsync(ticket, ct);
            return;
        }
        var snapshot = SlaSnapshot.Policy(instance.PolicySnapshotJson);
        if (policy is null)
        {
            await CancelInstanceAsync(instance, ticket, "no matching policy", ct);
            return;
        }
        var policyChanged = policy.Id != instance.PolicyId || policy.Version != instance.PolicyVersion || snapshot.CalendarId != calendarId;
        if (!policyChanged && instance.Priority == ticket.Priority) return;

        var now = db.Clock.UtcNow;
        var oldCalendar = SlaSnapshot.Calendar(instance.CalendarSnapshotJson);
        var newSnapshot = SnapshotOf(policy, calendarId, source);
        var calSnapshot = await SlaSnapshot.CaptureCalendarAsync(db, calendarId, ct);
        var newCalendar = SlaSnapshot.Calendar(Json.Serialize(calSnapshot));
        var target = newSnapshot.Targets.FirstOrDefault(t => t.Priority == ticket.Priority);
        var from = new { policy = snapshot.Name, priority = instance.Priority.ToString() };
        instance.PolicyId = policy.Id;
        instance.PolicyVersion = policy.Version;
        instance.PolicySnapshotJson = Json.Serialize(newSnapshot);
        instance.CalendarSnapshotJson = Json.Serialize(calSnapshot);
        instance.Priority = ticket.Priority;
        instance.UpdatedAt = now;
        ticket.SlaPolicyId = policy.Id;

        foreach (var m in instance.Milestones.Where(m => m.Status is MilestoneStatus.Active or MilestoneStatus.Paused))
        {
            // Bank the time elapsed so far under the old calendar, then continue under the new one.
            if (m.SegmentStartedAt is { } seg)
            {
                m.ElapsedMinutesBeforeSegment += (m.BusinessHours ? oldCalendar : WorkingCalendar.AlwaysOpen).WorkingMinutesBetween(seg, now);
                m.SegmentStartedAt = now;
            }
            var minutes = m.Kind switch
            {
                SlaMilestoneKind.FirstResponse => target?.FirstResponseMinutes,
                SlaMilestoneKind.NextResponse => target?.NextResponseMinutes,
                _ => target?.ResolutionMinutes
            };
            if (minutes is null)
            {
                m.Status = MilestoneStatus.Cancelled;
                m.CompletedAt = now;
                m.SegmentStartedAt = null;
                continue;
            }
            m.TargetMinutes = minutes.Value;
            m.BusinessHours = target!.BusinessHours;
            m.WarningRaised = m.ElapsedMinutesBeforeSegment >= m.TargetMinutes * target.WarningPercent / 100.0;
            SlaClock.Recompute(m, m.BusinessHours ? newCalendar : WorkingCalendar.AlwaysOpen, target.WarningPercent);
            m.UpdatedAt = now;
        }
        AddEvent(instance, null, policyChanged ? SlaEventTypes.PolicyChanged : SlaEventTypes.Recalculated, null,
            new { from, to = new { policy = newSnapshot.Name, priority = ticket.Priority.ToString() }, reason = changes.Select(c => c.Field) });
        Denormalise(ticket, instance, newCalendar);
    }

    public async Task OnStatusChangedAsync(Ticket ticket, TicketStatus from, TicketStatus to, CancellationToken ct)
    {
        var instance = await LatestInstanceAsync(ticket.Id, ct);
        if (instance is null)
        {
            if (to.Category.IsOpen()) await ApplyAsync(ticket, ct);
            return;
        }
        var now = db.Clock.UtcNow;
        var snapshot = SlaSnapshot.Policy(instance.PolicySnapshotJson);
        var calendar = SlaSnapshot.Calendar(instance.CalendarSnapshotJson);
        var target = snapshot.Targets.FirstOrDefault(t => t.Priority == instance.Priority);

        if (to.Category.IsResolvedOrClosed() && from.Category.IsOpen())
        {
            foreach (var m in instance.Milestones.Where(m => m.Status is MilestoneStatus.Active or MilestoneStatus.Paused || IsRunningBreached(m)))
            {
                if (m.Kind == SlaMilestoneKind.NextResponse && !IsRunningBreached(m)) { Stop(m, now, calendar, MilestoneStatus.Cancelled); continue; }
                Complete(instance, m, now, calendar);
            }
            instance.Status = SlaInstanceStatus.Completed;
            instance.CompletedAt = now;
            instance.PausedSince = null;
            AddEvent(instance, null, SlaEventTypes.Completed, null, new { status = to.Name });
        }
        else if (from.Category.IsResolvedOrClosed() && to.Category.IsOpen())
        {
            // Reopen: the resolution clock resumes from where it stopped; time spent resolved is not counted.
            var resolution = instance.Milestones.Where(m => m.Kind == SlaMilestoneKind.Resolution).OrderByDescending(m => m.Cycle).FirstOrDefault();
            if (resolution is not null && target?.ResolutionMinutes is not null)
            {
                if (resolution.Status == MilestoneStatus.Met) resolution.Status = MilestoneStatus.Active;
                resolution.CompletedAt = null;
                resolution.SegmentStartedAt = now;
                SlaClock.Recompute(resolution, resolution.BusinessHours ? calendar : WorkingCalendar.AlwaysOpen, target.WarningPercent);
                resolution.UpdatedAt = now;
            }
            instance.Status = SlaInstanceStatus.Active;
            instance.CompletedAt = null;
            AddEvent(instance, resolution?.Id, SlaEventTypes.Resumed, SlaMilestoneKind.Resolution, new { reason = "reopened" });
            if (await IsPausedStatusAsync(to.Id, snapshot, ct)) Pause(instance, now, calendar, "status " + to.Name);
        }
        else
        {
            var wasPaused = await IsPausedStatusAsync(from.Id, snapshot, ct);
            var nowPaused = await IsPausedStatusAsync(to.Id, snapshot, ct);
            if (!wasPaused && nowPaused) Pause(instance, now, calendar, "status " + to.Name);
            else if (wasPaused && !nowPaused) Resume(instance, now, calendar, target?.WarningPercent ?? 75, "status " + to.Name);
        }
        instance.UpdatedAt = now;
        Denormalise(ticket, instance, calendar);
    }

    public async Task OnAgentPublicReplyAsync(Ticket ticket, DateTime at, CancellationToken ct)
    {
        var instance = await LatestInstanceAsync(ticket.Id, ct);
        if (instance is null || instance.Status == SlaInstanceStatus.Cancelled) return;
        var calendar = SlaSnapshot.Calendar(instance.CalendarSnapshotJson);
        foreach (var m in instance.Milestones.Where(m => m.Kind is SlaMilestoneKind.FirstResponse or SlaMilestoneKind.NextResponse &&
                                                         (m.Status is MilestoneStatus.Active or MilestoneStatus.Paused || IsRunningBreached(m))))
            Complete(instance, m, at, calendar);
        instance.UpdatedAt = db.Clock.UtcNow;
        Denormalise(ticket, instance, calendar);
    }

    public async Task OnCustomerReplyAsync(Ticket ticket, DateTime at, CancellationToken ct)
    {
        var instance = await LatestInstanceAsync(ticket.Id, ct);
        if (instance is null || instance.Status is SlaInstanceStatus.Cancelled or SlaInstanceStatus.Completed) return;
        if (ticket.FirstResponseAt is null) return; // still waiting for the first response
        var snapshot = SlaSnapshot.Policy(instance.PolicySnapshotJson);
        var target = snapshot.Targets.FirstOrDefault(t => t.Priority == instance.Priority);
        if (target?.NextResponseMinutes is not { } minutes) return;
        if (instance.Milestones.Any(m => m.Kind == SlaMilestoneKind.NextResponse && (m.Status is MilestoneStatus.Active or MilestoneStatus.Paused || IsRunningBreached(m)))) return;
        var calendar = SlaSnapshot.Calendar(instance.CalendarSnapshotJson);
        var cycle = instance.Milestones.Where(m => m.Kind == SlaMilestoneKind.NextResponse).Select(m => m.Cycle).DefaultIfEmpty(0).Max() + 1;
        var m = NewMilestone(instance, ticket, SlaMilestoneKind.NextResponse, cycle, minutes, target, at, calendar, instance.Status == SlaInstanceStatus.Paused);
        instance.Milestones.Add(m);
        db.SlaMilestones.Add(m);
        AddEvent(instance, m.Id, SlaEventTypes.Started, SlaMilestoneKind.NextResponse, new { cycle });
        Denormalise(ticket, instance, calendar);
    }

    public async Task CancelAsync(Ticket ticket, string reason, CancellationToken ct)
    {
        var instance = await LatestInstanceAsync(ticket.Id, ct);
        if (instance is null || instance.Status == SlaInstanceStatus.Cancelled) return;
        await CancelInstanceAsync(instance, ticket, reason, ct);
    }

    // ───────────────────────────── Internals ─────────────────────────────

    private async Task CancelInstanceAsync(SlaInstance instance, Ticket ticket, string reason, CancellationToken ct)
    {
        var now = db.Clock.UtcNow;
        var calendar = SlaSnapshot.Calendar(instance.CalendarSnapshotJson);
        foreach (var m in instance.Milestones.Where(m => m.Status is MilestoneStatus.Active or MilestoneStatus.Paused))
            Stop(m, now, calendar, MilestoneStatus.Cancelled);
        instance.Status = SlaInstanceStatus.Cancelled;
        instance.UpdatedAt = now;
        AddEvent(instance, null, SlaEventTypes.Cancelled, null, new { reason });
        ticket.SlaState = SlaState.None;
        ticket.FirstResponseDueAt = ticket.NextResponseDueAt = ticket.DueAt = null;
        await Task.CompletedTask;
    }

    /// <summary>A breached milestone keeps running (for escalation) until it is completed.</summary>
    private static bool IsRunningBreached(SlaMilestone m) => m.Status == MilestoneStatus.Breached && m.CompletedAt is null;

    private void Complete(SlaInstance instance, SlaMilestone m, DateTime at, WorkingCalendar calendar)
    {
        var cal = m.BusinessHours ? calendar : WorkingCalendar.AlwaysOpen;
        if (m.SegmentStartedAt is { } seg && at > seg) m.ElapsedMinutesBeforeSegment += cal.WorkingMinutesBetween(seg, at);
        m.SegmentStartedAt = null;
        m.CompletedAt = at;
        var breached = m.Status == MilestoneStatus.Breached || m.ElapsedMinutesBeforeSegment > m.TargetMinutes;
        if (breached && m.BreachedAt is null) m.BreachedAt = m.DueAt;
        m.Status = breached ? MilestoneStatus.Breached : MilestoneStatus.Met;
        m.UpdatedAt = db.Clock.UtcNow;
        AddEvent(instance, m.Id, breached ? SlaEventTypes.Completed : SlaEventTypes.Met, m.Kind,
            new { elapsedMinutes = Math.Round(m.ElapsedMinutesBeforeSegment, 1), m.TargetMinutes, late = breached });
    }

    private static void Stop(SlaMilestone m, DateTime at, WorkingCalendar calendar, MilestoneStatus status)
    {
        var cal = m.BusinessHours ? calendar : WorkingCalendar.AlwaysOpen;
        if (m.SegmentStartedAt is { } seg && at > seg) m.ElapsedMinutesBeforeSegment += cal.WorkingMinutesBetween(seg, at);
        m.SegmentStartedAt = null;
        m.Status = status;
        m.CompletedAt = at;
        m.UpdatedAt = at;
    }

    private void Pause(SlaInstance instance, DateTime now, WorkingCalendar calendar, string reason)
    {
        foreach (var m in instance.Milestones.Where(m => m.Kind != SlaMilestoneKind.FirstResponse && (m.Status == MilestoneStatus.Active || IsRunningBreached(m))))
        {
            var cal = m.BusinessHours ? calendar : WorkingCalendar.AlwaysOpen;
            if (m.SegmentStartedAt is { } seg) m.ElapsedMinutesBeforeSegment += cal.WorkingMinutesBetween(seg, now);
            m.SegmentStartedAt = null;
            if (m.Status == MilestoneStatus.Active) m.Status = MilestoneStatus.Paused;
            m.UpdatedAt = now;
            AddEvent(instance, m.Id, SlaEventTypes.Paused, m.Kind, new { reason, elapsedMinutes = Math.Round(m.ElapsedMinutesBeforeSegment, 1) });
        }
        instance.Status = SlaInstanceStatus.Paused;
        instance.PausedSince = now;
    }

    private void Resume(SlaInstance instance, DateTime now, WorkingCalendar calendar, int warningPercent, string reason)
    {
        foreach (var m in instance.Milestones.Where(m => m.Status == MilestoneStatus.Paused || (IsRunningBreached(m) && m.SegmentStartedAt is null && m.Kind != SlaMilestoneKind.FirstResponse)))
        {
            if (m.Status == MilestoneStatus.Paused) m.Status = MilestoneStatus.Active;
            m.SegmentStartedAt = now;
            SlaClock.Recompute(m, m.BusinessHours ? calendar : WorkingCalendar.AlwaysOpen, warningPercent);
            m.UpdatedAt = now;
            AddEvent(instance, m.Id, SlaEventTypes.Resumed, m.Kind, new { reason, dueAt = m.DueAt });
        }
        instance.Status = SlaInstanceStatus.Active;
        instance.PausedSince = null;
    }

    private SlaMilestone NewMilestone(SlaInstance instance, Ticket ticket, SlaMilestoneKind kind, int cycle, int minutes, SlaTarget target, DateTime start,
        WorkingCalendar calendar, bool paused) =>
        NewMilestone(instance, ticket, kind, cycle, minutes, new SlaTargetSnapshot(target.Priority, target.FirstResponseMinutes, target.NextResponseMinutes,
            target.ResolutionMinutes, target.BusinessHours, target.WarningPercent), start, calendar, paused);

    private SlaMilestone NewMilestone(SlaInstance instance, Ticket ticket, SlaMilestoneKind kind, int cycle, int minutes, SlaTargetSnapshot target, DateTime start,
        WorkingCalendar calendar, bool paused)
    {
        var m = new SlaMilestone
        {
            InstanceId = instance.Id, TicketId = ticket.Id, Kind = kind, Cycle = cycle, TargetMinutes = minutes, BusinessHours = target.BusinessHours,
            StartedAt = start, Status = paused ? MilestoneStatus.Paused : MilestoneStatus.Active, SegmentStartedAt = paused ? null : start,
            UpdatedAt = db.Clock.UtcNow
        };
        var cal = target.BusinessHours ? calendar : WorkingCalendar.AlwaysOpen;
        if (paused)
        {
            m.DueAt = cal.AddWorkingMinutes(start, minutes);
            m.WarningAt = null;
        }
        else SlaClock.Recompute(m, cal, target.WarningPercent);
        return m;
    }

    private void Denormalise(Ticket ticket, SlaInstance instance, WorkingCalendar calendar)
    {
        var live = instance.Milestones.Where(m => m.Status is MilestoneStatus.Active or MilestoneStatus.Paused || IsRunningBreached(m)).ToList();
        ticket.FirstResponseDueAt = live.FirstOrDefault(m => m.Kind == SlaMilestoneKind.FirstResponse)?.DueAt;
        ticket.NextResponseDueAt = live.FirstOrDefault(m => m.Kind == SlaMilestoneKind.NextResponse)?.DueAt;
        ticket.DueAt = instance.Milestones.Where(m => m.Kind == SlaMilestoneKind.Resolution).OrderByDescending(m => m.Cycle).FirstOrDefault()?.DueAt;
        ticket.SlaState = StateOf(instance, db.Clock.UtcNow);
    }

    public static SlaState StateOf(SlaInstance instance, DateTime now)
    {
        if (instance.Status == SlaInstanceStatus.Cancelled) return SlaState.None;
        if (instance.Milestones.Any(m => m.Status == MilestoneStatus.Breached && m.CompletedAt is null)) return SlaState.Breached;
        if (instance.Status == SlaInstanceStatus.Completed)
            return instance.Milestones.Any(m => m.Status == MilestoneStatus.Breached) ? SlaState.Breached : SlaState.Met;
        if (instance.Milestones.Any(m => m.Status == MilestoneStatus.Active && (m.WarningRaised || m.WarningAt <= now))) return SlaState.AtRisk;
        if (instance.Status == SlaInstanceStatus.Paused) return SlaState.Paused;
        return SlaState.OnTrack;
    }

    private async Task<(SlaPolicy? Policy, Guid? CalendarId, string Source)> SelectPolicyAsync(Ticket ticket, CancellationToken ct)
    {
        var policies = await db.SlaPolicies.AsNoTracking().Include(p => p.Targets).Include(p => p.Escalations).Where(p => p.IsActive)
            .OrderBy(p => p.EvaluationOrder).ToListAsync(ct);
        if (policies.Count == 0) return (null, null, "none");
        Domain.Service.Contract? contract = null;
        if (ticket.ContractId is { } cid) contract = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct);

        SlaPolicy? policy = null;
        if (contract?.SlaPolicyId is { } pid) policy = policies.FirstOrDefault(p => p.Id == pid);
        if (policy is null)
        {
            var context = await TicketConditionContext.BuildAsync(db, ticket, null, ct);
            policy = policies.Where(p => !p.IsDefault).FirstOrDefault(p => ConditionEvaluator.Evaluate(ConditionGroup.Parse(p.ConditionsJson), context))
                     ?? policies.FirstOrDefault(p => p.IsDefault);
        }
        if (policy is null) return (null, null, "none");

        if (policy.UseContractCalendar && contract?.CalendarId is { } contractCal) return (policy, contractCal, "contract");
        if (policy.CalendarId is { } pc) return (policy, pc, "policy");
        var deptCal = await db.Departments.AsNoTracking().Where(d => d.Id == ticket.DepartmentId).Select(d => d.BusinessCalendarId).FirstOrDefaultAsync(ct);
        return (policy, deptCal, deptCal is null ? "24x7" : "department");
    }

    private static SlaPolicySnapshot SnapshotOf(SlaPolicy p, Guid? calendarId, string source) => new(
        p.Id, p.Name, p.Version,
        p.Targets.Select(t => new SlaTargetSnapshot(t.Priority, t.FirstResponseMinutes, t.NextResponseMinutes, t.ResolutionMinutes, t.BusinessHours, t.WarningPercent)).ToList(),
        p.Escalations.OrderBy(e => e.Milestone).ThenBy(e => e.Level)
            .Select(e => new SlaEscalationSnapshot(e.Milestone, e.Level, e.MinutesAfterBreach, e.Action, e.NotifyJson, e.ReassignToUserId, e.ReassignToTeamId, e.AtPercent)).ToList(),
        Json.GuidList(p.PauseStatusIdsJson), calendarId, source);

    private async Task<bool> IsPausedStatusAsync(Guid statusId, SlaPolicySnapshot snapshot, CancellationToken ct) =>
        snapshot.PauseStatusIds.Contains(statusId) || await db.TicketStatuses.AsNoTracking().AnyAsync(s => s.Id == statusId && s.PausesSla, ct);

    private async Task<SlaInstance?> ActiveInstanceAsync(Guid ticketId, CancellationToken ct) =>
        db.SlaInstances.Local.FirstOrDefault(i => i.TicketId == ticketId && i.Status is SlaInstanceStatus.Active or SlaInstanceStatus.Paused)
        ?? await db.SlaInstances.Include(i => i.Milestones).Where(i => i.TicketId == ticketId && (i.Status == SlaInstanceStatus.Active || i.Status == SlaInstanceStatus.Paused))
            .OrderByDescending(i => i.StartedAt).FirstOrDefaultAsync(ct);

    private async Task<SlaInstance?> LatestInstanceAsync(Guid ticketId, CancellationToken ct) =>
        db.SlaInstances.Local.Where(i => i.TicketId == ticketId).OrderByDescending(i => i.StartedAt).FirstOrDefault()
        ?? await db.SlaInstances.Include(i => i.Milestones).Where(i => i.TicketId == ticketId).OrderByDescending(i => i.StartedAt).FirstOrDefaultAsync(ct);

    private void AddEvent(SlaInstance instance, Guid? milestoneId, string type, SlaMilestoneKind? kind, object? details) =>
        db.SlaEvents.Add(new SlaEvent
        {
            InstanceId = instance.Id, MilestoneId = milestoneId, TicketId = instance.TicketId, Type = type, Milestone = kind,
            OccurredAt = db.Clock.UtcNow, DetailsJson = details is null ? null : Json.Serialize(details)
        });
}
