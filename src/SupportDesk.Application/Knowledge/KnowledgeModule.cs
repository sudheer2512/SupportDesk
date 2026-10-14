using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Knowledge;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Search;
using SupportDesk.Application.Seed;
using SupportDesk.Application.Tickets;

namespace SupportDesk.Application;

/// <summary>Knowledge base, community, guided troubleshooting and tenant search index.</summary>
public static class KnowledgeModule
{
    public static IServiceCollection AddKnowledgeModule(this IServiceCollection s, IConfiguration configuration)
    {
        // Knowledge base
        s.AddScoped<KbAccess>();
        s.AddScoped<ArticleService>();
        s.AddScoped<ArticleAuthoringService>();
        s.AddScoped<KbAssetService>();
        s.AddScoped<ArticleTranslationService>();
        s.AddScoped<KbCategoryService>();
        s.AddScoped<KbFeedbackService>();
        s.AddScoped<KbImportExportService>();
        s.AddScoped<CommunityService>();
        s.AddScoped<TroubleshootingService>();
        s.AddScoped<KnowledgeMaintenance>();
        s.AddScoped<IRecurringTask, KnowledgeMaintenanceTask>();
        s.AddScoped<ITicketContextContributor, KnowledgeContextContributor>();
        s.AddScoped<IArticleDraftWriter>(sp => sp.GetRequiredService<ArticleAuthoringService>());

        // Search index
        s.AddScoped<SearchIndexer>();
        s.AddScoped<SearchEngine>();
        s.AddScoped<SearchBackfill>();
        s.AddScoped<GlobalSearchService>();
        s.AddScoped<KnowledgeSearchService>();
        s.AddScoped<IKnowledgeSearch>(sp => sp.GetRequiredService<KnowledgeSearchService>());
        s.AddScoped<IOutboxHandler, SearchOutboxHandler>();
        s.AddScoped<IRecurringTask, SearchBackfillTask>();

        // Demo data
        s.AddScoped<IDemoSeedContributor, KnowledgeDemoSeed>();
        return s;
    }
}
