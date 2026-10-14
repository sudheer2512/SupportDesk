using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SupportDesk.Application;

/// <summary>Customer portal: branded configuration, scoped tickets, account (contracts, installations), feedback.</summary>
public static class PortalModule
{
    public static IServiceCollection AddPortalModule(this IServiceCollection s, IConfiguration configuration)
    {
        return s;
    }
}
