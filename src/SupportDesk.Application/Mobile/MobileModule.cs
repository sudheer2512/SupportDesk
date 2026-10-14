using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Mobile;
using SupportDesk.Application.Platform;

namespace SupportDesk.Application;

/// <summary>
/// Mobile app backend: push-device registration (agents and portal contacts), the per-device push fan-out used by the
/// notification dispatcher, and customer push for public replies / resolution. Push providers (FCM HTTP v1, APNs) are
/// adapters in SupportDesk.Integrations behind <see cref="Communication.IPushSender"/>.
/// </summary>
public static class MobileModule
{
    public static IServiceCollection AddMobileModule(this IServiceCollection s, IConfiguration configuration)
    {
        s.AddScoped<PushDeviceService>();
        s.AddScoped<PushFanout>();
        s.AddScoped<IOutboxHandler, ContactPushHandler>();
        return s;
    }
}
