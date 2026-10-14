using Microsoft.Extensions.Configuration;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Contracts.Subscriptions;
using SupportDesk.Domain;
using SupportDesk.Domain.Platform;

namespace SupportDesk.Application.Subscriptions;

/// <summary>Pure billing rules: period arithmetic, per-seat pricing and pro-rata charges (unit-tested).</summary>
public static class BillingMath
{
    public static DateTime PeriodEnd(DateTime start, BillingCycle cycle) => cycle == BillingCycle.Annual ? start.AddYears(1) : start.AddMonths(1);

    /// <summary>Price of one seat for one full period of the cycle.</summary>
    public static decimal SeatPrice(Plan plan, BillingCycle cycle) => cycle == BillingCycle.Annual ? plan.AnnualPricePerAgent : plan.MonthlyPricePerAgent;

    public static decimal PeriodAmount(Plan plan, BillingCycle cycle, long seats) => Money(SeatPrice(plan, cycle) * Math.Max(0, seats));

    /// <summary>Fraction of the period that remains at <paramref name="at"/> (0…1).</summary>
    public static decimal RemainingFraction(DateTime periodStart, DateTime periodEnd, DateTime at)
    {
        var total = (periodEnd - periodStart).TotalSeconds;
        if (total <= 0) return 0;
        var remaining = (periodEnd - at).TotalSeconds;
        return (decimal)Math.Clamp(remaining / total, 0, 1);
    }

    /// <summary>
    /// Pro-rata charge for switching from one plan/seat count to another mid-period: the difference of the two period
    /// amounts times the remaining fraction. Never negative — downgrades take effect at the end of the period instead.
    /// </summary>
    public static decimal ProratedCharge(Plan from, long fromSeats, Plan to, long toSeats, BillingCycle cycle, DateTime periodStart, DateTime periodEnd, DateTime at)
    {
        var diff = PeriodAmount(to, cycle, toSeats) - PeriodAmount(from, cycle, fromSeats);
        if (diff <= 0) return 0;
        return Money(diff * RemainingFraction(periodStart, periodEnd, at));
    }

    /// <summary>Upgrade = higher per-period price (or more seats on the same plan); downgrade = lower; same otherwise.</summary>
    public static string Direction(Plan from, long fromSeats, Plan to, long toSeats, BillingCycle cycle)
    {
        var a = PeriodAmount(from, cycle, fromSeats);
        var b = PeriodAmount(to, cycle, toSeats);
        if (from.Id == to.Id && fromSeats == toSeats) return "same";
        if (b > a || (b == a && to.SortOrder > from.SortOrder)) return "upgrade";
        return b < a || to.SortOrder < from.SortOrder ? "downgrade" : "upgrade";
    }

    public static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

/// <summary>Plan feature catalog (labels for the plan comparison) and DTO mapping.</summary>
public static class PlanCatalog
{
    public static readonly IReadOnlyList<PlanFeatureDto> Features =
    [
        new(PlanFeatures.Automation, "Automation rules"),
        new(PlanFeatures.Blueprints, "Blueprint workflows"),
        new(PlanFeatures.Ai, "AI assistant"),
        new(PlanFeatures.AiAgents, "Autonomous AI agents"),
        new(PlanFeatures.Sso, "Single sign-on"),
        new(PlanFeatures.AdvancedAnalytics, "Advanced analytics"),
        new(PlanFeatures.MultiBrand, "Multiple brands"),
        new(PlanFeatures.Community, "Community forums"),
        new(PlanFeatures.Telephony, "Telephony"),
        new(PlanFeatures.ErpIntegration, "ERP integration"),
        new(PlanFeatures.EngineeringIntegration, "Engineering integration"),
        new(PlanFeatures.AuditExport, "Audit export"),
        new(PlanFeatures.CustomRoles, "Custom roles"),
        new(PlanFeatures.DedicatedDatabase, "Dedicated database"),
    ];

    public static bool IsKnownFeature(string key) => Features.Any(f => f.Key == key);

    public static PlanDto ToDto(Plan p, int tenantCount = 0) => new(
        p.Id, p.Code, p.Name, p.Description, p.MonthlyPricePerAgent, p.AnnualPricePerAgent, p.Currency, p.IsPublic, p.TrialDays,
        p.MaxAgents, p.MaxLightAgents, p.MaxPortalUsers, p.StorageMb, p.MaxChannels, p.AiTokensMonthly, p.MaxAttachmentMb, p.ApiCallsMonthly, p.MaxBrands,
        Json.StringList(p.FeaturesJson), p.SortOrder, tenantCount);

    /// <summary>The metered dimensions shown on the subscription page, with their plan limit.</summary>
    public static IReadOnlyList<(string Metric, string Label, string Unit, Func<Plan, long?> Limit)> Meters { get; } =
    [
        (UsageMetrics.Agents, "Agents", "count", p => p.MaxAgents),
        (UsageMetrics.LightAgents, "Light agents", "count", p => p.MaxLightAgents),
        (UsageMetrics.PortalUsers, "Portal users", "count", p => p.MaxPortalUsers),
        (UsageMetrics.StorageBytes, "Storage", "bytes", p => p.StorageMb * 1024 * 1024),
        (UsageMetrics.Channels, "Channels", "count", p => p.MaxChannels),
        (UsageMetrics.AiTokens, "AI tokens (this month)", "count", p => p.AiTokensMonthly),
        (UsageMetrics.ApiCalls, "API calls (this month)", "count", p => p.ApiCallsMonthly),
        (UsageMetrics.Attachments, "Attachments", "count", _ => null),
    ];

    public static string LabelOf(string metric) => Meters.FirstOrDefault(m => m.Metric == metric).Label ?? metric;

    public static string UnitOf(string metric) => Meters.FirstOrDefault(m => m.Metric == metric).Unit ?? "count";

    /// <summary>Applies per-tenant overrides (Subscription.LimitOverridesJson) on top of the plan limit.</summary>
    public static long? EffectiveLimit(Plan plan, Subscription? sub, string metric)
    {
        var meter = Meters.FirstOrDefault(m => m.Metric == metric);
        var limit = meter.Limit?.Invoke(plan);
        var overrides = Json.Object(sub?.LimitOverridesJson);
        var key = metric switch
        {
            UsageMetrics.Agents => "MaxAgents",
            UsageMetrics.LightAgents => "MaxLightAgents",
            UsageMetrics.PortalUsers => "MaxPortalUsers",
            UsageMetrics.StorageBytes => "StorageMb",
            UsageMetrics.Channels => "MaxChannels",
            UsageMetrics.AiTokens => "AiTokensMonthly",
            UsageMetrics.ApiCalls => "ApiCallsMonthly",
            _ => null
        };
        if (key is not null && overrides.TryGetValue(key, out var v) && v.TryGetInt64(out var n))
            return metric == UsageMetrics.StorageBytes ? n * 1024 * 1024 : n;
        return limit;
    }

    public static UsageMeterDto Meter(string metric, long used, long? limit)
    {
        double? percent = limit is > 0 ? Math.Round(used * 100.0 / limit.Value, 1) : limit == 0 ? (used > 0 ? 100 : 0) : null;
        var state = limit is null ? "unlimited" : used > limit ? "exceeded" : percent >= 80 ? "warning" : "ok";
        return new UsageMeterDto(metric, LabelOf(metric), used, limit, UnitOf(metric), percent, state);
    }
}

/// <summary>
/// Collects payments for invoices. SupportDesk never stores card data: online providers hand off to a hosted checkout,
/// the built-in "manual" provider is offline (bank transfer / cheque) and invoices are marked paid by the platform operator.
/// </summary>
public interface IPaymentProvider
{
    string Key { get; }
    string DisplayName { get; }
    /// <summary>True when the provider confirms payments itself (webhook); false when an operator marks invoices paid.</summary>
    bool AutomaticConfirmation { get; }
    string Instructions(BillingInvoice? invoice, string tenantName);
}

public sealed class ManualPaymentProvider(IConfiguration configuration) : IPaymentProvider
{
    public const string ProviderKey = "manual";
    public string Key => ProviderKey;
    public string DisplayName => "Bank transfer (offline)";
    public bool AutomaticConfirmation => false;

    public string Instructions(BillingInvoice? invoice, string tenantName)
    {
        var bank = configuration["Billing:Manual:Instructions"] ?? "Pay by bank transfer to the account on your contract.";
        return invoice is null
            ? $"{bank} Quote the invoice number as the payment reference; your account is updated once the payment is received."
            : $"{bank} Quote reference {invoice.Number} ({tenantName}). The invoice is marked paid once the transfer is received.";
    }
}

/// <summary>Resolves the provider configured for a subscription (falls back to the manual provider).</summary>
public sealed class PaymentProviderRegistry(IEnumerable<IPaymentProvider> providers)
{
    public IPaymentProvider For(string? key) =>
        providers.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))
        ?? providers.First(p => p.Key == ManualPaymentProvider.ProviderKey);

    public IReadOnlyList<IPaymentProvider> All => providers.ToList();
}

/// <summary>Billing options (grace period, tax, payment terms).</summary>
public sealed class BillingOptions
{
    public int GraceDays { get; set; } = 7;
    public int PaymentTermDays { get; set; } = 14;
    /// <summary>Tax rate as a fraction (0.18 = 18 %).</summary>
    public decimal TaxRate { get; set; }
    public string InvoicePrefix { get; set; } = "SD";
    public string IssuerName { get; set; } = "SupportDesk Cloud";
    public string IssuerAddress { get; set; } = "";
}

internal static class SubscriptionGuards
{
    public static void Require(bool condition, string code, string message, int status = 422)
    {
        if (!condition) throw new DomainException(code, message, status);
    }
}
