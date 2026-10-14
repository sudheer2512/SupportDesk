using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Platform;
using SupportDesk.Domain;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Subscriptions;

/// <summary>
/// Support access instead of impersonation: a platform operator requests time-boxed access with a reason; a tenant
/// administrator (admin.security or admin.compliance) approves or rejects and can revoke at any time. While a grant is
/// active the operator sees read-only diagnostics — never tenant records or a tenant session. Every step is audited in
/// the tenant's and the platform's audit trail.
/// </summary>
public sealed class SupportAccessService(DeskDbContext db, RequestContext ctx, CatalogStore catalog, IAuditWriter audit, TenantScopeRunner runner)
{
    public const string ActionRequested = "support_access.requested";
    public const string ActionApproved = "support_access.approved";
    public const string ActionRejected = "support_access.rejected";
    public const string ActionRevoked = "support_access.revoked";
    public const string ActionUsed = "support_access.used";

    public static SupportAccessDto ToDto(SupportAccessGrant g, string tenantName, DateTime now) => new(
        g.Id, g.TenantId, tenantName, g.OperatorName, g.Reason, g.DurationMinutes, Effective(g, now).ToString(), g.CreatedAt, g.DecidedByName, g.DecidedAt,
        g.DecisionNote, g.ExpiresAt, Effective(g, now) == SupportAccessStatus.Approved);

    private static SupportAccessStatus Effective(SupportAccessGrant g, DateTime now) =>
        g.Status == SupportAccessStatus.Approved && g.ExpiresAt <= now ? SupportAccessStatus.Expired : g.Status;

    public async Task<List<SupportAccessDto>> ForTenantAsync(DeskDbContext source, Guid tenantId, CancellationToken ct)
    {
        var name = await source.Tenants.Where(t => t.Id == tenantId).Select(t => t.Name).FirstOrDefaultAsync(ct) ?? "";
        var now = source.Clock.UtcNow;
        return (await source.AllTenants<SupportAccessGrant>().AsNoTracking().Where(g => g.TenantId == tenantId).OrderByDescending(g => g.CreatedAt).Take(50).ToListAsync(ct))
            .Select(g => ToDto(g, name, now)).ToList();
    }

    // ───── Platform operator side ─────

    public async Task<SupportAccessDto> RequestAsync(Guid tenantId, RequestSupportAccessRequest r, CancellationToken ct = default)
    {
        PlatformAccess.Enter(ctx);
        var reason = r.Reason?.Trim() ?? "";
        var errors = new Dictionary<string, string>();
        if (reason.Length is < 10 or > 1000) errors["reason"] = "Explain why access is needed (10–1000 characters).";
        if (r.DurationMinutes is < 15 or > 24 * 60) errors["durationMinutes"] = "Between 15 minutes and 24 hours.";
        if (errors.Count > 0) throw new ValidationException(errors);
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct) ?? throw new NotFoundException("Tenant", tenantId);
        var now = db.Clock.UtcNow;
        var operatorId = ctx.UserId!.Value;
        if (await db.AllTenants<SupportAccessGrant>().AnyAsync(g => g.TenantId == tenantId && g.OperatorId == operatorId
                && (g.Status == SupportAccessStatus.Pending || (g.Status == SupportAccessStatus.Approved && g.ExpiresAt > now)), ct))
            throw new ConflictException("support_access_open", "You already have a pending or active support-access request for this tenant.");
        var grant = new SupportAccessGrant
        {
            TenantId = tenantId, OperatorId = operatorId, OperatorName = ctx.ActorName ?? "Platform operator", Reason = reason, DurationMinutes = r.DurationMinutes,
            Status = SupportAccessStatus.Pending
        };
        db.Add(grant);
        CatalogAudit.Write(db, tenantId, ActorType.Platform, operatorId, ctx.ActorName, ActionRequested, "SupportAccessGrant", grant.Id,
            $"{grant.OperatorName} requested support access for {r.DurationMinutes} minutes: {reason}", new { reason, r.DurationMinutes }, now, ctx.IpAddress);
        audit.Write(ActionRequested, "SupportAccessGrant", grant.Id, $"Requested support access to {tenant.Slug}", new { tenantId, reason, r.DurationMinutes });
        await db.SaveChangesAsync(ct);
        return ToDto(grant, tenant.Name, now);
    }

    public async Task<List<SupportAccessDto>> OperatorListAsync(Guid? tenantId, CancellationToken ct = default)
    {
        PlatformAccess.Enter(ctx);
        var q = db.AllTenants<SupportAccessGrant>().AsNoTracking();
        if (tenantId is { } t) q = q.Where(g => g.TenantId == t);
        var names = await db.Tenants.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var now = db.Clock.UtcNow;
        return (await q.OrderByDescending(g => g.CreatedAt).Take(200).ToListAsync(ct)).Select(g => ToDto(g, names.GetValueOrDefault(g.TenantId) ?? "?", now)).ToList();
    }

    /// <summary>Read-only diagnostics for an approved, unexpired grant held by the calling operator.</summary>
    public async Task<SupportDiagnosticsDto> DiagnosticsAsync(Guid grantId, CancellationToken ct = default)
    {
        PlatformAccess.Enter(ctx);
        var now = db.Clock.UtcNow;
        var grant = await db.AllTenants<SupportAccessGrant>().AsNoTracking().FirstOrDefaultAsync(g => g.Id == grantId, ct) ?? throw new NotFoundException("Support access", grantId);
        if (grant.OperatorId != ctx.UserId) throw new ForbiddenException("This support-access grant belongs to another operator.");
        if (Effective(grant, now) != SupportAccessStatus.Approved)
            throw new DomainException("support_access_inactive", "Support access is not active (pending, rejected, revoked or expired).", 403);
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == grant.TenantId, ct);
        var sub = await db.AllTenants<Subscription>().AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenant.Id, ct);
        var plan = sub is null ? null : await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == sub.PlanId, ct);

        Dictionary<string, long> usage = [];
        var counts = new Dictionary<string, long>();
        await runner.RunForTenantAsync(tenant.Id, "Support diagnostics", async sp =>
        {
            var tdb = sp.GetRequiredService<DeskDbContext>();
            usage = await sp.GetRequiredService<UsageCollector>().CurrentAsync(ct);
            counts["users"] = await tdb.Users.CountAsync(ct);
            counts["departments"] = await tdb.Departments.CountAsync(ct);
            counts["statuses"] = await tdb.TicketStatuses.CountAsync(ct);
            counts["categories"] = await tdb.TicketCategories.CountAsync(ct);
            counts["slaPolicies"] = await tdb.SlaPolicies.CountAsync(ct);
            counts["automationRules"] = await tdb.AutomationRules.CountAsync(ct);
            counts["channels"] = await tdb.ChannelAccounts.CountAsync(ct);
            counts["openTickets"] = await tdb.Tickets.CountAsync(t => t.StatusCategory < StatusCategory.Resolved, ct);
        }, ct);
        var failedJobs = await db.BackgroundJobs.CountAsync(j => j.TenantId == tenant.Id && (j.Status == JobStatus.Failed || j.Status == JobStatus.DeadLetter), ct);
        var deadEvents = await db.OutboxEvents.CountAsync(e => e.TenantId == tenant.Id && e.Status == OutboxStatus.DeadLetter, ct);
        var recent = await db.AuditLogs.AsNoTracking().Where(a => a.TenantId == tenant.Id).OrderByDescending(a => a.OccurredAt).Take(25).ToListAsync(ct);
        var meters = plan is null
            ? []
            : PlanCatalog.Meters.Select(m => PlanCatalog.Meter(m.Metric, usage.GetValueOrDefault(m.Metric), PlanCatalog.EffectiveLimit(plan, sub, m.Metric))).ToList();

        CatalogAudit.Write(db, tenant.Id, ActorType.Platform, ctx.UserId, ctx.ActorName, ActionUsed, "SupportAccessGrant", grant.Id,
            $"{ctx.ActorName} viewed support diagnostics", null, now, ctx.IpAddress);
        audit.Write(ActionUsed, "SupportAccessGrant", grant.Id, $"Viewed support diagnostics of {tenant.Slug}", null);
        await db.SaveChangesAsync(ct);
        return new SupportDiagnosticsDto(tenant.Id, tenant.Name, grant.ExpiresAt!.Value, meters, counts,
            recent.Select(a => PlatformAuditService.ToDto(a, tenant.Name)).ToList(), failedJobs, deadEvents);
    }

    // ───── Tenant administrator side ─────

    public async Task<List<SupportAccessDto>> TenantListAsync(CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.AdminSecurity, Permissions.AdminCompliance);
        return await ForTenantAsync(catalog.Db, ctx.TenantId, ct);
    }

    public async Task<SupportAccessDto> DecideAsync(Guid id, DecideSupportAccessRequest r, CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.AdminSecurity, Permissions.AdminCompliance);
        var cdb = catalog.Db;
        var grant = await cdb.AllTenants<SupportAccessGrant>().FirstOrDefaultAsync(g => g.Id == id && g.TenantId == ctx.TenantId, ct)
                    ?? throw new NotFoundException("Support access request", id);
        if (grant.Status != SupportAccessStatus.Pending) throw new ConflictException("support_access_decided", "This request was already decided.");
        var now = db.Clock.UtcNow;
        grant.Status = r.Approve ? SupportAccessStatus.Approved : SupportAccessStatus.Rejected;
        grant.DecidedAt = now;
        grant.DecidedById = ctx.UserId;
        grant.DecidedByName = ctx.ActorName;
        grant.DecisionNote = string.IsNullOrWhiteSpace(r.Note) ? null : r.Note.Trim()[..Math.Min(500, r.Note.Trim().Length)];
        grant.ExpiresAt = r.Approve ? now.AddMinutes(grant.DurationMinutes) : null;
        var action = r.Approve ? ActionApproved : ActionRejected;
        audit.Write(action, "SupportAccessGrant", grant.Id,
            r.Approve ? $"Approved support access for {grant.OperatorName} until {grant.ExpiresAt:yyyy-MM-dd HH:mm} UTC" : $"Rejected support access for {grant.OperatorName}",
            new { grant.OperatorName, grant.Reason, grant.ExpiresAt, note = grant.DecisionNote });
        CatalogAudit.Write(cdb, null, ActorType.User, ctx.UserId, ctx.ActorName, action, "SupportAccessGrant", grant.Id,
            $"Tenant {ctx.TenantSlug ?? ctx.TenantId.ToString()} {(r.Approve ? "approved" : "rejected")} support access for {grant.OperatorName}", null, now);
        await cdb.SaveChangesAsync(ct);
        await db.SaveChangesAsync(ct);
        return ToDto(grant, await TenantNameAsync(ct), now);
    }

    public async Task<SupportAccessDto> RevokeAsync(Guid id, CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.AdminSecurity, Permissions.AdminCompliance);
        var cdb = catalog.Db;
        var grant = await cdb.AllTenants<SupportAccessGrant>().FirstOrDefaultAsync(g => g.Id == id && g.TenantId == ctx.TenantId, ct)
                    ?? throw new NotFoundException("Support access request", id);
        var now = db.Clock.UtcNow;
        if (Effective(grant, now) is not (SupportAccessStatus.Approved or SupportAccessStatus.Pending))
            throw new ConflictException("support_access_inactive", "This access is no longer active.");
        grant.Status = SupportAccessStatus.Revoked;
        grant.ExpiresAt = grant.ExpiresAt is null ? null : now;
        audit.Write(ActionRevoked, "SupportAccessGrant", grant.Id, $"Revoked support access for {grant.OperatorName}", null);
        CatalogAudit.Write(cdb, null, ActorType.User, ctx.UserId, ctx.ActorName, ActionRevoked, "SupportAccessGrant", grant.Id,
            $"Tenant {ctx.TenantSlug ?? ctx.TenantId.ToString()} revoked support access for {grant.OperatorName}", null, now);
        await cdb.SaveChangesAsync(ct);
        await db.SaveChangesAsync(ct);
        return ToDto(grant, await TenantNameAsync(ct), now);
    }

    private async Task<string> TenantNameAsync(CancellationToken ct) =>
        await catalog.Db.Tenants.Where(t => t.Id == ctx.TenantId).Select(t => t.Name).FirstOrDefaultAsync(ct) ?? "";
}
