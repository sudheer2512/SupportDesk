using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Platform;
using SupportDesk.Contracts.Subscriptions;
using SupportDesk.Domain;
using SupportDesk.Domain.Platform;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Subscriptions;

/// <summary>
/// Subscription state machine shared by the recurring tasks, the tenant billing page and the platform console:
/// trial → (expiry) past due with grace → suspended → (payment) active; period roll-over with scheduled downgrades;
/// cancellation at period end. All changes are written to the shared catalog and audited.
/// </summary>
public sealed class BillingLifecycle(InvoiceIssuer issuer, BillingOptions options, DedicatedCatalogMirror mirror, ILogger<BillingLifecycle> logger)
{
    /// <summary>Starts the grace period (trial expired or invoice overdue). The tenant keeps working until the grace period ends.</summary>
    public void StartGrace(DeskDbContext cdb, Tenant tenant, Subscription sub, string reason, DateTime now)
    {
        if (sub.Status is SubscriptionStatus.PastDue or SubscriptionStatus.Suspended or SubscriptionStatus.Cancelled) return;
        sub.Status = SubscriptionStatus.PastDue;
        sub.GraceEndsAt = now.AddDays(options.GraceDays);
        CatalogAudit.Write(cdb, tenant.Id, ActorType.System, null, "Billing", AuditActions.SubscriptionChanged, "Subscription", sub.Id,
            $"{reason} Grace period until {sub.GraceEndsAt:yyyy-MM-dd}.", new { status = "PastDue", sub.GraceEndsAt, reason }, now);
    }

    public void Suspend(DeskDbContext cdb, Tenant tenant, Subscription? sub, string reason, DateTime now, ActorType actor = ActorType.System, Guid? actorId = null,
        string? actorName = "Billing")
    {
        tenant.Status = TenantStatus.Suspended;
        tenant.SuspendedAt = now;
        tenant.SuspensionReason = reason;
        if (sub is not null && sub.Status != SubscriptionStatus.Cancelled)
        {
            sub.Status = SubscriptionStatus.Suspended;
            sub.SuspendedAt = now;
        }
        CatalogAudit.Write(cdb, tenant.Id, actor, actorId, actorName, AuditActions.TenantSuspended, "Tenant", tenant.Id, $"Tenant suspended: {reason}",
            new { reason }, now);
        if (actor != ActorType.Platform)
            CatalogAudit.Write(cdb, null, actor, actorId, actorName, AuditActions.TenantSuspended, "Tenant", tenant.Id, $"Tenant {tenant.Slug} suspended: {reason}",
                new { reason }, now);
    }

    /// <summary>Restores service: tenant Active, subscription Active (or Trialing while the trial still runs).</summary>
    public void Reactivate(DeskDbContext cdb, Tenant tenant, Subscription? sub, string reason, DateTime now, ActorType actor = ActorType.System, Guid? actorId = null,
        string? actorName = "Billing")
    {
        // A subscription that never converted to a paid plan returns to its trial (extend the trial to give it more time).
        var trialRunning = sub?.TrialEndsAt is not null;
        tenant.Status = trialRunning ? TenantStatus.Trial : TenantStatus.Active;
        tenant.SuspendedAt = null;
        tenant.SuspensionReason = null;
        tenant.CancelledAt = null;
        tenant.DeletionScheduledAt = null;
        if (sub is not null)
        {
            sub.Status = trialRunning ? SubscriptionStatus.Trialing : SubscriptionStatus.Active;
            sub.SuspendedAt = null;
            sub.GraceEndsAt = null;
            sub.CancelledAt = null;
            sub.CancelAtPeriodEnd = false;
        }
        CatalogAudit.Write(cdb, tenant.Id, actor, actorId, actorName, AuditActions.TenantResumed, "Tenant", tenant.Id, $"Service restored: {reason}", new { reason }, now);
    }

    /// <summary>
    /// After a payment: a billing-suspended subscription is restored once no open invoice remains; a past-due paid
    /// subscription once no overdue invoice remains. Tenants suspended by an operator for another reason stay suspended,
    /// and an unconverted trial needs a paid plan first.
    /// </summary>
    public async Task<bool> ReactivateIfSettledAsync(DeskDbContext cdb, Guid tenantId, DateTime now, CancellationToken ct)
    {
        var tenant = await cdb.Tenants.FirstAsync(t => t.Id == tenantId, ct);
        var sub = await cdb.AllTenants<Subscription>().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        if (sub is null || sub.TrialEndsAt is not null) return false;
        var open = await cdb.AllTenants<BillingInvoice>().Where(i => i.TenantId == tenantId && i.Status == InvoiceStatus.Open).Select(i => i.DueAt).ToListAsync(ct);
        var billingSuspension = tenant.Status == TenantStatus.Suspended && (tenant.SuspensionReason?.StartsWith("Billing:", StringComparison.Ordinal) ?? false);
        var settled = (sub.Status == SubscriptionStatus.Suspended && billingSuspension && open.Count == 0)
                      || (sub.Status == SubscriptionStatus.PastDue && open.All(due => due >= now));
        if (!settled) return false;
        Reactivate(cdb, tenant, sub, "payment received", now);
        return true;
    }

    /// <summary>
    /// Rolls subscriptions whose period ended into the next period (applying scheduled downgrades / cancellation) and
    /// makes sure the current period is invoiced. Idempotent; safe to run repeatedly.
    /// </summary>
    public async Task<int> RollPeriodsAndInvoiceAsync(DeskDbContext cdb, DateTime now, CancellationToken ct)
    {
        var issued = 0;
        var subs = await cdb.AllTenants<Subscription>()
            .Where(s => s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.PastDue)
            .ToListAsync(ct);
        foreach (var sub in subs)
        {
            var tenant = await cdb.Tenants.FirstAsync(t => t.Id == sub.TenantId, ct);
            var agents = await LatestUsageAsync(cdb, tenant.Id, UsageMetrics.Agents, ct);
            var guard = 0;
            while (sub.CurrentPeriodEnd <= now && guard++ < 36)
            {
                // Billing is in advance: a period that ended without its invoice (task outage) is still billed once.
                if (sub.TrialEndsAt is null && await issuer.FindPeriodInvoiceAsync(cdb, sub, sub.CurrentPeriodStart, ct) is null)
                {
                    var ending = await cdb.Plans.FirstAsync(p => p.Id == sub.PlanId, ct);
                    await issuer.EnsurePeriodInvoiceAsync(cdb, tenant, sub, ending, agents, now, ct);
                    issued++;
                }
                if (sub.CancelAtPeriodEnd)
                {
                    Cancel(cdb, tenant, sub, "Cancelled at the end of the billing period", 30, now, ActorType.System, null, "Billing");
                    break;
                }
                sub.CurrentPeriodStart = sub.CurrentPeriodEnd;
                sub.CurrentPeriodEnd = BillingMath.PeriodEnd(sub.CurrentPeriodStart, sub.BillingCycle);
                if (sub.PendingPlanId is { } pending)
                {
                    var target = await cdb.Plans.FirstOrDefaultAsync(p => p.Id == pending, ct);
                    if (target is not null)
                    {
                        CatalogAudit.Write(cdb, tenant.Id, ActorType.System, null, "Billing", AuditActions.SubscriptionChanged, "Subscription", sub.Id,
                            $"Scheduled plan change applied: now on {target.Name}", new { fromPlanId = sub.PlanId, toPlanId = target.Id }, now);
                        sub.PlanId = target.Id;
                    }
                    sub.PendingPlanId = null;
                }
                await cdb.SaveChangesAsync(ct);
            }
            if (sub.Status == SubscriptionStatus.Cancelled)
            {
                await cdb.SaveChangesAsync(ct);
                continue;
            }
            var plan = await cdb.Plans.FirstAsync(p => p.Id == sub.PlanId, ct);
            var before = await issuer.FindPeriodInvoiceAsync(cdb, sub, sub.CurrentPeriodStart, ct);
            if (before is null && sub.TrialEndsAt is null)
            {
                await issuer.EnsurePeriodInvoiceAsync(cdb, tenant, sub, plan, agents, now, ct);
                issued++;
            }
            await cdb.SaveChangesAsync(ct);
            if (tenant.DatabaseMode == DatabaseMode.Dedicated) await mirror.SyncAsync(tenant.Id, ct);
        }
        return issued;
    }

    public void Cancel(DeskDbContext cdb, Tenant tenant, Subscription? sub, string reason, int retentionDays, DateTime now, ActorType actor, Guid? actorId, string? actorName)
    {
        tenant.Status = TenantStatus.Cancelled;
        tenant.CancelledAt = now;
        tenant.DeletionScheduledAt = now.AddDays(Math.Clamp(retentionDays, 0, 365));
        tenant.SuspensionReason = reason;
        if (sub is not null)
        {
            sub.Status = SubscriptionStatus.Cancelled;
            sub.CancelledAt = now;
            sub.CancelAtPeriodEnd = false;
            sub.PendingPlanId = null;
        }
        CatalogAudit.Write(cdb, tenant.Id, actor, actorId, actorName, AuditActions.SubscriptionChanged, "Tenant", tenant.Id,
            $"Subscription cancelled: {reason}. Data scheduled for deletion on {tenant.DeletionScheduledAt:yyyy-MM-dd}.", new { reason, tenant.DeletionScheduledAt }, now);
    }

    /// <summary>
    /// Trial expiry and dunning: trials past their end and invoices past due start a grace period; when the grace period
    /// ends the tenant is suspended (writes return 402); cancelled tenants past their deletion date become PendingDeletion.
    /// </summary>
    public async Task<(int Grace, int Suspended, int PendingDeletion)> EnforceAsync(DeskDbContext cdb, DateTime now, CancellationToken ct)
    {
        int grace = 0, suspended = 0, pending = 0;
        var changed = new HashSet<Guid>();

        var expiredTrials = await cdb.AllTenants<Subscription>()
            .Where(s => s.Status == SubscriptionStatus.Trialing && s.TrialEndsAt != null && s.TrialEndsAt <= now).ToListAsync(ct);
        foreach (var sub in expiredTrials)
        {
            var tenant = await cdb.Tenants.FirstAsync(t => t.Id == sub.TenantId, ct);
            StartGrace(cdb, tenant, sub, $"Trial ended on {sub.TrialEndsAt:yyyy-MM-dd}.", now);
            changed.Add(tenant.Id);
            grace++;
        }

        var overdueTenants = await cdb.AllTenants<BillingInvoice>()
            .Where(i => i.Status == InvoiceStatus.Open && i.DueAt < now).Select(i => i.TenantId).Distinct().ToListAsync(ct);
        foreach (var tenantId in overdueTenants)
        {
            var sub = await cdb.AllTenants<Subscription>().FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Status == SubscriptionStatus.Active, ct);
            if (sub is null) continue;
            var tenant = await cdb.Tenants.FirstAsync(t => t.Id == tenantId, ct);
            StartGrace(cdb, tenant, sub, "An invoice is overdue.", now);
            changed.Add(tenantId);
            grace++;
        }

        var lapsed = await cdb.AllTenants<Subscription>()
            .Where(s => s.Status == SubscriptionStatus.PastDue && s.GraceEndsAt != null && s.GraceEndsAt <= now).ToListAsync(ct);
        foreach (var sub in lapsed)
        {
            var tenant = await cdb.Tenants.FirstAsync(t => t.Id == sub.TenantId, ct);
            var reason = sub.TrialEndsAt is not null && sub.TrialEndsAt <= now
                ? "Billing: the trial ended and no paid plan was activated."
                : "Billing: an invoice remained unpaid after the grace period.";
            Suspend(cdb, tenant, sub, reason, now);
            changed.Add(tenant.Id);
            suspended++;
        }

        var due = await cdb.Tenants.Where(t => t.Status == TenantStatus.Cancelled && t.DeletionScheduledAt != null && t.DeletionScheduledAt <= now).ToListAsync(ct);
        foreach (var tenant in due)
        {
            tenant.Status = TenantStatus.PendingDeletion;
            CatalogAudit.Write(cdb, tenant.Id, ActorType.System, null, "Billing", AuditActions.SubscriptionChanged, "Tenant", tenant.Id,
                "Retention after cancellation ended; tenant is pending deletion.", null, now);
            CatalogAudit.Write(cdb, null, ActorType.System, null, "Billing", AuditActions.SubscriptionChanged, "Tenant", tenant.Id,
                $"Tenant {tenant.Slug} is pending deletion (scheduled {tenant.DeletionScheduledAt:yyyy-MM-dd}).", null, now);
            changed.Add(tenant.Id);
            pending++;
        }

        await cdb.SaveChangesAsync(ct);
        foreach (var id in changed)
        {
            try { await mirror.SyncAsync(id, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Catalog mirror failed for tenant {Tenant}", id); }
        }
        return (grace, suspended, pending);
    }

    public static async Task<long> LatestUsageAsync(DeskDbContext cdb, Guid tenantId, string metric, CancellationToken ct) =>
        await cdb.AllTenants<UsageSnapshot>().Where(s => s.TenantId == tenantId && s.Metric == metric)
            .OrderByDescending(s => s.Day).Select(s => (long?)s.Quantity).FirstOrDefaultAsync(ct) ?? 0;
}

/// <summary>Monthly (period-boundary) invoicing: runs hourly, bills each period once.</summary>
public sealed class InvoiceGenerationTask(CatalogStore catalog, BillingLifecycle lifecycle, IClock clock) : IRecurringTask
{
    public const string TaskName = "invoice-generation";
    public string Name => TaskName;
    public TimeSpan Interval => TimeSpan.FromHours(1);
    public Task RunAsync(CancellationToken ct) => lifecycle.RollPeriodsAndInvoiceAsync(catalog.Db, clock.UtcNow, ct);
}

/// <summary>Trial expiry → grace → suspension, dunning of overdue invoices, deletion schedule and support-access expiry.</summary>
public sealed class SubscriptionLifecycleTask(CatalogStore catalog, BillingLifecycle lifecycle, IClock clock) : IRecurringTask
{
    public const string TaskName = "subscription-lifecycle";
    public string Name => TaskName;
    public TimeSpan Interval => TimeSpan.FromMinutes(30);

    public async Task RunAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        await lifecycle.EnforceAsync(catalog.Db, now, ct);
        var expired = await catalog.Db.AllTenants<SupportAccessGrant>()
            .Where(g => g.Status == SupportAccessStatus.Approved && g.ExpiresAt != null && g.ExpiresAt <= now).ToListAsync(ct);
        foreach (var grant in expired)
        {
            grant.Status = SupportAccessStatus.Expired;
            CatalogAudit.Write(catalog.Db, grant.TenantId, ActorType.System, null, "Platform", "support_access.expired", "SupportAccessGrant", grant.Id,
                $"Support access for {grant.OperatorName} expired", null, now);
        }
        await catalog.Db.SaveChangesAsync(ct);
    }
}
