using SupportDesk.Domain;

namespace SupportDesk.Application.Ai.Analytics;

public sealed record AnalyticsFilter(string Field, string Value);

/// <summary>
/// Structured question produced by the NL parser (local rules or a model in JSON mode). It is only ever interpreted
/// through <see cref="AnalyticsCatalog"/> whitelists by <see cref="SafeAnalyticsQuery"/>; nothing from it reaches SQL text.
/// </summary>
public sealed class AnalyticsIntent
{
    public string Metric { get; set; } = "ticket_count";
    public string Dimension { get; set; } = "none";
    public List<AnalyticsFilter> Filters { get; set; } = [];
    public string Period { get; set; } = "last_30_days";
    public string Sort { get; set; } = "desc";
    public int Limit { get; set; } = 10;
}

public enum MetricKind { Count, Average }

public sealed record MetricDefinition(string Key, string Label, MetricKind Kind, string Unit, bool ByResolvedDate);

public static class AnalyticsCatalog
{
    /// <summary>Metrics the assistant can answer. Definitions (label, unit, aggregation, date basis) come from the single
    /// analytics <see cref="global::SupportDesk.Application.Analytics.MetricCatalog"/> so AI answers match reports and dashboards.</summary>
    public static readonly IReadOnlyList<string> MetricKeys =
    [
        "ticket_count", "open_tickets", "resolved_tickets", "sla_breaches", "avg_resolution_hours", "avg_first_response_hours", "csat_avg",
        "reopened_tickets", "escalated_tickets", "frustrated_tickets"
    ];

    public static readonly IReadOnlyDictionary<string, MetricDefinition> Metrics = MetricKeys
        .Select(k => global::SupportDesk.Application.Analytics.MetricCatalog.Metrics[k])
        .ToDictionary(m => m.Key, m => new MetricDefinition(m.Key, m.Label,
            m.Aggregation == global::SupportDesk.Application.Analytics.MetricAggregation.Count ? MetricKind.Count : MetricKind.Average, m.Unit,
            m.Basis == global::SupportDesk.Application.Analytics.DateBasis.Resolved), StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlyDictionary<string, string> Dimensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["none"] = "Total", ["customer"] = "Customer", ["product"] = "Product", ["category"] = "Category", ["priority"] = "Priority",
        ["channel"] = "Channel", ["department"] = "Department", ["assignee"] = "Agent", ["team"] = "Team", ["status"] = "Status",
        ["sentiment"] = "Sentiment", ["day"] = "Day", ["week"] = "Week", ["month"] = "Month"
    };

    public static readonly IReadOnlyList<string> Periods =
    [
        "today", "yesterday", "this_week", "last_week", "this_month", "last_month", "last_7_days", "last_30_days", "last_90_days",
        "this_quarter", "this_year", "last_365_days", "all_time"
    ];

    /// <summary>Filterable fields. Entity fields take ids; enum fields take enum names.</summary>
    public static readonly IReadOnlyList<string> FilterFields =
        ["customer", "product", "category", "department", "assignee", "team", "priority", "channel", "status_category", "sentiment"];

    public static readonly IReadOnlySet<string> EntityFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "customer", "product", "category", "department", "assignee", "team" };

    public static bool IsTimeDimension(string dimension) => dimension is "day" or "week" or "month";

    /// <summary>Normalises and validates an intent. Unknown metrics, dimensions, periods or filters are rejected.</summary>
    public static AnalyticsIntent Validate(AnalyticsIntent? intent)
    {
        if (intent is null) throw Unsupported("The question could not be understood.");
        var metric = (intent.Metric ?? "").Trim().ToLowerInvariant();
        if (!Metrics.ContainsKey(metric)) throw Unsupported($"'{intent.Metric}' is not a supported metric. Try: {string.Join(", ", Metrics.Keys)}.");
        var dimension = string.IsNullOrWhiteSpace(intent.Dimension) ? "none" : intent.Dimension.Trim().ToLowerInvariant();
        if (!Dimensions.ContainsKey(dimension)) throw Unsupported($"'{intent.Dimension}' is not a supported breakdown. Try: {string.Join(", ", Dimensions.Keys)}.");
        var period = string.IsNullOrWhiteSpace(intent.Period) ? "last_30_days" : intent.Period.Trim().ToLowerInvariant();
        if (!Periods.Contains(period)) throw Unsupported($"'{intent.Period}' is not a supported period.");
        var sort = string.Equals(intent.Sort, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";
        var filters = new List<AnalyticsFilter>();
        foreach (var f in intent.Filters ?? [])
        {
            var field = (f.Field ?? "").Trim().ToLowerInvariant();
            if (!FilterFields.Contains(field)) throw Unsupported($"Filtering by '{f.Field}' is not supported.");
            var value = (f.Value ?? "").Trim();
            if (value.Length == 0 || value.Length > 100) throw Unsupported($"Invalid value for filter '{field}'.");
            if (EntityFields.Contains(field) && !Guid.TryParse(value, out _)) throw Unsupported($"Unknown {field} '{value}'.");
            switch (field)
            {
                case "priority" when !Enum.TryParse<TicketPriority>(value, true, out _):
                case "channel" when !Enum.TryParse<Channel>(value, true, out _):
                case "status_category" when !Enum.TryParse<StatusCategory>(value, true, out _):
                case "sentiment" when !Enum.TryParse<Domain.Tickets.Sentiment>(value, true, out _):
                    throw Unsupported($"'{value}' is not a valid {field}.");
            }
            if (filters.Count < 8 && filters.All(x => !(x.Field == field && x.Value.Equals(value, StringComparison.OrdinalIgnoreCase))))
                filters.Add(new AnalyticsFilter(field, value));
        }
        return new AnalyticsIntent
        {
            Metric = metric, Dimension = dimension, Filters = filters, Period = period, Sort = sort,
            // Time series always cover the whole period; "top N" applies to categorical breakdowns.
            Limit = IsTimeDimension(dimension) ? 400 : Math.Clamp(intent.Limit <= 0 ? 10 : intent.Limit, 1, 50)
        };
    }

    /// <summary>Period boundaries [from, to) in UTC, computed on the tenant's local calendar (shared with the analytics module).</summary>
    public static (DateTime From, DateTime To) ResolvePeriod(string period, DateTime nowUtc, TimeZoneInfo tz) =>
        global::SupportDesk.Application.Analytics.AnalyticsPeriods.Resolve(period, nowUtc, tz);

    public static string PeriodLabel(string period) => period switch
    {
        "today" => "today", "yesterday" => "yesterday", "this_week" => "this week", "last_week" => "last week", "this_month" => "this month",
        "last_month" => "last month", "last_7_days" => "the last 7 days", "last_90_days" => "the last 90 days", "this_quarter" => "this quarter",
        "this_year" => "this year", "last_365_days" => "the last 365 days", "all_time" => "all time", _ => "the last 30 days"
    };

    private static DomainException Unsupported(string message) => new("nl_unsupported", message, 422);
}
