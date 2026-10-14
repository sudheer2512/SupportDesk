using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SupportDesk.Application;

/// <summary>Plans, subscriptions, usage, billing invoices, trials, suspension and the platform operator console.</summary>
public static class SubscriptionsModule
{
    public static IServiceCollection AddSubscriptionsModule(this IServiceCollection s, IConfiguration configuration)
    {
        return s;
    }
}
