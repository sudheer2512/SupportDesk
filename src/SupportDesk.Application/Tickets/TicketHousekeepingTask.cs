using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Notifications;
using SupportDesk.Application.Platform;
using SupportDesk.Domain;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Tickets;

/// <summary>
/// Periodic ticket housekeeping per tenant: fires follow-up reminders, auto-closes resolved tickets after the
/// configured idle period (customer confirmation window), removes expired temporary participants and expires
/// stale approval requests.
/// </summary>
public sealed class TicketHousekeepingTask(DeskDbContext db, TenantScopeRunner runner, ILogger<TicketHousekeepingTask> logger) : IRecurringTask
{
    public string Name => "ticket-housekeeping";
    public TimeSpan Interval => TimeSpan.FromSeconds(60);

    public async Task RunAsync(CancellationToken ct)
    {
        var now = db.Clock.UtcNow;
        var tenants = await db.Tenants.AsNoTracking().Where(t => t.Status == Domain.Platform.TenantStatus.Active || t.Status == Domain.Platform.TenantStatus.Trial)
            .Select(t => t.Id).ToListAsync(ct);
        foreach (var tenant in tenants)
        {
            try
            {
                await runner.RunForTenantAsync(tenant, "Housekeeping", sp => sp.GetRequiredService<TicketHousekeepingProcessor>().RunAsync(now, ct), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Housekeeping failed for tenant {Tenant}", tenant);
            }
        }
    }
}

public sealed class TicketHousekeepingProcessor(DeskDbContext db, TenantSettingsService settings, TicketService tickets, NotificationService notifications)
{
    public async Task RunAsync(DateTime now, CancellationToken ct)
    {
        // Follow-up reminders
        var due = await db.TicketFollowUps.Where(f => f.Status == FollowUpStatus.Pending && f.RemindAt <= now).OrderBy(f => f.RemindAt).ThenBy(f => f.Id).Take(200).ToListAsync(ct);
        foreach (var f in due)
        {
            var t = await db.Tickets.AsNoTracking().Where(x => x.Id == f.TicketId).Select(x => new { x.Number, x.Subject }).FirstOrDefaultAsync(ct);
            f.Status = FollowUpStatus.Fired;
            f.FiredAt = now;
            await db.SaveChangesAsync(ct);
            if (t is not null)
                await notifications.NotifyAsync(new NotifyRequest([f.UserId], NotificationTypes.FollowUpDue, $"Follow up on #{t.Number}", f.Note ?? t.Subject,
                    $"/tickets/{f.TicketId}", NotificationSeverity.Warning, false, "Ticket", f.TicketId, $"followup:{f.Id}"), ct);
        }

        // Auto-close resolved tickets after the idle period (also ends the customer confirmation window).
        var s = await settings.TicketsAsync(ct);
        if (s.AutoCloseResolvedAfterDays > 0)
        {
            var cutoff = now.AddDays(-s.AutoCloseResolvedAfterDays);
            var ids = await db.Tickets.Where(t => t.StatusCategory == StatusCategory.Resolved && t.ResolvedAt != null && t.ResolvedAt < cutoff)
                .OrderBy(t => t.ResolvedAt).ThenBy(t => t.Id).Select(t => t.Id).Take(200).ToListAsync(ct);
            if (ids.Count > 0)
            {
                var closed = await tickets.StatusForCategoryAsync(StatusCategory.Closed, ct);
                foreach (var id in ids)
                {
                    var t = await db.Tickets.Include(x => x.Tags).FirstAsync(x => x.Id == id, ct);
                    await tickets.ApplyStatusAsync(t, closed, null, $"Auto-closed after {s.AutoCloseResolvedAfterDays} days without customer response", bypassRules: true, ct);
                    t.AwaitingCustomerConfirmation = false;
                    try { await db.SaveChangesAsync(ct); }
                    catch (DbUpdateConcurrencyException) { /* changed concurrently; retried next run */ }
                    db.ChangeTracker.Clear();
                }
            }
        }

        // Temporary participants (specialists) whose access window ended.
        await db.TicketWatchers.Where(w => w.ExpiresAt != null && w.ExpiresAt < now).ExecuteDeleteAsync(ct);

        // Approval requests past their expiry.
        await db.ApprovalRequests.Where(a => a.Status == ApprovalStatus.Pending && a.ExpiresAt != null && a.ExpiresAt < now)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.Status, ApprovalStatus.Expired).SetProperty(a => a.DecidedAt, now), ct);
    }
}
