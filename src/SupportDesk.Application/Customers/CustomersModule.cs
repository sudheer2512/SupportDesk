using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SupportDesk.Application;

/// <summary>Customers, contacts, locations, groups, Customer 360, products, installations, assets, contracts and entitlements.</summary>
public static class CustomersModule
{
    public static IServiceCollection AddCustomersModule(this IServiceCollection s, IConfiguration configuration)
    {
        return s;
    }
}
