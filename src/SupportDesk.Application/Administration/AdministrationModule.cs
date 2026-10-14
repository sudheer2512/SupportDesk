using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Administration;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Seed;

namespace SupportDesk.Application;

/// <summary>Users, roles, ticket configuration, layouts, security policies, configuration transfer, compliance, audit.</summary>
public static class AdministrationModule
{
    public static IServiceCollection AddAdministrationModule(this IServiceCollection s, IConfiguration configuration)
    {
        // Shared helpers
        s.AddScoped<AdminSettingsStore>();
        s.AddScoped<AdminChangeNotifier>();
        s.AddScoped<IOutboxHandler, ConfigurationChangedRealtimeHandler>();

        // Roles & permissions
        s.AddScoped<RoleAdminService>();

        // Ticket configuration, custom fields and form layouts
        s.AddScoped<TicketConfigService>();
        s.AddScoped<FieldLayoutService>();

        // Brands & portals
        s.AddScoped<BrandAdminService>();
        s.AddScoped<BrandAssetService>();

        // Notification defaults and e-mail templates (EmailTemplateService is used by the sending modules)
        s.AddScoped<EmailTemplateService>();
        s.AddScoped<NotificationAdminService>();

        // Security: policies, IP allow-list (enforced at sign-in and per request), SSO, sessions
        s.AddScoped<IpAccessPolicy>();
        s.AddScoped<SecurityAdminService>();

        // API credentials
        s.AddScoped<ApiCredentialAdminService>();

        s.AddScoped<IDemoSeedContributor, AdministrationDemoSeed>();
        return s;
    }
}
