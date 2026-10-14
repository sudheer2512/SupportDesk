using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SupportDesk.Application;

/// <summary>Wings ERP / Wings Books, engineering trackers (Azure DevOps, GitHub), outbound/inbound webhooks, integration logs.</summary>
public static class IntegrationModule
{
    public static IServiceCollection AddIntegrationModule(this IServiceCollection s, IConfiguration configuration)
    {
        return s;
    }
}
