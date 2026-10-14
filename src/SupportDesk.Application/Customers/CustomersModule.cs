using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SupportDesk.Application.Customers;
using SupportDesk.Application.Integrations.Erp;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Products;
using SupportDesk.Application.Seed;
using SupportDesk.Application.Service;
using SupportDesk.Application.Tickets;

namespace SupportDesk.Application;

/// <summary>Customers, contacts, locations, groups, Customer 360, products, installations, assets, contracts and entitlements.</summary>
public static class CustomersModule
{
    public static IServiceCollection AddCustomersModule(this IServiceCollection s, IConfiguration configuration)
    {
        // Customers & contacts
        s.AddScoped<CustomerService>();
        s.AddScoped<ContactService>();
        s.AddScoped<ContactMergeService>();
        s.AddScoped<CustomerMergeService>();
        s.AddScoped<ServiceActivityService>();
        s.AddScoped<Customer360Service>();

        // Products & installed base
        s.AddScoped<ProductCatalogService>();
        s.AddScoped<InstallationService>();

        // Contracts & entitlements
        s.AddScoped<EntitlementService>();
        s.AddScoped<ContractService>();
        s.AddScoped<ContractLifecycleProcessor>();
        s.AddScoped<IRecurringTask, ContractLifecycleTask>();
        s.AddScoped<IJobHandler, ContractEligibilityJobHandler>();

        // Wings ERP / Wings Books integration (adapters are registered by SupportDesk.Integrations; null factory otherwise)
        s.TryAddScoped<IErpConnectorFactory, NullErpConnectorFactory>();
        s.AddSingleton<IErpCallLog, ErpCallLog>();
        s.AddScoped<ErpConnectorResolver>();
        s.AddScoped<ErpIntegrationService>();
        s.AddScoped<ErpSyncService>();
        s.AddScoped<ErpTicketService>();
        s.AddScoped<IErpRecordUpdater, ErpRecordUpdater>();
        s.AddScoped<ITicketContextContributor, ErpTicketContextContributor>();
        s.AddScoped<IRecurringTask, ErpSyncTask>();

        // Demo data
        s.AddScoped<IDemoSeedContributor, CustomersDemoSeedContributor>();
        return s;
    }
}
