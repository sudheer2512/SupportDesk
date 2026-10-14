using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SupportDesk.Application.Integrations.Collaboration;
using SupportDesk.Application.Integrations.Engineering;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Seed;
using SupportDesk.Application.Tickets;

namespace SupportDesk.Application;

/// <summary>
/// Wings ERP / Wings Books, engineering trackers (Azure DevOps Services/Server, GitHub, the built-in local tracker),
/// Microsoft Teams / Slack collaboration channels, outbound/inbound webhooks, integration logs.
/// </summary>
public static class IntegrationModule
{
    public static IServiceCollection AddIntegrationModule(this IServiceCollection s, IConfiguration configuration)
    {
        // Engineering escalation (UC-9). HTTP tracker adapters are registered by SupportDesk.Integrations (AddEngineeringAdapters);
        // without them only the built-in tracker works.
        s.TryAddScoped<IEngineeringTrackerFactory, LocalOnlyTrackerFactory>();
        s.AddSingleton<IEngineeringCallLog, EngineeringCallLog>();
        s.AddScoped<EngineeringTrackerResolver>();
        s.AddScoped<EngineeringSyncService>();
        s.AddScoped<EngineeringTicketService>();
        s.AddScoped<EngineeringAdminService>();
        s.AddScoped<EngineeringBoardService>();
        s.AddScoped<EngineeringWebhookService>();
        s.AddScoped<IOutboxHandler, EngineeringOutboxHandler>();
        s.AddScoped<IRecurringTask, EngineeringSyncTask>();
        s.AddScoped<ITicketContextContributor, EngineeringContextContributor>();
        s.AddScoped<IDemoSeedContributor, EngineeringDemoSeed>();

        // Microsoft Teams / Slack collaboration channels.
        s.AddScoped<CollaborationMessageFactory>();
        s.AddScoped<CollaborationSender>();
        s.AddScoped<CollaborationService>();
        s.AddScoped<IOutboxHandler, CollaborationOutboxHandler>();
        s.AddScoped<IJobHandler, CollaborationSendJobHandler>();
        return s;
    }
}
