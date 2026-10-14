using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Approvals;
using SupportDesk.Application.Automation;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Seed;

namespace SupportDesk.Application;

/// <summary>Automation rules (trigger → conditions → actions), blueprint workflows, webhooks.</summary>
public static class AutomationModule
{
    public static IServiceCollection AddAutomationModule(this IServiceCollection s, IConfiguration configuration)
    {
        var webhookOptions = configuration.GetSection("Automation:Webhooks").Get<WebhookTransportOptions>() ?? new WebhookTransportOptions();
        s.AddSingleton(webhookOptions);
        s.AddHttpClient(OutboundHttpSender.ClientName, c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => OutboundHttpSender.CreateHandler(webhookOptions));

        // Rules
        s.AddScoped<AutomationReferenceLoader>();
        s.AddScoped<ActionExecutor>();
        s.AddScoped<RuleEngine>();
        s.AddScoped<RuleService>();
        s.AddScoped<InboundWebhookService>();
        s.AddScoped<OutboundHttpSender>();
        s.AddScoped<IOutboxHandler, AutomationOutboxHandler>();
        s.AddScoped<IRecurringTask, ScheduledRulesTask>();
        s.AddScoped<IRecurringTask, AutomationHousekeepingTask>();
        s.AddScoped<IJobHandler, RetryExecutionJobHandler>();
        s.AddScoped<IJobHandler, HttpActionJobHandler>();

        // Blueprints
        s.AddScoped<BlueprintService>();
        s.AddScoped<IOutboxHandler, BlueprintAssignmentHandler>();
        s.AddScoped<IApprovalAction, WorkflowTransitionApprovalAction>();

        // Outbound webhooks
        s.AddScoped<WebhookService>();
        s.AddScoped<WebhookDeliveryProcessor>();
        s.AddScoped<IOutboxHandler, WebhookDispatchHandler>();
        s.AddScoped<IJobHandler, WebhookDeliveryJobHandler>();

        // Demo data
        s.AddScoped<IDemoSeedContributor, AutomationDemoSeed>();
        return s;
    }
}
