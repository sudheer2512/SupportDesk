using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SupportDesk.Application;

/// <summary>Ticket/agent metrics, dashboards, reports, exports, scheduled reports, CSAT.</summary>
public static class AnalyticsModule
{
    public static IServiceCollection AddAnalyticsModule(this IServiceCollection s, IConfiguration configuration)
    {
        return s;
    }
}
