using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SupportDesk.Integrations;

/// <summary>Wings ERP / Wings Books connectors (HTTP API) and the local Wings ERP simulator for development.</summary>
public static class ErpAdapters
{
    public static IServiceCollection AddErpAdapters(this IServiceCollection s, IConfiguration configuration)
    {
        return s;
    }
}
