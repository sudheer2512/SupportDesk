using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Administration.Compliance;
using SupportDesk.Application.Administration.ConfigTransfer;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Seed;
using SupportDesk.Application.Subscriptions;

namespace SupportDesk.Application;

/// <summary>
/// Plans, subscriptions, usage metering, billing invoices, trials and self-service sign-up, suspension, the platform
/// operator console (tenants, plans, invoices, platform audit, dedicated databases, support access) — plus the tenant
/// compliance features this module owns: audit viewer, retention, legal holds, data-subject requests and configuration
/// transfer (code in Application/Administration/Compliance and Application/Administration/ConfigTransfer).
/// </summary>
public static class SubscriptionsModule
{
    public static IServiceCollection AddSubscriptionsModule(this IServiceCollection s, IConfiguration configuration)
    {
        s.AddSingleton(configuration.GetSection("Billing").Get<BillingOptions>() ?? new BillingOptions());

        // Catalog, billing and payments
        s.AddScoped<CatalogStore>();
        s.AddSingleton<DedicatedCatalogMirror>();
        s.AddSingleton<IPaymentProvider, ManualPaymentProvider>();
        s.AddScoped<PaymentProviderRegistry>();
        s.AddScoped<InvoiceIssuer>();
        s.AddScoped<InvoicePresenter>();
        s.AddScoped<BillingLifecycle>();
        s.AddScoped<SubscriptionViewBuilder>();
        s.AddScoped<SubscriptionService>();
        s.AddScoped<SignupService>();

        // Usage metering (API calls are buffered in memory and flushed by the recurring task)
        s.AddSingleton<ApiCallCounter>();
        s.AddScoped<UsageCollector>();
        s.AddScoped<UsageSnapshotWriter>();
        s.AddScoped<ApiUsageFilter>();
        s.Configure<MvcOptions>(o => o.Filters.AddService<ApiUsageFilter>());

        // Platform operator console
        s.AddScoped<PlatformTenantService>();
        s.AddScoped<PlatformPlanService>();
        s.AddScoped<PlatformInvoiceService>();
        s.AddScoped<PlatformAuditService>();
        s.AddScoped<SupportAccessService>();
        s.AddScoped<DedicatedDatabaseProvisioner>();

        // Compliance: audit viewer, retention, legal holds, data-subject requests
        s.AddScoped<AuditQueryService>();
        s.AddScoped<LegalHoldMatcher>();
        s.AddScoped<LegalHoldService>();
        s.AddScoped<DependentRowPurger>();
        s.AddScoped<RetentionService>();
        s.AddScoped<DataRequestService>();
        s.AddScoped<IJobHandler, DataExportJobHandler>();
        s.AddScoped<IJobHandler, DataErasureJobHandler>();

        // Configuration transfer
        s.AddScoped<ConfigTransferEngine>();
        s.AddScoped<ConfigTransferService>();

        // Recurring tasks
        s.AddScoped<IRecurringTask, UsageMeteringTask>();
        s.AddScoped<IRecurringTask, InvoiceGenerationTask>();
        s.AddScoped<IRecurringTask, SubscriptionLifecycleTask>();
        s.AddScoped<IRecurringTask, RetentionTask>();

        s.AddScoped<IDemoSeedContributor, SubscriptionsDemoSeed>();
        return s;
    }
}
