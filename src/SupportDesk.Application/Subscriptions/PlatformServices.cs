using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Platform;
using SupportDesk.Contracts.Subscriptions;
using SupportDesk.Domain;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Platform;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Subscriptions;

/// <summary>
/// Entry check for every platform-console service: only a platform operator principal (platform token) may call them;
/// the platform scope is entered explicitly so cross-tenant catalog writes are allowed for this request only.
/// </summary>
public static class PlatformAccess
{
    public static void Enter(RequestContext ctx)
    {
        if (!ctx.IsPlatformOperator || ctx.HasTenant) throw new ForbiddenException("Platform operator access required.");
        ctx.EnterPlatformScope();
    }
}

/// <summary>Tenants in the platform console: overview, list/search/detail, suspend, reactivate, cancel, extend trial, change plan.</summary>
public sealed class PlatformTenantService(
    DeskDbContext db,
    RequestContext ctx,
    IAuditWriter audit,
    BillingLifecycle lifecycle,
    DedicatedCatalogMirror mirror,
    SubscriptionViewBuilder views,
    TenantScopeRunner runner,
    SupportAccessService supportAccess)
{
    public async Task<PlatformOverviewDto> OverviewAsync(CancellationToken ct = default)
    {
        PlatformAccess.Enter(ctx);
        var now = db.Clock.UtcNow;
        var statuses = await db.Tenants.AsNoTracking().GroupBy(t => t.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        int Count(TenantStatus s) => statuses.FirstOrDefault(x => x.Key == s)?.Count ?? 0;
        var soon = now.AddDays(7);
        var trialsEnding = await db.AllTenants<Subscription>().CountAsync(s => s.Status == SubscriptionStatus.Trialing && s.TrialEndsAt != null && s.TrialEndsAt <= soon, ct);
        var plans = await db.Plans.AsNoTracking().ToDictionaryAsync(p => p.Id, ct);
        var active = await db.AllTenants<Subscription>().AsNoTracking()
            .Where(s => s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.PastDue).ToListAsync(ct);
        var mrr = active.Sum(s => plans.TryGetValue(s.PlanId, out var p)
            ? s.BillingCycle == BillingCycle.Annual ? BillingMath.Money(p.AnnualPricePerAgent * s.Seats / 12m) : BillingMath.Money(p.MonthlyPricePerAgent * s.Seats)
            : 0);
        var open = await db.AllTenants<BillingInvoice>().AsNoTracking().Where(i => i.Status == InvoiceStatus.Open).Select(i => new { i.Total, i.DueAt }).ToListAsync(ct);
        var pendingSupport = await db.AllTenants<SupportAccessGrant>().CountAsync(g => g.Status == SupportAccessStatus.Pending, ct);
        return new PlatformOverviewDto(statuses.Sum(s => s.Count), Count(TenantStatus.Trial), Count(TenantStatus.Active), Count(TenantStatus.Suspended),
            Count(TenantStatus.Cancelled) + Count(TenantStatus.PendingDeletion), trialsEnding, mrr, open.Sum(o => o.Total), open.Count(o => o.DueAt < now),
            pendingSupport, plans.Values.FirstOrDefault()?.Currency ?? "USD");
    }

    public async Task<PagedResult<PlatformTenantDto>> ListAsync(string? search, string? status, string? cursor, int limit, CancellationToken ct = default)
    {
        PlatformAccess.Enter(ctx);
        limit = Math.Clamp(limit, 1, 200);
        var q = db.Tenants.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            q = q.Where(t => t.Slug.Contains(term) || t.Name.Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<TenantStatus>(status, true, out var st)) throw new ValidationException(new Dictionary<string, string> { ["status"] = "Unknown status." });
            q = q.Where(t => t.Status == st);
        }
        if (DbExtensions.DecodeCursor(cursor) is { } c)
        {
            var at = DateTime.Parse(c.V, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            q = q.Where(t => t.CreatedAt < at || (t.CreatedAt == at && t.Id.CompareTo(c.Id) < 0));
        }
        var rows = await Project(q.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)).Take(limit + 1).ToListAsync(ct);
        var next = rows.Count > limit ? DbExtensions.EncodeCursor(rows[limit - 1].CreatedAt.ToString("O", CultureInfo.InvariantCulture), rows[limit - 1].Id) : null;
        var total = await q.CountAsync(ct);
        return new PagedResult<PlatformTenantDto>(rows.Take(limit).ToList(), next, total);
    }

    private IQueryable<PlatformTenantDto> Project(IQueryable<Tenant> q) =>
        from t in q
        let sub = db.AllTenants<Subscription>().FirstOrDefault(s => s.TenantId == t.Id)
        let plan = sub == null ? null : db.Plans.FirstOrDefault(p => p.Id == sub.PlanId)
        select new PlatformTenantDto(
            t.Id, t.Slug, t.Name, t.Status.ToString(), plan == null ? "none" : plan.Code, plan == null ? "No plan" : plan.Name,
            sub == null ? "None" : sub.Status.ToString(), t.CreatedAt,
            db.AllTenants<User>().Where(u => u.TenantId == t.Id).Max(u => u.LastLoginAt),
            sub == null ? null : sub.TrialEndsAt,
            db.AllTenants<UsageSnapshot>().Where(s => s.TenantId == t.Id && s.Metric == UsageMetrics.Agents).OrderByDescending(s => s.Day).Select(s => s.Quantity).FirstOrDefault(),
            db.AllTenants<UsageSnapshot>().Where(s => s.TenantId == t.Id && s.Metric == UsageMetrics.StorageBytes).OrderByDescending(s => s.Day).Select(s => s.Quantity).FirstOrDefault(),
            t.DatabaseMode.ToString(), t.DedicatedConnectionName, t.DeletionScheduledAt);

    public async Task<PlatformTenantDetailDto> GetAsync(Guid tenantId, CancellationToken ct = default)
    {
        PlatformAccess.Enter(ctx);
        var dto = await Project(db.Tenants.AsNoTracking().Where(t => t.Id == tenantId)).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Tenant", tenantId);
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == tenantId, ct);
        var sub = await db.AllTenants<Subscription>().AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        var now = db.Clock.UtcNow;
        SubscriptionDto? subscription = null;
        if (sub is not null)
        {
            Dictionary<string, long> usage = [];
            if (tenant.Status != TenantStatus.PendingDeletion)
                await runner.RunForTenantAsync(tenantId, "Platform console", async sp => usage = await sp.GetRequiredService<UsageCollector>().CurrentAsync(ct), ct);
            subscription = await views.BuildAsync(db, tenant, sub, usage, now, ct);
        }
        var invoices = await db.AllTenants<BillingInvoice>().AsNoTracking().Where(i => i.TenantId == tenantId)
            .OrderByDescending(i => i.IssuedAt).Take(10).ToListAsync(ct);
        var activity = await db.AuditLogs.AsNoTracking().Where(a => a.TenantId == tenantId && (a.ActorType == ActorType.Platform || a.ActorType == ActorType.System))
            .OrderByDescending(a => a.OccurredAt).Take(15).ToListAsync(ct);
        return new PlatformTenantDetailDto(dto, subscription, invoices.Select(i => InvoicePresenter.Summary(i, tenant.Name, now)).ToList(),
            await supportAccess.ForTenantAsync(db, tenantId, ct), activity.Select(a => PlatformAuditService.ToDto(a, tenant.Name)).ToList(),
            tenant.SuspensionReason, tenant.SuspendedAt, tenant.Country, tenant.DefaultTimeZone);
    }

    public async Task<PlatformTenantDetailDto> SuspendAsync(Guid tenantId, TenantActionRequest r, CancellationToken ct = default)
    {
        PlatformAccess.Enter(ctx);
        var reason = Required(r.Reason, "reason");
        var (tenant, sub) = await LoadAsync(tenantId, ct);
        if (tenant.Status is TenantStatus.Suspended) throw new ConflictException("already_suspended", "The tenant is already suspended.");
        if (tenant.Status is TenantStatus.Cancelled or TenantStatus.PendingDeletion) throw new ConflictException("tenant_cancelled", "The tenant is cancelled.");
        lifecycle.Suspend(db, tenant, sub, $"Operator: {reason}", db.Clock.UtcNow, ActorType.Platform, ctx.UserId, ctx.ActorName);
        audit.Write(AuditActions.TenantSuspended, "Tenant", tenant.Id, $"Suspended tenant {tenant.Slug}: {reason}", new { reason });
        return await SaveAsync(tenant, ct);
    }

    public async Task<PlatformTenantDetailDto> ReactivateAsync(Guid tenantId, TenantActionRequest r, CancellationToken ct = default)
    {
        PlatformAccess.Enter(ctx);
        var (tenant, sub) = await LoadAsync(tenantId, ct);
        if (tenant.Status is TenantStatus.Active or TenantStatus.Trial && sub?.Status is not SubscriptionStatus.PastDue)
            throw new ConflictException("not_suspended", "The tenant is not suspended.");
        if (tenant.Status == TenantStatus.PendingDeletion) throw new ConflictException("pending_deletion", "The tenant is pending deletion and cannot be reactivated.");
        var reason = string.IsNullOrWhiteSpace(r.Reason) ? "reactivated by the platform operator" : r.Reason.Trim();
        lifecycle.Reactivate(db, tenant, sub, reason, db.Clock.UtcNow, ActorType.Platform, ctx.UserId, ctx.ActorName);
        audit.Write(AuditActions.TenantResumed, "Tenant", tenant.Id, $"Reactivated tenant {tenant.Slug}: {reason}", new { reason });
        return await SaveAsync(tenant, ct);
    }

    public async Task<PlatformTenantDetailDto> CancelAsync(Guid tenantId, CancelTenantRequest r, CancellationToken ct = default)
    {
        PlatformAccess.Enter(ctx);
        var reason = Required(r.Reason, "reason");
        if (r.RetentionDays is < 0 or > 365) throw new ValidationException(new Dictionary<string, string> { ["retentionDays"] = "Between 0 and 365 days." });
        var (tenant, sub) = await LoadAsync(tenantId, ct);
        if (tenant.Status is TenantStatus.Cancelled or TenantStatus.PendingDeletion) throw new ConflictException("tenant_cancelled", "The tenant is already cancelled.");
        lifecycle.Cancel(db, tenant, sub, reason, r.RetentionDays, db.Clock.UtcNow, ActorType.Platform, ctx.UserId, ctx.ActorName);
        audit.Write(AuditActions.SubscriptionChanged, "Tenant", tenant.Id,
            $"Cancelled tenant {tenant.Slug}: {reason}; deletion scheduled {tenant.DeletionScheduledAt:yyyy-MM-dd}", new { reason, r.RetentionDays });
        return await SaveAsync(tenant, ct);
    }

    public async Task<PlatformTenantDetailDto> ExtendTrialAsync(Guid tenantId, ExtendTrialRequest r, CancellationToken ct = default)
    {
        PlatformAccess.Enter(ctx);
        if (r.Days is < 1 or > 90) throw new ValidationException(new Dictionary<string, string> { ["days"] = "Extend by 1 to 90 days." });
        var (tenant, sub) = await LoadAsync(tenantId, ct);
        if (sub?.TrialEndsAt is not { } end) throw new ConflictException("not_trial", "This tenant is not on a trial.");
        var now = db.Clock.UtcNow;
        var newEnd = (end > now ? end : now).AddDays(r.Days);
        sub.TrialEndsAt = newEnd;
        if (sub.Status is SubscriptionStatus.PastDue or SubscriptionStatus.Suspended || tenant.Status == TenantStatus.Suspended)
            lifecycle.Reactivate(db, tenant, sub, $"trial extended to {newEnd:yyyy-MM-dd}", now, ActorType.Platform, ctx.UserId, ctx.ActorName);
        CatalogAudit.Write(db, tenant.Id, ActorType.Platform, ctx.UserId, ctx.ActorName, AuditActions.SubscriptionChanged, "Subscription", sub.Id,
            $"Trial extended by {r.Days} days (until {newEnd:yyyy-MM-dd})", new { from = end, to = newEnd }, now);
        audit.Write(AuditActions.SubscriptionChanged, "Tenant", tenant.Id, $"Extended the trial of {tenant.Slug} by {r.Days} days", new { from = end, to = newEnd });
        return await SaveAsync(tenant, ct);
    }

    public async Task<PlatformTenantDetailDto> ChangePlanAsync(Guid tenantId, OperatorChangePlanRequest r, CancellationToken ct = default)
    {
        PlatformAccess.Enter(ctx);
        var (tenant, sub) = await LoadAsync(tenantId, ct);
        if (sub is null) throw new NotFoundException("Subscription");
        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Code == (r.PlanCode ?? "").Trim().ToLower(), ct) ?? throw new NotFoundException("Plan", r.PlanCode);
        var before = new { planId = sub.PlanId, sub.Seats, cycle = sub.BillingCycle.ToString() };
        sub.PlanId = plan.Id;
        sub.PendingPlanId = null;
        if (r.Seats is { } seats)
        {
            if (seats is < 1 or > 100_000) throw new ValidationException(new Dictionary<string, string> { ["seats"] = "Between 1 and 100,000." });
            sub.Seats = seats;
        }
        if (!string.IsNullOrWhiteSpace(r.BillingCycle))
        {
            if (!Enum.TryParse<BillingCycle>(r.BillingCycle, true, out var cycle))
                throw new ValidationException(new Dictionary<string, string> { ["billingCycle"] = "Use Monthly or Annual." });
            sub.BillingCycle = cycle;
        }
        var now = db.Clock.UtcNow;
        CatalogAudit.Write(db, tenant.Id, ActorType.Platform, ctx.UserId, ctx.ActorName, AuditActions.SubscriptionChanged, "Subscription", sub.Id,
            $"Plan changed by the platform operator to {plan.Name} ({sub.Seats} seats, {sub.BillingCycle})", new { before, after = new { planId = plan.Id, sub.Seats } }, now);
        audit.Write(AuditActions.SubscriptionChanged, "Tenant", tenant.Id, $"Changed the plan of {tenant.Slug} to {plan.Name}", new { before, plan = plan.Code });
        return await SaveAsync(tenant, ct);
    }

    private async Task<PlatformTenantDetailDto> SaveAsync(Tenant tenant, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        if (tenant.DatabaseMode == DatabaseMode.Dedicated) await mirror.SyncAsync(tenant.Id, ct);
        return await GetAsync(tenant.Id, ct);
    }

    private async Task<(Tenant, Subscription?)> LoadAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct) ?? throw new NotFoundException("Tenant", tenantId);
        var sub = await db.AllTenants<Subscription>().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        return (tenant, sub);
    }

    private static string Required(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ValidationException(new Dictionary<string, string> { [field] = "Required." });
        return value.Trim().Length > 500 ? value.Trim()[..500] : value.Trim();
    }
}

/// <summary>Plans CRUD (limits, features, prices in decimal + currency).</summary>
public sealed partial class PlatformPlanService(DeskDbContext db, RequestContext ctx, IAuditWriter audit, DedicatedCatalogMirror mirror)
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,39}$")]
    private static partial Regex CodePattern();

    public async Task<List<PlanDto>> ListAsync(CancellationToken ct = default)
    {
        PlatformAccess.Enter(ctx);
        var counts = await db.AllTenants<Subscription>().GroupBy(s => s.PlanId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return (await db.Plans.AsNoTracking().OrderBy(p => p.SortOrder).ThenBy(p => p.Name).ToListAsync(ct))
            .Select(p => PlanCatalog.ToDto(p, counts.GetValueOrDefault(p.Id))).ToList();
    }

    public IReadOnlyList<PlanFeatureDto> Features() => PlanCatalog.Features;

    public async Task<PlanDto> CreateAsync(SavePlanRequest r, CancellationToken ct = default)
    {
        PlatformAccess.Enter(ctx);
        Validate(r);
        var code = r.Code.Trim().ToLowerInvariant();
        if (await db.Plans.AnyAsync(p => p.Code == code, ct)) throw new ConflictException("plan_code_taken", $"A plan with code '{code}' already exists.");
        var plan = new Plan { Code = code };
        Apply(plan, r);
        db.Plans.Add(plan);
        audit.Write("platform.plan_created", "Plan", plan.Id, $"Plan {plan.Name} ({plan.Code}) created", r);
        await db.SaveChangesAsync(ct);
        return PlanCatalog.ToDto(plan);
    }

    public async Task<PlanDto> UpdateAsync(Guid id, SavePlanRequest r, CancellationToken ct = default)
    {
        PlatformAccess.Enter(ctx);
        Validate(r);
        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Plan", id);
        var code = r.Code.Trim().ToLowerInvariant();
        if (code != plan.Code) throw new ValidationException(new Dictionary<string, string> { ["code"] = "The plan code cannot be changed." });
        var before = PlanCatalog.ToDto(plan);
        Apply(plan, r);
        audit.Write("platform.plan_updated", "Plan", plan.Id, $"Plan {plan.Name} ({plan.Code}) updated", new { before, after = r });
        await db.SaveChangesAsync(ct);
        var dedicated = await db.Tenants.Where(t => t.DatabaseMode == DatabaseMode.Dedicated).Select(t => t.Id).ToListAsync(ct);
        foreach (var t in dedicated) await mirror.SyncAsync(t, ct);
        var count = await db.AllTenants<Subscription>().CountAsync(s => s.PlanId == plan.Id, ct);
        return PlanCatalog.ToDto(plan, count);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        PlatformAccess.Enter(ctx);
        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Plan", id);
        if (await db.AllTenants<Subscription>().AnyAsync(s => s.PlanId == id || s.PendingPlanId == id, ct))
            throw new ConflictException("plan_in_use", "Tenants are subscribed to this plan. Hide it from sign-up (not public) instead of deleting it.");
        if (plan.Code == "trial") throw new ConflictException("plan_required", "The trial plan is required for self-service sign-up.");
        db.Plans.Remove(plan);
        audit.Write("platform.plan_deleted", "Plan", plan.Id, $"Plan {plan.Name} ({plan.Code}) deleted", null);
        await db.SaveChangesAsync(ct);
    }

    private static void Validate(SavePlanRequest r)
    {
        var e = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(r.Code) || !CodePattern().IsMatch(r.Code.Trim().ToLowerInvariant()))
            e["code"] = "2–40 lower-case letters, digits or hyphens.";
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 100) e["name"] = "Required (at most 100 characters).";
        if (r.MonthlyPricePerAgent < 0 || r.MonthlyPricePerAgent > 1_000_000) e["monthlyPricePerAgent"] = "Must be between 0 and 1,000,000.";
        if (r.AnnualPricePerAgent < 0 || r.AnnualPricePerAgent > 12_000_000) e["annualPricePerAgent"] = "Must be between 0 and 12,000,000.";
        if (string.IsNullOrWhiteSpace(r.Currency) || r.Currency.Trim().Length != 3 || !r.Currency.Trim().All(char.IsLetter)) e["currency"] = "Use a 3-letter ISO currency code.";
        if (r.TrialDays is < 0 or > 365) e["trialDays"] = "Between 0 and 365.";
        void NonNegative(string key, long? v) { if (v is < 0) e[key] = "Must be 0 or more (leave empty for unlimited)."; }
        NonNegative("maxAgents", r.MaxAgents);
        NonNegative("maxLightAgents", r.MaxLightAgents);
        NonNegative("maxPortalUsers", r.MaxPortalUsers);
        NonNegative("storageMb", r.StorageMb);
        NonNegative("maxChannels", r.MaxChannels);
        NonNegative("aiTokensMonthly", r.AiTokensMonthly);
        NonNegative("maxAttachmentMb", r.MaxAttachmentMb);
        NonNegative("apiCallsMonthly", r.ApiCallsMonthly);
        NonNegative("maxBrands", r.MaxBrands);
        var unknown = (r.Features ?? []).Where(f => !PlanCatalog.IsKnownFeature(f)).ToList();
        if (unknown.Count > 0) e["features"] = $"Unknown feature(s): {string.Join(", ", unknown)}.";
        if (e.Count > 0) throw new ValidationException(e);
    }

    private static void Apply(Plan p, SavePlanRequest r)
    {
        p.Name = r.Name.Trim();
        p.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
        p.MonthlyPricePerAgent = BillingMath.Money(r.MonthlyPricePerAgent);
        p.AnnualPricePerAgent = BillingMath.Money(r.AnnualPricePerAgent);
        p.Currency = r.Currency.Trim().ToUpperInvariant();
        p.IsPublic = r.IsPublic;
        p.TrialDays = r.TrialDays;
        p.MaxAgents = r.MaxAgents;
        p.MaxLightAgents = r.MaxLightAgents;
        p.MaxPortalUsers = r.MaxPortalUsers;
        p.StorageMb = r.StorageMb;
        p.MaxChannels = r.MaxChannels;
        p.AiTokensMonthly = r.AiTokensMonthly;
        p.MaxAttachmentMb = r.MaxAttachmentMb;
        p.ApiCallsMonthly = r.ApiCallsMonthly;
        p.MaxBrands = r.MaxBrands;
        p.FeaturesJson = Json.Serialize((r.Features ?? []).Distinct().ToList());
        p.SortOrder = r.SortOrder;
    }
}

/// <summary>Invoices across tenants: list/filter, detail + PDF, mark paid (manual provider; reactivates on settlement), void.</summary>
public sealed class PlatformInvoiceService(DeskDbContext db, RequestContext ctx, IAuditWriter audit, InvoicePresenter presenter, BillingLifecycle lifecycle,
    DedicatedCatalogMirror mirror)
{
    public async Task<PagedResult<InvoiceSummaryDto>> ListAsync(string? status, Guid? tenantId, string? cursor, int limit, CancellationToken ct = default)
    {
        PlatformAccess.Enter(ctx);
        var q = db.AllTenants<BillingInvoice>().AsQueryable();
        var now = db.Clock.UtcNow;
        if (tenantId is { } t) q = q.Where(i => i.TenantId == t);
        if (string.Equals(status, "Overdue", StringComparison.OrdinalIgnoreCase)) q = q.Where(i => i.Status == InvoiceStatus.Open && i.DueAt < now);
        else if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<InvoiceStatus>(status, true, out var st)) throw new ValidationException(new Dictionary<string, string> { ["status"] = "Unknown status." });
            q = q.Where(i => i.Status == st);
        }
        var names = await db.Tenants.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var page = await InvoicePaging.PageAsync(q, id => names.GetValueOrDefault(id) ?? "?", cursor, limit, now, ct);
        return page with { Total = await q.CountAsync(ct) };
    }

    public async Task<InvoiceDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        PlatformAccess.Enter(ctx);
        var invoice = await db.AllTenants<BillingInvoice>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException("Invoice", id);
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == invoice.TenantId, ct);
        var sub = await db.AllTenants<Subscription>().AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == invoice.TenantId, ct);
        return presenter.Detail(invoice, tenant, sub, db.Clock.UtcNow);
    }

    public async Task<(byte[] Content, string FileName)> PdfAsync(Guid id, CancellationToken ct = default)
    {
        var detail = await GetAsync(id, ct);
        return (presenter.RenderPdf(detail), $"{detail.Summary.Number}.pdf");
    }

    public async Task<InvoiceDto> MarkPaidAsync(Guid id, MarkInvoicePaidRequest r, CancellationToken ct = default)
    {
        PlatformAccess.Enter(ctx);
        var invoice = await db.AllTenants<BillingInvoice>().FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException("Invoice", id);
        if (invoice.Status != InvoiceStatus.Open) throw new ConflictException("invoice_not_open", $"Invoice {invoice.Number} is {invoice.Status}.");
        var now = db.Clock.UtcNow;
        var paidAt = r.PaidAt is { } p ? DateTime.SpecifyKind(p, DateTimeKind.Utc) : now;
        if (paidAt > now.AddMinutes(5)) throw new ValidationException(new Dictionary<string, string> { ["paidAt"] = "The payment date cannot be in the future." });
        invoice.Status = InvoiceStatus.Paid;
        invoice.PaidAt = paidAt;
        invoice.PaymentReference = string.IsNullOrWhiteSpace(r.Reference) ? null : r.Reference.Trim()[..Math.Min(200, r.Reference.Trim().Length)];
        invoice.PaymentProvider ??= ManualPaymentProvider.ProviderKey;
        CatalogAudit.Write(db, invoice.TenantId, ActorType.Platform, ctx.UserId, ctx.ActorName, AuditActions.SubscriptionChanged, "BillingInvoice", invoice.Id,
            $"Invoice {invoice.Number} marked paid ({invoice.Total:N2} {invoice.Currency}){(invoice.PaymentReference is null ? "" : $", reference {invoice.PaymentReference}")}",
            new { invoice.Number, invoice.PaymentReference, paidAt }, now);
        audit.Write("platform.invoice_paid", "BillingInvoice", invoice.Id, $"Marked invoice {invoice.Number} paid", new { invoice.PaymentReference, paidAt });
        await db.SaveChangesAsync(ct);
        if (await lifecycle.ReactivateIfSettledAsync(db, invoice.TenantId, now, ct))
        {
            audit.Write(AuditActions.TenantResumed, "Tenant", invoice.TenantId, "Tenant reactivated after payment", new { invoice.Number });
            await db.SaveChangesAsync(ct);
            var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == invoice.TenantId, ct);
            if (tenant.DatabaseMode == DatabaseMode.Dedicated) await mirror.SyncAsync(tenant.Id, ct);
        }
        return await GetAsync(id, ct);
    }

    public async Task<InvoiceDto> VoidAsync(Guid id, VoidInvoiceRequest r, CancellationToken ct = default)
    {
        PlatformAccess.Enter(ctx);
        var invoice = await db.AllTenants<BillingInvoice>().FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException("Invoice", id);
        if (invoice.Status != InvoiceStatus.Open) throw new ConflictException("invoice_not_open", $"Invoice {invoice.Number} is {invoice.Status}.");
        if (string.IsNullOrWhiteSpace(r.Reason)) throw new ValidationException(new Dictionary<string, string> { ["reason"] = "Required." });
        invoice.Status = InvoiceStatus.Void;
        invoice.Notes = $"Voided: {r.Reason.Trim()}";
        var now = db.Clock.UtcNow;
        CatalogAudit.Write(db, invoice.TenantId, ActorType.Platform, ctx.UserId, ctx.ActorName, AuditActions.SubscriptionChanged, "BillingInvoice", invoice.Id,
            $"Invoice {invoice.Number} voided: {r.Reason.Trim()}", null, now);
        audit.Write("platform.invoice_voided", "BillingInvoice", invoice.Id, $"Voided invoice {invoice.Number}", new { reason = r.Reason });
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }
}

/// <summary>The platform audit trail: platform-level rows plus platform-operator actions recorded in tenant trails.</summary>
public sealed class PlatformAuditService(DeskDbContext db, RequestContext ctx, AuditVerifier verifier)
{
    public static PlatformAuditEntryDto ToDto(AuditLog a, string? tenantName) => new(
        a.Id, a.TenantId, tenantName, a.Sequence, a.OccurredAt, a.ActorType.ToString(), a.ActorName, a.Action, a.EntityType, a.EntityId, a.Summary, a.IpAddress);

    public async Task<PagedResult<PlatformAuditEntryDto>> ListAsync(string? search, string? action, Guid? tenantId, string? cursor, int limit, CancellationToken ct = default)
    {
        PlatformAccess.Enter(ctx);
        limit = Math.Clamp(limit, 1, 200);
        var q = db.AuditLogs.AsNoTracking().Where(a => a.TenantId == null || a.ActorType == ActorType.Platform);
        if (tenantId is { } t) q = q.Where(a => a.TenantId == t || (a.TenantId == null && a.EntityId == t));
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action == action);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            q = q.Where(a => (a.Summary != null && a.Summary.Contains(term)) || (a.ActorName != null && a.ActorName.Contains(term)));
        }
        if (DbExtensions.DecodeCursor(cursor) is { } c)
        {
            var at = DateTime.Parse(c.V, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            q = q.Where(a => a.OccurredAt < at || (a.OccurredAt == at && a.Id.CompareTo(c.Id) < 0));
        }
        var rows = await q.OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id).Take(limit + 1).ToListAsync(ct);
        var names = await db.Tenants.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var next = rows.Count > limit ? DbExtensions.EncodeCursor(rows[limit - 1].OccurredAt.ToString("O", CultureInfo.InvariantCulture), rows[limit - 1].Id) : null;
        return new PagedResult<PlatformAuditEntryDto>(rows.Take(limit)
            .Select(a => ToDto(a, a.TenantId is { } id ? names.GetValueOrDefault(id) : a.EntityId is { } e ? names.GetValueOrDefault(e) : null)).ToList(), next);
    }

    public async Task<List<string>> ActionsAsync(CancellationToken ct = default)
    {
        PlatformAccess.Enter(ctx);
        return await db.AuditLogs.AsNoTracking().Where(a => a.TenantId == null || a.ActorType == ActorType.Platform)
            .Select(a => a.Action).Distinct().OrderBy(a => a).ToListAsync(ct);
    }

    public async Task<AuditVerificationResult> VerifyAsync(CancellationToken ct = default)
    {
        PlatformAccess.Enter(ctx);
        return await verifier.VerifyAsync(null, ct);
    }
}
