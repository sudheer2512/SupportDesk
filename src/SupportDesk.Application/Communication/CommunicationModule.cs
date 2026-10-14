using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SupportDesk.Application.Communication;
using SupportDesk.Application.Platform;

namespace SupportDesk.Application;

/// <summary>Omnichannel: channel accounts, inbound processing and threading, outbound delivery, chat, telephony, notification delivery.</summary>
public static class CommunicationModule
{
    public static IServiceCollection AddCommunicationModule(this IServiceCollection s, IConfiguration configuration)
    {
        // Infrastructure of the module (provider adapters are registered by SupportDesk.Integrations).
        s.AddScoped<ChannelProviderRegistry>();
        s.AddScoped<ChannelContextFactory>();
        s.AddScoped<CommunicationKeys>();
        s.AddSingleton<ChatTokens>();
        s.TryAddSingleton<IChatRealtime, NullChatRealtime>();

        // Channel administration, inbound pipeline, outbound delivery
        s.AddScoped<ChannelAccountService>();
        s.AddScoped<ThreadAppender>();
        s.AddScoped<InboundEmailProcessor>();
        s.AddScoped<InboundMessagingProcessor>();
        s.AddScoped<OutboundService>();
        s.AddScoped<DeliveryProcessor>();
        s.AddScoped<MailboxPoller>();
        s.AddScoped<WebhookIngress>();
        s.AddScoped<IJobHandler, DeliverMessageJob>();
        s.AddScoped<IJobHandler, InboundEventRetryJob>();
        s.AddScoped<IRecurringTask, MailboxPollingTask>();
        s.AddScoped<IRecurringTask, MessageDeliveryTask>();
        s.AddScoped<IOutboxHandler, ChannelReplyHandler>();
        s.AddScoped<IOutboxHandler, CommunicationRealtimeHandler>();
        s.AddScoped<IOutboundMessenger, OutboundMessenger>();

        // Live chat, inbox, CSAT, notification delivery, simulator
        s.AddScoped<ChatService>();
        s.AddScoped<ConversationQueryService>();
        s.AddScoped<CsatService>();
        s.AddScoped<IOutboxHandler, CsatSurveyHandler>();
        s.AddScoped<IOutboxHandler, ResolutionNoticeHandler>();
        s.AddScoped<NotificationDeliveryProcessor>();
        s.AddScoped<IRecurringTask, NotificationDeliveryTask>();
        s.AddScoped<ChannelSimulator>();

        // Demo data
        s.AddScoped<Seed.IDemoSeedContributor, CommunicationDemoSeed>();
        return s;
    }
}
