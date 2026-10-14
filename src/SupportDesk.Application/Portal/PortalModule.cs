using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Portal;

namespace SupportDesk.Application;

/// <summary>Customer portal: branded configuration, scoped tickets, account (contracts, installations), feedback.</summary>
public static class PortalModule
{
    public static IServiceCollection AddPortalModule(this IServiceCollection s, IConfiguration configuration)
    {
        s.AddScoped<PortalContext>();
        s.AddScoped<PortalKnowledgeService>();
        s.AddScoped<PortalTicketService>();
        s.AddScoped<PortalAccountService>();
        s.AddScoped<PortalSubmissionService>();
        s.AddScoped<PortalAssistantService>();
        return s;
    }
}
