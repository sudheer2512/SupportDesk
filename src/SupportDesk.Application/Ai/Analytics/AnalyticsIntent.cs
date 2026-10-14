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
    public static readonly IReadOnlyDictionary<string, MetricDefinition> Metrics = new Dictionary<string, MetricDefinition>(StringComparer.OrdinalIgnoreCase)
    {
        ["ticket_count"] = new("ticket_count", "Tickets created", MetricKind.Count, "tickets", false),
        ["open_tickets"] = new("open_tickets", "Open tickets", MetricKind.Count, "tickets", false),
        ["resolved_tickets"] = new("resolved_tickets", "Tickets resolved", MetricKind.Count, "tickets", true),
        ["sla_breaches"] = new("sla_breaches", "Tickets with SLA violations", MetricKind.Count, "tickets", false),
        ["avg_resolution_hours"] = new("avg_resolution_hours", "Average resolution time", MetricKind.Average, "hours", true),
        ["avg_first_response_hours"] = new("avg_first_response_hours", "Average first response time", MetricKind.Average, "hours", false),
        ["csat_avg"] = new("csat_avg", "Average CSAT", MetricKind.Average, "score", false),
        ["reopened_tickets"] = new("reopened_tickets", "Reopened tickets", MetricKind.Count, "tickets", false),
        ["escalated_tickets"] = new("escalated_tickets", "Escalated tickets", MetricKind.Count, "tickets", false),
        ["frustrated_tickets"] = new("frustrated_tickets", "Tickets with frustrated customers", MetricKind.Count, "tickets", false)
    };

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
            Limit = Math.Clamp(intent.Limit <= 0 ? 10 : intent.Limit, 1, IsTimeDimension(dimension) ? 400 : 50)
        };
    }

    /// <summary>Period boundaries [from, to) in UTC, computed on the tenant's local calendar.</summary>
    public static (DateTime From, DateTime To) ResolvePeriod(string period, DateTime nowUtc, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var today = local.Date;
        DateTime Utc(DateTime localDate) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified), tz);
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var quarterStart = new DateTime(today.Year, (today.Month - 1) / 3 * 3 + 1, 1);
        return period switch
        {
            "today" => (Utc(today), nowUtc.AddSeconds(1)),
            "yesterday" => (Utc(today.AddDays(-1)), Utc(today)),
            "this_week" => (Utc(weekStart), nowUtc.AddSeconds(1)),
            "last_week" => (Utc(weekStart.AddDays(-7)), Utc(weekStart)),
            "this_month" => (Utc(monthStart), nowUtc.AddSeconds(1)),
            "last_month" => (Utc(monthStart.AddMonths(-1)), Utc(monthStart)),
            "last_7_days" => (Utc(today.AddDays(-6)), nowUtc.AddSeconds(1)),
            "last_90_days" => (Utc(today.AddDays(-89)), nowUtc.AddSeconds(1)),
            "this_quarter" => (Utc(quarterStart), nowUtc.AddSeconds(1)),
            "this_year" => (Utc(new DateTime(today.Year, 1, 1)), nowUtc.AddSeconds(1)),
            "last_365_days" => (Utc(today.AddDays(-364)), nowUtc.AddSeconds(1)),
            "all_time" => (new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), nowUtc.AddSeconds(1)),
            _ => (Utc(today.AddDays(-29)), nowUtc.AddSeconds(1))
        };
    }

    public static string PeriodLabel(string period) => period switch
    {
        "today" => "today", "yesterday" => "yesterday", "this_week" => "this week", "last_week" => "last week", "this_month" => "this month",
        "last_month" => "last month", "last_7_days" => "the last 7 days", "last_90_days" => "the last 90 days", "this_quarter" => "this quarter",
        "this_year" => "this year", "last_365_days" => "the last 365 days", "all_time" => "all time", _ => "the last 30 days"
    };

    private static DomainException Unsupported(string message) => new("nl_unsupported", message, 422);
}
