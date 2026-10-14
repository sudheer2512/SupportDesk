using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Analytics;
using SupportDesk.Domain.Organisation;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Analytics;

/// <summary>
/// Builds the daily roll-ups for local days: <see cref="AgentMetric"/> per agent and <see cref="BacklogSnapshot"/> per
/// department × priority. Every value is recomputed from source rows (threads, activity, time entries, CSAT responses,
/// SLA milestones, ticket metrics), so re-running a day simply rewrites it — the roll-up is idempotent.
/// </summary>
public sealed class AnalyticsRollupService(DeskDbContext db, TicketMetricBuilder builder)
{
    /// <summary>Recomputes agent and backlog rows for the inclusive local-day range (chunks of 31 days).</summary>
    public async Task RebuildDaysAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        for (var start = from; start <= to; start = start.AddDays(31))
        {
            var end = start.AddDays(30) < to ? start.AddDays(30) : to;
            await AgentDaysAsync(start, end, ct);
            await BacklogDaysAsync(start, end, ct);
        }
    }

    // ───────────────────────────── Agents ─────────────────────────────

    public async Task AgentDaysAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var env = await builder.EnvironmentAsync(ct);
        var tz = env.TimeZone;
        var now = db.Clock.UtcNow;
        var fromUtc = AnalyticsPeriods.DayStartUtc(from, tz);
        var toUtc = AnalyticsPeriods.DayStartUtc(to.AddDays(1), tz);
        DateOnly Day(DateTime utc) => AnalyticsPeriods.LocalDay(utc, tz);

        var agents = await db.Agents.AsNoTracking().Select(a => new { a.UserId, a.DefaultShiftId, a.CreatedAt }).ToListAsync(ct);
        if (agents.Count == 0) return;
        var userIds = agents.Select(a => a.UserId).ToHashSet();

        var replies = await db.TicketThreads.AsNoTracking()
            .Where(x => x.CreatedAt >= fromUtc && x.CreatedAt < toUtc && x.AuthorUserId != null &&
                        (x.Kind == ThreadKind.AgentReply || x.Kind == ThreadKind.InternalNote || x.Kind == ThreadKind.HandoverNote))
            .Select(x => new { x.AuthorUserId, x.Kind, x.Visibility, x.CreatedAt }).ToListAsync(ct);
        // Assignment history stores display names, so "assigned" counts each ticket's latest assignment (Ticket.AssignedAt).
        var assigned = await db.Tickets.AsNoTracking()
            .Where(t => t.AssignedAt >= fromUtc && t.AssignedAt < toUtc && t.AssigneeId != null && !t.IsSpam && t.MergedIntoTicketId == null)
            .Select(t => new { t.AssigneeId, t.AssignedAt }).ToListAsync(ct);
        var reopened = await db.TicketEvents.AsNoTracking()
            .Where(e => e.OccurredAt >= fromUtc && e.OccurredAt < toUtc && e.Type == TicketEventTypes.Reopened)
            .Select(e => new { e.TicketId, e.OccurredAt }).ToListAsync(ct);
        var metrics = db.TicketMetrics.AsNoTracking().Where(m => !m.Excluded);
        var resolved = await metrics.Where(m => m.ResolvedAt >= fromUtc && m.ResolvedAt < toUtc && m.AssigneeId != null)
            .Select(m => new { m.AssigneeId, m.ResolvedAt, m.ResolutionMinutes }).ToListAsync(ct);
        var firstResponses = await metrics.Where(m => m.FirstResponseAt >= fromUtc && m.FirstResponseAt < toUtc && m.FirstResponderId != null)
            .Select(m => new { m.FirstResponderId, m.FirstResponseAt, m.FirstResponseMinutes }).ToListAsync(ct);
        var time = await db.TimeEntries.AsNoTracking().Where(e => e.StartedAt >= fromUtc && e.StartedAt < toUtc)
            .Select(e => new { e.UserId, e.StartedAt, e.Minutes }).ToListAsync(ct);
        var csat = await db.SatisfactionResponses.AsNoTracking()
            .Where(r => r.RespondedAt >= fromUtc && r.RespondedAt < toUtc && r.Rating != null && r.AgentUserId != null)
            .Select(r => new { r.AgentUserId, r.RespondedAt, r.Rating }).ToListAsync(ct);
        var breaches = await (from ms in db.SlaMilestones.AsNoTracking()
                              join m in metrics on ms.TicketId equals m.TicketId
                              where ms.BreachedAt >= fromUtc && ms.BreachedAt < toUtc && m.AssigneeId != null
                              select new { m.AssigneeId, ms.BreachedAt }).ToListAsync(ct);
        var ticketAssignee = await metrics.Where(m => m.AssigneeId != null).Select(m => new { m.TicketId, m.AssigneeId }).ToDictionaryAsync(x => x.TicketId, x => x.AssigneeId, ct);
        var openSpans = await metrics.Where(m => m.AssigneeId != null && m.CreatedAt < toUtc && (m.ResolvedAt == null || m.ResolvedAt >= fromUtc))
            .Select(m => new { m.AssigneeId, m.CreatedAt, m.ResolvedAt }).ToListAsync(ct);
        var available = await AvailabilityAsync(agents.Select(a => (a.UserId, a.DefaultShiftId)).ToList(), from, to, env, ct);

        var rows = new Dictionary<(Guid, DateOnly), AgentMetric>();
        AgentMetric Row(Guid user, DateOnly day)
        {
            if (!rows.TryGetValue((user, day), out var r)) rows[(user, day)] = r = new AgentMetric { UserId = user, Day = day };
            return r;
        }

        foreach (var x in replies.Where(x => userIds.Contains(x.AuthorUserId!.Value)))
        {
            var r = Row(x.AuthorUserId!.Value, Day(x.CreatedAt));
            if (x.Kind == ThreadKind.AgentReply && x.Visibility == Visibility.Public) r.Replies++;
            else if (x.Kind != ThreadKind.AgentReply) r.Notes++;
        }
        foreach (var x in assigned.Where(x => userIds.Contains(x.AssigneeId!.Value))) Row(x.AssigneeId!.Value, Day(x.AssignedAt!.Value)).TicketsAssigned++;
        foreach (var e in reopened)
            if (ticketAssignee.TryGetValue(e.TicketId, out var u) && u is { } uid && userIds.Contains(uid)) Row(uid, Day(e.OccurredAt)).Reopens++;
        foreach (var x in resolved.Where(x => userIds.Contains(x.AssigneeId!.Value)))
        {
            var r = Row(x.AssigneeId!.Value, Day(x.ResolvedAt!.Value));
            r.TicketsResolved++;
            r.ResolutionMinutesSum += x.ResolutionMinutes ?? 0;
        }
        foreach (var x in firstResponses.Where(x => userIds.Contains(x.FirstResponderId!.Value)))
        {
            var r = Row(x.FirstResponderId!.Value, Day(x.FirstResponseAt!.Value));
            r.FirstResponses++;
            r.FirstResponseMinutesSum += x.FirstResponseMinutes ?? 0;
        }
        foreach (var x in time.Where(x => userIds.Contains(x.UserId))) Row(x.UserId, Day(x.StartedAt)).HandleMinutes += x.Minutes;
        foreach (var x in csat.Where(x => userIds.Contains(x.AgentUserId!.Value)))
        {
            var r = Row(x.AgentUserId!.Value, Day(x.RespondedAt!.Value));
            r.CsatCount++;
            r.CsatSum += x.Rating!.Value;
        }
        foreach (var x in breaches.Where(x => userIds.Contains(x.AssigneeId!.Value))) Row(x.AssigneeId!.Value, Day(x.BreachedAt!.Value)).SlaBreaches++;

        // Availability and end-of-day open workload for every agent and day (zero activity days included).
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var end = AnalyticsPeriods.DayStartUtc(day.AddDays(1), tz);
            if (end > now) end = now;
            if (AnalyticsPeriods.DayStartUtc(day, tz) > now) break;
            var openByUser = openSpans.Where(s => s.CreatedAt < end && (s.ResolvedAt == null || s.ResolvedAt >= end))
                .GroupBy(s => s.AssigneeId!.Value).ToDictionary(g => g.Key, g => g.Count());
            foreach (var a in agents)
            {
                var minutes = available.TryGetValue((a.UserId, day), out var mins) ? mins : 0;
                var open = openByUser.GetValueOrDefault(a.UserId);
                if (minutes == 0 && open == 0 && !rows.ContainsKey((a.UserId, day))) continue;
                var r = Row(a.UserId, day);
                r.OnlineMinutes = minutes;
                r.OpenAtEnd = open;
            }
        }

        // Upsert: replace the range's rows.
        var existing = await db.AgentMetrics.Where(m => m.Day >= from && m.Day <= to).ToListAsync(ct);
        var byKey = existing.ToDictionary(m => (m.UserId, m.Day));
        foreach (var (key, r) in rows)
        {
            if (!byKey.Remove(key, out var target))
            {
                target = new AgentMetric { UserId = r.UserId, Day = r.Day };
                db.AgentMetrics.Add(target);
            }
            target.TicketsAssigned = r.TicketsAssigned;
            target.TicketsResolved = r.TicketsResolved;
            target.Replies = r.Replies;
            target.Notes = r.Notes;
            target.FirstResponses = r.FirstResponses;
            target.FirstResponseMinutesSum = r.FirstResponseMinutesSum;
            target.ResolutionMinutesSum = r.ResolutionMinutesSum;
            target.HandleMinutes = r.HandleMinutes;
            target.OnlineMinutes = r.OnlineMinutes;
            target.CsatCount = r.CsatCount;
            target.CsatSum = r.CsatSum;
            target.SlaBreaches = r.SlaBreaches;
            target.Reopens = r.Reopens;
            target.OpenAtEnd = r.OpenAtEnd;
            target.UpdatedAt = now;
        }
        db.AgentMetrics.RemoveRange(byKey.Values);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Scheduled minutes per agent and local day: shift assignment → default shift → department calendar; approved leave = 0.</summary>
    private async Task<Dictionary<(Guid, DateOnly), int>> AvailabilityAsync(List<(Guid UserId, Guid? DefaultShiftId)> agents, DateOnly from, DateOnly to,
        MetricEnvironment env, CancellationToken ct)
    {
        var shifts = await db.Shifts.AsNoTracking().ToDictionaryAsync(s => s.Id, s => WorkingCalendar.ParseWeekly(s.WeeklyHoursJson), ct);
        var assignments = await db.AgentShiftAssignments.AsNoTracking()
            .Where(a => a.EffectiveFrom <= to && (a.EffectiveTo == null || a.EffectiveTo >= from)).ToListAsync(ct);
        var tz = env.TimeZone;
        var fromUtc = AnalyticsPeriods.DayStartUtc(from, tz);
        var toUtc = AnalyticsPeriods.DayStartUtc(to.AddDays(1), tz);
        var leave = await db.AgentLeaves.AsNoTracking().Where(l => l.Status == LeaveStatus.Approved && l.StartsAt < toUtc && l.EndsAt > fromUtc)
            .Select(l => new { l.UserId, l.StartsAt, l.EndsAt }).ToListAsync(ct);
        var firstDept = (await db.AgentDepartments.AsNoTracking().Select(d => new { d.UserId, d.DepartmentId }).ToListAsync(ct))
            .GroupBy(d => d.UserId).ToDictionary(g => g.Key, g => g.First().DepartmentId);

        var result = new Dictionary<(Guid, DateOnly), int>();
        foreach (var (userId, defaultShift) in agents)
        {
            var calendar = firstDept.TryGetValue(userId, out var dept) ? env.ForDepartment(dept) : WorkingCalendar.AlwaysOpen;
            for (var day = from; day <= to; day = day.AddDays(1))
            {
                var dayStart = AnalyticsPeriods.DayStartUtc(day, tz);
                var dayEnd = AnalyticsPeriods.DayStartUtc(day.AddDays(1), tz);
                if (leave.Any(l => l.UserId == userId && l.StartsAt < dayEnd && l.EndsAt > dayStart)) continue;
                var shiftId = assignments.Where(a => a.UserId == userId && a.EffectiveFrom <= day && (a.EffectiveTo == null || a.EffectiveTo >= day))
                    .Select(a => (Guid?)a.ShiftId).FirstOrDefault() ?? defaultShift;
                int minutes;
                if (shiftId is { } sid && shifts.TryGetValue(sid, out var weekly))
                    minutes = weekly.TryGetValue(day.DayOfWeek, out var ivs) ? (int)ivs.Sum(i => (i.End - i.Start).TotalMinutes) : 0;
                else if (calendar.Is24x7)
                    minutes = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 0 : 480; // no schedule: an 8-hour weekday
                else
                    minutes = (int)Math.Round(calendar.WorkingMinutesBetween(dayStart, dayEnd));
                if (minutes > 0) result[(userId, day)] = minutes;
            }
        }
        return result;
    }

    // ───────────────────────────── Backlog ─────────────────────────────

    public async Task BacklogDaysAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var env = await builder.EnvironmentAsync(ct);
        var tz = env.TimeZone;
        var now = db.Clock.UtcNow;
        var fromUtc = AnalyticsPeriods.DayStartUtc(from, tz);
        var toUtc = AnalyticsPeriods.DayStartUtc(to.AddDays(1), tz);
        var rows = await db.TicketMetrics.AsNoTracking()
            .Where(m => !m.Excluded && m.CreatedAt < toUtc && (m.ResolvedAt == null || m.ResolvedAt >= fromUtc))
            .Select(m => new { m.DepartmentId, m.Priority, m.CreatedAt, m.ResolvedAt, m.SlaBreached }).ToListAsync(ct);

        var snapshots = new Dictionary<(DateOnly, Guid, TicketPriority), BacklogSnapshot>();
        BacklogSnapshot Snap(DateOnly day, Guid dept, TicketPriority p)
        {
            if (!snapshots.TryGetValue((day, dept, p), out var s)) snapshots[(day, dept, p)] = s = new BacklogSnapshot { Day = day, DepartmentId = dept, Priority = p };
            return s;
        }
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var start = AnalyticsPeriods.DayStartUtc(day, tz);
            if (start > now) break;
            var end = AnalyticsPeriods.DayStartUtc(day.AddDays(1), tz);
            var asOf = end > now ? now : end;
            foreach (var r in rows)
            {
                if (r.CreatedAt >= start && r.CreatedAt < end) Snap(day, r.DepartmentId, r.Priority).CreatedCount++;
                if (r.ResolvedAt >= start && r.ResolvedAt < end) Snap(day, r.DepartmentId, r.Priority).ResolvedCount++;
                if (r.CreatedAt >= asOf || r.ResolvedAt is { } res && res < asOf) continue;
                var s = Snap(day, r.DepartmentId, r.Priority);
                s.OpenCount++;
                if (r.SlaBreached) s.BreachedOpen++;
                var age = (asOf - r.CreatedAt).TotalDays;
                if (age < 1) s.Age0To1++;
                else if (age < 3) s.Age1To3++;
                else if (age < 7) s.Age3To7++;
                else if (age < 30) s.Age7To30++;
                else s.Age30Plus++;
            }
        }

        var existing = await db.BacklogSnapshots.Where(s => s.Day >= from && s.Day <= to).ToListAsync(ct);
        var byKey = existing.ToDictionary(s => (s.Day, s.DepartmentId, s.Priority));
        foreach (var (key, s) in snapshots)
        {
            if (!byKey.Remove(key, out var target))
            {
                target = new BacklogSnapshot { Day = s.Day, DepartmentId = s.DepartmentId, Priority = s.Priority };
                db.BacklogSnapshots.Add(target);
            }
            target.OpenCount = s.OpenCount;
            target.CreatedCount = s.CreatedCount;
            target.ResolvedCount = s.ResolvedCount;
            target.Age0To1 = s.Age0To1;
            target.Age1To3 = s.Age1To3;
            target.Age3To7 = s.Age3To7;
            target.Age7To30 = s.Age7To30;
            target.Age30Plus = s.Age30Plus;
            target.BreachedOpen = s.BreachedOpen;
            target.UpdatedAt = now;
        }
        db.BacklogSnapshots.RemoveRange(byKey.Values);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Today and yesterday on the tenant calendar.</summary>
    public async Task<(DateOnly Yesterday, DateOnly Today)> RecentDaysAsync(CancellationToken ct)
    {
        var env = await builder.EnvironmentAsync(ct);
        var today = AnalyticsPeriods.LocalDay(db.Clock.UtcNow, env.TimeZone);
        return (today.AddDays(-1), today);
    }

}
