using Microsoft.EntityFrameworkCore;
using SupportDesk.Contracts.Analytics;
using SupportDesk.Domain;
using SupportDesk.Domain.Analytics;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Analytics;

public sealed record ReportDef(string Key, string Name, string Description, string Group, string Icon);

/// <summary>The standard report library. Every report accepts period, compare and the nine standard filters.</summary>
public static class ReportCatalog
{
    public static readonly IReadOnlyList<string> StandardFilters = ["period", "department", "team", "assignee", "channel", "priority", "customer", "product", "tier"];

    public static readonly IReadOnlyList<ReportDef> All =
    [
        new("volume", "Ticket volume, backlog & ageing", "Created vs resolved, backlog trend and how long open tickets have been waiting.", "Operations", "inbox"),
        new("response_times", "Response & resolution times", "Median and 90th percentile first response and resolution times in business hours.", "Operations", "timer"),
        new("sla", "SLA compliance", "Compliance by milestone, breaches by priority and department, and the breach list.", "Operations", "shield-check"),
        new("quality", "FCR, reopens & escalations", "First contact resolution, reopen and escalation rates by agent and category.", "Quality", "circle-check"),
        new("agents", "Agent productivity & workload", "Resolved tickets, replies, handle time, utilisation and current workload per agent.", "People", "users"),
        new("channels", "Channel performance", "Volume, speed, first contact resolution, SLA and CSAT by channel.", "Quality", "messages"),
        new("csat", "Customer satisfaction", "CSAT trend, rating distribution, CSAT by agent and the lowest-rated tickets.", "Quality", "smile"),
        new("recurring", "Recurring issues & Pareto", "Pareto of categories and root causes and repeated issue combinations.", "Product", "repeat"),
        new("product_quality", "Product quality", "Tickets, breaches and reopens per product, module and version.", "Product", "package"),
        new("customer_health", "Customer health", "Tickets, open backlog, breaches, reopens and CSAT per customer with a health score.", "Customers", "building"),
    ];

    public static ReportDef Get(string? key) =>
        All.FirstOrDefault(r => string.Equals(r.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new NotFoundException("Report", key);

    public static ReportDefinitionDto ToDto(ReportDef r) => new(r.Key, r.Name, r.Description, r.Group, r.Icon, StandardFilters);
}

/// <summary>Builds the standard reports from catalogue metrics (so reports, dashboards, exports and AI agree).</summary>
public sealed class ReportService(DeskDbContext db, RequestContext ctx, AnalyticsEngine engine)
{
    public async Task<ReportResultDto> RunAsync(string key, AnalyticsQuery q, CancellationToken ct)
    {
        var def = ReportCatalog.Get(key);
        var r = await engine.RequestAsync(q, ct);
        var b = new Builder(engine, r, ct);
        switch (def.Key)
        {
            case "volume": await VolumeAsync(b); break;
            case "response_times": await ResponseTimesAsync(b); break;
            case "sla": await SlaAsync(b); break;
            case "quality": await QualityAsync(b); break;
            case "agents": await AgentsAsync(b); break;
            case "channels": await ChannelsAsync(b); break;
            case "csat": await CsatAsync(b); break;
            case "recurring": await RecurringAsync(b); break;
            case "product_quality": await ProductQualityAsync(b); break;
            case "customer_health": await CustomerHealthAsync(b); break;
        }
        await engine.Labels.LoadAsync(ct);
        await engine.Labels.LoadCustomersAsync(r.Filters.Customers, ct);
        var applied = r.Filters.Describe(engine.Labels.Name).ToList();
        if (!AnalyticsScope.IsTeam(ctx)) applied.Insert(0, "Scope: my tickets");
        return new ReportResultDto(def.Key, def.Name, def.Description, r.Period.ToDto(), r.Compare?.ToDto(), b.Kpis, b.Charts, b.Tables, applied,
            r.TimeZone.Id, db.Clock.UtcNow);
    }

    // ───────────────────────────── Reports ─────────────────────────────

    private static async Task VolumeAsync(Builder b)
    {
        await b.KpisAsync("ticket_count", "resolved_tickets", "backlog", "avg_open_age_hours");
        await b.TimeChartAsync("created_resolved", "Created vs resolved", "line", "ticket_count", "resolved_tickets");
        await b.TimeChartAsync("backlog_trend", "Backlog at the end of each " + b.Grain, "line", "backlog");
        await b.AgeingChartAsync("ageing", "Open tickets by age and priority", "priority");
        await b.GroupChartAsync("by_channel", "Tickets by channel", "pie", "ticket_count", "channel", 10);
        await b.TableAsync("by_department", "By department", "department", 25, "ticket_count", "resolved_tickets", "backlog", "avg_open_age_hours");
        await b.TimeTableAsync("trend", "Volume by " + b.Grain, "ticket_count", "resolved_tickets", "backlog");
    }

    private static async Task ResponseTimesAsync(Builder b)
    {
        await b.KpisAsync("median_first_response_business_hours", "p90_first_response_business_hours", "median_resolution_business_hours",
            "p90_resolution_business_hours", "median_first_response_hours", "median_resolution_hours");
        await b.TimeChartAsync("frt_trend", "First response (business hours)", "line", "median_first_response_business_hours", "p90_first_response_business_hours");
        await b.TimeChartAsync("resolution_trend", "Resolution (business hours)", "line", "median_resolution_business_hours", "p90_resolution_business_hours");
        await b.GroupChartAsync("resolution_by_priority", "Median resolution by priority (business hours)", "bar", "median_resolution_business_hours", "priority", 10);
        string[] cols = ["median_first_response_business_hours", "p90_first_response_business_hours", "median_resolution_business_hours", "p90_resolution_business_hours",
            "avg_first_response_hours", "avg_resolution_hours"];
        await b.TableAsync("by_priority", "By priority", "priority", 10, cols);
        await b.TableAsync("by_department", "By department", "department", 25, cols);
    }

    private static async Task SlaAsync(Builder b)
    {
        await b.KpisAsync("sla_compliance", "first_response_sla_compliance", "resolution_sla_compliance", "sla_breaches", "next_response_breaches");
        await b.TimeChartAsync("compliance_trend", "Compliance by milestone", "line", "first_response_sla_compliance", "resolution_sla_compliance");
        await b.GroupChartAsync("breaches_by_priority", "Tickets with SLA violations by priority", "bar", "sla_breaches", "priority", 10);
        await b.TableAsync("by_department", "By department", "department", 25, "sla_compliance", "first_response_sla_compliance", "resolution_sla_compliance",
            "sla_breaches", "next_response_breaches");
        await b.TicketTableAsync("breaches", "Breach list", m => m.SlaBreached, DateBasis.Created, 200, includeSla: true);
    }

    private static async Task QualityAsync(Builder b)
    {
        await b.KpisAsync("fcr_rate", "reopen_rate", "escalation_rate", "escalated_tickets", "reopened_tickets", "transfers");
        await b.TimeChartAsync("rates_trend", "FCR and reopen rate", "line", "fcr_rate", "reopen_rate");
        await b.GroupChartAsync("escalated_by_department", "Escalated tickets by department", "bar", "escalated_tickets", "department", 10);
        await b.TableAsync("by_agent", "By agent", "assignee", 50, "resolved_tickets", "fcr_rate", "reopen_rate", "escalated_tickets", "transfers");
        await b.TableAsync("by_category", "By category", "category", 25, "ticket_count", "fcr_rate", "reopen_rate", "escalation_rate");
    }

    private static async Task AgentsAsync(Builder b)
    {
        await b.KpisAsync("resolved_tickets", "avg_handle_minutes", "fcr_rate");
        await b.KpisAsync(agentOnly: true, "agent_replies", "logged_hours", "utilisation");
        await b.GroupChartAsync("resolved_by_agent", "Resolved tickets per agent", "bar", "resolved_tickets", "assignee", 15);
        await b.AgentTableAsync();
    }

    private static async Task ChannelsAsync(Builder b)
    {
        await b.KpisAsync("ticket_count", "median_first_response_business_hours", "fcr_rate", "csat_avg");
        await b.GroupChartAsync("volume_by_channel", "Volume by channel", "pie", "ticket_count", "channel", 15);
        await b.GroupChartAsync("frt_by_channel", "Median first response by channel (business hours)", "bar", "median_first_response_business_hours", "channel", 15);
        await b.TableAsync("by_channel", "By channel", "channel", 20, "ticket_count", "resolved_tickets", "median_first_response_business_hours",
            "median_resolution_business_hours", "fcr_rate", "sla_compliance", "csat_avg");
    }

    private static async Task CsatAsync(Builder b)
    {
        await b.KpisAsync("csat_avg", "csat_positive_rate", "csat_responses");
        await b.TimeChartAsync("csat_trend", "Average CSAT", "line", "csat_avg");
        await b.RatingDistributionAsync();
        await b.GroupChartAsync("csat_by_agent", "Average CSAT by agent", "bar", "csat_avg", "assignee", 15);
        await b.TableAsync("by_agent", "By agent", "assignee", 50, "csat_avg", "csat_responses", "csat_positive_rate");
        await b.TicketTableAsync("low_ratings", "Lowest-rated tickets", m => m.CsatScore != null && m.CsatScore <= 2, DateBasis.Created, 100, includeCsat: true);
    }

    private static async Task RecurringAsync(Builder b)
    {
        await b.KpisAsync("ticket_count", "reopened_tickets", "resolved_tickets");
        await b.ParetoChartAsync("pareto_category", "Pareto of categories", "ticket_count", "category");
        await b.ParetoChartAsync("pareto_root_cause", "Pareto of root causes (resolved tickets)", "resolved_tickets", "root_cause");
        await b.RecurringTablesAsync();
    }

    private static async Task ProductQualityAsync(Builder b)
    {
        await b.KpisAsync("ticket_count", "sla_breaches", "reopened_tickets", "escalated_tickets");
        await b.GroupChartAsync("by_product", "Tickets per product", "bar", "ticket_count", "product", 15);
        await b.GroupChartAsync("by_version", "Tickets per version", "bar", "ticket_count", "version", 15);
        await b.ParetoChartAsync("pareto_module", "Pareto of modules", "ticket_count", "module");
        string[] cols = ["ticket_count", "backlog", "median_resolution_business_hours", "sla_breaches", "reopened_tickets", "csat_avg"];
        await b.TableAsync("by_product", "By product", "product", 50, cols);
        await b.TableAsync("by_module", "By module", "module", 50, cols);
        await b.TableAsync("by_version", "By version", "version", 50, cols);
    }

    private static async Task CustomerHealthAsync(Builder b)
    {
        await b.KpisAsync("ticket_count", "sla_breaches", "csat_avg", "backlog");
        await b.GroupChartAsync("top_customers", "Customers with the most tickets", "bar", "ticket_count", "customer", 15);
        await b.GroupChartAsync("by_tier", "Tickets by customer tier", "pie", "ticket_count", "customer_tier", 10);
        await b.CustomerHealthTableAsync();
    }

    // ───────────────────────────── Builder ─────────────────────────────

    /// <summary>Accumulates KPIs, charts and tables for one report run.</summary>
    private sealed class Builder(AnalyticsEngine engine, AnalyticsRequest r, CancellationToken ct)
    {
        public List<ReportKpiDto> Kpis { get; } = [];
        public List<ReportChartDto> Charts { get; } = [];
        public List<ReportTableDto> Tables { get; } = [];

        /// <summary>Time grain from the period length: up to 31 days → day, up to 26 weeks → week, else month.</summary>
        public string Grain { get; } = (r.Period.ToDay.DayNumber - r.Period.FromDay.DayNumber) switch { <= 31 => "day", <= 182 => "week", _ => "month" };

        private AnalyticsRequest AgentRequest => r.Filters.HasTicketOnlyFilters ? r with { Filters = AgentFilters(r.Filters) } : r;

        private static AnalyticsFilters AgentFilters(AnalyticsFilters f)
        {
            var a = new AnalyticsFilters();
            a.Departments.AddRange(f.Departments);
            a.Teams.AddRange(f.Teams);
            a.Assignees.AddRange(f.Assignees);
            return a;
        }

        private AnalyticsRequest For(MetricDef m) => m.Source == MetricSource.AgentDaily ? AgentRequest : r;

        public Task KpisAsync(params string[] keys) => KpisAsync(false, keys);

        public async Task KpisAsync(bool agentOnly, params string[] keys)
        {
            foreach (var k in keys)
            {
                var m = MetricCatalog.Get(k);
                var req = For(m);
                var (v, _) = await engine.ValueAsync(m, req, req.Period, ct);
                double? c = req.Compare is { } cp ? (await engine.ValueAsync(m, req, cp, ct)).Value : null;
                Kpis.Add(new ReportKpiDto(m.Key, m.Label, m.Unit, v, c, MetricCatalog.Delta(v, c), m.HigherIsBetter));
            }
        }

        public async Task TimeChartAsync(string id, string title, string type, params string[] keys)
        {
            var series = new List<ReportSeriesDto>();
            List<SeriesBucket>? first = null;
            foreach (var k in keys)
            {
                var m = MetricCatalog.Get(k);
                var req = For(m);
                var s = await engine.SeriesAsync(m, req, req.Period, Grain, ct);
                var c = req.Compare is { } cp ? await engine.SeriesAsync(m, req, cp, Grain, ct) : null;
                first ??= s;
                series.Add(new ReportSeriesDto(m.Key, m.Label, m.Unit, s.Select(x => x.Value).ToList(),
                    c?.Select(x => x.Value).Concat(Enumerable.Repeat<double?>(null, Math.Max(0, s.Count - c.Count))).Take(s.Count).ToList()));
            }
            Charts.Add(new ReportChartDto(id, title, type, first!.Select(x => x.Label).ToList(), first!.Select(x => (string?)x.Key).ToList(), series, Grain,
                keys.Select(MetricCatalog.Get).FirstOrDefault(m => m.Source == MetricSource.Tickets)?.Key));
        }

        public async Task GroupChartAsync(string id, string title, string type, string key, string dimension, int limit)
        {
            var m = MetricCatalog.Get(key);
            var (_, rows, _) = await engine.TableAsync([m], For(m), r.Period, dimension, limit, false, ct);
            var data = rows.Where(x => x.Values[0] is not null).ToList();
            Charts.Add(new ReportChartDto(id, title, type, data.Select(x => x.Label).ToList(), data.Select(x => x.Key).ToList(),
                [new ReportSeriesDto(m.Key, m.Label, m.Unit, data.Select(x => x.Values[0]).ToList(), null)], dimension,
                m.Source == MetricSource.Tickets ? m.Key : null));
        }

        public async Task ParetoChartAsync(string id, string title, string key, string dimension)
        {
            var m = MetricCatalog.Get(key);
            var (rows, _, _) = await engine.ParetoRowsAsync(m, r, r.Period, dimension, 15, ct);
            Charts.Add(new ReportChartDto(id, title, "pareto", rows.Select(x => x.Label).ToList(), rows.Select(x => x.Key).ToList(),
            [
                new ReportSeriesDto(m.Key, m.Label, m.Unit, rows.Select(x => (double?)x.Value).ToList(), null),
                new ReportSeriesDto("cumulative_share", "Cumulative share", "percent", rows.Select(x => (double?)x.CumulativeShare).ToList(), null)
            ], dimension, m.Key));
        }

        public async Task AgeingChartAsync(string id, string title, string dimension)
        {
            var (rows, _, _) = await engine.AgeingRowsAsync(r, r.Period, dimension, ct);
            Charts.Add(new ReportChartDto(id, title, "stacked-bar", rows.Select(x => x.Label).ToList(), rows.Select(x => x.Key).ToList(),
                AnalyticsEngine.AgeBuckets.Select((bk, i) => new ReportSeriesDto(bk.Key, bk.Label, "tickets", rows.Select(x => (double?)x.Counts[i]).ToList(), null)).ToList(),
                dimension, "backlog"));
        }

        public async Task TableAsync(string id, string title, string dimension, int limit, params string[] keys)
        {
            var metrics = keys.Select(MetricCatalog.Get).ToList();
            var (columns, rows, totals) = await engine.TableAsync(metrics, r, r.Period, dimension, limit, false, ct);
            var dimLabel = MetricCatalog.DimensionsByKey[dimension].Label;
            var cols = new List<ReportColumnDto> { new("label", dimLabel, "text") };
            cols.AddRange(columns.Select(c => new ReportColumnDto(c.Key, c.Label, ColumnType(c.Unit))));
            var data = rows.Select(x =>
            {
                var d = new Dictionary<string, object?> { ["_key"] = x.Key, ["label"] = x.Label };
                for (var i = 0; i < metrics.Count; i++) d[metrics[i].Key] = x.Values[i];
                return (IReadOnlyDictionary<string, object?>)d;
            }).ToList();
            var total = new Dictionary<string, object?> { ["_key"] = null, ["_total"] = true, ["label"] = "Total" };
            for (var i = 0; i < metrics.Count; i++) total[metrics[i].Key] = totals[i];
            data.Add(total);
            Tables.Add(new ReportTableDto(id, title, cols, data, dimension, metrics[0].Key));
        }

        public Task TimeTableAsync(string id, string title, params string[] keys) => TableAsync(id, title, Grain, 400, keys);

        public async Task TicketTableAsync(string id, string title, Func<TicketMetric, bool> predicate, DateBasis basis, int take, bool includeSla = false, bool includeCsat = false)
        {
            var tickets = await engine.TicketsAsync(r, r.Period, basis, predicate, take, ct);
            var cols = new List<ReportColumnDto>
            {
                new("number", "Ticket", "ticket"), new("subject", "Subject", "text"), new("priority", "Priority", "text"), new("status", "Status", "text"),
                new("assignee", "Agent", "text"), new("customer", "Customer", "text"), new("createdAt", "Created", "datetime")
            };
            if (includeSla) cols.Add(new ReportColumnDto("breached", "Breached milestones", "text"));
            if (includeCsat) cols.Add(new ReportColumnDto("csat", "CSAT", "score"));
            var rows = tickets.Select(x =>
            {
                var metric = x.Metric;
                var dto = x.Dto!;
                var d = new Dictionary<string, object?>
                {
                    ["_ticket"] = dto.Number, ["_ticketId"] = dto.TicketId, ["number"] = dto.DisplayNumber, ["subject"] = dto.Subject, ["priority"] = dto.Priority,
                    ["status"] = dto.Status, ["assignee"] = dto.Assignee ?? "Unassigned", ["customer"] = dto.Customer, ["createdAt"] = dto.CreatedAt
                };
                if (includeSla)
                    d["breached"] = string.Join(", ", new[] { metric.FirstResponseBreached ? "First response" : null, metric.ResolutionBreached ? "Resolution" : null,
                        metric.NextResponseBreaches > 0 ? $"Next response ×{metric.NextResponseBreaches}" : null }.Where(s => s is not null));
                if (includeCsat) d["csat"] = dto.CsatScore;
                return (IReadOnlyDictionary<string, object?>)d;
            }).ToList();
            Tables.Add(new ReportTableDto(id, title, cols, rows, null, null));
        }

        public async Task RatingDistributionAsync()
        {
            var rows = await engine.TicketsAsync(r, r.Period, DateBasis.Created, m => m.CsatScore != null, int.MaxValue, ct, map: false);
            var counts = Enumerable.Range(1, 5).Select(s => (double?)rows.Count(x => x.Metric.CsatScore == s)).ToList();
            Charts.Add(new ReportChartDto("rating_distribution", "Rating distribution", "bar", ["1 star", "2 stars", "3 stars", "4 stars", "5 stars"], ["1", "2", "3", "4", "5"],
                [new ReportSeriesDto("csat_responses", "Ratings", "tickets", counts, null)], null, null));
        }

        public async Task AgentTableAsync()
        {
            // Ticket-based columns follow every filter; daily roll-up columns (replies, notes, hours, utilisation) only department/team/agent.
            string[] ticketKeys = ["resolved_tickets", "median_first_response_business_hours", "median_resolution_business_hours", "csat_avg", "sla_breaches", "reopened_tickets"];
            var ticketMetrics = ticketKeys.Select(MetricCatalog.Get).ToList();
            var (_, ticketRows, _) = await engine.TableAsync(ticketMetrics, r, r.Period, "assignee", 500, false, ct);
            var backlog = (await engine.GroupAsync(MetricCatalog.Get("backlog"), r, r.Period, "assignee", ct)).ToDictionary(g => g.Key ?? "", g => g.Value);
            var agentReq = AgentRequest;
            var daily = await engine.AgentRowsAsync(agentReq.Filters, r.Period.FromDay, r.Period.ToDay, ct);
            var capacity = await engine.CapacitiesAsync(ct);
            var byUser = daily.GroupBy(d => d.UserId).ToDictionary(g => g.Key.ToString(), g => g.ToList());
            var keys = ticketRows.Select(x => x.Key).Where(k => k is not null && k != MetricCatalog.NoneKey).Cast<string>()
                .Union(byUser.Keys).Union(backlog.Keys.Where(k => k != MetricCatalog.NoneKey && k != "")).Distinct().ToList();
            await engine.Labels.LoadAsync(ct);
            var cols = new List<ReportColumnDto>
            {
                new("label", "Agent", "text"), new("assigned", "Assigned", "number"), new("resolved_tickets", "Resolved", "number"),
                new("agent_replies", "Replies", "number"), new("notes", "Notes", "number"),
                new("median_first_response_business_hours", "Median first response (bh)", "hours"),
                new("median_resolution_business_hours", "Median resolution (bh)", "hours"), new("csat_avg", "CSAT", "score"),
                new("sla_breaches", "SLA violations", "number"), new("reopened_tickets", "Reopened", "number"), new("logged_hours", "Hours logged", "hours"),
                new("utilisation", "Utilisation", "percent"), new("open", "Open now", "number"), new("capacity", "Capacity", "number"), new("load", "Workload", "percent")
            };
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            foreach (var k in keys)
            {
                var tr = ticketRows.FirstOrDefault(x => x.Key == k);
                var d = byUser.GetValueOrDefault(k) ?? [];
                var open = backlog.TryGetValue(k, out var o) ? o ?? 0 : 0;
                var cap = Guid.TryParse(k, out var uid) && capacity.TryGetValue(uid, out var c) ? c : (int?)null;
                var row = new Dictionary<string, object?>
                {
                    ["_key"] = k, ["label"] = engine.Labels.Label("assignee", k), ["assigned"] = d.Sum(x => x.TicketsAssigned),
                    ["agent_replies"] = d.Sum(x => x.Replies), ["notes"] = d.Sum(x => x.Notes),
                    ["logged_hours"] = MetricCatalog.AggregateAgents(MetricCatalog.Get("logged_hours"), d).Value,
                    ["utilisation"] = MetricCatalog.AggregateAgents(MetricCatalog.Get("utilisation"), d).Value,
                    ["open"] = open, ["capacity"] = cap, ["load"] = cap is > 0 ? Math.Round(open * 100.0 / cap.Value, 1) : null
                };
                for (var i = 0; i < ticketKeys.Length; i++) row[ticketKeys[i]] = tr?.Values[i] ?? (ticketMetrics[i].IsAdditive ? 0 : null);
                rows.Add(row);
            }
            rows = rows.OrderByDescending(x => Convert.ToDouble(x["resolved_tickets"] ?? 0)).ThenBy(x => x["label"] as string).ToList();
            Tables.Add(new ReportTableDto("agents", "Agents", cols, rows, "assignee", "resolved_tickets"));
            Charts.Add(new ReportChartDto("utilisation_by_agent", "Utilisation per agent", "bar", rows.Select(x => (string)x["label"]!).ToList(),
                rows.Select(x => (string?)x["_key"]).ToList(), [new ReportSeriesDto("utilisation", "Utilisation", "percent", rows.Select(x => x["utilisation"] as double?).ToList(), null)],
                "assignee", null));
        }

        public async Task RecurringTablesAsync()
        {
            var rows = await engine.TicketsAsync(r, r.Period, DateBasis.Created, _ => true, int.MaxValue, ct, map: false);
            await engine.Labels.LoadAsync(ct);
            var combos = rows.Select(x => x.Metric).Where(x => x.CategoryId is not null)
                .GroupBy(x => (x.CategoryId, x.ProductId)).Where(g => g.Count() >= 2)
                .Select(g => new Dictionary<string, object?>
                {
                    ["category"] = engine.Labels.Label("category", g.Key.CategoryId?.ToString()), ["product"] = engine.Labels.Label("product", g.Key.ProductId?.ToString()),
                    ["tickets"] = g.Count(), ["customers"] = g.Where(x => x.CustomerId != null).Select(x => x.CustomerId).Distinct().Count(),
                    ["reopened"] = g.Count(x => x.ReopenCount > 0), ["open"] = g.Count(x => x.StatusCategory < StatusCategory.Resolved),
                    ["_key"] = g.Key.CategoryId?.ToString()
                }).OrderByDescending(x => (int)x["tickets"]!).Take(50).Select(x => (IReadOnlyDictionary<string, object?>)x).ToList();
            Tables.Add(new ReportTableDto("recurring", "Recurring issues (category × product, 2+ tickets)",
                [new("category", "Category", "text"), new("product", "Product", "text"), new("tickets", "Tickets", "number"), new("customers", "Customers", "number"),
                 new("reopened", "Reopened", "number"), new("open", "Open", "number")], combos, "category", "ticket_count"));

            var customerIds = rows.Select(x => x.Metric.CustomerId).Where(c => c is not null).Select(c => c!.Value).Distinct().ToList();
            await engine.Labels.LoadCustomersAsync(customerIds, ct);
            var repeat = rows.Select(x => x.Metric).Where(x => x.CustomerId is not null && x.CategoryId is not null)
                .GroupBy(x => (x.CustomerId, x.CategoryId)).Where(g => g.Count() >= 2)
                .Select(g => new Dictionary<string, object?>
                {
                    ["customer"] = engine.Labels.Label("customer", g.Key.CustomerId?.ToString()), ["category"] = engine.Labels.Label("category", g.Key.CategoryId?.ToString()),
                    ["tickets"] = g.Count(), ["last"] = g.Max(x => x.CreatedAt), ["_key"] = g.Key.CustomerId?.ToString()
                }).OrderByDescending(x => (int)x["tickets"]!).Take(50).Select(x => (IReadOnlyDictionary<string, object?>)x).ToList();
            Tables.Add(new ReportTableDto("repeat_customers", "Customers repeatedly raising the same category",
                [new("customer", "Customer", "text"), new("category", "Category", "text"), new("tickets", "Tickets", "number"), new("last", "Latest", "datetime")],
                repeat, "customer", "ticket_count"));
        }

        public async Task CustomerHealthTableAsync()
        {
            string[] keys = ["ticket_count", "backlog", "sla_breaches", "reopened_tickets", "escalated_tickets", "csat_avg", "median_resolution_business_hours"];
            var metrics = keys.Select(MetricCatalog.Get).ToList();
            var (_, rows, _) = await engine.TableAsync(metrics, r, r.Period, "customer", 100, false, ct);
            var ids = rows.Where(x => x.Key is not null && x.Key != MetricCatalog.NoneKey).Select(x => Guid.Parse(x.Key!)).ToList();
            var tiers = await engine.CustomerTiersAsync(ids, ct);
            var cols = new List<ReportColumnDto> { new("label", "Customer", "text"), new("tier", "Tier", "text") };
            cols.AddRange(metrics.Select(m => new ReportColumnDto(m.Key, m.Label, ColumnType(m.Unit))));
            cols.Add(new ReportColumnDto("health", "Health score", "number"));
            var data = rows.Where(x => x.Key != MetricCatalog.NoneKey).Select(x =>
            {
                var d = new Dictionary<string, object?> { ["_key"] = x.Key, ["label"] = x.Label, ["tier"] = x.Key is not null && Guid.TryParse(x.Key, out var g) ? tiers.GetValueOrDefault(g) : null };
                for (var i = 0; i < metrics.Count; i++) d[metrics[i].Key] = x.Values[i];
                d["health"] = HealthScore(x.Values[0], x.Values[2], x.Values[3], x.Values[4], x.Values[5]);
                return d;
            }).OrderBy(d => (double)d["health"]!).ThenByDescending(d => Convert.ToDouble(d["ticket_count"] ?? 0)).Select(d => (IReadOnlyDictionary<string, object?>)d).ToList();
            Tables.Add(new ReportTableDto("customers", "Customers (lowest health first)", cols, data, "customer", "ticket_count"));
        }

        private static string ColumnType(string unit) => unit switch { "percent" => "percent", "hours" => "hours", "minutes" => "minutes", "score" => "score", _ => "number" };
    }

    /// <summary>
    /// Customer health (0–100): 100 − 40×breach share − 20×reopen share − 20×escalation share − 10×(4 − CSAT, when below 4).
    /// Composite indicator for ranking customers; documented in docs/METRICS.md.
    /// </summary>
    public static double HealthScore(double? tickets, double? breaches, double? reopened, double? escalated, double? csat)
    {
        var n = tickets ?? 0;
        if (n <= 0) return 100;
        var score = 100 - 40 * (breaches ?? 0) / n - 20 * (reopened ?? 0) / n - 20 * (escalated ?? 0) / n - (csat is { } c && c < 4 ? 10 * (4 - c) : 0);
        return Math.Round(Math.Clamp(score, 0, 100), 1);
    }

    public static IReadOnlyList<ReportDefinitionDto> Library() => ReportCatalog.All.Select(ReportCatalog.ToDto).ToList();

}
