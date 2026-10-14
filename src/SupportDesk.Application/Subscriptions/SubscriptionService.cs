using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Subscriptions;
using SupportDesk.Domain;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Subscriptions;

/// <summary>Builds the subscription view (plan, status, usage meters with effective limits, billing contact, amounts due).</summary>
public sealed class SubscriptionViewBuilder(PaymentProviderRegistry providers)
{
    public async Task<SubscriptionDto> BuildAsync(DeskDbContext cdb, Tenant tenant, Subscription sub, IReadOnlyDictionary<string, long> usage, DateTime now,
        CancellationToken ct)
    {
        var plans = await cdb.Plans.AsNoTracking().ToListAsync(ct);
        var plan = plans.First(p => p.Id == sub.PlanId);
        var pending = sub.PendingPlanId is { } pid ? plans.FirstOrDefault(p => p.Id == pid) : null;
        var open = await cdb.AllTenants<BillingInvoice>().AsNoTracking()
            .Where(i => i.TenantId == tenant.Id && i.Status == InvoiceStatus.Open).Select(i => i.Total).ToListAsync(ct);
        var meters = PlanCatalog.Meters
            .Select(m => PlanCatalog.Meter(m.Metric, usage.TryGetValue(m.Metric, out var v) ? v : 0, PlanCatalog.EffectiveLimit(plan, sub, m.Metric)))
            .ToList();
        var agents = usage.TryGetValue(UsageMetrics.Agents, out var a) ? a : 0;
        var nextPlan = pending ?? plan;
        var estimate = sub.TrialEndsAt is not null && sub.Status == SubscriptionStatus.Trialing
            ? BillingMath.PeriodAmount(nextPlan, sub.BillingCycle, Math.Max(sub.Seats, agents))
            : BillingMath.PeriodAmount(nextPlan, sub.BillingCycle, Math.Max(sub.Seats, agents));
        int? trialDaysLeft = sub.TrialEndsAt is { } end && sub.Status is SubscriptionStatus.Trialing or SubscriptionStatus.PastDue
            ? Math.Max(0, (int)Math.Ceiling((end - now).TotalDays)) : null;
        var provider = providers.For(sub.PaymentProvider);
        return new SubscriptionDto(
            sub.Id, tenant.Id, tenant.Name, tenant.Slug, tenant.Status.ToString(), PlanCatalog.ToDto(plan), sub.Status.ToString(), sub.BillingCycle.ToString(), sub.Seats,
            sub.StartedAt, sub.TrialEndsAt, trialDaysLeft, sub.CurrentPeriodStart, sub.CurrentPeriodEnd, sub.CancelAtPeriodEnd,
            pending is null ? null : PlanCatalog.ToDto(pending), sub.GraceEndsAt, tenant.SuspendedAt, tenant.SuspensionReason, provider.DisplayName,
            provider.Instructions(null, tenant.Name),
            new BillingContactDto(sub.BillingContactName, sub.BillingContactEmail, sub.BillingAddress, sub.BillingTaxId),
            meters, estimate, plan.Currency, open.Count, open.Sum(), Convert.ToBase64String(sub.RowVersion));
    }
}

/// <summary>
/// The tenant administrator's billing page (admin.billing): plan and trial status, limits vs usage with history, plan
/// comparison and change (upgrade now with a pro-rata invoice, downgrade at period end if usage fits), invoices + PDF and
/// the billing contact. Reachable while suspended (billing paths are exempt from the 402 write block).
/// </summary>
public sealed class SubscriptionService(
    DeskDbContext db,
    RequestContext ctx,
    CatalogStore catalog,
    UsageCollector usage,
    UsageSnapshotWriter history,
    SubscriptionViewBuilder views,
    InvoiceIssuer issuer,
    InvoicePresenter invoices,
    DedicatedCatalogMirror mirror,
    IAuditWriter audit)
{
    private DeskDbContext Cdb => catalog.Db;

    public async Task<SubscriptionDto> GetAsync(CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminBilling);
        var (tenant, sub) = await LoadAsync(ct);
        // Live gauges (cheap counts) so the page never shows stale numbers; today's history point is refreshed too.
        var values = await usage.RecomputeAsync(ct);
        await history.WriteAsync(tenant.Id, values, ct);
        return await views.BuildAsync(Cdb, tenant, sub, values, db.Clock.UtcNow, ct);
    }

    /// <summary>Public plans for the comparison table (plus the current plan if it is private).</summary>
    public async Task<List<PlanDto>> PlansAsync(CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminBilling);
        var (_, sub) = await LoadAsync(ct);
        var plans = await Cdb.Plans.AsNoTracking().Where(p => p.IsPublic || p.Id == sub.PlanId).OrderBy(p => p.SortOrder).ToListAsync(ct);
        return plans.Select(p => PlanCatalog.ToDto(p)).ToList();
    }

    public IReadOnlyList<PlanFeatureDto> Features() => PlanCatalog.Features;

    public async Task<IReadOnlyList<UsageHistoryDto>> HistoryAsync(int days, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminBilling);
        return await history.HistoryAsync(ctx.TenantId, days, ct);
    }

    public async Task<PlanChangePreviewDto> PreviewAsync(ChangePlanRequest request, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminBilling);
        var (tenant, sub) = await LoadAsync(ct);
        return (await EvaluateAsync(tenant, sub, request, ct)).Preview;
    }

    public async Task<ChangePlanResultDto> ChangePlanAsync(ChangePlanRequest request, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminBilling);
        var (tenant, sub) = await LoadAsync(ct);
        if (!string.IsNullOrEmpty(request.RowVersion) && request.RowVersion != Convert.ToBase64String(sub.RowVersion))
            throw new ConflictException("concurrency_conflict", "The subscription was changed by someone else. Reload and try again.");
        if (sub.Status == SubscriptionStatus.Cancelled || tenant.Status is TenantStatus.Cancelled or TenantStatus.PendingDeletion)
            throw new ConflictException("subscription_cancelled", "This subscription is cancelled. Contact the SupportDesk team to restore it.");

        var eval = await EvaluateAsync(tenant, sub, request, ct);
        var preview = eval.Preview;
        if (preview.Direction == "same") throw new ConflictException("plan_unchanged", "You are already on this plan with these seats.");
        if (preview.Conflicts.Count > 0)
            throw new ConflictException("plan_usage_exceeded",
                $"Current usage does not fit the {preview.ToPlan} plan: " + string.Join("; ", preview.Conflicts.Select(c => $"{c.Label} {c.Used:N0} of {c.Limit:N0}")) + ".");

        var now = db.Clock.UtcNow;
        var fromPlan = eval.From;
        var toPlan = eval.To;
        BillingInvoice? invoice = null;
        string message;
        var before = new { plan = fromPlan.Code, sub.Seats, cycle = sub.BillingCycle.ToString(), status = sub.Status.ToString() };

        if (sub.TrialEndsAt is not null)
        {
            // Converting a trial: a new paid period starts now and is invoiced immediately.
            sub.PlanId = toPlan.Id;
            sub.Seats = preview.Seats;
            sub.BillingCycle = Enum.Parse<BillingCycle>(preview.BillingCycle);
            sub.TrialEndsAt = null;
            sub.PendingPlanId = null;
            sub.CurrentPeriodStart = now;
            sub.CurrentPeriodEnd = BillingMath.PeriodEnd(now, sub.BillingCycle);
            if (sub.Status is SubscriptionStatus.Trialing or SubscriptionStatus.PastDue)
            {
                sub.Status = SubscriptionStatus.Active;
                sub.GraceEndsAt = null;
                if (tenant.Status == TenantStatus.Trial) tenant.Status = TenantStatus.Active;
            }
            await Cdb.SaveChangesAsync(ct);
            invoice = await issuer.EnsurePeriodInvoiceAsync(Cdb, tenant, sub, toPlan, await AgentsAsync(ct), now, ct);
            message = sub.Status == SubscriptionStatus.Suspended
                ? $"You are now on the {toPlan.Name} plan. Service resumes as soon as invoice {invoice.Number} is paid."
                : $"Welcome to the {toPlan.Name} plan. Invoice {invoice.Number} has been issued for the first period.";
        }
        else if (preview.Immediate)
        {
            if (preview.ProratedAmount > 0)
            {
                var lines = new List<InvoiceLineDto>
                {
                    new($"Pro-rata upgrade {fromPlan.Name} ({before.Seats} seats) → {toPlan.Name} ({preview.Seats} seats) for the rest of the period " +
                        $"({now:dd MMM yyyy} – {sub.CurrentPeriodEnd.AddDays(-1):dd MMM yyyy})", 1, preview.ProratedAmount, preview.ProratedAmount)
                };
                invoice = await issuer.IssueAsync(Cdb, tenant, sub, toPlan.Currency, InvoiceKind.Proration, now, sub.CurrentPeriodEnd, lines, null, now, ct);
            }
            sub.PlanId = toPlan.Id;
            sub.Seats = preview.Seats;
            sub.PendingPlanId = null;
            message = invoice is null
                ? $"Your plan was changed to {toPlan.Name}."
                : $"Upgraded to {toPlan.Name}. A pro-rata invoice {invoice.Number} for {preview.ProratedAmount:N2} {toPlan.Currency} was issued.";
        }
        else
        {
            sub.PendingPlanId = toPlan.Id == sub.PlanId ? null : toPlan.Id;
            if (toPlan.Id == sub.PlanId) sub.Seats = preview.Seats; // fewer seats on the same plan: effective now, no refund
            message = toPlan.Id == sub.PlanId
                ? $"Seats reduced to {preview.Seats}. The next invoice reflects the change."
                : $"Your plan changes to {toPlan.Name} on {sub.CurrentPeriodEnd:dd MMM yyyy}, at the end of the current period.";
        }

        audit.Write(AuditActions.SubscriptionChanged, "Subscription", sub.Id, message,
            new { before, after = new { plan = toPlan.Code, seats = preview.Seats, cycle = preview.BillingCycle, pendingPlan = sub.PendingPlanId is null ? null : toPlan.Code } });
        await Cdb.SaveChangesAsync(ct);
        await db.SaveChangesAsync(ct);
        if (tenant.DatabaseMode == DatabaseMode.Dedicated) await mirror.SyncAsync(tenant.Id, ct);
        return new ChangePlanResultDto(await GetAsync(ct), invoice is null ? null : InvoicePresenter.Summary(invoice, tenant.Name, now), message);
    }

    public async Task<SubscriptionDto> CancelPendingChangeAsync(CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminBilling);
        var (tenant, sub) = await LoadAsync(ct);
        if (sub.PendingPlanId is null) throw new ConflictException("no_pending_change", "There is no scheduled plan change.");
        sub.PendingPlanId = null;
        audit.Write(AuditActions.SubscriptionChanged, "Subscription", sub.Id, "Scheduled plan change cancelled", null);
        await Cdb.SaveChangesAsync(ct);
        await db.SaveChangesAsync(ct);
        if (tenant.DatabaseMode == DatabaseMode.Dedicated) await mirror.SyncAsync(tenant.Id, ct);
        return await GetAsync(ct);
    }

    public async Task<SubscriptionDto> UpdateBillingContactAsync(UpdateBillingContactRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminBilling);
        var errors = new Dictionary<string, string>();
        var email = r.Email?.Trim();
        if (!string.IsNullOrEmpty(email) && TextNormalization.NormalizeEmail(email) is null) errors["email"] = "Enter a valid e-mail address.";
        if ((r.Name?.Length ?? 0) > 200) errors["name"] = "At most 200 characters.";
        if ((r.Address?.Length ?? 0) > 1000) errors["address"] = "At most 1000 characters.";
        if ((r.TaxId?.Length ?? 0) > 64) errors["taxId"] = "At most 64 characters.";
        if (errors.Count > 0) throw new ValidationException(errors);
        var (_, sub) = await LoadAsync(ct);
        var before = new { sub.BillingContactName, sub.BillingContactEmail, sub.BillingAddress, sub.BillingTaxId };
        sub.BillingContactName = Blank(r.Name);
        sub.BillingContactEmail = Blank(email);
        sub.BillingAddress = Blank(r.Address);
        sub.BillingTaxId = Blank(r.TaxId);
        audit.Write(AuditActions.SubscriptionChanged, "Subscription", sub.Id, "Billing contact updated",
            new { before, after = new { sub.BillingContactName, sub.BillingContactEmail, sub.BillingAddress, sub.BillingTaxId } });
        await Cdb.SaveChangesAsync(ct);
        await db.SaveChangesAsync(ct);
        return await GetAsync(ct);
    }

    public async Task<PagedResult<InvoiceSummaryDto>> InvoicesAsync(string? cursor, int limit, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminBilling);
        var (tenant, _) = await LoadAsync(ct);
        return await InvoicePaging.PageAsync(Cdb.AllTenants<BillingInvoice>().Where(i => i.TenantId == tenant.Id), _ => tenant.Name, cursor, limit, db.Clock.UtcNow, ct);
    }

    public async Task<InvoiceDto> InvoiceAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminBilling);
        var (tenant, sub) = await LoadAsync(ct);
        var invoice = await Cdb.AllTenants<BillingInvoice>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenant.Id, ct)
                      ?? throw new NotFoundException("Invoice", id);
        return invoices.Detail(invoice, tenant, sub, db.Clock.UtcNow);
    }

    public async Task<(byte[] Content, string FileName)> InvoicePdfAsync(Guid id, CancellationToken ct = default)
    {
        var detail = await InvoiceAsync(id, ct);
        return (invoices.RenderPdf(detail), $"{detail.Summary.Number}.pdf");
    }

    private async Task<(Tenant Tenant, Subscription Sub)> LoadAsync(CancellationToken ct)
    {
        if (!ctx.HasTenant) throw new ForbiddenException();
        var tenant = await Cdb.Tenants.FirstOrDefaultAsync(t => t.Id == ctx.TenantId, ct) ?? throw new NotFoundException("Tenant");
        var sub = await Cdb.AllTenants<Subscription>().FirstOrDefaultAsync(s => s.TenantId == tenant.Id, ct) ?? throw new NotFoundException("Subscription");
        return (tenant, sub);
    }

    private async Task<long> AgentsAsync(CancellationToken ct) => (await usage.CurrentAsync(ct)).GetValueOrDefault(UsageMetrics.Agents);

    private sealed record Evaluation(Plan From, Plan To, PlanChangePreviewDto Preview);

    private async Task<Evaluation> EvaluateAsync(Tenant tenant, Subscription sub, ChangePlanRequest request, CancellationToken ct)
    {
        var plans = await Cdb.Plans.AsNoTracking().ToListAsync(ct);
        var from = plans.First(p => p.Id == sub.PlanId);
        var to = plans.FirstOrDefault(p => string.Equals(p.Code, request.PlanCode?.Trim(), StringComparison.OrdinalIgnoreCase))
                 ?? throw new NotFoundException("Plan", request.PlanCode);
        if (!to.IsPublic && to.Id != from.Id) throw new NotFoundException("Plan", request.PlanCode);
        if (to.Code == "trial" && from.Code != "trial") throw new DomainException("validation_failed", "The trial plan cannot be selected again.");
        var current = await usage.CurrentAsync(ct);
        var agents = current.GetValueOrDefault(UsageMetrics.Agents);
        var seats = request.Seats ?? Math.Max(sub.Seats, (int)agents);
        if (seats < 1 || seats > 100_000) throw new ValidationException(new Dictionary<string, string> { ["seats"] = "Seats must be between 1 and 100,000." });
        var cycle = sub.BillingCycle;
        if (!string.IsNullOrWhiteSpace(request.BillingCycle))
        {
            if (!Enum.TryParse<BillingCycle>(request.BillingCycle, true, out cycle))
                throw new ValidationException(new Dictionary<string, string> { ["billingCycle"] = "Use Monthly or Annual." });
        }

        var conflicts = new List<PlanConflictDto>();
        if (seats < agents) conflicts.Add(new PlanConflictDto("seats", "Seats (active agents)", agents, seats));
        foreach (var metric in new[] { UsageMetrics.Agents, UsageMetrics.LightAgents, UsageMetrics.PortalUsers, UsageMetrics.StorageBytes, UsageMetrics.Channels })
        {
            var limit = PlanCatalog.EffectiveLimit(to, sub, metric);
            var used = current.GetValueOrDefault(metric);
            if (limit is { } l && used > l) conflicts.Add(new PlanConflictDto(metric, PlanCatalog.LabelOf(metric), used, l));
        }

        var now = db.Clock.UtcNow;
        var trial = sub.TrialEndsAt is not null;
        var direction = trial ? (to.Id == from.Id && to.Code == "trial" ? "same" : "upgrade") : BillingMath.Direction(from, sub.Seats, to, seats, cycle);
        if (!trial && cycle != sub.BillingCycle && direction == "same") direction = "upgrade";
        if (!trial && cycle != sub.BillingCycle && direction == "downgrade")
            throw new DomainException("validation_failed", "Change the billing cycle together with an upgrade, or when your trial converts.");
        var immediate = trial || direction == "upgrade";
        var prorated = trial || !immediate ? 0 : BillingMath.ProratedCharge(from, sub.Seats, to, seats, sub.BillingCycle, sub.CurrentPeriodStart, sub.CurrentPeriodEnd, now);
        var effective = immediate ? now : sub.CurrentPeriodEnd;
        var allowed = conflicts.Count == 0 && direction != "same";
        var message = direction switch
        {
            "same" => "No change.",
            _ when conflicts.Count > 0 => "Reduce usage before switching to this plan.",
            _ when trial => $"Your trial converts to {to.Name} now; the first {cycle.ToString().ToLowerInvariant()} period is invoiced immediately.",
            "upgrade" => prorated > 0 ? $"Upgrade now; {prorated:N2} {to.Currency} is charged pro rata for the rest of the period." : $"Upgrade now to {to.Name}.",
            _ => $"Downgrade to {to.Name} takes effect on {sub.CurrentPeriodEnd:dd MMM yyyy}."
        };
        var preview = new PlanChangePreviewDto(from.Name, to.Name, direction, immediate, effective, prorated, BillingMath.PeriodAmount(to, cycle, Math.Max(seats, agents)),
            to.Currency, seats, cycle.ToString(), conflicts, allowed, message);
        return new Evaluation(from, to, preview);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
