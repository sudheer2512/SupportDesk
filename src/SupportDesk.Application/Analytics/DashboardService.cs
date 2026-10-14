using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Analytics;
using SupportDesk.Contracts.Common;
using SupportDesk.Domain;
using SupportDesk.Domain.Analytics;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Analytics;

/// <summary>
/// Dashboards: personal (owner only) and shared (analytics.team to create/edit) dashboards whose widgets reference
/// catalogue metrics, plus the role defaults (Agent, Supervisor, Manager, Executive) every tenant gets.
/// </summary>
public sealed class DashboardService(DeskDbContext db, RequestContext ctx, IAuditWriter audit)
{
    public const int MaxWidgets = 30;
    private static readonly string[] WidgetTypes = ["kpi", "line", "bar", "pie", "table"];
    private static readonly string[] Audiences = ["agent", "supervisor", "manager", "executive"];

    private Guid Me => ctx.UserId ?? Guid.Empty;

    private static int AudienceRank(string audience) { var i = Array.IndexOf(Audiences, audience); return i < 0 ? 99 : i; }
    private bool Team => AnalyticsScope.IsTeam(ctx);

    public async Task<List<DashboardSummaryDto>> ListAsync(CancellationToken ct)
    {
        ctx.Require(Permissions.AnalyticsView);
        await EnsureDefaultsAsync(ct);
        var me = Me;
        var list = await db.Set<Dashboard>().AsNoTracking().Where(d => d.IsShared || d.OwnerUserId == me).ToListAsync(ct);
        // The caller's natural default first: supervisors/analysts → supervisor board, agents → agent board.
        var preferred = Team ? "supervisor" : "agent";
        return list.OrderByDescending(d => d.IsDefault && d.Audience == preferred).ThenByDescending(d => d.IsDefault)
            .ThenBy(d => AudienceRank(d.Audience)).ThenBy(d => d.Name)
            .Select(Summary).ToList();
    }

    public async Task<DashboardDto> GetAsync(Guid id, CancellationToken ct)
    {
        ctx.Require(Permissions.AnalyticsView);
        return Map(await LoadVisibleAsync(id, ct, track: false));
    }

    public async Task<DashboardDto> CreateAsync(SaveDashboardRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AnalyticsView);
        if (r.IsShared) ctx.Require(Permissions.AnalyticsTeam);
        var d = new Dashboard { OwnerUserId = Me, Audience = "custom" };
        Apply(d, r);
        db.Add(d);
        if (d.IsShared) audit.Write("analytics.dashboard_created", "Dashboard", d.Id, $"Shared dashboard \"{d.Name}\" created");
        await db.SaveChangesAsync(ct);
        return Map(d);
    }

    public async Task<DashboardDto> UpdateAsync(Guid id, SaveDashboardRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AnalyticsView);
        var d = await LoadVisibleAsync(id, ct, track: true);
        EnsureCanEdit(d);
        if (r.IsShared && !d.IsShared) ctx.Require(Permissions.AnalyticsTeam);
        if (string.IsNullOrWhiteSpace(r.RowVersion)) throw new ValidationException(new Dictionary<string, string> { ["rowVersion"] = "Reload the dashboard and try again." });
        db.Entry(d).Property(x => x.RowVersion).OriginalValue = Convert.FromBase64String(r.RowVersion);
        Apply(d, r with { IsShared = d.IsDefault || r.IsShared });
        if (d.IsShared) audit.Write("analytics.dashboard_changed", "Dashboard", d.Id, $"Shared dashboard \"{d.Name}\" updated");
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(ErrorCodes.ConcurrencyConflict, "Someone else changed this dashboard. Reload to see their version.");
        }
        return Map(d);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        ctx.Require(Permissions.AnalyticsView);
        var d = await LoadVisibleAsync(id, ct, track: true);
        EnsureCanEdit(d);
        if (d.IsDefault) throw new ConflictException("dashboard_default", "Role default dashboards can be edited but not deleted.");
        db.Remove(d);
        if (d.IsShared) audit.Write("analytics.dashboard_deleted", "Dashboard", d.Id, $"Shared dashboard \"{d.Name}\" deleted");
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Copies any visible dashboard into a personal one the caller can change.</summary>
    public async Task<DashboardDto> DuplicateAsync(Guid id, CancellationToken ct)
    {
        ctx.Require(Permissions.AnalyticsView);
        var src = await LoadVisibleAsync(id, ct, track: false);
        var copy = new Dashboard
        {
            Name = Truncate($"{src.Name} (copy)", 200), Description = src.Description, OwnerUserId = Me, Audience = "custom", Period = src.Period,
            WidgetsJson = src.WidgetsJson
        };
        db.Add(copy);
        await db.SaveChangesAsync(ct);
        return Map(copy);
    }

    private async Task<Dashboard> LoadVisibleAsync(Guid id, CancellationToken ct, bool track)
    {
        var me = Me;
        var q = db.Set<Dashboard>().Where(d => d.Id == id && (d.IsShared || d.OwnerUserId == me));
        return await (track ? q : q.AsNoTracking()).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Dashboard", id);
    }

    private bool CanEdit(Dashboard d) => d.OwnerUserId == Me && !d.IsShared || Team;

    private void EnsureCanEdit(Dashboard d)
    {
        if (!CanEdit(d)) throw new ForbiddenException(d.IsShared ? "Shared dashboards can be changed by users with team analytics access." : "Only the owner can change this dashboard.");
    }

    private void Apply(Dashboard d, SaveDashboardRequest r)
    {
        var errors = new Dictionary<string, string>();
        var name = (r.Name ?? "").Trim();
        if (name.Length is 0 or > 120) errors["name"] = "Enter a name of up to 120 characters.";
        var period = string.IsNullOrWhiteSpace(r.Period) ? "last_30_days" : r.Period.Trim().ToLowerInvariant();
        if (!AnalyticsPeriods.IsKnown(period) || period == "custom") errors["period"] = "Choose a period.";
        if (r.Description is { Length: > 500 }) errors["description"] = "The description may be at most 500 characters.";
        List<DashboardWidgetDto> widgets = [];
        try
        {
            widgets = ValidateWidgets(r.Widgets ?? []);
        }
        catch (ValidationException ex)
        {
            foreach (var (k, v) in ex.Errors) errors[k] = v;
        }
        if (errors.Count > 0) throw new ValidationException(errors);
        d.Name = name;
        d.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
        d.IsShared = r.IsShared;
        d.Period = period;
        d.WidgetsJson = Json.Serialize(widgets);
    }

    /// <summary>Validates widgets against the catalogue and the 12-column grid.</summary>
    public static List<DashboardWidgetDto> ValidateWidgets(IReadOnlyList<DashboardWidgetDto> widgets)
    {
        if (widgets.Count > MaxWidgets) throw new ValidationException(new Dictionary<string, string> { ["widgets"] = $"A dashboard can have at most {MaxWidgets} widgets." });
        var result = new List<DashboardWidgetDto>();
        var ids = new HashSet<string>();
        for (var i = 0; i < widgets.Count; i++)
        {
            var w = widgets[i];
            string P(string f) => $"widgets[{i}].{f}";
            void Fail(string f, string msg) => throw new ValidationException(new Dictionary<string, string> { [P(f)] = msg });
            var type = (w.Type ?? "").Trim().ToLowerInvariant();
            if (!WidgetTypes.Contains(type)) Fail("type", "Type must be kpi, line, bar, pie or table.");
            var title = (w.Title ?? "").Trim();
            if (title.Length is 0 or > 120) Fail("title", "Enter a title of up to 120 characters.");
            if (!MetricCatalog.Metrics.TryGetValue(w.Metric ?? "", out var metric)) Fail("metric", $"'{w.Metric}' is not a catalogue metric.");
            var extra = (w.Metrics ?? []).Select(k => k.Trim()).Where(k => k.Length > 0 && !string.Equals(k, metric!.Key, StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
            if (extra.Count > 7) Fail("metrics", "A table shows at most 8 metrics.");
            foreach (var k in extra) if (!MetricCatalog.Metrics.ContainsKey(k)) Fail("metrics", $"'{k}' is not a catalogue metric.");
            string dimension;
            try
            {
                dimension = type == "kpi" ? MetricCatalog.NoneKey : AnalyticsEngine.ValidateDimension(metric!, w.Dimension, type == "line" ? "day" : "department");
                foreach (var k in extra) AnalyticsEngine.ValidateDimension(MetricCatalog.Metrics[k], dimension);
            }
            catch (DomainException ex)
            {
                Fail("dimension", ex.Message);
                return result;
            }
            if (type == "line" && !MetricCatalog.IsTime(dimension)) Fail("dimension", "Line charts need a day, week or month dimension.");
            if (type is "bar" or "pie" or "table" && dimension == MetricCatalog.NoneKey) Fail("dimension", "Choose a dimension to break the metric down by.");
            if (type == "pie" && (MetricCatalog.IsTime(dimension) || !metric!.IsAdditive)) Fail("dimension", "Pie charts need a count metric and a categorical dimension.");
            var filters = new Dictionary<string, string>();
            foreach (var (k, v) in w.Filters ?? new Dictionary<string, string>())
            {
                if (string.IsNullOrWhiteSpace(v)) continue;
                var key = k.Trim().ToLowerInvariant();
                if (MetricCatalog.Filters.All(f => f.Key != key)) Fail("filters", $"'{k}' is not a filter.");
                filters[key] = v.Trim();
            }
            try
            {
                var parsed = AnalyticsFilters.Parse(filters.ToDictionary(x => x.Key, x => (string?)x.Value));
                if (metric!.Source == MetricSource.AgentDaily && parsed.HasTicketOnlyFilters) Fail("filters", $"'{metric.Label}' can only be filtered by department, team and agent.");
            }
            catch (ValidationException ex)
            {
                Fail("filters", ex.Errors.Values.FirstOrDefault() ?? "Invalid filter.");
            }
            var period = string.IsNullOrWhiteSpace(w.Period) ? null : w.Period.Trim().ToLowerInvariant();
            if (period is not null && (!AnalyticsPeriods.IsKnown(period) || period == "custom")) Fail("period", "Choose a period.");
            var id = string.IsNullOrWhiteSpace(w.Id) ? Guid.NewGuid().ToString("N")[..10] : w.Id.Trim();
            if (id.Length > 40 || !ids.Add(id)) Fail("id", "Widget ids must be unique (40 characters at most).");
            var width = Math.Clamp(w.W, 1, 12);
            result.Add(new DashboardWidgetDto
            {
                Id = id, Title = title, Type = type, Metric = metric!.Key, Metrics = type == "table" && extra.Count > 0 ? extra : null,
                Dimension = type == "kpi" ? null : dimension, Limit = type is "bar" or "pie" or "table" ? Math.Clamp(w.Limit ?? 10, 1, 50) : null,
                Filters = filters.Count > 0 ? filters : null, Period = period, Compare = w.Compare,
                X = Math.Clamp(w.X, 0, 12 - width), Y = Math.Clamp(w.Y, 0, 500), W = width, H = Math.Clamp(w.H, 1, 8)
            });
        }
        return result;
    }

    private DashboardSummaryDto Summary(Dashboard d) =>
        new(d.Id, d.Name, d.Description, d.Audience, d.IsShared, d.IsDefault, d.OwnerUserId == Me, CanEdit(d), Widgets(d).Count, d.UpdatedAt);

    private DashboardDto Map(Dashboard d) =>
        new(d.Id, d.Name, d.Description, d.Audience, d.IsShared, d.IsDefault, d.OwnerUserId == Me, CanEdit(d), d.Period, Widgets(d),
            Convert.ToBase64String(d.RowVersion ?? []), d.UpdatedAt);

    private static List<DashboardWidgetDto> Widgets(Dashboard d) => Json.DeserializeOr(d.WidgetsJson, new List<DashboardWidgetDto>());

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    // ───────────────────────────── Role defaults ─────────────────────────────

    /// <summary>Creates any missing role-default dashboard for the current tenant (idempotent).</summary>
    public async Task EnsureDefaultsAsync(CancellationToken ct)
    {
        if (!ctx.HasTenant) return;
        var present = await db.Set<Dashboard>().AsNoTracking().Where(d => d.IsDefault).Select(d => d.Audience).ToListAsync(ct);
        var missing = DefaultDashboards.All.Where(d => !present.Contains(d.Audience)).ToList();
        if (missing.Count == 0) return;
        foreach (var def in missing)
            db.Add(new Dashboard
            {
                Name = def.Name, Description = def.Description, Audience = def.Audience, IsShared = true, IsDefault = true, Period = def.Period,
                WidgetsJson = Json.Serialize(ValidateWidgets(def.Widgets))
            });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear(); // created concurrently by another request
        }
    }
}

/// <summary>Role default dashboards seeded for every tenant.</summary>
public static class DefaultDashboards
{
    public sealed record Def(string Audience, string Name, string Description, string Period, IReadOnlyList<DashboardWidgetDto> Widgets);

    private static DashboardWidgetDto W(string id, string title, string type, string metric, int x, int y, int w, int h, string? dimension = null,
        int? limit = null, IReadOnlyList<string>? metrics = null, bool compare = false) =>
        new() { Id = id, Title = title, Type = type, Metric = metric, Dimension = dimension, Limit = limit, Metrics = metrics, X = x, Y = y, W = w, H = h, Compare = compare };

    public static readonly IReadOnlyList<Def> All =
    [
        new("agent", "My performance", "Your open work, speed and customer feedback.", "last_30_days",
        [
            W("open", "My open tickets", "kpi", "backlog", 0, 0, 3, 2),
            W("resolved", "Resolved", "kpi", "resolved_tickets", 3, 0, 3, 2, compare: true),
            W("frt", "Median first response (bh)", "kpi", "median_first_response_business_hours", 6, 0, 3, 2, compare: true),
            W("csat", "CSAT", "kpi", "csat_avg", 9, 0, 3, 2, compare: true),
            W("resolved_trend", "Resolved per day", "line", "resolved_tickets", 0, 2, 6, 4, "day"),
            W("open_priority", "Open tickets by priority", "pie", "backlog", 6, 2, 6, 4, "priority"),
            W("by_status", "Open work by status", "table", "backlog", 0, 6, 12, 3, "status_category", 10, ["avg_open_age_hours"])
        ]),
        new("supervisor", "Team operations", "Backlog, SLA, speed and agent scorecard for your departments.", "last_7_days",
        [
            W("backlog", "Backlog", "kpi", "backlog", 0, 0, 3, 2, compare: true),
            W("sla", "SLA compliance", "kpi", "sla_compliance", 3, 0, 3, 2, compare: true),
            W("frt", "Median first response (bh)", "kpi", "median_first_response_business_hours", 6, 0, 3, 2, compare: true),
            W("fcr", "First contact resolution", "kpi", "fcr_rate", 9, 0, 3, 2, compare: true),
            W("created", "Tickets created per day", "line", "ticket_count", 0, 2, 6, 4, "day"),
            W("backlog_agent", "Backlog by agent", "bar", "backlog", 6, 2, 6, 4, "assignee", 10),
            W("scorecard", "Agent scorecard", "table", "resolved_tickets", 0, 6, 12, 4, "assignee", 25,
                ["median_first_response_business_hours", "sla_breaches", "reopened_tickets", "csat_avg"]),
            W("breach_priority", "SLA violations by priority", "bar", "sla_breaches", 0, 10, 6, 4, "priority"),
            W("channel", "Tickets by channel", "pie", "ticket_count", 6, 10, 6, 4, "channel")
        ]),
        new("manager", "Service performance", "Volume, speed, quality and satisfaction across departments.", "last_30_days",
        [
            W("created", "Tickets created", "kpi", "ticket_count", 0, 0, 3, 2, compare: true),
            W("resolved", "Tickets resolved", "kpi", "resolved_tickets", 3, 0, 3, 2, compare: true),
            W("sla", "SLA compliance", "kpi", "sla_compliance", 6, 0, 3, 2, compare: true),
            W("csat", "Average CSAT", "kpi", "csat_avg", 9, 0, 3, 2, compare: true),
            W("resolution_trend", "Median resolution time (bh) by week", "line", "median_resolution_business_hours", 0, 2, 6, 4, "week"),
            W("by_department", "Tickets by department", "bar", "ticket_count", 6, 2, 6, 4, "department"),
            W("department_table", "Department scorecard", "table", "ticket_count", 0, 6, 12, 4, "department", 25,
                ["sla_compliance", "fcr_rate", "reopen_rate", "csat_avg"]),
            W("escalated", "Escalations by category", "bar", "escalated_tickets", 0, 10, 6, 4, "category"),
            W("backlog_trend", "Backlog by week", "line", "backlog", 6, 10, 6, 4, "week")
        ]),
        new("executive", "Executive overview", "Quarter-level demand, service quality and customer sentiment.", "last_90_days",
        [
            W("created", "Tickets created", "kpi", "ticket_count", 0, 0, 3, 2, compare: true),
            W("sla", "SLA compliance", "kpi", "sla_compliance", 3, 0, 3, 2, compare: true),
            W("csat", "Average CSAT", "kpi", "csat_avg", 6, 0, 3, 2, compare: true),
            W("fcr", "First contact resolution", "kpi", "fcr_rate", 9, 0, 3, 2, compare: true),
            W("demand", "Demand by week", "line", "ticket_count", 0, 2, 6, 4, "week"),
            W("csat_trend", "CSAT by week", "line", "csat_avg", 6, 2, 6, 4, "week"),
            W("products", "Tickets by product", "bar", "ticket_count", 0, 6, 6, 4, "product"),
            W("customers", "Top customers by tickets", "bar", "ticket_count", 6, 6, 6, 4, "customer", 10),
            W("tiers", "Service by customer tier", "table", "ticket_count", 0, 10, 12, 3, "customer_tier", 10, ["sla_breaches", "csat_avg"])
        ])
    ];
}
