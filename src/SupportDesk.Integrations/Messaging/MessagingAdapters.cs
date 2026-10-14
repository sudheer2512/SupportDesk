using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SupportDesk.Integrations;

/// <summary>Messaging providers: WhatsApp Business (Meta Cloud API), SMS gateways, Facebook/Instagram, telephony, Teams, push.</summary>
public static class MessagingAdapters
{
    public static IServiceCollection AddMessagingAdapters(this IServiceCollection s, IConfiguration configuration)
    {
        return s;
    }
}
