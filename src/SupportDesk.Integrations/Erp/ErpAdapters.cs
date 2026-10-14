using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Integrations.Erp;
using SupportDesk.Domain;
using SupportDesk.Integrations.Erp;
using SupportDesk.Persistence;

namespace SupportDesk.Integrations;

/// <summary>Wings ERP / Wings Books connectors (HTTP API) and the local Wings ERP simulator for development.</summary>
public static class ErpAdapters
{
    public static IServiceCollection AddErpAdapters(this IServiceCollection s, IConfiguration configuration)
    {
        s.AddHttpClient(WingsErpHttpConnector.ClientName, c =>
            {
                c.DefaultRequestHeaders.UserAgent.ParseAdd("SupportDesk-ErpConnector/1.0");
                c.Timeout = Timeout.InfiniteTimeSpan; // the resilience pipeline and per-integration timeouts govern
            })
            .AddStandardResilienceHandler(o =>
            {
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(90);
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
                o.Retry.MaxRetryAttempts = 2;
                // Record updates (PATCH/POST) are not retried automatically: the ERP may have applied them.
                o.Retry.DisableForUnsafeHttpMethods();
            });
        s.AddSingleton<ErpTokenCache>();
        s.AddScoped<IErpConnectorFactory, ErpConnectorFactory>();
        return s;
    }
}
