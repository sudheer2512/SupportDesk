using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Sla;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Sla;

/// <summary>
/// Periodic SLA monitor: raises approaching-breach warnings, marks breaches (re-checking elapsed business time so a
/// stale due date never produces a false breach) and runs multi-level escalations (notify, reassign, raise priority).
/// Idempotent: each step is guarded by milestone state (WarningRaised, Status, EscalationLevelReached).
/// </summary>
public sealed class SlaMonitorTask(DeskDbContext db, TenantScopeRunner runner, ILogger<SlaMonitorTask> logger) : IRecurringTask
{
    public string Name => "sla-monitor";
    public TimeSpan Interval => TimeSpan.FromSeconds(30);

    public async Task RunAsync(CancellationToken ct)
    {
        var now = db.Clock.UtcNow;
        var horizon = now.AddDays(1);
        // Find tenants with due milestones in every database (dedicated tenants' live data is not in the shared one).
        var tenants = new List<Guid>();
        await runner.ForEachDatabaseAsync("SLA monitor", async (sp, partition, token) =>
        {
            var found = await sp.GetRequiredService<DeskDbContext>().AllTenants<SlaMilestone>()
                .Where(m => (m.Status == MilestoneStatus.Active && ((!m.WarningRaised && m.WarningAt <= now) || m.DueAt <= horizon)) ||
                            (m.Status == MilestoneStatus.Breached && m.CompletedAt == null))
                .Select(m => m.TenantId).Distinct().ToListAsync(token);
            tenants.AddRange(found.Where(partition.Owns));
        }, ct);
        foreach (var tenantId in tenants)
        {
            try
            {
                await runner.RunForTenantAsync(tenantId, "SLA monitor", sp => sp.GetRequiredService<SlaMonitorProcessor>().ProcessAsync(ct), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "SLA monitor failed for tenant {Tenant}", tenantId);
            }
        }
    }
}

public sealed class SlaMonitorProcessor(DeskDbContext db, IEventPublisher events, TicketHistory history, TicketService tickets, ISlaEngine engine,
    ILogger<SlaMonitorProcessor> logger)
{
    public async Task ProcessAsync(CancellationToken ct)
    {
        var now = db.Clock.UtcNow;
        var candidates = await db.SlaMilestones.AsNoTracking()
            .Where(m => (m.Status == MilestoneStatus.Active && ((!m.WarningRaised && m.WarningAt <= now) || m.DueAt <= now.AddDays(1))) ||
                        (m.Status == MilestoneStatus.Breached && m.CompletedAt == null))
            // Most urgent first, deterministically, so a backlog larger than one batch never starves the oldest breaches.
            .GroupBy(m => new { m.TicketId, m.InstanceId })
            .OrderBy(g => g.Min(m => m.DueAt)).ThenBy(g => g.Key.TicketId)
            .Select(g => g.Key).Take(500).ToListAsync(ct);

        foreach (var c in candidates)
        {
            try
            {
                await ProcessInstanceAsync(c.InstanceId, now, ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // The ticket changed concurrently (e.g. an agent replied); the next tick re-evaluates with fresh state.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "SLA processing failed for ticket {Ticket}", c.TicketId);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task ProcessInstanceAsync(Guid instanceId, DateTime now, CancellationToken ct)
    {
        var instance = await db.SlaInstances.Include(i => i.Milestones).FirstOrDefaultAsync(i => i.Id == instanceId, ct);
        if (instance is null || instance.Status == SlaInstanceStatus.Cancelled) return;
        var ticket = await db.Tickets.Include(t => t.Tags).FirstOrDefaultAsync(t => t.Id == instance.TicketId, ct);
        if (ticket is null) return;
        var snapshot = SlaSnapshot.Policy(instance.PolicySnapshotJson);
        var calendar = SlaSnapshot.Calendar(instance.CalendarSnapshotJson);
        var target = snapshot.Targets.FirstOrDefault(t => t.Priority == instance.Priority);
        var changed = false;

        foreach (var m in instance.Milestones.ToList())
        {
            var cal = m.BusinessHours ? calendar : WorkingCalendar.AlwaysOpen;
            if (m.Status == MilestoneStatus.Active)
            {
                var elapsed = SlaClock.Elapsed(m, now, cal);
                if (!m.WarningRaised && m.WarningAt <= now && elapsed < m.TargetMinutes)
                {
                    m.WarningRaised = true;
                    Event(instance, m, SlaEventTypes.Warning, new { elapsed = Math.Round(elapsed, 1), m.TargetMinutes, m.DueAt });
                    history.Record(ticket, TicketEventTypes.SlaEvent, $"{Label(m)} due {m.DueAt:yyyy-MM-dd HH:mm} UTC — approaching breach", null, new { milestone = m.Kind.ToString(), m.DueAt });
                    events.Publish(EventTypes.SlaWarning, "Ticket", ticket.Id, new SlaEventPayload(ticket.Id, ticket.Number, instance.Id, m.Id, m.Kind, m.DueAt));
                    changed = true;
                }
                if (elapsed >= m.TargetMinutes)
                {
                    m.Status = MilestoneStatus.Breached;
                    m.BreachedAt = m.DueAt <= now ? m.DueAt : now;
                    m.WarningRaised = true;
                    m.UpdatedAt = now;
                    Event(instance, m, SlaEventTypes.Breached, new { elapsed = Math.Round(elapsed, 1), m.TargetMinutes });
                    history.Record(ticket, TicketEventTypes.SlaEvent, $"{Label(m)} SLA breached", null, new { milestone = m.Kind.ToString(), m.DueAt });
                    events.Publish(EventTypes.SlaBreached, "Ticket", ticket.Id, new SlaEventPayload(ticket.Id, ticket.Number, instance.Id, m.Id, m.Kind, m.DueAt));
                    changed = true;
                }
                else if (m.DueAt <= now)
                {
                    // Due date was stale (e.g. calendar edited); recompute instead of breaching.
                    SlaClock.Recompute(m, cal, target?.WarningPercent ?? 75);
                    changed = true;
                }
            }

            // Escalation levels: pre-breach levels (negative minutes) for running milestones, post-breach levels after breach.
            var running = m.Status == MilestoneStatus.Active || (m.Status == MilestoneStatus.Breached && m.CompletedAt is null);
            if (!running) continue;
            foreach (var level in snapshot.Escalations.Where(e => e.Milestone == m.Kind && e.Level > m.EscalationLevelReached).OrderBy(e => e.Level))
            {
                if (level.AtPercent is { } percent)
                {
                    // Percentage levels compare elapsed (business) time with the target, before or after the breach.
                    if (SlaClock.Elapsed(m, now, cal) < m.TargetMinutes * percent / 100.0) break;
                }
                else
                {
                    var anchor = m.Status == MilestoneStatus.Breached ? m.BreachedAt ?? m.DueAt : m.DueAt;
                    if (m.Status == MilestoneStatus.Active && level.MinutesAfterBreach >= 0) break;
                    if (anchor.AddMinutes(level.MinutesAfterBreach) > now) break;
                }
                await EscalateAsync(ticket, instance, m, level, ct);
                m.EscalationLevelReached = level.Level;
                changed = true;
            }
        }

        if (!changed) return;
        ticket.SlaState = SlaEngine.StateOf(instance, now);
        instance.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    private async Task EscalateAsync(Ticket ticket, SlaInstance instance, SlaMilestone m, SlaEscalationSnapshot level, CancellationToken ct)
    {
        ticket.IsEscalated = true;
        ticket.EscalationLevel = Math.Max(ticket.EscalationLevel, level.Level);
        var actions = new List<string>();
        if (level.Action is EscalationAction.RaisePriority && ticket.Priority < TicketPriority.Critical)
        {
            var from = ticket.Priority;
            ticket.Priority += 1;
            actions.Add($"priority {from} → {ticket.Priority}");
            await engine.OnTicketChangedAsync(ticket, [new FieldChange("priority", from.ToString(), ticket.Priority.ToString())], ct);
        }
        if (level.Action is EscalationAction.Reassign or EscalationAction.NotifyAndReassign && (level.ReassignToUserId is not null || level.ReassignToTeamId is not null))
        {
            try
            {
                await tickets.AssignCoreAsync(ticket, level.ReassignToUserId, level.ReassignToTeamId, $"SLA escalation level {level.Level}", ct);
                actions.Add("reassigned");
            }
            catch (DomainException ex)
            {
                actions.Add($"reassignment skipped: {ex.Message}");
            }
        }
        Event(instance, m, SlaEventTypes.Escalated, new { level.Level, level.Action, actions });
        history.Record(ticket, TicketEventTypes.Escalated, $"SLA escalation level {level.Level} ({Label(m)}){(actions.Count > 0 ? ": " + string.Join(", ", actions) : "")}", null,
            new { level.Level, milestone = m.Kind.ToString(), notify = Json.Element(level.NotifyJson) });
        events.Publish(EventTypes.SlaEscalated, "Ticket", ticket.Id, new SlaEventPayload(ticket.Id, ticket.Number, instance.Id, m.Id, m.Kind, m.DueAt, level.Level));
        events.Publish(EventTypes.TicketEscalated, "Ticket", ticket.Id, new TicketEventPayload(ticket.Id, ticket.Number, null, null, $"SLA escalation level {level.Level}"));
    }

    private void Event(SlaInstance instance, SlaMilestone m, string type, object details) =>
        db.SlaEvents.Add(new SlaEvent
        {
            InstanceId = instance.Id, MilestoneId = m.Id, TicketId = instance.TicketId, Type = type, Milestone = m.Kind, OccurredAt = db.Clock.UtcNow,
            DetailsJson = Json.Serialize(details)
        });

    private static string Label(SlaMilestone m) => m.Kind switch
    {
        SlaMilestoneKind.FirstResponse => "First response",
        SlaMilestoneKind.NextResponse => $"Next response #{m.Cycle}",
        _ => "Resolution"
    };
}
