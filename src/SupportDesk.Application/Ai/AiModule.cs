using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Ai;
using SupportDesk.Application.Ai.Admin;
using SupportDesk.Application.Ai.Agents;
using SupportDesk.Application.Ai.Analytics;
using SupportDesk.Application.Ai.Anomalies;
using SupportDesk.Application.Ai.Features;
using SupportDesk.Application.Ai.Retrieval;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Seed;
using SupportDesk.Application.Tickets;

namespace SupportDesk.Application;

/// <summary>AI gateway, features (classification, summaries, replies, RAG…), autonomous agents, usage and budgets.</summary>
public static class AiModule
{
    public static IServiceCollection AddAiModule(this IServiceCollection s, IConfiguration configuration)
    {
        s.AddSingleton(configuration.GetSection("Ai").Get<AiOptions>() ?? new AiOptions());
        s.AddSingleton<ChunkVectorCache>();
        s.AddSingleton<AiCorpusCache>();

        // Gateway, configuration, prompts, retrieval
        s.AddScoped<AiConfigurationService>();
        s.AddScoped<AiPromptService>();
        s.AddScoped<AiGateway>();
        s.AddScoped<KnowledgeIndexer>();
        s.AddScoped<KnowledgeRetriever>();

        // Features
        s.AddScoped<AiTicketLoader>();
        s.AddScoped<AiClassificationService>();
        s.AddScoped<AiSummaryService>();
        s.AddScoped<AiSentimentService>();
        s.AddScoped<AiReplyService>();
        s.AddScoped<AiAssistantService>();
        s.AddScoped<AiSearchService>();
        s.AddScoped<AiInsightService>();
        s.AddScoped<SafeAnalyticsQuery>();
        s.AddScoped<NlAnalyticsService>();
        s.AddScoped<AnomalyDetectionService>();

        // Agents & proposals
        s.AddScoped<AiProposalService>();
        s.AddScoped<AiAgentRunner>();
        s.AddScoped<AiAdminService>();

        // Ports implemented for other modules (registered after the kernel's null defaults, so these win)
        s.AddScoped<IConversationAssistant, AiConversationAssistant>();
        s.AddScoped<IAiActionGateway, AiActionGatewayService>();
        s.AddScoped<ITicketContextContributor, AiTicketContextContributor>();

        // Events, background work, demo data
        s.AddScoped<IOutboxHandler, AiKnowledgeIndexHandler>();
        s.AddScoped<IOutboxHandler, AiTicketEventsHandler>();
        s.AddScoped<IJobHandler, AiReembedJobHandler>();
        s.AddScoped<IRecurringTask, AiKnowledgeBackfillTask>();
        s.AddScoped<IRecurringTask, AnomalyDetectionTask>();
        s.AddScoped<IRecurringTask, AiHousekeepingTask>();
        s.AddScoped<IDemoSeedContributor, AiDemoSeedContributor>();
        return s;
    }
}
