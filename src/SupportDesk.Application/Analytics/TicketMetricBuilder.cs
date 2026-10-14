using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Sla;
using SupportDesk.Application.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Analytics;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Sla;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Analytics;

/// <summary>Tenant settings used while computing metrics: local time zone and business calendars.</summary>
public sealed class MetricEnvironment(TimeZoneInfo timeZone, IReadOnlyDictionary<Guid, WorkingCalendar> calendars,
    IReadOnlyDictionary<Guid, Guid?> departmentCalendars, Guid? defaultCalendarId)
{
    public TimeZoneInfo TimeZone { get; } = timeZone;
    public IReadOnlyDictionary<Guid, WorkingCalendar> Calendars { get; } = calendars;

    /// <summary>Business calendar of a department (its own, else the tenant default, else 24×7).</summary>
    public WorkingCalendar ForDepartment(Guid departmentId)
    {
        var id = departmentCalendars.TryGetValue(departmentId, out var c) && c is not null ? c : defaultCalendarId;
        return id is { } g && Calendars.TryGetValue(g, out var cal) ? cal : WorkingCalendar.AlwaysOpen;
    }

    public static async Task<MetricEnvironment> LoadAsync(DeskDbContext db, Guid tenantId, CancellationToken ct)
    {
        var tzId = await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.DefaultTimeZone).FirstOrDefaultAsync(ct);
        var calendars = await db.BusinessCalendars.AsNoTracking().ToListAsync(ct);
        var holidays = await db.CalendarHolidays.AsNoTracking().ToListAsync(ct);
        var map = new Dictionary<Guid, WorkingCalendar>();
        foreach (var c in calendars)
        {
            try
            {
                map[c.Id] = c.Is24x7
                    ? WorkingCalendar.AlwaysOpen
                    : new WorkingCalendar(c.TimeZone, false, WorkingCalendar.ParseWeekly(c.WeeklyHoursJson),
                        holidays.Where(h => h.CalendarId == c.Id && !h.Recurring).Select(h => h.Date),
                        holidays.Where(h => h.CalendarId == c.Id && h.Recurring).Select(h => (h.Date.Month, h.Date.Day)));
            }
            catch (DomainException)
            {
                map[c.Id] = WorkingCalendar.AlwaysOpen; // misconfigured calendar: fall back to calendar time
            }
        }
        var departments = await db.Departments.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking()
            .Select(d => new { d.Id, d.BusinessCalendarId }).ToDictionaryAsync(d => d.Id, d => d.BusinessCalendarId, ct);
        var defaultId = calendars.Where(c => c.IsDefault).Select(c => (Guid?)c.Id).FirstOrDefault();
        return new MetricEnvironment(WorkingCalendar.ResolveTimeZone(tzId), map, departments, defaultId);
    }
}

/// <summary>
/// Recomputes <see cref="TicketMetric"/> rows from the tickets themselves (threads, activity, SLA milestones, time
/// entries). Pure upsert: running it any number of times for the same tickets gives the same rows, so the outbox handler,
/// the catch-up task and the backfill job can all call it.
/// </summary>
public sealed class TicketMetricBuilder(DeskDbContext db, RequestContext ctx)
{
    public const int BatchSize = 200;
    private MetricEnvironment? _env;

    public async Task<MetricEnvironment> EnvironmentAsync(CancellationToken ct) => _env ??= await MetricEnvironment.LoadAsync(db, ctx.TenantId, ct);

    /// <summary>Recomputes the given tickets (any size; processed in batches). Returns the number of rows written.</summary>
    public async Task<int> RebuildAsync(IReadOnlyCollection<Guid> ticketIds, CancellationToken ct)
    {
        var written = 0;
        foreach (var chunk in ticketIds.Distinct().Chunk(BatchSize))
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    written += await RebuildBatchAsync(chunk, ct);
                    break;
                }
                catch (DbUpdateException) when (attempt < 2)
                {
                    // A concurrent writer inserted the same ticket's row first: reload and update instead.
                    db.ChangeTracker.Clear();
                }
            }
        }
        return written;
    }

    private async Task<int> RebuildBatchAsync(Guid[] ids, CancellationToken ct)
    {
        var env = await EnvironmentAsync(ct);
        var now = db.Clock.UtcNow;
        var tickets = await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(t => ids.Contains(t.Id)).ToListAsync(ct);
        var existing = await db.TicketMetrics.Where(m => ids.Contains(m.TicketId)).ToDictionaryAsync(m => m.TicketId, ct);

        // Hard-deleted tickets (erasure): drop their rows.
        foreach (var gone in existing.Values.Where(m => tickets.All(t => t.Id != m.TicketId)).ToList()) db.TicketMetrics.Remove(gone);
        if (tickets.Count == 0)
        {
            await db.SaveChangesAsync(ct);
            return 0;
        }

        var threads = (await db.TicketThreads.AsNoTracking().Where(x => ids.Contains(x.TicketId))
                .Select(x => new { x.TicketId, x.Kind, x.Visibility, x.AuthorUserId, x.CreatedAt }).ToListAsync(ct))
            .ToLookup(x => x.TicketId, x => new ThreadFact(x.Kind, x.Visibility, x.AuthorUserId, x.CreatedAt));
        string[] activityTypes = [TicketEventTypes.Reopened, TicketEventTypes.Transferred, TicketEventTypes.Escalated];
        var activity = (await db.TicketEvents.AsNoTracking().Where(e => ids.Contains(e.TicketId) && activityTypes.Contains(e.Type))
                .Select(e => new { e.TicketId, e.Type, e.OccurredAt }).ToListAsync(ct))
            .ToLookup(x => x.TicketId, x => new ActivityFact(x.Type, x.OccurredAt));
        var milestones = (await db.SlaMilestones.AsNoTracking().Where(m => ids.Contains(m.TicketId))
                .Select(m => new { m.TicketId, m.Kind, m.Status, m.CompletedAt, m.BreachedAt }).ToListAsync(ct))
            .ToLookup(x => x.TicketId, x => new MilestoneFact(x.Kind, x.Status, x.CompletedAt, x.BreachedAt));
        var calendars = (await db.SlaInstances.AsNoTracking().Where(i => ids.Contains(i.TicketId))
                .Select(i => new { i.TicketId, i.StartedAt, i.CalendarSnapshotJson }).ToListAsync(ct))
            .GroupBy(i => i.TicketId).ToDictionary(g => g.Key, g => g.OrderByDescending(i => i.StartedAt).First().CalendarSnapshotJson);
        var time = await db.TimeEntries.AsNoTracking().Where(e => ids.Contains(e.TicketId))
            .GroupBy(e => e.TicketId)
            .Select(g => new { TicketId = g.Key, Minutes = g.Sum(e => e.Minutes), Billable = g.Sum(e => e.IsBillable ? e.Minutes : 0), Cost = g.Sum(e => e.CostAmount) })
            .ToDictionaryAsync(x => x.TicketId, ct);
        var customerIds = tickets.Where(t => t.CustomerId.HasValue).Select(t => t.CustomerId!.Value).Distinct().ToList();
        var tiers = await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(c => customerIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Tier, ct);

        foreach (var t in tickets)
        {
            var calendar = calendars.TryGetValue(t.Id, out var snap) && !string.IsNullOrWhiteSpace(snap) ? SafeCalendar(snap, env, t) : env.ForDepartment(t.DepartmentId);
            var logged = time.TryGetValue(t.Id, out var te) ? te : null;
            var facts = new TicketFacts(t, threads[t.Id].ToList(), activity[t.Id].ToList(), milestones[t.Id].ToList(),
                logged?.Minutes ?? 0, logged?.Billable ?? 0, logged?.Cost ?? 0m,
                t.CustomerId is { } cid && tiers.TryGetValue(cid, out var tier) ? tier : null);
            if (!existing.TryGetValue(t.Id, out var row))
            {
                row = new TicketMetric();
                db.TicketMetrics.Add(row);
            }
            TicketMetricCalculator.Apply(row, facts, calendar, env.TimeZone, now);
        }
        await db.SaveChangesAsync(ct);
        return tickets.Count;
    }

    private static WorkingCalendar SafeCalendar(string snapshotJson, MetricEnvironment env, Ticket t)
    {
        try
        {
            return SlaSnapshot.Calendar(snapshotJson);
        }
        catch (Exception ex) when (ex is DomainException or System.Text.Json.JsonException or FormatException)
        {
            return env.ForDepartment(t.DepartmentId);
        }
    }

    /// <summary>Tickets whose metric row is missing or older than the ticket (rowversion moved on). Bounded per call.</summary>
    public async Task<List<Guid>> StaleTicketIdsAsync(int max, CancellationToken ct) =>
        await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(t => !db.TicketMetrics.Any(m => m.TicketId == t.Id && m.SourceVersion == t.RowVersion))
            .OrderBy(t => t.Id).Select(t => t.Id).Take(max).ToListAsync(ct);
}
