using System.Linq.Expressions;
using SupportDesk.Contracts.Analytics;
using SupportDesk.Domain;
using SupportDesk.Domain.Analytics;
using SupportDesk.Domain.Tickets;

namespace SupportDesk.Application.Analytics;

public enum MetricAggregation { Count, Sum, Average, Median, P90, Percent, Ratio }

/// <summary>Which instant places a ticket in a period: creation, final resolution, closure, or "open at the end of the bucket".</summary>
public enum DateBasis { Created, Resolved, Closed, OpenAt, Day }

public enum MetricSource { Tickets, AgentDaily }

/// <summary>
/// One catalogue metric. Ticket metrics are a population (SQL-translatable predicate over <see cref="TicketMetric"/>)
/// plus a per-ticket value aggregated in memory (count, sum, average, median, p90 or percent). Agent metrics aggregate
/// the daily <see cref="AgentMetric"/> roll-up. Every screen, export, scheduled report and the AI assistant reads numbers
/// through these definitions, so they always agree (docs/METRICS.md).
/// </summary>
public sealed class MetricDef
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Description { get; init; }
    public required string Group { get; init; }
    /// <summary>tickets, hours, minutes, percent, score.</summary>
    public required string Unit { get; init; }
    public required MetricAggregation Aggregation { get; init; }
    public DateBasis Basis { get; init; } = DateBasis.Created;
    public MetricSource Source { get; init; } = MetricSource.Tickets;
    public bool HigherIsBetter { get; init; } = true;
    public int Decimals { get; init; }
    /// <summary>Tickets that belong to the metric (null = every ticket of the period).</summary>
    public Expression<Func<TicketMetric, bool>>? Population { get; init; }
    /// <summary>Per-ticket value (row, reference instant). Null for counts. Percent metrics return 1 or 0.</summary>
    public Func<TicketMetric, DateTime, double?>? Value { get; init; }
    /// <summary>Agent daily metrics: aggregate of the roll-up rows.</summary>
    public Func<IReadOnlyCollection<AgentMetric>, double?>? AgentAggregate { get; init; }

    private Func<TicketMetric, bool>? _compiled;
    public Func<TicketMetric, bool> PopulationFunc => _compiled ??= Population?.Compile() ?? (_ => true);

    public bool IsAdditive => Aggregation is MetricAggregation.Count or MetricAggregation.Sum;
}

/// <summary>Breakdown dimension. Time dimensions bucket on the tenant's local calendar.</summary>
public sealed record DimensionDef(string Key, string Label, bool IsTime, Func<TicketMetric, string?>? KeyOf);

public sealed record FilterDef(string Key, string Label, string Kind);

/// <summary>The single metric definition catalogue (see docs/METRICS.md for the prose definitions).</summary>
public static class MetricCatalog
{
    public const string NoneKey = "none";

    private static double? Hours(int? minutes) => minutes is { } m ? m / 60.0 : null;
    private static double Flag(bool b) => b ? 1 : 0;

    private static readonly List<MetricDef> All =
    [
        // ── Volume ──
        new() { Key = "ticket_count", Label = "Tickets created", Group = "Volume", Unit = "tickets", Aggregation = MetricAggregation.Count,
            Description = "Tickets created in the period (spam, merged and deleted tickets are never counted)." },
        new() { Key = "resolved_tickets", Label = "Tickets resolved", Group = "Volume", Unit = "tickets", Aggregation = MetricAggregation.Count, Basis = DateBasis.Resolved,
            Population = m => m.ResolvedAt != null,
            Description = "Tickets whose final resolution falls in the period (a reopened and re-resolved ticket counts once, at its last resolution)." },
        new() { Key = "closed_tickets", Label = "Tickets closed", Group = "Volume", Unit = "tickets", Aggregation = MetricAggregation.Count, Basis = DateBasis.Closed,
            Population = m => m.ClosedAt != null, Description = "Tickets moved to a Closed status in the period." },
        new() { Key = "open_tickets", Label = "Open tickets (created in period)", Group = "Volume", Unit = "tickets", Aggregation = MetricAggregation.Count, HigherIsBetter = false,
            Population = m => m.StatusCategory < StatusCategory.Resolved,
            Description = "Tickets created in the period that are still open now (status category before Resolved)." },
        new() { Key = "backlog", Label = "Backlog", Group = "Volume", Unit = "tickets", Aggregation = MetricAggregation.Count, Basis = DateBasis.OpenAt, HigherIsBetter = false,
            Description = "Tickets open at the end of the period (or of each time bucket): created before that instant and not resolved by then." },
        new() { Key = "avg_open_age_hours", Label = "Average age of open tickets", Group = "Volume", Unit = "hours", Aggregation = MetricAggregation.Average, Basis = DateBasis.OpenAt,
            HigherIsBetter = false, Decimals = 2, Value = (m, at) => (at - m.CreatedAt).TotalHours,
            Description = "Mean calendar hours since creation of the tickets in the backlog at the end of the period." },

        // ── Response & resolution times ──
        Duration("avg_first_response_hours", "Average first response time", MetricAggregation.Average, false, DateBasis.Created),
        Duration("median_first_response_hours", "Median first response time", MetricAggregation.Median, false, DateBasis.Created),
        Duration("p90_first_response_hours", "90th percentile first response time", MetricAggregation.P90, false, DateBasis.Created),
        Duration("avg_first_response_business_hours", "Average first response time (business hours)", MetricAggregation.Average, true, DateBasis.Created),
        Duration("median_first_response_business_hours", "Median first response time (business hours)", MetricAggregation.Median, true, DateBasis.Created),
        Duration("p90_first_response_business_hours", "90th percentile first response time (business hours)", MetricAggregation.P90, true, DateBasis.Created),
        Duration("avg_resolution_hours", "Average resolution time", MetricAggregation.Average, false, DateBasis.Resolved),
        Duration("median_resolution_hours", "Median resolution time", MetricAggregation.Median, false, DateBasis.Resolved),
        Duration("p90_resolution_hours", "90th percentile resolution time", MetricAggregation.P90, false, DateBasis.Resolved),
        Duration("avg_resolution_business_hours", "Average resolution time (business hours)", MetricAggregation.Average, true, DateBasis.Resolved),
        Duration("median_resolution_business_hours", "Median resolution time (business hours)", MetricAggregation.Median, true, DateBasis.Resolved),
        Duration("p90_resolution_business_hours", "90th percentile resolution time (business hours)", MetricAggregation.P90, true, DateBasis.Resolved),
        new() { Key = "avg_handle_minutes", Label = "Average handle time", Group = "Effort", Unit = "minutes", Aggregation = MetricAggregation.Average, Basis = DateBasis.Resolved,
            HigherIsBetter = false, Decimals = 1, Population = m => m.ResolvedAt != null, Value = (m, _) => m.TimeSpentMinutes,
            Description = "Mean minutes logged in time entries per ticket resolved in the period (tickets without entries count as 0)." },
        new() { Key = "handle_hours", Label = "Handle time logged", Group = "Effort", Unit = "hours", Aggregation = MetricAggregation.Sum, Basis = DateBasis.Resolved,
            HigherIsBetter = false, Decimals = 1, Population = m => m.ResolvedAt != null, Value = (m, _) => m.TimeSpentMinutes / 60.0,
            Description = "Total hours logged in time entries on tickets resolved in the period." },

        // ── Quality ──
        new() { Key = "fcr_rate", Label = "First contact resolution", Group = "Quality", Unit = "percent", Aggregation = MetricAggregation.Percent, Basis = DateBasis.Resolved,
            Decimals = 1, Population = m => m.ResolvedAt != null, Value = (m, _) => Flag(m.FirstContactResolution),
            Description = "Share of tickets resolved in the period that were resolved with at most one public agent reply and never reopened." },
        new() { Key = "reopen_rate", Label = "Reopen rate", Group = "Quality", Unit = "percent", Aggregation = MetricAggregation.Percent, Basis = DateBasis.Resolved,
            HigherIsBetter = false, Decimals = 1, Population = m => m.ResolvedAt != null, Value = (m, _) => Flag(m.ReopenCount > 0),
            Description = "Share of tickets resolved in the period that had been reopened at least once." },
        new() { Key = "reopened_tickets", Label = "Reopened tickets", Group = "Quality", Unit = "tickets", Aggregation = MetricAggregation.Count, HigherIsBetter = false,
            Population = m => m.ReopenCount > 0, Description = "Tickets created in the period that were reopened at least once." },
        new() { Key = "escalated_tickets", Label = "Escalated tickets", Group = "Quality", Unit = "tickets", Aggregation = MetricAggregation.Count, HigherIsBetter = false,
            Population = m => m.IsEscalated, Description = "Tickets created in the period that were escalated (manually or by an SLA escalation level)." },
        new() { Key = "escalation_rate", Label = "Escalation rate", Group = "Quality", Unit = "percent", Aggregation = MetricAggregation.Percent, HigherIsBetter = false,
            Decimals = 1, Value = (m, _) => Flag(m.IsEscalated), Description = "Share of tickets created in the period that were escalated." },
        new() { Key = "transfers", Label = "Department transfers", Group = "Quality", Unit = "tickets", Aggregation = MetricAggregation.Sum, HigherIsBetter = false,
            Value = (m, _) => m.Transfers, Description = "Number of department transfers on tickets created in the period." },
        new() { Key = "frustrated_tickets", Label = "Tickets with frustrated customers", Group = "Quality", Unit = "tickets", Aggregation = MetricAggregation.Count,
            HigherIsBetter = false, Population = m => m.Sentiment == Sentiment.Frustrated,
            Description = "Tickets created in the period whose detected customer sentiment is Frustrated." },

        // ── SLA ──
        new() { Key = "sla_breaches", Label = "Tickets with SLA violations", Group = "SLA", Unit = "tickets", Aggregation = MetricAggregation.Count, HigherIsBetter = false,
            Population = m => m.SlaBreached, Description = "Tickets created in the period with at least one breached SLA milestone (first response, next response or resolution)." },
        new() { Key = "sla_compliance", Label = "SLA compliance", Group = "SLA", Unit = "percent", Aggregation = MetricAggregation.Percent, Decimals = 1,
            Population = m => m.SlaBreached || m.FirstResponseSlaMet || m.ResolutionSlaMet || m.NextResponseMet > 0, Value = (m, _) => Flag(!m.SlaBreached),
            Description = "Share of tickets created in the period with a decided SLA milestone (met or breached) that breached none." },
        new() { Key = "first_response_sla_compliance", Label = "First response SLA met", Group = "SLA", Unit = "percent", Aggregation = MetricAggregation.Percent, Decimals = 1,
            Population = m => m.FirstResponseSlaMet || m.FirstResponseBreached, Value = (m, _) => Flag(!m.FirstResponseBreached),
            Description = "Share of first-response milestones (tickets created in the period) that were met; pending milestones are excluded." },
        new() { Key = "resolution_sla_compliance", Label = "Resolution SLA met", Group = "SLA", Unit = "percent", Aggregation = MetricAggregation.Percent, Decimals = 1,
            Population = m => m.ResolutionSlaMet || m.ResolutionBreached, Value = (m, _) => Flag(!m.ResolutionBreached),
            Description = "Share of resolution milestones (tickets created in the period) that were met; running milestones are excluded until met or breached." },
        new() { Key = "next_response_breaches", Label = "Next-response breaches", Group = "SLA", Unit = "tickets", Aggregation = MetricAggregation.Sum, HigherIsBetter = false,
            Population = m => m.NextResponseBreaches > 0, Value = (m, _) => m.NextResponseBreaches,
            Description = "Breached next-response cycles on tickets created in the period." },

        // ── Satisfaction ──
        new() { Key = "csat_avg", Label = "Average CSAT", Group = "Satisfaction", Unit = "score", Aggregation = MetricAggregation.Average, Decimals = 2,
            Population = m => m.CsatScore != null, Value = (m, _) => m.CsatScore,
            Description = "Mean customer satisfaction rating (1–5) of tickets created in the period that were rated." },
        new() { Key = "csat_responses", Label = "CSAT responses", Group = "Satisfaction", Unit = "tickets", Aggregation = MetricAggregation.Count,
            Population = m => m.CsatScore != null, Description = "Rated tickets among those created in the period." },
        new() { Key = "csat_positive_rate", Label = "CSAT (positive share)", Group = "Satisfaction", Unit = "percent", Aggregation = MetricAggregation.Percent, Decimals = 1,
            Population = m => m.CsatScore != null, Value = (m, _) => Flag(m.CsatScore >= 4),
            Description = "Share of ratings of 4 or 5 among rated tickets created in the period." },

        // ── Agent productivity (daily roll-up) ──
        new() { Key = "agent_replies", Label = "Agent replies", Group = "Productivity", Unit = "replies", Aggregation = MetricAggregation.Sum, Basis = DateBasis.Day,
            Source = MetricSource.AgentDaily, AgentAggregate = rows => rows.Sum(r => r.Replies),
            Description = "Public agent replies sent on the days of the period (daily agent roll-up)." },
        new() { Key = "logged_hours", Label = "Hours logged", Group = "Productivity", Unit = "hours", Aggregation = MetricAggregation.Sum, Basis = DateBasis.Day,
            Source = MetricSource.AgentDaily, Decimals = 1, AgentAggregate = rows => rows.Sum(r => r.HandleMinutes) / 60.0,
            Description = "Hours of time entries started on the days of the period, by the agent who logged them." },
        new() { Key = "utilisation", Label = "Utilisation", Group = "Productivity", Unit = "percent", Aggregation = MetricAggregation.Ratio, Basis = DateBasis.Day,
            Source = MetricSource.AgentDaily, Decimals = 1,
            AgentAggregate = Utilisation,
            Description = "Logged minutes divided by scheduled minutes (shift, otherwise department business hours; approved leave excluded)." },
    ];

    private static double? Utilisation(IReadOnlyCollection<AgentMetric> rows)
    {
        long available = rows.Sum(r => (long)r.OnlineMinutes);
        return available > 0 ? rows.Sum(r => (long)r.HandleMinutes) * 100.0 / available : null;
    }

    private static MetricDef Duration(string key, string label, MetricAggregation agg, bool business, DateBasis basis)
    {
        var firstResponse = basis == DateBasis.Created;
        var what = firstResponse ? "creation to the first public agent reply" : "creation to the final resolution";
        var population = firstResponse ? "tickets created in the period that have a first response" : "tickets resolved in the period";
        var stat = agg switch { MetricAggregation.Median => "Median", MetricAggregation.P90 => "90th percentile (linear interpolation)", _ => "Mean" };
        Expression<Func<TicketMetric, bool>> pop = firstResponse
            ? (business ? m => m.FirstResponseBusinessMinutes != null : m => m.FirstResponseMinutes != null)
            : (business ? m => m.ResolutionBusinessMinutes != null : m => m.ResolutionMinutes != null);
        Func<TicketMetric, DateTime, double?> value = firstResponse
            ? (business ? (m, _) => Hours(m.FirstResponseBusinessMinutes) : (m, _) => Hours(m.FirstResponseMinutes))
            : (business ? (m, _) => Hours(m.ResolutionBusinessMinutes) : (m, _) => Hours(m.ResolutionMinutes));
        return new MetricDef
        {
            Key = key, Label = label, Group = firstResponse ? "Response" : "Resolution", Unit = "hours", Aggregation = agg, Basis = basis,
            HigherIsBetter = false, Decimals = 2, Population = pop, Value = value,
            Description = $"{stat} of the {(business ? "business-calendar working" : "calendar")} hours from {what}, over {population}." +
                          (business ? " Business time uses the ticket's SLA calendar (else its department calendar, else the tenant default)." : "")
        };
    }

    public static readonly IReadOnlyDictionary<string, MetricDef> Metrics = All.ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<MetricDef> List => All;

    public static readonly IReadOnlyList<DimensionDef> Dimensions =
    [
        new(NoneKey, "Total", false, null),
        new("day", "Day", true, null),
        new("week", "Week", true, null),
        new("month", "Month", true, null),
        new("department", "Department", false, m => m.DepartmentId.ToString()),
        new("team", "Team", false, m => m.TeamId?.ToString()),
        new("assignee", "Agent", false, m => m.AssigneeId?.ToString()),
        new("channel", "Channel", false, m => m.Channel.ToString()),
        new("priority", "Priority", false, m => m.Priority.ToString()),
        new("status_category", "Status", false, m => m.StatusCategory.ToString()),
        new("customer", "Customer", false, m => m.CustomerId?.ToString()),
        new("customer_tier", "Customer tier", false, m => m.CustomerTier),
        new("product", "Product", false, m => m.ProductId?.ToString()),
        new("module", "Module", false, m => m.ModuleId?.ToString()),
        new("version", "Version", false, m => m.VersionId?.ToString()),
        new("category", "Category", false, m => m.CategoryId?.ToString()),
        new("issue_type", "Issue type", false, m => m.IssueTypeId?.ToString()),
        new("root_cause", "Root cause", false, m => m.RootCause),
        new("sentiment", "Sentiment", false, m => m.Sentiment.ToString()),
    ];

    public static readonly IReadOnlyDictionary<string, DimensionDef> DimensionsByKey = Dimensions.ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Dimensions available for agent daily metrics.</summary>
    public static readonly IReadOnlySet<string> AgentDimensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { NoneKey, "day", "week", "month", "assignee" };

    public static readonly IReadOnlyList<FilterDef> Filters =
    [
        new("department", "Department", "entity"), new("team", "Team", "entity"), new("assignee", "Agent", "entity"),
        new("channel", "Channel", "enum"), new("priority", "Priority", "enum"), new("customer", "Customer", "entity"),
        new("product", "Product", "entity"), new("module", "Module", "entity"), new("version", "Version", "entity"),
        new("category", "Category", "entity"), new("tier", "Customer tier", "text"), new("status_category", "Status", "enum")
    ];

    public static readonly IReadOnlySet<string> AgentFilters = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "department", "team", "assignee" };

    public static readonly IReadOnlyList<(string Key, string Label)> Periods =
    [
        ("today", "Today"), ("yesterday", "Yesterday"), ("this_week", "This week"), ("last_week", "Last week"), ("last_7_days", "Last 7 days"),
        ("this_month", "This month"), ("last_month", "Last month"), ("last_30_days", "Last 30 days"), ("last_90_days", "Last 90 days"),
        ("this_quarter", "This quarter"), ("this_year", "This year"), ("last_365_days", "Last 365 days"), ("all_time", "All time"), ("custom", "Custom range")
    ];

    public static bool IsTime(string dimension) => dimension is "day" or "week" or "month";

    public static MetricDef Get(string? key) =>
        key is not null && Metrics.TryGetValue(key.Trim(), out var m) ? m
            : throw new DomainException("analytics_unknown_metric", $"'{key}' is not a catalogue metric.", 400);

    public static IReadOnlyList<string> DimensionsFor(MetricDef m) =>
        m.Source == MetricSource.AgentDaily ? Dimensions.Where(d => AgentDimensions.Contains(d.Key)).Select(d => d.Key).ToList() : Dimensions.Select(d => d.Key).ToList();

    public static IReadOnlyList<string> FiltersFor(MetricDef m) =>
        m.Source == MetricSource.AgentDaily ? Filters.Where(f => AgentFilters.Contains(f.Key)).Select(f => f.Key).ToList() : Filters.Select(f => f.Key).ToList();

    public static MetricDefinitionDto ToDto(MetricDef m) => new(m.Key, m.Label, m.Description, m.Group, m.Unit, m.Aggregation.ToString().ToLowerInvariant(),
        m.Basis switch { DateBasis.Created => "created", DateBasis.Resolved => "resolved", DateBasis.Closed => "closed", DateBasis.OpenAt => "open_at_end", _ => "day" },
        m.HigherIsBetter, m.Source == MetricSource.Tickets ? "tickets" : "agent_daily", DimensionsFor(m), FiltersFor(m), m.Source == MetricSource.Tickets);

    /// <summary>Aggregates the population of <paramref name="rows"/> (already limited to the period) at <paramref name="reference"/>.</summary>
    public static (double? Value, int Count) Aggregate(MetricDef m, IEnumerable<TicketMetric> rows, DateTime reference)
    {
        var pop = rows.Where(m.PopulationFunc).ToList();
        if (m.Aggregation == MetricAggregation.Count) return (pop.Count, pop.Count);
        var values = pop.Select(r => m.Value!(r, reference)).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        double? result = m.Aggregation switch
        {
            MetricAggregation.Sum => values.Sum(),
            MetricAggregation.Average => values.Count == 0 ? null : values.Average(),
            MetricAggregation.Median => Percentile(values, 0.5),
            MetricAggregation.P90 => Percentile(values, 0.9),
            MetricAggregation.Percent => values.Count == 0 ? null : values.Average() * 100,
            _ => null
        };
        return (Round(m, result), pop.Count);
    }

    public static (double? Value, int Count) AggregateAgents(MetricDef m, IReadOnlyCollection<AgentMetric> rows) =>
        (Round(m, rows.Count == 0 && m.Aggregation == MetricAggregation.Ratio ? null : m.AgentAggregate!(rows)), rows.Count);

    public static double? Round(MetricDef m, double? v) => v is { } x ? Math.Round(x, m.Decimals, MidpointRounding.AwayFromZero) : null;

    /// <summary>PERCENTILE_CONT semantics: linear interpolation between the closest ranks.</summary>
    public static double? Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToArray();
        var rank = p * (sorted.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    /// <summary>Percent change from <paramref name="compare"/> to <paramref name="value"/> (null when not computable).</summary>
    public static double? Delta(double? value, double? compare) =>
        value is { } v && compare is { } c && Math.Abs(c) > 1e-9 ? Math.Round((v - c) / Math.Abs(c) * 100, 1) : null;
}
