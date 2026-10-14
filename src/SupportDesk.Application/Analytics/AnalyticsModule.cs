using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Analytics;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Seed;

namespace SupportDesk.Application;

/// <summary>Ticket/agent metrics, dashboards, reports, exports, scheduled reports, CSAT.</summary>
public static class AnalyticsModule
{
    public static IServiceCollection AddAnalyticsModule(this IServiceCollection s, IConfiguration configuration)
    {
        // Metrics pipeline (ticket rows from events; daily agent/backlog roll-ups; catch-up and backfill)
        s.AddScoped<TicketMetricBuilder>();
        s.AddScoped<AnalyticsRollupService>();
        s.AddScoped<MetricsMaintenanceService>();
        s.AddScoped<IOutboxHandler, TicketMetricsOutboxHandler>();
        s.AddScoped<IRecurringTask, AnalyticsRollupTask>();
        s.AddScoped<IJobHandler, MetricsBackfillJobHandler>();

        // Query engine, catalogue, reports and dashboards
        s.AddScoped<AnalyticsLabels>();
        s.AddScoped<AnalyticsEngine>();
        s.AddScoped<AnalyticsCatalogService>();
        s.AddScoped<ReportService>();
        s.AddScoped<DashboardService>();

        // Exports and scheduled delivery
        s.AddSingleton<ReportRunner>();
        s.AddScoped<ReportExportService>();
        s.AddScoped<IJobHandler, ReportExportJobHandler>();
        s.AddScoped<ScheduledReportService>();
        s.AddScoped<IRecurringTask, ScheduledReportTask>();
        s.AddScoped<IJobHandler, ScheduledReportJobHandler>();

        // Demo data
        s.AddScoped<IDemoSeedContributor, AnalyticsDemoSeed>();
        return s;
    }
}
