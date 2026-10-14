using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Domain;
using SupportDesk.Domain.Platform;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Platform;

/// <summary>Effective limits for the current tenant (plan + per-tenant overrides). Null = unlimited.</summary>
public sealed record PlanLimits(
    string PlanCode,
    string PlanName,
    SubscriptionStatus Status,
    TenantStatus TenantStatus,
    int? MaxAgents,
    int? MaxPortalUsers,
    long? StorageMb,
    int? MaxChannels,
    long? AiTokensMonthly,
    int? MaxAttachmentMb,
    long? ApiCallsMonthly,
    int? MaxBrands,
    IReadOnlyList<string> Features,
    int? MaxLightAgents = null)
{
    public long? LimitFor(string metric) => metric switch
    {
        UsageMetrics.Agents => MaxAgents,
        UsageMetrics.LightAgents => MaxLightAgents,
        UsageMetrics.PortalUsers => MaxPortalUsers,
        UsageMetrics.StorageBytes => StorageMb * 1024 * 1024,
        UsageMetrics.Channels => MaxChannels,
        UsageMetrics.AiTokens => AiTokensMonthly,
        UsageMetrics.ApiCalls => ApiCallsMonthly,
        _ => null
    };
}

/// <summary>Atomic monthly usage counters per tenant.</summary>
public sealed class UsageMeter(DeskDbContext db)
{
    public static DateTime PeriodOf(DateTime utc) => new(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Gauges (agents, storage, channels) are tracked in a fixed period so they are not reset monthly.</summary>
    public static bool IsGauge(string metric) => metric is UsageMetrics.Agents or UsageMetrics.StorageBytes or UsageMetrics.Channels or UsageMetrics.PortalUsers
        or UsageMetrics.LightAgents or UsageMetrics.Attachments;

    private DateTime PeriodFor(string metric) => IsGauge(metric) ? new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc) : PeriodOf(db.Clock.UtcNow);

    public async Task IncrementAsync(string metric, long by = 1, CancellationToken ct = default)
    {
        var tenant = db.Request.TenantId;
        if (tenant == Guid.Empty || by == 0) return;
        await db.Database.ExecuteSqlRawAsync("""
            MERGE dbo.UsageRecords WITH (HOLDLOCK) AS t
            USING (SELECT @tenant AS TenantId, @metric AS Metric, @period AS PeriodStart) AS s
            ON t.TenantId = s.TenantId AND t.Metric = s.Metric AND t.PeriodStart = s.PeriodStart
            WHEN MATCHED THEN UPDATE SET Quantity = t.Quantity + @by, UpdatedAt = @now
            WHEN NOT MATCHED THEN INSERT (Id, TenantId, Metric, PeriodStart, Quantity, UpdatedAt) VALUES (@id, @tenant, @metric, @period, @by, @now);
            """,
            [
                new SqlParameter("@tenant", tenant), new SqlParameter("@metric", metric), new SqlParameter("@period", PeriodFor(metric)),
                new SqlParameter("@by", by), new SqlParameter("@now", db.Clock.UtcNow), new SqlParameter("@id", SequentialGuid.New())
            ], ct);
    }

    public async Task SetGaugeAsync(string metric, long value, CancellationToken ct = default)
    {
        var current = await GetAsync(metric, ct);
        if (current != value) await IncrementAsync(metric, value - current, ct);
    }

    public async Task<long> GetAsync(string metric, CancellationToken ct = default)
    {
        var period = PeriodFor(metric);
        return await db.UsageRecords.Where(u => u.Metric == metric && u.PeriodStart == period).Select(u => (long?)u.Quantity).FirstOrDefaultAsync(ct) ?? 0;
    }

    public async Task<Dictionary<string, long>> SnapshotAsync(CancellationToken ct = default)
    {
        var month = PeriodOf(db.Clock.UtcNow);
        var gaugePeriod = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return await db.UsageRecords.Where(u => u.PeriodStart == month || u.PeriodStart == gaugePeriod)
            .ToDictionaryAsync(u => u.Metric, u => u.Quantity, ct);
    }
}

/// <summary>Enforces subscription state and plan limits. Called by services before creating limited resources.</summary>
public sealed class LimitGuard(DeskDbContext db, UsageMeter usage)
{
    private PlanLimits? _limits;

    public async Task<PlanLimits> GetLimitsAsync(CancellationToken ct = default)
    {
        if (_limits is not null) return _limits;
        var tenantId = db.Request.TenantId;
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct)
                     ?? throw new NotFoundException("Tenant");
        var sub = await db.Subscriptions.AsNoTracking().OrderByDescending(s => s.StartedAt).FirstOrDefaultAsync(ct);
        var plan = sub is null ? null : await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == sub.PlanId, ct);
        var overrides = Json.Object(sub?.LimitOverridesJson);
        long? L(string key, long? planValue) => overrides.TryGetValue(key, out var v) && v.TryGetInt64(out var n) ? n : planValue;
        _limits = new PlanLimits(
            plan?.Code ?? "none", plan?.Name ?? "No plan", sub?.Status ?? SubscriptionStatus.Active, tenant.Status,
            (int?)L("MaxAgents", plan?.MaxAgents), (int?)L("MaxPortalUsers", plan?.MaxPortalUsers), L("StorageMb", plan?.StorageMb),
            (int?)L("MaxChannels", plan?.MaxChannels), L("AiTokensMonthly", plan?.AiTokensMonthly), (int?)L("MaxAttachmentMb", plan?.MaxAttachmentMb),
            L("ApiCallsMonthly", plan?.ApiCallsMonthly), (int?)L("MaxBrands", plan?.MaxBrands),
            Json.StringList(plan?.FeaturesJson), (int?)L("MaxLightAgents", plan?.MaxLightAgents));
        return _limits;
    }

    /// <summary>Throws when the tenant is suspended/cancelled (writes are blocked; billing pages stay available).</summary>
    public async Task EnsureActiveAsync(CancellationToken ct = default)
    {
        var l = await GetLimitsAsync(ct);
        if (l.TenantStatus is TenantStatus.Suspended or TenantStatus.Cancelled or TenantStatus.PendingDeletion ||
            l.Status is SubscriptionStatus.Suspended or SubscriptionStatus.Cancelled)
            throw new DomainException("tenant_suspended", "This account is suspended. Contact your administrator to restore service.", 402);
    }

    public async Task EnsureWithinAsync(string metric, long increment = 1, CancellationToken ct = default)
    {
        await EnsureActiveAsync(ct);
        var limits = await GetLimitsAsync(ct);
        var limit = limits.LimitFor(metric);
        if (limit is null) return;
        var current = await usage.GetAsync(metric, ct);
        if (current + increment > limit)
            throw new LimitExceededException(metric, $"Your plan ({limits.PlanName}) allows {limit:N0} for '{metric}'. Current usage is {current:N0}. Upgrade the plan or remove unused items.");
    }

    public async Task<bool> HasFeatureAsync(string feature, CancellationToken ct = default) =>
        (await GetLimitsAsync(ct)).Features.Contains(feature, StringComparer.OrdinalIgnoreCase);

    public async Task RequireFeatureAsync(string feature, CancellationToken ct = default)
    {
        if (!await HasFeatureAsync(feature, ct))
            throw new DomainException("feature_not_in_plan", $"The '{feature}' feature is not included in your plan.", 402);
    }
}

public static class PlanFeatures
{
    public const string Automation = "automation";
    public const string Blueprints = "blueprints";
    public const string Ai = "ai";
    public const string AiAgents = "ai_agents";
    public const string Sso = "sso";
    public const string AdvancedAnalytics = "advanced_analytics";
    public const string MultiBrand = "multi_brand";
    public const string Community = "community";
    public const string Telephony = "telephony";
    public const string ErpIntegration = "erp_integration";
    public const string EngineeringIntegration = "engineering_integration";
    public const string DedicatedDatabase = "dedicated_database";
    public const string AuditExport = "audit_export";
    public const string CustomRoles = "custom_roles";
}
