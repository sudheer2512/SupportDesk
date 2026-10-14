using System.Globalization;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Contracts.Analytics;
using SupportDesk.Domain;
using SupportDesk.Domain.Analytics;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Analytics;

/// <summary>A validated analytics request: tenant time zone, period, optional comparison period and typed filters.</summary>
public sealed record AnalyticsRequest(TimeZoneInfo TimeZone, PeriodRange Period, PeriodRange? Compare, AnalyticsFilters Filters, DateTime Now);

public sealed record SeriesBucket(DateOnly Start, string Key, string Label, double? Value, int Count);

public sealed record GroupValue(string? Key, double? Value, int Count);

/// <summary>
/// Whitelisted metric × dimension × filter × period engine over <see cref="TicketMetric"/> (and the daily agent roll-up).
/// Clients never send SQL: metrics, dimensions and filters are catalogue keys mapped to compiled expressions; values
/// are parsed into typed parameters. Rows are always limited to the caller's analytics scope (<see cref="AnalyticsScope"/>).
/// </summary>
public sealed class AnalyticsEngine(DeskDbContext db, RequestContext ctx, AnalyticsLabels labels)
{
    public const int MaxRows = 200_000;
    private const int MaxBuckets = 400;
    private readonly Dictionary<(DateBasis, DateTime, DateTime, AnalyticsFilters), List<TicketMetric>> _rows = new();
    private readonly Dictionary<(DateOnly, DateOnly, AnalyticsFilters), List<AgentMetric>> _agentRows = new();
    private TimeZoneInfo? _tz;

    public bool Truncated { get; private set; }
    public AnalyticsLabels Labels => labels;

    public async Task<TimeZoneInfo> TimeZoneAsync(CancellationToken ct)
    {
        if (_tz is not null) return _tz;
        var id = await db.Tenants.AsNoTracking().Where(t => t.Id == ctx.TenantId).Select(t => t.DefaultTimeZone).FirstOrDefaultAsync(ct);
        return _tz = WorkingCalendar.ResolveTimeZone(id);
    }

    public async Task<AnalyticsRequest> RequestAsync(AnalyticsQuery q, CancellationToken ct)
    {
        ctx.Require(Permissions.AnalyticsView);
        var tz = await TimeZoneAsync(ct);
        var now = db.Clock.UtcNow;
        var period = AnalyticsPeriods.Build(q.Period, q.From, q.To, now, tz);
        return new AnalyticsRequest(tz, period, AnalyticsPeriods.Comparison(period, q.Compare, tz), AnalyticsFilters.From(q), now);
    }

    /// <summary>Builds an <see cref="AnalyticsQuery"/> from stored parameters (dashboard widgets, exports, schedules).</summary>
    public static AnalyticsQuery QueryFrom(IReadOnlyDictionary<string, string>? p)
    {
        string? G(string k) => p is not null && p.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        return new AnalyticsQuery
        {
            Period = G("period"), From = G("from"), To = G("to"), Compare = G("compare"), Dimension = G("dimension"),
            Department = G("department"), Team = G("team"), Assignee = G("assignee"), Channel = G("channel"), Priority = G("priority"),
            Customer = G("customer"), Product = G("product"), Module = G("module"), Version = G("version"), Category = G("category"),
            Tier = G("tier"), StatusCategory = G("status_category") ?? G("statusCategory")
        };
    }

    /// <summary>Validates stored parameters (only known keys, valid values).</summary>
    public static IReadOnlyDictionary<string, string> NormaliseParameters(IReadOnlyDictionary<string, string>? p)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "period", "from", "to", "compare", "dimension" };
        foreach (var f in MetricCatalog.Filters) allowed.Add(f.Key);
        var result = new Dictionary<string, string>();
        foreach (var (k, v) in p ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            var key = k.Trim().ToLowerInvariant() == "statuscategory" ? "status_category" : k.Trim().ToLowerInvariant();
            if (!allowed.Contains(key)) throw AnalyticsPeriods.Invalid(k, $"'{k}' is not a report parameter.");
            if (v.Length > 2000) throw AnalyticsPeriods.Invalid(k, "Value too long.");
            result[key] = v.Trim();
        }
        // Parse once to validate filter values and the period.
        var q = QueryFrom(result);
        AnalyticsFilters.From(q);
        AnalyticsPeriods.Build(q.Period, q.From, q.To, DateTime.UtcNow, TimeZoneInfo.Utc);
        AnalyticsPeriods.Comparison(AnalyticsPeriods.Build(q.Period, q.From, q.To, DateTime.UtcNow, TimeZoneInfo.Utc), q.Compare, TimeZoneInfo.Utc);
        return result;
    }

    // ───────────────────────────── Row loading ─────────────────────────────

    private async Task<List<TicketMetric>> RowsAsync(AnalyticsFilters filters, DateBasis basis, PeriodRange p, DateTime now, CancellationToken ct)
    {
        var to = basis == DateBasis.OpenAt && p.To > now ? now : p.To;
        var key = (basis, p.From, to, filters);
        if (_rows.TryGetValue(key, out var cached)) return cached;
        var q = filters.Apply(AnalyticsScope.Metrics(db, ctx).AsNoTracking());
        q = basis switch
        {
            DateBasis.Resolved => q.Where(m => m.ResolvedAt >= p.From && m.ResolvedAt < to),
            DateBasis.Closed => q.Where(m => m.ClosedAt >= p.From && m.ClosedAt < to),
            DateBasis.OpenAt => q.Where(m => m.CreatedAt < to && (m.ResolvedAt == null || m.ResolvedAt >= p.From)),
            _ => q.Where(m => m.CreatedAt >= p.From && m.CreatedAt < to)
        };
        var list = await q.Take(MaxRows + 1).ToListAsync(ct);
        if (list.Count > MaxRows)
        {
            Truncated = true;
            list.RemoveAt(list.Count - 1);
        }
        return _rows[key] = list;
    }

    public async Task<List<AgentMetric>> AgentRowsAsync(AnalyticsFilters filters, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var key = (from, to, filters);
        if (_agentRows.TryGetValue(key, out var cached)) return cached;
        var q = db.AgentMetrics.AsNoTracking().Where(m => m.Day >= from && m.Day <= to);
        var uid = ctx.UserId ?? Guid.Empty;
        if (!AnalyticsScope.IsTeam(ctx)) q = q.Where(m => m.UserId == uid);
        else if (!ctx.IsSystem && !(ctx.ActorType == ActorType.ApiClient && ctx.UserId is null))
        {
            var depts = ctx.DepartmentIds.ToList();
            var teams = ctx.TeamIds.ToList();
            q = ctx.TicketScope switch
            {
                TicketScope.All => q,
                TicketScope.Departments => q.Where(m => m.UserId == uid || db.AgentDepartments.Any(d => d.UserId == m.UserId && depts.Contains(d.DepartmentId))),
                TicketScope.Team => q.Where(m => m.UserId == uid || db.TeamMembers.Any(t => t.UserId == m.UserId && teams.Contains(t.TeamId))),
                _ => q.Where(m => m.UserId == uid)
            };
        }
        if (filters.Assignees.Count > 0) q = q.Where(m => filters.Assignees.Contains(m.UserId));
        if (filters.Departments.Count > 0) q = q.Where(m => db.AgentDepartments.Any(d => d.UserId == m.UserId && filters.Departments.Contains(d.DepartmentId)));
        if (filters.Teams.Count > 0) q = q.Where(m => db.TeamMembers.Any(t => t.UserId == m.UserId && filters.Teams.Contains(t.TeamId)));
        return _agentRows[key] = await q.ToListAsync(ct);
    }

    private static bool OpenAt(TicketMetric m, DateTime at) => m.CreatedAt < at && (m.ResolvedAt == null || m.ResolvedAt >= at);

    private static DateTime Reference(PeriodRange p, DateTime now) => p.To > now ? now : p.To;

    private static DateTime? BasisInstant(TicketMetric m, DateBasis basis) => basis switch
    {
        DateBasis.Resolved => m.ResolvedAt,
        DateBasis.Closed => m.ClosedAt,
        _ => m.CreatedAt
    };

    private static void EnsureFilters(MetricDef m, AnalyticsFilters f)
    {
        if (m.Source == MetricSource.AgentDaily && f.HasTicketOnlyFilters)
            throw new DomainException("analytics_unsupported_filter", $"'{m.Label}' can only be filtered by department, team and agent.", 400);
    }

    // ───────────────────────────── Primitives ─────────────────────────────

    public async Task<(double? Value, int Count)> ValueAsync(MetricDef m, AnalyticsRequest r, PeriodRange p, CancellationToken ct)
    {
        EnsureFilters(m, r.Filters);
        if (m.Source == MetricSource.AgentDaily)
            return MetricCatalog.AggregateAgents(m, await AgentRowsAsync(r.Filters, p.FromDay, p.ToDay, ct));
        var rows = await RowsAsync(r.Filters, m.Basis, p, r.Now, ct);
        var at = Reference(p, r.Now);
        return MetricCatalog.Aggregate(m, m.Basis == DateBasis.OpenAt ? rows.Where(x => OpenAt(x, at)) : rows, at);
    }

    public static DateOnly BucketStart(DateOnly day, string grain) => grain switch
    {
        "week" => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
        "month" => new DateOnly(day.Year, day.Month, 1),
        _ => day
    };

    private static DateOnly NextBucket(DateOnly b, string grain) => grain switch { "week" => b.AddDays(7), "month" => b.AddMonths(1), _ => b.AddDays(1) };

    public static string BucketLabel(DateOnly b, string grain) => grain switch
    {
        "month" => b.ToString("MMM yyyy", CultureInfo.InvariantCulture),
        "week" => "Wk " + b.ToString("d MMM", CultureInfo.InvariantCulture),
        _ => b.ToString("d MMM", CultureInfo.InvariantCulture)
    };

    public async Task<List<SeriesBucket>> SeriesAsync(MetricDef m, AnalyticsRequest r, PeriodRange p, string grain, CancellationToken ct)
    {
        EnsureFilters(m, r.Filters);
        var tz = r.TimeZone;
        var today = AnalyticsPeriods.LocalDay(r.Now, tz);
        var lastDay = p.ToDay > today ? today : p.ToDay;
        List<TicketMetric>? rows = null;
        List<AgentMetric>? agentRows = null;
        var firstDay = p.FromDay;
        if (m.Source == MetricSource.AgentDaily)
        {
            agentRows = await AgentRowsAsync(r.Filters, p.FromDay, p.ToDay, ct);
            if (p.Key == "all_time" && agentRows.Count > 0) firstDay = agentRows.Min(x => x.Day);
        }
        else
        {
            rows = await RowsAsync(r.Filters, m.Basis, p, r.Now, ct);
            if (p.Key == "all_time" && rows.Count > 0) firstDay = AnalyticsPeriods.LocalDay(rows.Min(x => x.CreatedAt), tz);
        }
        if (firstDay > lastDay) firstDay = lastDay;

        var starts = new List<DateOnly>();
        for (var b = BucketStart(firstDay, grain); b <= lastDay; b = NextBucket(b, grain)) starts.Add(b);
        if (starts.Count > MaxBuckets) starts = starts.TakeLast(MaxBuckets).ToList();

        var result = new List<SeriesBucket>(starts.Count);
        if (agentRows is not null)
        {
            var byBucket = agentRows.ToLookup(x => BucketStart(x.Day, grain));
            foreach (var b in starts)
            {
                var (v, n) = MetricCatalog.AggregateAgents(m, byBucket[b].ToList());
                result.Add(new SeriesBucket(b, Key(b), BucketLabel(b, grain), v ?? (m.IsAdditive ? 0 : null), n));
            }
            return result;
        }
        if (m.Basis == DateBasis.OpenAt)
        {
            foreach (var b in starts)
            {
                var end = AnalyticsPeriods.DayStartUtc(NextBucket(b, grain), tz);
                var at = end > r.Now ? r.Now : end > p.To ? p.To : end;
                var (v, n) = MetricCatalog.Aggregate(m, rows!.Where(x => OpenAt(x, at)), at);
                result.Add(new SeriesBucket(b, Key(b), BucketLabel(b, grain), v, n));
            }
            return result;
        }
        var grouped = rows!.ToLookup(x => BasisInstant(x, m.Basis) is { } i ? BucketStart(AnalyticsPeriods.LocalDay(i, tz), grain) : DateOnly.MinValue);
        foreach (var b in starts)
        {
            var end = AnalyticsPeriods.DayStartUtc(NextBucket(b, grain), tz);
            var (v, n) = MetricCatalog.Aggregate(m, grouped[b], end > r.Now ? r.Now : end);
            result.Add(new SeriesBucket(b, Key(b), BucketLabel(b, grain), v, n));
        }
        return result;
    }

    private static string Key(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async Task<List<GroupValue>> GroupAsync(MetricDef m, AnalyticsRequest r, PeriodRange p, string dimension, CancellationToken ct)
    {
        EnsureFilters(m, r.Filters);
        if (m.Source == MetricSource.AgentDaily)
        {
            if (dimension != "assignee") throw Unsupported(m, dimension);
            var agentRows = await AgentRowsAsync(r.Filters, p.FromDay, p.ToDay, ct);
            return agentRows.GroupBy(x => x.UserId).Select(g =>
            {
                var (v, n) = MetricCatalog.AggregateAgents(m, g.ToList());
                return new GroupValue(g.Key.ToString(), v, n);
            }).ToList();
        }
        var dim = MetricCatalog.DimensionsByKey[dimension];
        var rows = await RowsAsync(r.Filters, m.Basis, p, r.Now, ct);
        var at = Reference(p, r.Now);
        IEnumerable<TicketMetric> source = m.Basis == DateBasis.OpenAt ? rows.Where(x => OpenAt(x, at)) : rows;
        return source.Where(m.PopulationFunc).GroupBy(x => dim.KeyOf!(x) ?? MetricCatalog.NoneKey).Select(g =>
        {
            var (v, n) = MetricCatalog.Aggregate(m, g, at);
            return new GroupValue(g.Key, v, n);
        }).ToList();
    }

    private static DomainException Unsupported(MetricDef m, string dimension) =>
        new("analytics_unsupported_dimension", $"'{m.Label}' cannot be broken down by '{dimension}'.", 400);

    public static string ValidateDimension(MetricDef m, string? dimension, string fallback = MetricCatalog.NoneKey)
    {
        var d = string.IsNullOrWhiteSpace(dimension) ? fallback : dimension.Trim().ToLowerInvariant();
        if (!MetricCatalog.DimensionsByKey.ContainsKey(d)) throw new DomainException("analytics_unknown_dimension", $"'{dimension}' is not a supported breakdown.", 400);
        if (m.Source == MetricSource.AgentDaily && !MetricCatalog.AgentDimensions.Contains(d)) throw Unsupported(m, d);
        return d;
    }

    private async Task PrepareLabelsAsync(string dimension, IEnumerable<string?> keys, CancellationToken ct)
    {
        await labels.LoadAsync(ct);
        if (dimension == "customer")
            await labels.LoadCustomersAsync(keys.Where(k => k is not null && Guid.TryParse(k, out _)).Select(k => Guid.Parse(k!)), ct);
    }

    // ───────────────────────────── Public queries ─────────────────────────────

    public async Task<AnalyticsResultDto> QueryAsync(AnalyticsQuery q, CancellationToken ct)
    {
        var r = await RequestAsync(q, ct);
        var m = MetricCatalog.Get(q.Metric);
        var dimension = ValidateDimension(m, q.Dimension);
        var (total, count) = await ValueAsync(m, r, r.Period, ct);
        double? compareTotal = r.Compare is { } cp ? (await ValueAsync(m, r, cp, ct)).Value : null;
        var rows = new List<AnalyticsRowDto>();
        if (MetricCatalog.IsTime(dimension))
        {
            var series = await SeriesAsync(m, r, r.Period, dimension, ct);
            var compare = r.Compare is { } c ? await SeriesAsync(m, r, c, dimension, ct) : null;
            for (var i = 0; i < series.Count; i++)
            {
                var cv = compare is not null && i < compare.Count ? compare[i].Value : null;
                rows.Add(new AnalyticsRowDto(series[i].Key, series[i].Label, series[i].Value, series[i].Count, cv, MetricCatalog.Delta(series[i].Value, cv)));
            }
        }
        else if (dimension != MetricCatalog.NoneKey)
        {
            var limit = Math.Clamp(q.Limit ?? 10, 1, 100);
            var asc = string.Equals(q.Sort, "asc", StringComparison.OrdinalIgnoreCase);
            var groups = await GroupAsync(m, r, r.Period, dimension, ct);
            var compare = r.Compare is { } c ? (await GroupAsync(m, r, c, dimension, ct)).ToDictionary(g => g.Key ?? "", g => g.Value) : null;
            var top = (asc ? groups.OrderBy(g => g.Value ?? double.MaxValue) : groups.OrderByDescending(g => g.Value ?? double.MinValue)).ThenBy(g => g.Key).Take(limit).ToList();
            await PrepareLabelsAsync(dimension, top.Select(g => g.Key), ct);
            foreach (var g in top)
            {
                var cv = compare is not null && compare.TryGetValue(g.Key ?? "", out var x) ? x : null;
                rows.Add(new AnalyticsRowDto(g.Key, labels.Label(dimension, g.Key), g.Value, g.Count, cv, MetricCatalog.Delta(g.Value, cv)));
            }
        }
        return new AnalyticsResultDto(MetricCatalog.ToDto(m), dimension, r.Period.ToDto(), r.Compare?.ToDto(), total, count, compareTotal,
            MetricCatalog.Delta(total, compareTotal), rows, Truncated);
    }

    public async Task<AnalyticsBreakdownDto> BreakdownAsync(AnalyticsQuery q, CancellationToken ct)
    {
        var r = await RequestAsync(q, ct);
        var keys = (q.Metrics ?? q.Metric ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (keys.Count is 0 or > 10) throw new DomainException("analytics_metrics_required", "Choose between 1 and 10 metrics.", 400);
        var metrics = keys.Select(MetricCatalog.Get).ToList();
        var dimension = metrics.Select(m => ValidateDimension(m, q.Dimension, "department")).First();
        if (dimension == MetricCatalog.NoneKey) throw new DomainException("analytics_dimension_required", "A breakdown needs a dimension.", 400);
        var (columns, rows, totals) = await TableAsync(metrics, r, r.Period, dimension, Math.Clamp(q.Limit ?? 25, 1, 200),
            string.Equals(q.Sort, "asc", StringComparison.OrdinalIgnoreCase), ct);
        return new AnalyticsBreakdownDto(dimension, r.Period.ToDto(), columns, rows, totals);
    }

    /// <summary>Multi-metric table by one dimension (time buckets in order; categories sorted by the first metric).</summary>
    public async Task<(List<BreakdownColumnDto> Columns, List<BreakdownRowDto> Rows, List<double?> Totals)> TableAsync(
        IReadOnlyList<MetricDef> metrics, AnalyticsRequest r, PeriodRange p, string dimension, int limit, bool asc, CancellationToken ct)
    {
        var columns = metrics.Select(m => new BreakdownColumnDto(m.Key, m.Label, m.Unit, m.HigherIsBetter)).ToList();
        var totals = new List<double?>();
        foreach (var m in metrics) totals.Add((await ValueAsync(m, r, p, ct)).Value);
        var rows = new List<BreakdownRowDto>();
        if (MetricCatalog.IsTime(dimension))
        {
            var series = new List<List<SeriesBucket>>();
            foreach (var m in metrics) series.Add(await SeriesAsync(m, r, p, dimension, ct));
            for (var i = 0; i < series[0].Count; i++)
                rows.Add(new BreakdownRowDto(series[0][i].Key, series[0][i].Label, series.Select(s => i < s.Count ? s[i].Value : null).ToList()));
            return (columns, rows, totals);
        }
        var groups = new List<Dictionary<string, GroupValue>>();
        foreach (var m in metrics) groups.Add((await GroupAsync(m, r, p, dimension, ct)).ToDictionary(g => g.Key ?? MetricCatalog.NoneKey));
        var allKeys = groups.SelectMany(g => g.Keys).Distinct().ToList();
        double? First(string k) => groups[0].TryGetValue(k, out var g) ? g.Value : null;
        var ordered = (asc ? allKeys.OrderBy(k => First(k) ?? double.MaxValue) : allKeys.OrderByDescending(k => First(k) ?? double.MinValue)).ThenBy(k => k).Take(limit).ToList();
        await PrepareLabelsAsync(dimension, ordered, ct);
        foreach (var k in ordered)
            rows.Add(new BreakdownRowDto(k, labels.Label(dimension, k), groups.Select(g => g.TryGetValue(k, out var v) ? v.Value : null).ToList()));
        return (columns, rows, totals);
    }

    public async Task<AnalyticsParetoDto> ParetoAsync(AnalyticsQuery q, CancellationToken ct)
    {
        var r = await RequestAsync(q, ct);
        var m = MetricCatalog.Get(q.Metric ?? "ticket_count");
        var dimension = ValidateDimension(m, q.Dimension, "category");
        var (rows, total, vital) = await ParetoRowsAsync(m, r, r.Period, dimension, Math.Clamp(q.Limit ?? 20, 3, 50), ct);
        return new AnalyticsParetoDto(m.Key, dimension, r.Period.ToDto(), total, rows, vital);
    }

    public async Task<(List<ParetoRowDto> Rows, double Total, int VitalFew)> ParetoRowsAsync(MetricDef m, AnalyticsRequest r, PeriodRange p, string dimension, int limit, CancellationToken ct)
    {
        if (!m.IsAdditive || m.Source != MetricSource.Tickets)
            throw new DomainException("analytics_unsupported_metric", "Pareto analysis needs a count or sum ticket metric.", 400);
        if (MetricCatalog.IsTime(dimension) || dimension == MetricCatalog.NoneKey)
            throw new DomainException("analytics_unsupported_dimension", "Pareto analysis needs a categorical dimension.", 400);
        var groups = (await GroupAsync(m, r, p, dimension, ct)).Where(g => (g.Value ?? 0) > 0).OrderByDescending(g => g.Value).ThenBy(g => g.Key).ToList();
        var total = groups.Sum(g => g.Value ?? 0);
        var top = groups.Take(limit).ToList();
        await PrepareLabelsAsync(dimension, top.Select(g => g.Key), ct);
        var rows = new List<ParetoRowDto>();
        double cumulative = 0;
        var vital = 0;
        foreach (var g in top)
        {
            var v = g.Value ?? 0;
            var before = cumulative;
            cumulative += v;
            if (total > 0 && before / total < 0.8) vital++;
            rows.Add(new ParetoRowDto(g.Key, labels.Label(dimension, g.Key), v, total > 0 ? Math.Round(v / total * 100, 1) : 0, total > 0 ? Math.Round(cumulative / total * 100, 1) : 0));
        }
        var rest = groups.Skip(limit).Sum(g => g.Value ?? 0);
        if (rest > 0)
        {
            cumulative += rest;
            rows.Add(new ParetoRowDto(null, $"Other ({groups.Count - limit})", rest, Math.Round(rest / total * 100, 1), Math.Round(cumulative / total * 100, 1)));
        }
        return (rows, total, vital);
    }

    public static readonly int[] CohortOffsets = [1, 2, 3, 7, 14, 30];

    public async Task<AnalyticsCohortsDto> CohortsAsync(AnalyticsQuery q, CancellationToken ct)
    {
        var r = await RequestAsync(q, ct);
        return new AnalyticsCohortsDto(r.Period.ToDto(), CohortOffsets, await CohortRowsAsync(r, r.Period, ct));
    }

    /// <summary>Created-week cohorts → share resolved within N days (only tickets at least N days old are counted for N).</summary>
    public async Task<List<CohortRowDto>> CohortRowsAsync(AnalyticsRequest r, PeriodRange p, CancellationToken ct)
    {
        var rows = await RowsAsync(r.Filters, DateBasis.Created, p, r.Now, ct);
        return rows.GroupBy(x => BucketStart(AnalyticsPeriods.LocalDay(x.CreatedAt, r.TimeZone), "week")).OrderBy(g => g.Key).TakeLast(26).Select(g =>
        {
            var values = CohortOffsets.Select(d =>
            {
                var observable = g.Where(x => x.CreatedAt.AddDays(d) <= r.Now).ToList();
                if (observable.Count == 0) return (double?)null;
                var resolved = observable.Count(x => x.ResolvedAt is { } res && (res - x.CreatedAt).TotalDays <= d);
                return Math.Round(resolved * 100.0 / observable.Count, 1);
            }).ToList();
            return new CohortRowDto(Key(g.Key), "Wk " + g.Key.ToString("d MMM yyyy", CultureInfo.InvariantCulture), g.Count(), values);
        }).ToList();
    }

    public static readonly (string Key, string Label, double MinDays)[] AgeBuckets =
        [("0-1", "< 1 day", 0), ("1-3", "1–3 days", 1), ("3-7", "3–7 days", 3), ("7-30", "7–30 days", 7), ("30+", "30+ days", 30)];

    public async Task<AnalyticsAgeingDto> AgeingAsync(AnalyticsQuery q, CancellationToken ct)
    {
        var r = await RequestAsync(q, ct);
        var dimension = ValidateDimension(MetricCatalog.Get("backlog"), q.Dimension, "priority");
        if (MetricCatalog.IsTime(dimension)) throw new DomainException("analytics_unsupported_dimension", "Ageing needs a categorical dimension.", 400);
        var (rows, totals, asOf) = await AgeingRowsAsync(r, r.Period, dimension, ct);
        return new AnalyticsAgeingDto(dimension, asOf, AgeBuckets.Select(b => new AgeingBucketDto(b.Key, b.Label)).ToList(), rows, totals);
    }

    public async Task<(List<AgeingRowDto> Rows, List<int> Totals, DateTime AsOf)> AgeingRowsAsync(AnalyticsRequest r, PeriodRange p, string dimension, CancellationToken ct)
    {
        var at = Reference(p, r.Now);
        var open = (await RowsAsync(r.Filters, DateBasis.OpenAt, p, r.Now, ct)).Where(x => OpenAt(x, at)).ToList();
        int Bucket(TicketMetric x)
        {
            var days = (at - x.CreatedAt).TotalDays;
            for (var i = AgeBuckets.Length - 1; i >= 0; i--) if (days >= AgeBuckets[i].MinDays) return i;
            return 0;
        }
        var dim = MetricCatalog.DimensionsByKey[dimension];
        var groups = dimension == MetricCatalog.NoneKey
            ? open.GroupBy(_ => (string?)MetricCatalog.NoneKey).ToList()
            : open.GroupBy(x => dim.KeyOf!(x) ?? MetricCatalog.NoneKey).Select(g => (IGrouping<string?, TicketMetric>)g).ToList();
        await PrepareLabelsAsync(dimension, groups.Select(g => g.Key), ct);
        var rows = groups.Select(g =>
        {
            var counts = new int[AgeBuckets.Length];
            foreach (var x in g) counts[Bucket(x)]++;
            return new AgeingRowDto(g.Key, dimension == MetricCatalog.NoneKey ? "All open tickets" : labels.Label(dimension, g.Key), counts, g.Count(), g.Count(x => x.SlaBreached));
        }).OrderByDescending(x => x.Total).ThenBy(x => x.Label).ToList();
        if (dimension == "priority")
            rows = rows.OrderByDescending(x => Enum.TryParse<TicketPriority>(x.Key, out var pr) ? (int)pr : 0).ToList();
        var totals = Enumerable.Range(0, AgeBuckets.Length).Select(i => rows.Sum(x => x.Counts[i])).ToList();
        return (rows, totals, at);
    }

    // ───────────────────────────── Drill-down ─────────────────────────────

    public async Task<AnalyticsDrilldownDto> DrilldownAsync(AnalyticsQuery q, CancellationToken ct)
    {
        var r = await RequestAsync(q, ct);
        var m = MetricCatalog.Get(q.Metric ?? "ticket_count");
        if (m.Source != MetricSource.Tickets) throw new DomainException("analytics_not_drillable", $"'{m.Label}' is computed from daily agent totals and has no ticket list.", 400);
        var tz = r.TimeZone;
        var p = r.Period;
        var title = m.Label;

        // Narrow the window to one time bucket.
        if (!string.IsNullOrWhiteSpace(q.Bucket))
        {
            if (!DateOnly.TryParseExact(q.Bucket, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var b))
                throw AnalyticsPeriods.Invalid("bucket", "Bucket must be yyyy-MM-dd.");
            var grain = string.IsNullOrWhiteSpace(q.Dimension) || !MetricCatalog.IsTime(q.Dimension) ? "day" : q.Dimension.ToLowerInvariant();
            var start = AnalyticsPeriods.DayStartUtc(b, tz);
            var end = AnalyticsPeriods.DayStartUtc(NextBucket(b, grain), tz);
            p = new PeriodRange("bucket", BucketLabel(b, grain), start < p.From ? p.From : start, end > p.To ? p.To : end, b, NextBucket(b, grain).AddDays(-1));
            title += $" · {BucketLabel(b, grain)}";
        }

        var query = r.Filters.Apply(AnalyticsScope.Metrics(db, ctx).AsNoTracking());
        var at = Reference(p, r.Now);
        query = m.Basis switch
        {
            DateBasis.Resolved => query.Where(x => x.ResolvedAt >= p.From && x.ResolvedAt < p.To),
            DateBasis.Closed => query.Where(x => x.ClosedAt >= p.From && x.ClosedAt < p.To),
            DateBasis.OpenAt => query.Where(x => x.CreatedAt < at && (x.ResolvedAt == null || x.ResolvedAt >= at)),
            _ => query.Where(x => x.CreatedAt >= p.From && x.CreatedAt < p.To)
        };
        if (m.Population is not null) query = query.Where(m.Population);
        if (!string.IsNullOrWhiteSpace(q.Key) && q.Dimension is { } dimKey && !MetricCatalog.IsTime(dimKey) && dimKey != MetricCatalog.NoneKey)
        {
            var dimension = ValidateDimension(m, dimKey);
            query = query.Where(DimensionEquals(dimension, q.Key));
            await PrepareLabelsAsync(dimension, [q.Key], ct);
            title += $" · {labels.Label(dimension, q.Key)}";
        }

        var total = await query.CountAsync(ct);
        var size = Math.Clamp(q.PageSize ?? 50, 1, 200);
        if (DbExtensions.DecodeCursor(q.Cursor) is { } cursor)
        {
            var v = DateTime.Parse(cursor.V, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            query = query.Where(x => x.CreatedAt < v || (x.CreatedAt == v && x.TicketId.CompareTo(cursor.Id) < 0));
        }
        var page = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.TicketId).Take(size + 1).ToListAsync(ct);
        var items = page.Take(size).ToList();
        var next = page.Count > size ? DbExtensions.EncodeCursor(items[^1].CreatedAt.ToString("O", CultureInfo.InvariantCulture), items[^1].TicketId) : null;
        return new AnalyticsDrilldownDto(title, p.ToDto(), await MapTicketsAsync(items, ct), next, total);
    }

    /// <summary>Tickets of the period (basis) matching <paramref name="predicate"/>, newest first; mapped for display unless <paramref name="map"/> is false.</summary>
    public async Task<List<(TicketMetric Metric, DrilldownTicketDto? Dto)>> TicketsAsync(AnalyticsRequest r, PeriodRange p, DateBasis basis,
        Func<TicketMetric, bool> predicate, int take, CancellationToken ct, bool map = true)
    {
        var rows = await RowsAsync(r.Filters, basis, p, r.Now, ct);
        var at = Reference(p, r.Now);
        var selected = (basis == DateBasis.OpenAt ? rows.Where(x => OpenAt(x, at)) : rows).Where(predicate)
            .OrderByDescending(x => x.CreatedAt).Take(take).ToList();
        if (!map) return selected.Select(x => (x, (DrilldownTicketDto?)null)).ToList();
        var dtos = await MapTicketsAsync(selected, ct);
        return selected.Select((x, i) => (x, (DrilldownTicketDto?)dtos[i])).ToList();
    }

    /// <summary>Maximum concurrently open tickets per agent (workload capacity).</summary>
    public Task<Dictionary<Guid, int>> CapacitiesAsync(CancellationToken ct) =>
        db.Agents.AsNoTracking().ToDictionaryAsync(a => a.UserId, a => a.MaxOpenTickets, ct);

    public Task<Dictionary<Guid, string>> CustomerTiersAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Tier, ct);

    public async Task<List<DrilldownTicketDto>> MapTicketsAsync(IReadOnlyList<TicketMetric> items, CancellationToken ct)
    {
        await labels.LoadAsync(ct);
        var ids = items.Select(i => i.TicketId).ToList();
        var tickets = await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(t => ids.Contains(t.Id))
            .Select(t => new { t.Id, t.Subject, t.StatusId }).ToDictionaryAsync(t => t.Id, ct);
        await labels.LoadCustomersAsync(items.Where(i => i.CustomerId.HasValue).Select(i => i.CustomerId!.Value), ct);
        var lk = labels.Lookups;
        return items.Select(i =>
        {
            var t = tickets.GetValueOrDefault(i.TicketId);
            return new DrilldownTicketDto(i.TicketId, i.TicketNumber, lk.Display(i.TicketNumber), t?.Subject ?? "", t is null ? "" : lk.Status(t.StatusId),
                i.StatusCategory.ToString(), i.Priority.ToString(), i.Channel.ToString(), lk.Department(i.DepartmentId), lk.User(i.AssigneeId),
                i.CustomerId is { } c ? labels.Label("customer", c.ToString()) : null, i.CreatedAt, i.FirstResponseAt, i.ResolvedAt,
                i.FirstResponseMinutes, i.FirstResponseBusinessMinutes, i.ResolutionMinutes, i.ResolutionBusinessMinutes, i.SlaBreached, i.ReopenCount, i.CsatScore);
        }).ToList();
    }

    /// <summary>Typed equality predicate for one dimension value ("none" = no value).</summary>
    public static Expression<Func<TicketMetric, bool>> DimensionEquals(string dimension, string key)
    {
        var none = key == MetricCatalog.NoneKey;
        Guid G() => Guid.TryParse(key, out var g) ? g : throw AnalyticsPeriods.Invalid("key", $"'{key}' is not a valid id.");
        T E<T>() where T : struct, Enum => Enum.TryParse<T>(key, true, out var v) ? v : throw AnalyticsPeriods.Invalid("key", $"'{key}' is not a valid value.");
        switch (dimension)
        {
            case "department": { var g = G(); return m => m.DepartmentId == g; }
            case "team": { if (none) return m => m.TeamId == null; var g = G(); return m => m.TeamId == g; }
            case "assignee": { if (none) return m => m.AssigneeId == null; var g = G(); return m => m.AssigneeId == g; }
            case "customer": { if (none) return m => m.CustomerId == null; var g = G(); return m => m.CustomerId == g; }
            case "product": { if (none) return m => m.ProductId == null; var g = G(); return m => m.ProductId == g; }
            case "module": { if (none) return m => m.ModuleId == null; var g = G(); return m => m.ModuleId == g; }
            case "version": { if (none) return m => m.VersionId == null; var g = G(); return m => m.VersionId == g; }
            case "category": { if (none) return m => m.CategoryId == null; var g = G(); return m => m.CategoryId == g; }
            case "issue_type": { if (none) return m => m.IssueTypeId == null; var g = G(); return m => m.IssueTypeId == g; }
            case "channel": { var v = E<Channel>(); return m => m.Channel == v; }
            case "priority": { var v = E<TicketPriority>(); return m => m.Priority == v; }
            case "status_category": { var v = E<StatusCategory>(); return m => m.StatusCategory == v; }
            case "sentiment": { var v = E<Sentiment>(); return m => m.Sentiment == v; }
            case "customer_tier": { if (none) return m => m.CustomerTier == null; var s = key; return m => m.CustomerTier == s; }
            case "root_cause": { if (none) return m => m.RootCause == null; var s = key; return m => m.RootCause == s; }
            default: throw new DomainException("analytics_unknown_dimension", $"'{dimension}' is not a supported breakdown.", 400);
        }
    }
}
