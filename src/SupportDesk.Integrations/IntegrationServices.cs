using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Platform;
using SupportDesk.Integrations.Platform;

namespace SupportDesk.Integrations;

public static class IntegrationServices
{
    /// <summary>
    /// Registers infrastructure adapters: storage, malware scanning, system mail, distributed cache (Redis when
    /// Redis:Configuration is set, in-memory otherwise) and every external-system adapter module.
    /// </summary>
    public static IServiceCollection AddDeskIntegrations(this IServiceCollection s, IConfiguration configuration)
    {
        s.AddSingleton<IFileStorage, LocalFileStorage>();
        s.AddSingleton<IMalwareScanner, SignatureMalwareScanner>();
        s.AddSingleton<ISystemMailer, SystemMailer>();

        var redis = configuration["Redis:Configuration"];
        if (!string.IsNullOrWhiteSpace(redis))
            s.AddStackExchangeRedisCache(o =>
            {
                o.Configuration = redis;
                o.InstanceName = configuration["Redis:InstanceName"] ?? "supportdesk:";
            });
        else
            s.AddDistributedMemoryCache();

        s.AddEmailAdapters(configuration);
        s.AddMessagingAdapters(configuration);
        s.AddAiProviders(configuration);
        s.AddErpAdapters(configuration);
        s.AddEngineeringAdapters(configuration);
        return s;
    }
}
