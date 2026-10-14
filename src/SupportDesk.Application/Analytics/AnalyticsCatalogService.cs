using Microsoft.EntityFrameworkCore;
using SupportDesk.Contracts.Analytics;
using SupportDesk.Contracts.Common;
using SupportDesk.Domain;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Analytics;

/// <summary>The metric catalogue, report library and filter values as seen by the caller.</summary>
public sealed class AnalyticsCatalogService(DeskDbContext db, RequestContext ctx, AnalyticsEngine engine)
{
    public async Task<AnalyticsCatalogDto> CatalogAsync(CancellationToken ct)
    {
        ctx.Require(Permissions.AnalyticsView);
        var tz = await engine.TimeZoneAsync(ct);
        return new AnalyticsCatalogDto(
            MetricCatalog.List.Select(MetricCatalog.ToDto).ToList(),
            MetricCatalog.Dimensions.Select(d => new DimensionDto(d.Key, d.Label, d.IsTime)).ToList(),
            MetricCatalog.Filters.Select(f => new FilterFieldDto(f.Key, f.Label, f.Kind)).ToList(),
            MetricCatalog.Periods.Select(p => new PeriodDto(p.Key, p.Label)).ToList(),
            ReportService.Library(),
            tz.Id,
            AnalyticsScope.IsTeam(ctx),
            ctx.Has(Permissions.AnalyticsExport),
            ctx.Has(Permissions.AnalyticsSchedule));
    }

    public IReadOnlyList<ReportDefinitionDto> Reports()
    {
        ctx.Require(Permissions.AnalyticsView);
        return ReportService.Library();
    }

    public async Task<AnalyticsFilterOptionsDto> FilterOptionsAsync(CancellationToken ct)
    {
        ctx.Require(Permissions.AnalyticsView);
        var team = AnalyticsScope.IsTeam(ctx);
        var me = ctx.UserId ?? Guid.Empty;
        var departments = await db.Departments.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.SortOrder).ThenBy(d => d.Name)
            .Select(d => new OptionDto(d.Id.ToString(), d.Name)).ToListAsync(ct);
        var teams = await db.Teams.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.Name).Select(t => new OptionDto(t.Id.ToString(), t.Name)).ToListAsync(ct);
        var agentsQuery = db.Agents.AsNoTracking().Join(db.Users.AsNoTracking(), a => a.UserId, u => u.Id, (a, u) => new { u.Id, u.DisplayName, u.IsActive });
        if (!team) agentsQuery = agentsQuery.Where(a => a.Id == me);
        var agents = await agentsQuery.Where(a => a.IsActive).OrderBy(a => a.DisplayName).Select(a => new OptionDto(a.Id.ToString(), a.DisplayName)).ToListAsync(ct);
        var products = await db.Products.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Name).Select(p => new OptionDto(p.Id.ToString(), p.Name)).ToListAsync(ct);
        var modules = await db.ProductModules.AsNoTracking().Where(m => m.IsActive)
            .Join(db.Products.AsNoTracking(), m => m.ProductId, p => p.Id, (m, p) => new { m.Id, Label = p.Name + " › " + m.Name })
            .OrderBy(x => x.Label).Select(x => new OptionDto(x.Id.ToString(), x.Label)).ToListAsync(ct);
        var versions = await db.ProductVersions.AsNoTracking()
            .Join(db.Products.AsNoTracking(), v => v.ProductId, p => p.Id, (v, p) => new { v.Id, Label = p.Name + " " + v.Version })
            .OrderBy(x => x.Label).Select(x => new OptionDto(x.Id.ToString(), x.Label)).ToListAsync(ct);
        var categories = await db.TicketCategories.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new OptionDto(c.Id.ToString(), c.Name)).ToListAsync(ct);
        var tiers = await db.Customers.AsNoTracking().Select(c => c.Tier).Distinct().OrderBy(t => t).ToListAsync(ct);
        return new AnalyticsFilterOptionsDto(departments, teams, agents, products, modules, versions, categories,
            tiers.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => new OptionDto(t, t)).ToList(),
            Enum.GetValues<Channel>().Select(c => new OptionDto(c.ToString(), AnalyticsLabels.Words(c.ToString()))).ToList(),
            Enum.GetValues<TicketPriority>().Reverse().Select(p => new OptionDto(p.ToString(), p.ToString())).ToList(),
            Enum.GetValues<StatusCategory>().Select(s => new OptionDto(s.ToString(), AnalyticsLabels.Words(s.ToString()))).ToList());
    }
}
