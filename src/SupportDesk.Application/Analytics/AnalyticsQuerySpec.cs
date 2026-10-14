using System.Globalization;
using SupportDesk.Contracts.Analytics;
using SupportDesk.Domain;
using SupportDesk.Domain.Analytics;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;
using SupportDesk.Application.Tickets;

namespace SupportDesk.Application.Analytics;

/// <summary>A resolved period: [From, To) in UTC plus the inclusive local days it covers.</summary>
public sealed record PeriodRange(string Key, string Label, DateTime From, DateTime To, DateOnly FromDay, DateOnly ToDay)
{
    public PeriodRangeDto ToDto() => new(Key, Label, From, To, FromDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ToDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
}

/// <summary>Period keys resolved on the tenant's local calendar (the AI assistant uses the same resolution).</summary>
public static class AnalyticsPeriods
{
    public static readonly DateTime Epoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static bool IsKnown(string key) => MetricCatalog.Periods.Any(p => p.Key == key);

    public static string Label(string key) => MetricCatalog.Periods.FirstOrDefault(p => p.Key == key).Label ?? key;

    /// <summary>Period boundaries [from, to) in UTC.</summary>
    public static (DateTime From, DateTime To) Resolve(string period, DateTime nowUtc, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var today = local.Date;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var quarterStart = new DateTime(today.Year, (today.Month - 1) / 3 * 3 + 1, 1);
        var end = nowUtc.AddSeconds(1);
        return period switch
        {
            "today" => (ToUtc(today, tz), end),
            "yesterday" => (ToUtc(today.AddDays(-1), tz), ToUtc(today, tz)),
            "this_week" => (ToUtc(weekStart, tz), end),
            "last_week" => (ToUtc(weekStart.AddDays(-7), tz), ToUtc(weekStart, tz)),
            "this_month" => (ToUtc(monthStart, tz), end),
            "last_month" => (ToUtc(monthStart.AddMonths(-1), tz), ToUtc(monthStart, tz)),
            "last_7_days" => (ToUtc(today.AddDays(-6), tz), end),
            "last_90_days" => (ToUtc(today.AddDays(-89), tz), end),
            "this_quarter" => (ToUtc(quarterStart, tz), end),
            "this_year" => (ToUtc(new DateTime(today.Year, 1, 1), tz), end),
            "last_365_days" => (ToUtc(today.AddDays(-364), tz), end),
            "all_time" => (Epoch, end),
            _ => (ToUtc(today.AddDays(-29), tz), end)
        };
    }

    public static DateTime ToUtc(DateTime localDate, TimeZoneInfo tz)
    {
        var local = DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified);
        while (tz.IsInvalidTime(local)) local = local.AddMinutes(30);
        return TimeZoneInfo.ConvertTimeToUtc(local, tz);
    }

    public static DateOnly LocalDay(DateTime utc, TimeZoneInfo tz) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz));

    public static DateTime DayStartUtc(DateOnly day, TimeZoneInfo tz) => ToUtc(day.ToDateTime(TimeOnly.MinValue), tz);

    public static PeriodRange Build(string? key, string? from, string? to, DateTime nowUtc, TimeZoneInfo tz)
    {
        key = string.IsNullOrWhiteSpace(key) ? "last_30_days" : key.Trim().ToLowerInvariant();
        if (!IsKnown(key)) throw Invalid("period", $"'{key}' is not a supported period.");
        DateTime f, t;
        if (key == "custom")
        {
            if (!DateOnly.TryParseExact(from ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fd))
                throw Invalid("from", "Enter a start date (yyyy-MM-dd).");
            if (!DateOnly.TryParseExact(to ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var td))
                throw Invalid("to", "Enter an end date (yyyy-MM-dd).");
            if (td < fd) throw Invalid("to", "The end date must be on or after the start date.");
            if (td.DayNumber - fd.DayNumber > 3 * 366) throw Invalid("to", "Custom ranges may span at most three years.");
            f = DayStartUtc(fd, tz);
            t = DayStartUtc(td.AddDays(1), tz);
            var label = $"{fd:d MMM yyyy} – {td:d MMM yyyy}";
            return new PeriodRange(key, label, f, t, fd, td);
        }
        (f, t) = Resolve(key, nowUtc, tz);
        return new PeriodRange(key, Label(key), f, t, LocalDay(f, tz), LocalDay(t.AddTicks(-1), tz));
    }

    /// <summary>previous = the same length immediately before; last_year = the same local dates one year earlier.</summary>
    public static PeriodRange? Comparison(PeriodRange p, string? compare, TimeZoneInfo tz)
    {
        switch ((compare ?? "").Trim().ToLowerInvariant())
        {
            case "" or "none":
                return null;
            case "previous":
            {
                if (p.Key == "all_time") return null;
                var length = p.To - p.From;
                var f = p.From - length;
                return new PeriodRange("previous", "Previous period", f, p.From, LocalDay(f, tz), LocalDay(p.From.AddTicks(-1), tz));
            }
            case "last_year":
            {
                if (p.Key == "all_time") return null;
                var lf = TimeZoneInfo.ConvertTimeFromUtc(p.From, tz).AddYears(-1);
                var lt = TimeZoneInfo.ConvertTimeFromUtc(p.To, tz).AddYears(-1);
                var f = ToUtc(lf, tz);
                var t = ToUtc(lt, tz);
                return new PeriodRange("last_year", "Same period last year", f, t, LocalDay(f, tz), LocalDay(t.AddTicks(-1), tz));
            }
            default:
                throw Invalid("compare", "Compare must be 'previous' or 'last_year'.");
        }
    }

    internal static DomainException Invalid(string field, string message) =>
        new ValidationException(new Dictionary<string, string> { [field] = message });
}

/// <summary>Validated, typed filters (whitelisted fields; ids parsed as GUIDs, enums by name).</summary>
public sealed class AnalyticsFilters
{
    public List<Guid> Departments { get; } = [];
    public List<Guid> Teams { get; } = [];
    public List<Guid> Assignees { get; } = [];
    public List<Channel> Channels { get; } = [];
    public List<TicketPriority> Priorities { get; } = [];
    public List<Guid> Customers { get; } = [];
    public List<Guid> Products { get; } = [];
    public List<Guid> Modules { get; } = [];
    public List<Guid> Versions { get; } = [];
    public List<Guid> Categories { get; } = [];
    public List<string> Tiers { get; } = [];
    public List<StatusCategory> StatusCategories { get; } = [];

    public bool IsEmpty => Departments.Count + Teams.Count + Assignees.Count + Channels.Count + Priorities.Count + Customers.Count + Products.Count +
                           Modules.Count + Versions.Count + Categories.Count + Tiers.Count + StatusCategories.Count == 0;

    /// <summary>Raw field → comma-separated value map (query string, dashboard widget, export/schedule parameters).</summary>
    public static AnalyticsFilters Parse(IReadOnlyDictionary<string, string?> raw)
    {
        var f = new AnalyticsFilters();
        foreach (var (field, value) in raw)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var key = field.Trim().ToLowerInvariant().Replace("statuscategory", "status_category");
            var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (parts.Count > 50) throw AnalyticsPeriods.Invalid(key, "At most 50 values per filter.");
            switch (key)
            {
                case "department": f.Departments.AddRange(Guids(key, parts)); break;
                case "team": f.Teams.AddRange(Guids(key, parts)); break;
                case "assignee": f.Assignees.AddRange(Guids(key, parts)); break;
                case "customer": f.Customers.AddRange(Guids(key, parts)); break;
                case "product": f.Products.AddRange(Guids(key, parts)); break;
                case "module": f.Modules.AddRange(Guids(key, parts)); break;
                case "version": f.Versions.AddRange(Guids(key, parts)); break;
                case "category": f.Categories.AddRange(Guids(key, parts)); break;
                case "channel": f.Channels.AddRange(Enums<Channel>(key, parts)); break;
                case "priority": f.Priorities.AddRange(Enums<TicketPriority>(key, parts)); break;
                case "status_category": f.StatusCategories.AddRange(Enums<StatusCategory>(key, parts)); break;
                case "tier":
                    if (parts.Any(p => p.Length > 60)) throw AnalyticsPeriods.Invalid(key, "Tier names are at most 60 characters.");
                    f.Tiers.AddRange(parts);
                    break;
                default:
                    throw AnalyticsPeriods.Invalid(key, $"Filtering by '{field}' is not supported.");
            }
        }
        return f;
    }

    public static AnalyticsFilters From(AnalyticsQuery q) => Parse(new Dictionary<string, string?>
    {
        ["department"] = q.Department, ["team"] = q.Team, ["assignee"] = q.Assignee, ["channel"] = q.Channel, ["priority"] = q.Priority,
        ["customer"] = q.Customer, ["product"] = q.Product, ["module"] = q.Module, ["version"] = q.Version, ["category"] = q.Category,
        ["tier"] = q.Tier, ["status_category"] = q.StatusCategory
    });

    private static IEnumerable<Guid> Guids(string field, List<string> parts) =>
        parts.Select(p => Guid.TryParse(p, out var g) ? g : throw AnalyticsPeriods.Invalid(field, $"'{p}' is not a valid id."));

    private static IEnumerable<T> Enums<T>(string field, List<string> parts) where T : struct, Enum =>
        parts.Select(p => Enum.TryParse<T>(p, true, out var v) && Enum.IsDefined(v) ? v : throw AnalyticsPeriods.Invalid(field, $"'{p}' is not a valid {field.Replace('_', ' ')}."));

    public IQueryable<TicketMetric> Apply(IQueryable<TicketMetric> q)
    {
        if (Departments.Count > 0) q = q.Where(m => Departments.Contains(m.DepartmentId));
        if (Teams.Count > 0) q = q.Where(m => m.TeamId != null && Teams.Contains(m.TeamId.Value));
        if (Assignees.Count > 0) q = q.Where(m => m.AssigneeId != null && Assignees.Contains(m.AssigneeId.Value));
        if (Channels.Count > 0) q = q.Where(m => Channels.Contains(m.Channel));
        if (Priorities.Count > 0) q = q.Where(m => Priorities.Contains(m.Priority));
        if (Customers.Count > 0) q = q.Where(m => m.CustomerId != null && Customers.Contains(m.CustomerId.Value));
        if (Products.Count > 0) q = q.Where(m => m.ProductId != null && Products.Contains(m.ProductId.Value));
        if (Modules.Count > 0) q = q.Where(m => m.ModuleId != null && Modules.Contains(m.ModuleId.Value));
        if (Versions.Count > 0) q = q.Where(m => m.VersionId != null && Versions.Contains(m.VersionId.Value));
        if (Categories.Count > 0) q = q.Where(m => (m.CategoryId != null && Categories.Contains(m.CategoryId.Value)) || (m.SubcategoryId != null && Categories.Contains(m.SubcategoryId.Value)));
        if (Tiers.Count > 0) q = q.Where(m => m.CustomerTier != null && Tiers.Contains(m.CustomerTier));
        if (StatusCategories.Count > 0) q = q.Where(m => StatusCategories.Contains(m.StatusCategory));
        return q;
    }

    /// <summary>True when filters other than department/team/agent are set (agent daily metrics cannot honour them).</summary>
    public bool HasTicketOnlyFilters => Channels.Count + Priorities.Count + Customers.Count + Products.Count + Modules.Count + Versions.Count +
                                        Categories.Count + Tiers.Count + StatusCategories.Count > 0;

    /// <summary>Human-readable description of the applied filters (export headers).</summary>
    public IReadOnlyList<string> Describe(Func<string, Guid, string?> name)
    {
        var list = new List<string>();
        void Ids(string label, string field, List<Guid> ids) { if (ids.Count > 0) list.Add($"{label}: {string.Join(", ", ids.Select(i => name(field, i) ?? i.ToString()))}"); }
        Ids("Department", "department", Departments);
        Ids("Team", "team", Teams);
        Ids("Agent", "assignee", Assignees);
        if (Channels.Count > 0) list.Add($"Channel: {string.Join(", ", Channels)}");
        if (Priorities.Count > 0) list.Add($"Priority: {string.Join(", ", Priorities)}");
        Ids("Customer", "customer", Customers);
        Ids("Product", "product", Products);
        Ids("Module", "module", Modules);
        Ids("Version", "version", Versions);
        Ids("Category", "category", Categories);
        if (Tiers.Count > 0) list.Add($"Tier: {string.Join(", ", Tiers)}");
        if (StatusCategories.Count > 0) list.Add($"Status: {string.Join(", ", StatusCategories)}");
        return list;
    }
}

/// <summary>
/// Analytics data scope. Agents count only tickets they may see (ticket data scope); without analytics.team the scope is
/// personal — tickets assigned to the caller. Supervisors and analysts (analytics.team) see their full ticket scope.
/// </summary>
public static class AnalyticsScope
{
    public static bool IsTeam(RequestContext ctx) => ctx.IsSystem || ctx.Has(Permissions.AnalyticsTeam);

    public static IQueryable<Ticket> Tickets(DeskDbContext db, RequestContext ctx)
    {
        var q = db.Tickets.VisibleTo(ctx, db);
        if (!IsTeam(ctx))
        {
            var uid = ctx.UserId ?? Guid.Empty;
            q = q.Where(t => t.AssigneeId == uid);
        }
        return q;
    }

    public static IQueryable<TicketMetric> Metrics(DeskDbContext db, RequestContext ctx)
    {
        var visible = Tickets(db, ctx);
        return db.TicketMetrics.Where(m => !m.Excluded && visible.Any(t => t.Id == m.TicketId));
    }
}
