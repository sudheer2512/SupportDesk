using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SupportDesk.Application.Approvals;
using SupportDesk.Application.Customers;
using SupportDesk.Application.Identity;
using SupportDesk.Application.Notifications;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Realtime;
using SupportDesk.Application.Routing;
using SupportDesk.Application.Seed;
using SupportDesk.Application.Service;
using SupportDesk.Application.Sla;
using SupportDesk.Application.Tenancy;
using SupportDesk.Application.Tickets;

namespace SupportDesk.Application;

public static class ApplicationServices
{
    /// <summary>Registers the application kernel and every functional module.</summary>
    public static IServiceCollection AddDeskApplication(this IServiceCollection s, IConfiguration configuration)
    {
        // Platform kernel
        s.TryAddSingleton<IRealtimePublisher, NullRealtimePublisher>();
        s.AddSingleton<HtmlSanitization>();
        s.AddSingleton<PasswordService>();
        s.AddSingleton<ISecretProtector, AesGcmSecretProtector>();
        s.AddScoped<ITenantCache, TenantCache>();
        s.AddScoped<IEventPublisher, EventPublisher>();
        s.AddScoped<IJobQueue, JobQueue>();
        s.AddScoped<IAuditWriter, AuditWriter>();
        s.AddScoped<AuditVerifier>();
        s.AddScoped<TenantSettingsService>();
        s.AddScoped<UsageMeter>();
        s.AddScoped<LimitGuard>();
        s.AddScoped<FileService>();
        s.AddSingleton<TenantScopeRunner>();
        s.AddScoped<IRecurringTask, AuditSealerTask>();

        // Identity & tenancy
        s.AddSingleton<SigningKeyProvider>();
        s.AddSingleton<TokenService>();
        s.AddScoped<PrincipalLoader>();
        s.AddScoped<AuthService>();
        s.AddScoped<PortalAuthService>();
        s.AddScoped<PlatformAuthService>();
        s.AddScoped<ApiKeyService>();
        s.AddScoped<OAuthClientService>();
        s.AddScoped<SsoService>();
        s.AddHttpClient("sso");
        s.AddScoped<PlatformCatalogService>();
        s.AddScoped<TenantProvisioningService>();
        s.AddScoped<TenantDatabaseDirectoryAccessor>();

        // Tickets core
        s.AddScoped<TicketLookups>();
        s.AddScoped<TicketHistory>();
        s.AddScoped<CustomFieldValidator>();
        s.AddScoped<ContactResolver>();
        s.AddScoped<TicketService>();
        s.AddScoped<ConversationService>();
        s.AddScoped<TicketQueryService>();
        s.AddScoped<TicketCollaborationService>();
        s.AddScoped<MetaService>();
        s.AddScoped<ISlaEngine, SlaEngine>();
        s.AddScoped<IRoutingService, RoutingService>();
        s.AddScoped<IEntitlementService, EntitlementService>();
        s.AddScoped<SlaMonitorProcessor>();
        s.AddScoped<IRecurringTask, SlaMonitorTask>();
        s.AddScoped<TicketHousekeepingProcessor>();
        s.AddScoped<IRecurringTask, TicketHousekeepingTask>();
        s.AddScoped<ApprovalService>();
        s.AddScoped<IApprovalAction, TransferApprovalAction>();

        // Notifications & realtime
        s.AddScoped<NotificationService>();
        s.AddScoped<IOutboxHandler, NotificationRulesHandler>();
        s.AddScoped<IOutboxHandler, RealtimeOutboxHandler>();

        // Demo data
        s.AddScoped<DemoSeeder>();

        // Functional modules (each module owns its registration file)
        s.AddCustomersModule(configuration);
        s.AddOrganisationModule(configuration);
        s.AddCommunicationModule(configuration);
        s.AddAutomationModule(configuration);
        s.AddKnowledgeModule(configuration);
        s.AddPortalModule(configuration);
        s.AddAnalyticsModule(configuration);
        s.AddAiModule(configuration);
        s.AddAdministrationModule(configuration);
        s.AddSubscriptionsModule(configuration);
        s.AddIntegrationModule(configuration);
        return s;
    }
}
