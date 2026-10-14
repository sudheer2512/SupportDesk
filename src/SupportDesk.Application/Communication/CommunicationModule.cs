using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SupportDesk.Application;

/// <summary>Omnichannel: channel accounts, inbound processing and threading, outbound delivery, chat, telephony, notification delivery.</summary>
public static class CommunicationModule
{
    public static IServiceCollection AddCommunicationModule(this IServiceCollection s, IConfiguration configuration)
    {
        return s;
    }
}
