using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using SupportDesk.Application.Automation;
using SupportDesk.Application.Integrations.Engineering;
using SupportDesk.Domain;
using SupportDesk.Domain.Tickets;
using SupportDesk.Integrations.Engineering;

namespace SupportDesk.Integrations;

/// <summary>Engineering trackers: Azure DevOps Services/Server (TFS), GitHub, and the built-in local tracker.</summary>
public static class EngineeringAdapters
{
    public const string ClientName = "engineering-trackers";

    public static IServiceCollection AddEngineeringAdapters(this IServiceCollection s, IConfiguration configuration)
    {
        // Tracker URLs are tenant-configured: the SSRF guard of automation webhooks applies (on-premises TFS needs
        // Automation:Webhooks:AllowPrivateNetworks=true). Redirects are not followed (GitHub 301 = transferred issue).
        s.AddHttpClient(ClientName, c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(sp => OutboundHttpSender.CreateHandler(sp.GetRequiredService<WebhookTransportOptions>()))
            .AddStandardResilienceHandler(o =>
            {
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(75);
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
                o.Retry.MaxRetryAttempts = 2;
                // Creating a work item (POST) must not be repeated automatically: the tracker may have created it.
                o.Retry.DisableForUnsafeHttpMethods();
            });
        s.AddSingleton<GitHubAppTokenCache>();
        s.AddScoped<IEngineeringTrackerFactory, EngineeringTrackerFactory>();
        return s;
    }
}

/// <summary>Builds the adapter for a configured tracker: Azure DevOps or GitHub over HTTP, or the database-backed local tracker.</summary>
public sealed class EngineeringTrackerFactory(IHttpClientFactory http, IEngineeringCallLog log, GitHubAppTokenCache tokens, IClock clock, IServiceProvider sp)
    : IEngineeringTrackerFactory
{
    public IEngineeringTracker Create(TrackerConnection connection) => connection.Provider switch
    {
        EngineeringProvider.AzureDevOps => new AzureDevOpsTracker(http.CreateClient(EngineeringAdapters.ClientName), connection, log),
        EngineeringProvider.GitHub => new GitHubTracker(http.CreateClient(EngineeringAdapters.ClientName), connection, log, tokens, () => clock.UtcNow),
        EngineeringProvider.Local => ActivatorUtilities.CreateInstance<LocalTracker>(sp, connection),
        _ => throw new DomainException("integration_unavailable", "Jira is not supported.")
    };
}
