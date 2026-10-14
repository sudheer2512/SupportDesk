using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Communication;
using SupportDesk.Integrations.Messaging;

namespace SupportDesk.Integrations;

/// <summary>Messaging providers: WhatsApp Business (Meta Cloud API), SMS gateways, Facebook/Instagram, telephony, Teams, push.</summary>
public static class MessagingAdapters
{
    public static IServiceCollection AddMessagingAdapters(this IServiceCollection s, IConfiguration configuration)
    {
        s.AddHttpClient<WhatsAppCloudProvider>(c => c.Timeout = TimeSpan.FromSeconds(60)).AddStandardResilienceHandler();
        s.AddTransient<IMessagingProvider>(sp => sp.GetRequiredService<WhatsAppCloudProvider>());
        s.AddHttpClient<MetaMessengerProvider>(c => c.Timeout = TimeSpan.FromSeconds(60)).AddStandardResilienceHandler();
        s.AddTransient<IMessagingProvider>(sp => sp.GetRequiredService<MetaMessengerProvider>());
        s.AddHttpClient<TwilioSmsProvider>(c => c.Timeout = TimeSpan.FromSeconds(60)).AddStandardResilienceHandler();
        s.AddTransient<IMessagingProvider>(sp => sp.GetRequiredService<TwilioSmsProvider>());
        s.AddHttpClient<GenericSmsProvider>(c => c.Timeout = TimeSpan.FromSeconds(60)).AddStandardResilienceHandler();
        s.AddTransient<IMessagingProvider>(sp => sp.GetRequiredService<GenericSmsProvider>());
        s.AddTransient<IMessagingProvider, GenericVoiceProvider>();
        s.AddTransient<IMessagingProvider, SimulatorProvider>();

        s.AddHttpClient<TeamsWebhookNotifier>(c => c.Timeout = TimeSpan.FromSeconds(30)).AddStandardResilienceHandler();
        s.AddTransient<ITeamsNotifier>(sp => sp.GetRequiredService<TeamsWebhookNotifier>());

        // Push: FCM HTTP v1 (Push:Fcm:*) and/or APNs (Push:Apns:*), routed per device platform by PushRouter; with neither
        // configured every push is only logged.
        s.AddHttpClient<FcmPushSender>(c => c.Timeout = TimeSpan.FromSeconds(30)).AddStandardResilienceHandler();
        s.AddHttpClient<ApnsPushSender>(c => c.Timeout = TimeSpan.FromSeconds(30)).AddStandardResilienceHandler();
        s.AddSingleton<LogOnlyPushSender>();
        s.AddTransient<IPushSender, PushRouter>();
        return s;
    }
}
