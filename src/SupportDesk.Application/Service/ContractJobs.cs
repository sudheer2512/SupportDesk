using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Customers;
using SupportDesk.Application.Notifications;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Products;
using SupportDesk.Domain.Service;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Service;

public sealed record ContractEligibilityPayload(Guid CustomerId);

/// <summary>
/// Re-evaluates support eligibility of a customer's open tickets after a contract or entitlement change (or a
/// contract expiring). Tickets whose eligibility/contract changed get a history entry, an SLA re-target and a
/// ticket.updated event. Idempotent: a second run finds nothing to change.
/// </summary>
public sealed class ContractEligibilityJobHandler(
    DeskDbContext db,
    IEntitlementService entitlements,
    ISlaEngine sla,
    IEventPublisher events,
    ILogger<ContractEligibilityJobHandler> logger) : JobHandler<ContractEligibilityPayload>
{
    public const string JobType = "contracts.reevaluate_eligibility";
    public override string Type => JobType;

    protected override async Task ExecuteAsync(ContractEligibilityPayload payload, BackgroundJob job, CancellationToken ct)
    {
        var tickets = await db.Tickets.Where(t => t.CustomerId == payload.CustomerId && t.StatusCategory < StatusCategory.Resolved &&
                                                  t.EntitlementStatus != EntitlementStatus.Overridden)
            .OrderBy(t => t.CreatedAt).Take(1000).ToListAsync(ct);
        var changed = 0;
        foreach (var t in tickets)
        {
            var before = (t.EntitlementStatus, t.ContractId);
            await entitlements.EvaluateAsync(t, ct);
            if (before.EntitlementStatus == t.EntitlementStatus && before.ContractId == t.ContractId) continue;
            changed++;
            List<FieldChange> changes = [new("entitlementStatus", before.EntitlementStatus.ToString(), t.EntitlementStatus.ToString())];
            if (before.ContractId != t.ContractId) changes.Add(new FieldChange("contractId", before.ContractId?.ToString(), t.ContractId?.ToString()));
            if (before.ContractId != t.ContractId) await sla.OnTicketChangedAsync(t, changes, ct);
            events.Publish(EventTypes.TicketUpdated, "Ticket", t.Id, new TicketEventPayload(t.Id, t.Number, changes, Note: "Support eligibility re-evaluated"));
        }
        await db.SaveChangesAsync(ct);
        if (changed > 0) logger.LogInformation("Eligibility re-evaluated for customer {Customer}: {Changed} of {Total} open tickets changed", payload.CustomerId, changed, tickets.Count);
    }
}

/// <summary>
/// Daily-ish contract lifecycle: flips Active contracts past their end date to Expired (and queues eligibility
/// re-evaluation for the customer's open tickets), expires licences, and notifies account managers 30 and 7 days
/// before a contract expires (de-duplicated per contract, threshold and end date).
/// </summary>
public sealed class ContractLifecycleTask(DeskDbContext db, TenantScopeRunner runner, ILogger<ContractLifecycleTask> logger) : IRecurringTask
{
    public const string TaskName = "contract-lifecycle";
    public string Name => TaskName;
    public TimeSpan Interval => TimeSpan.FromHours(1);

    public async Task RunAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(db.Clock.UtcNow);
        var horizon = today.AddDays(30);
        var tenants = await db.AllTenants<Contract>()
            .Where(c => c.Status == ContractStatus.Active && c.EndDate <= horizon)
            .Select(c => c.TenantId).Distinct()
            .Union(db.AllTenants<Licence>().Where(l => l.Status == LicenceStatus.Active && l.ExpiresOn != null && l.ExpiresOn < today).Select(l => l.TenantId).Distinct())
            .ToListAsync(ct);
        foreach (var tenantId in tenants)
        {
            try
            {
                await runner.RunForTenantAsync(tenantId, "Contract lifecycle", sp => sp.GetRequiredService<ContractLifecycleProcessor>().ProcessAsync(ct), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Contract lifecycle failed for tenant {Tenant}", tenantId);
            }
        }
    }
}

public sealed record ContractLifecycleResult(int Expired, int Notified, int LicencesExpired);

public sealed class ContractLifecycleProcessor(DeskDbContext db, IJobQueue jobs, IAuditWriter audit, NotificationService notifications)
{
    public const string NotificationType = "contract.expiring";

    public async Task<ContractLifecycleResult> ProcessAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(db.Clock.UtcNow);

        // 1. Expire contracts past their end date.
        var expired = await db.Contracts.Where(c => c.Status == ContractStatus.Active && c.EndDate < today).ToListAsync(ct);
        foreach (var c in expired)
        {
            c.Status = ContractStatus.Expired;
            audit.Write(CustomerAuditActions.ContractChanged, "Contract", c.Id, $"Contract {c.Number} expired on {c.EndDate:yyyy-MM-dd}",
                new { from = "Active", to = "Expired", automatic = true });
        }
        foreach (var customerId in expired.Select(c => c.CustomerId).Distinct())
            jobs.Enqueue(ContractEligibilityJobHandler.JobType, new ContractEligibilityPayload(customerId));

        // 2. Expire licences.
        var licences = await db.Licences.Where(l => l.Status == LicenceStatus.Active && l.ExpiresOn != null && l.ExpiresOn < today)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.Status, LicenceStatus.Expired), ct);
        await db.SaveChangesAsync(ct);

        // 3. Warn account managers 30 and 7 days ahead.
        var horizon = today.AddDays(30);
        var expiring = await db.Contracts.AsNoTracking().Where(c => c.Status == ContractStatus.Active && c.EndDate >= today && c.EndDate <= horizon).ToListAsync(ct);
        var customerIds = expiring.Select(c => c.CustomerId).Distinct().ToList();
        var customers = await db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name, c.AccountManagerUserId }).ToDictionaryAsync(c => c.Id, ct);
        var notified = 0;
        foreach (var c in expiring)
        {
            if (!customers.TryGetValue(c.CustomerId, out var customer) || customer.AccountManagerUserId is not { } am) continue;
            var days = c.EndDate.DayNumber - today.DayNumber;
            var threshold = days <= 7 ? 7 : 30;
            var renewal = c.AutoRenew ? " It is set to auto-renew — confirm the renewal order." : " Start the renewal conversation.";
            notified += await notifications.NotifyAsync(new NotifyRequest(
                [am], NotificationType,
                $"{c.Type} {c.Number} for {customer.Name} expires in {days} day{(days == 1 ? "" : "s")}",
                $"{c.Name} ends on {c.EndDate:dd MMM yyyy}.{renewal}",
                $"/products/contracts/{c.Id}",
                threshold == 7 ? NotificationSeverity.Warning : NotificationSeverity.Info,
                EntityType: "Contract", EntityId: c.Id,
                DedupKey: $"contract-expiry:{c.Id:N}:{threshold}:{c.EndDate:yyyyMMdd}"), ct);
        }
        return new ContractLifecycleResult(expired.Count, notified, licences);
    }
}
