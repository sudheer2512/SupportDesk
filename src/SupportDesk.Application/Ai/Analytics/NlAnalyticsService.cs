using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Ai.Features;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Ai;
using SupportDesk.Domain;
using SupportDesk.Domain.Ai;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Ai.Analytics;

public sealed record AnalyticsRow(string? Key, string Label, double Value, int Count);

/// <summary>Projection used to group by an arbitrary whitelisted key.</summary>
public sealed class KeyedValue<TKey>
{
    public TKey Key { get; set; } = default!;
    public double Value { get; set; }
}

/// <summary>
/// Executes a validated <see cref="AnalyticsIntent"/> as LINQ over the caller's visible tickets. Metrics, dimensions and
/// filters map to fixed, compiled expressions; values from the intent are only ever used as typed parameters (Guid,
/// enum), so neither a person nor a model can inject SQL.
/// </summary>
public sealed class SafeAnalyticsQuery(DeskDbContext db, RequestContext ctx, TicketLookups lookups)
{
    public async Task<(List<AnalyticsRow> Rows, DateTime From, DateTime To)> ExecuteAsync(AnalyticsIntent raw, TimeZoneInfo tz, CancellationToken ct)
    {
        var intent = AnalyticsCatalog.Validate(raw);
        var metric = AnalyticsCatalog.Metrics[intent.Metric];
        var (from, to) = AnalyticsCatalog.ResolvePeriod(intent.Period, db.Clock.UtcNow, tz);
        var q = db.Tickets.AsNoTracking().VisibleTo(ctx, db).Where(t => !t.IsSpam && t.MergedIntoTicketId == null);
        q = metric.ByResolvedDate ? q.Where(t => t.ResolvedAt >= from && t.ResolvedAt < to) : q.Where(t => t.CreatedAt >= from && t.CreatedAt < to);
        q = intent.Metric switch
        {
            "open_tickets" => q.Where(t => t.StatusCategory < StatusCategory.Resolved),
            "resolved_tickets" or "avg_resolution_hours" => q.Where(t => t.ResolvedAt != null),
            "sla_breaches" => q.Where(t => t.SlaState == SlaState.Breached || db.SlaMilestones.Any(m => m.TicketId == t.Id && m.BreachedAt != null)),
            "avg_first_response_hours" => q.Where(t => t.FirstResponseAt != null),
            "csat_avg" => q.Where(t => t.CsatScore != null),
            "reopened_tickets" => q.Where(t => t.ReopenCount > 0),
            "escalated_tickets" => q.Where(t => t.IsEscalated),
            "frustrated_tickets" => q.Where(t => t.Sentiment == Sentiment.Frustrated),
            _ => q
        };
        foreach (var f in intent.Filters) q = ApplyFilter(q, f);
        Expression<Func<Ticket, double>> value = intent.Metric switch
        {
            "avg_resolution_hours" => t => EF.Functions.DateDiffMinute(t.CreatedAt, t.ResolvedAt!.Value) / 60.0,
            "avg_first_response_hours" => t => EF.Functions.DateDiffMinute(t.CreatedAt, t.FirstResponseAt!.Value) / 60.0,
            "csat_avg" => t => (double)t.CsatScore!.Value,
            _ => t => 1.0
        };

        await lookups.LoadAsync(ct);
        List<AnalyticsRow> rows;
        switch (intent.Dimension)
        {
            case "none":
            {
                var projected = q.Select(Combine<int>(t => 0, value));
                var count = await projected.CountAsync(ct);
                var sum = count == 0 ? 0 : await projected.SumAsync(x => x.Value, ct);
                rows = [new AnalyticsRow(null, metric.Label, Finish(metric, sum, count), count)];
                break;
            }
            case "customer":
            {
                var g = await GroupAsync(q, t => t.CustomerId, value, ct);
                var ids = g.Where(x => x.Key is not null).Select(x => x.Key!.Value).ToList();
                var names = await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(c => ids.Contains(c.Id))
                    .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
                rows = g.Select(x => Row(metric, x.Key?.ToString(), x.Key is { } id ? names.GetValueOrDefault(id, "Unknown customer") : "(no customer)", x.Sum, x.Count)).ToList();
                break;
            }
            case "product": rows = Map(metric, await GroupAsync(q, t => t.ProductId, value, ct), id => lookups.Product(id)); break;
            case "category": rows = Map(metric, await GroupAsync(q, t => t.CategoryId, value, ct), id => lookups.Category(id)); break;
            case "department": rows = Map(metric, await GroupAsync(q, t => (Guid?)t.DepartmentId, value, ct), id => lookups.Department(id)); break;
            case "assignee": rows = Map(metric, await GroupAsync(q, t => t.AssigneeId, value, ct), id => lookups.User(id)); break;
            case "team": rows = Map(metric, await GroupAsync(q, t => t.TeamId, value, ct), id => lookups.Team(id)); break;
            case "status": rows = Map(metric, await GroupAsync(q, t => (Guid?)t.StatusId, value, ct), id => id is { } s ? lookups.Status(s) : null); break;
            case "priority":
                rows = (await GroupAsync(q, t => t.Priority, value, ct)).Select(x => Row(metric, x.Key.ToString(), x.Key.ToString(), x.Sum, x.Count)).ToList();
                break;
            case "channel":
                rows = (await GroupAsync(q, t => t.Channel, value, ct)).Select(x => Row(metric, x.Key.ToString(), x.Key.ToString(), x.Sum, x.Count)).ToList();
                break;
            case "sentiment":
                rows = (await GroupAsync(q, t => t.Sentiment, value, ct)).Select(x => Row(metric, x.Key.ToString(), x.Key.ToString(), x.Sum, x.Count)).ToList();
                break;
            case "day" or "week" or "month":
            {
                var byDay = metric.ByResolvedDate
                    ? await GroupAsync(q, t => t.ResolvedAt!.Value.Date, value, ct)
                    : await GroupAsync(q, t => t.CreatedAt.Date, value, ct);
                DateTime Bucket(DateTime d) => intent.Dimension switch
                {
                    "week" => d.AddDays(-(((int)d.DayOfWeek + 6) % 7)),
                    "month" => new DateTime(d.Year, d.Month, 1, 0, 0, 0, DateTimeKind.Utc),
                    _ => d
                };
                var buckets = byDay.GroupBy(x => Bucket(DateTime.SpecifyKind(x.Key, DateTimeKind.Utc)))
                    .ToDictionary(g => g.Key, g => (Sum: g.Sum(x => x.Sum), Count: g.Sum(x => x.Count)));
                // Continuous series for count metrics (empty days are zero, not missing); averages only show buckets with data.
                var end = (to < db.Clock.UtcNow ? to.AddSeconds(-1) : db.Clock.UtcNow).Date;
                var first = buckets.Count > 0 && from.Year <= 2000 ? buckets.Keys.Min() : Bucket(DateTime.SpecifyKind(from.Date, DateTimeKind.Utc));
                if (metric.Kind == MetricKind.Count)
                    for (var b = first; b <= end && buckets.Count < 400; b = intent.Dimension switch { "week" => b.AddDays(7), "month" => b.AddMonths(1), _ => b.AddDays(1) })
                        buckets.TryAdd(b, (0, 0));
                string Label(DateTime d) => intent.Dimension == "month" ? d.ToString("MMM yyyy", CultureInfo.InvariantCulture) : d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                rows = buckets.OrderBy(kv => kv.Key)
                    .Select(kv => Row(metric, kv.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Label(kv.Key), kv.Value.Sum, kv.Value.Count)).ToList();
                return (rows.TakeLast(intent.Limit).ToList(), from, to);
            }
            default:
                throw new DomainException("nl_unsupported", $"'{intent.Dimension}' is not a supported breakdown.", 422);
        }
        var sorted = intent.Sort == "asc" ? rows.OrderBy(r => r.Value).ThenBy(r => r.Label) : rows.OrderByDescending(r => r.Value).ThenBy(r => r.Label);
        return (sorted.Take(intent.Limit).ToList(), from, to);
    }

    private static List<AnalyticsRow> Map(MetricDefinition metric, List<(Guid? Key, double Sum, int Count)> groups, Func<Guid?, string?> label) =>
        groups.Select(x => Row(metric, x.Key?.ToString(), x.Key is null ? "(none)" : label(x.Key) ?? "Unknown", x.Sum, x.Count)).ToList();

    private static AnalyticsRow Row(MetricDefinition metric, string? key, string label, double sum, int count) => new(key, label, Finish(metric, sum, count), count);

    private static double Finish(MetricDefinition metric, double sum, int count) =>
        metric.Kind == MetricKind.Count ? sum : count == 0 ? 0 : Math.Round(sum / count, 2);

    private static IQueryable<Ticket> ApplyFilter(IQueryable<Ticket> q, AnalyticsFilter f)
    {
        // Values were validated by AnalyticsCatalog.Validate; parse them into typed parameters before building the query.
        Guid? id = AnalyticsCatalog.EntityFields.Contains(f.Field) ? Guid.Parse(f.Value) : null;
        switch (f.Field)
        {
            case "customer": return q.Where(t => t.CustomerId == id);
            case "product": return q.Where(t => t.ProductId == id);
            case "category": return q.Where(t => t.CategoryId == id || t.SubcategoryId == id);
            case "department": return q.Where(t => t.DepartmentId == id);
            case "assignee": return q.Where(t => t.AssigneeId == id);
            case "team": return q.Where(t => t.TeamId == id);
            case "priority":
            {
                var p = Enum.Parse<TicketPriority>(f.Value, true);
                return q.Where(t => t.Priority == p);
            }
            case "channel":
            {
                var c = Enum.Parse<Channel>(f.Value, true);
                return q.Where(t => t.Channel == c);
            }
            case "status_category":
            {
                var s = Enum.Parse<StatusCategory>(f.Value, true);
                return q.Where(t => t.StatusCategory == s);
            }
            case "sentiment":
            {
                var s = Enum.Parse<Sentiment>(f.Value, true);
                return q.Where(t => t.Sentiment == s);
            }
            default:
                throw new DomainException("nl_unsupported", $"Filtering by '{f.Field}' is not supported.", 422);
        }
    }

    private static Expression<Func<Ticket, KeyedValue<TKey>>> Combine<TKey>(Expression<Func<Ticket, TKey>> key, Expression<Func<Ticket, double>> value)
    {
        var p = Expression.Parameter(typeof(Ticket), "t");
        var keyBody = new Rebind(key.Parameters[0], p).Visit(key.Body)!;
        var valueBody = new Rebind(value.Parameters[0], p).Visit(value.Body)!;
        var type = typeof(KeyedValue<TKey>);
        var init = Expression.MemberInit(Expression.New(type),
            Expression.Bind(type.GetProperty(nameof(KeyedValue<TKey>.Key))!, keyBody),
            Expression.Bind(type.GetProperty(nameof(KeyedValue<TKey>.Value))!, valueBody));
        return Expression.Lambda<Func<Ticket, KeyedValue<TKey>>>(init, p);
    }

    private static async Task<List<(TKey Key, double Sum, int Count)>> GroupAsync<TKey>(IQueryable<Ticket> q, Expression<Func<Ticket, TKey>> key,
        Expression<Func<Ticket, double>> value, CancellationToken ct)
    {
        var rows = await q.Select(Combine(key, value)).GroupBy(x => x.Key).Select(g => new { g.Key, Sum = g.Sum(x => x.Value), Count = g.Count() }).ToListAsync(ct);
        return rows.Select(r => (r.Key, r.Sum, r.Count)).ToList();
    }

    private sealed class Rebind(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}

/// <summary>
/// "Which customers had the most SLA violations this month?" → structured intent (local rule parser or a model in JSON
/// mode) → validated against whitelists → <see cref="SafeAnalyticsQuery"/> → rows plus a chart hint.
/// </summary>
public sealed class NlAnalyticsService(DeskDbContext db, RequestContext ctx, AiGateway gateway, SafeAnalyticsQuery query)
{
    public async Task<AnalyticsAnswerDto> AskAsync(AskAnalyticsRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AnalyticsView);
        var question = (r.Question ?? "").Trim();
        if (question.Length < 5) throw new ValidationException(new Dictionary<string, string> { ["question"] = "Ask a question, e.g. \"Which customers had the most SLA violations this month?\"" });
        question = TextNormalization.Truncate(question, 500);
        var vocabulary = await VocabularyAsync(ct);
        var vocabText = new StringBuilder();
        foreach (var v in vocabulary.Take(400)) vocabText.AppendLine($"{v.Field} | {v.Value} | {v.Label}");
        var now = db.Clock.UtcNow;
        var result = await gateway.CompleteAsync(new AiCall
        {
            Feature = AiFeatures.NlAnalytics, PromptKey = PromptKeys.NlAnalytics, EntityType = "Analytics", Interactive = true,
            Values = new Dictionary<string, string> { ["vocabulary"] = vocabText.ToString(), ["today"] = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            Untrusted = new Dictionary<string, string> { ["question"] = question },
            LocalTask = new NlAnalyticsTask(question, vocabulary, now), MaxTokens = 300, Temperature = 0
        }, raw => ParseIntent(raw, vocabulary), ct);

        AnalyticsIntent intent;
        List<AnalyticsRow> rows;
        DateTime from, to;
        try
        {
            intent = AnalyticsCatalog.Validate(result.Value);
            var tzId = await db.Tenants.AsNoTracking().Where(t => t.Id == ctx.TenantId).Select(t => t.DefaultTimeZone).FirstOrDefaultAsync(ct);
            (rows, from, to) = await query.ExecuteAsync(intent, WorkingCalendar.ResolveTimeZone(tzId), ct);
        }
        catch (DomainException)
        {
            await gateway.MarkOutcomeAsync(result.ExecutionId, AiOutcome.Rejected, ct);
            throw;
        }
        var metric = AnalyticsCatalog.Metrics[intent.Metric];
        var dimLabel = AnalyticsCatalog.Dimensions[intent.Dimension];
        var columns = new List<AnalyticsColumnDto> { new("label", dimLabel, "string"), new("value", $"{metric.Label} ({metric.Unit})", "number") };
        if (metric.Kind == MetricKind.Average) columns.Add(new AnalyticsColumnDto("count", "Tickets", "number"));
        var rowDicts = rows.Select(x => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["key"] = x.Key, ["label"] = x.Label, ["value"] = x.Value, ["count"] = x.Count
        }).ToList();
        var chart = intent.Dimension == "none" ? new AnalyticsChartDto("kpi", "label", "value")
            : AnalyticsCatalog.IsTimeDimension(intent.Dimension) ? new AnalyticsChartDto("line", "label", "value")
            : intent.Dimension is "priority" or "channel" or "sentiment" && rows.Count <= 6 && metric.Kind == MetricKind.Count ? new AnalyticsChartDto("pie", "label", "value")
            : new AnalyticsChartDto("bar", "label", "value");
        var labels = vocabulary.ToDictionary(v => (v.Field, v.Value), v => v.Label);
        var filters = intent.Filters.Select(f => new AnalyticsFilterDto(f.Field, f.Value, labels.GetValueOrDefault((f.Field, f.Value), f.Value))).ToList();
        var interpretation = $"{metric.Label}" + (intent.Dimension == "none" ? "" : $" by {dimLabel.ToLowerInvariant()}") + $", {AnalyticsCatalog.PeriodLabel(intent.Period)}" +
                             (filters.Count > 0 ? " where " + string.Join(" and ", filters.Select(f => $"{f.Field.Replace('_', ' ')} = {f.Label}")) : "") +
                             (intent.Dimension is "none" || AnalyticsCatalog.IsTimeDimension(intent.Dimension) ? "" : $" — top {intent.Limit}, {(intent.Sort == "asc" ? "lowest" : "highest")} first");
        await gateway.MarkOutcomeAsync(result.ExecutionId, AiOutcome.Accepted, ct);
        return new AnalyticsAnswerDto(result.ExecutionId, question,
            new AnalyticsIntentDto(intent.Metric, intent.Dimension, filters, intent.Period, intent.Sort, intent.Limit), interpretation, columns, rowDicts, chart,
            from, to, result.IsLocal ? "local-rules" : $"{result.Provider}:{result.Model}");
    }

    private static AiOutput<AnalyticsIntent> ParseIntent(string raw, IReadOnlyList<AnalyticsVocabularyEntry> vocabulary)
    {
        var json = AiJson.ParseObject(raw);
        var intent = new AnalyticsIntent
        {
            Metric = AiJson.Str(json, "metric") ?? "",
            Dimension = AiJson.Str(json, "dimension") ?? "none",
            Period = AiJson.Str(json, "period") ?? "last_30_days",
            Sort = AiJson.Str(json, "sort") ?? "desc",
            Limit = (int)(AiJson.Num(json, "limit") ?? 10)
        };
        if (AiJson.Prop(json, "filters") is { ValueKind: System.Text.Json.JsonValueKind.Array } filters)
        {
            foreach (var f in filters.EnumerateArray())
            {
                var field = AiJson.Str(f, "field")?.Trim().ToLowerInvariant();
                var value = AiJson.Str(f, "value")?.Trim();
                if (field is null || value is null) continue;
                // A model may answer with a label instead of the id: map it through the vocabulary (never trust free text).
                if (AnalyticsCatalog.EntityFields.Contains(field) && !Guid.TryParse(value, out _))
                    value = vocabulary.FirstOrDefault(v => v.Field == field && v.Label.Equals(value, StringComparison.OrdinalIgnoreCase))?.Value ?? value;
                intent.Filters.Add(new AnalyticsFilter(field, value));
            }
        }
        return new AiOutput<AnalyticsIntent>(intent, 1, Logged: intent);
    }

    /// <summary>Names the question may mention (customers, products, categories, departments, agents, teams) mapped to ids.</summary>
    private async Task<List<AnalyticsVocabularyEntry>> VocabularyAsync(CancellationToken ct)
    {
        var list = new List<AnalyticsVocabularyEntry>();
        list.AddRange((await db.Customers.AsNoTracking().OrderBy(c => c.Name).Take(500).Select(c => new { c.Id, c.Name }).ToListAsync(ct))
            .Select(c => new AnalyticsVocabularyEntry("customer", c.Id.ToString(), c.Name)));
        list.AddRange((await db.Products.AsNoTracking().Select(p => new { p.Id, p.Name }).ToListAsync(ct))
            .Select(p => new AnalyticsVocabularyEntry("product", p.Id.ToString(), p.Name)));
        list.AddRange((await db.TicketCategories.AsNoTracking().Where(c => c.IsActive).Select(c => new { c.Id, c.Name }).ToListAsync(ct))
            .Select(c => new AnalyticsVocabularyEntry("category", c.Id.ToString(), c.Name)));
        list.AddRange((await db.Departments.AsNoTracking().Where(d => d.IsActive).Select(d => new { d.Id, d.Name }).ToListAsync(ct))
            .Select(d => new AnalyticsVocabularyEntry("department", d.Id.ToString(), d.Name)));
        list.AddRange((await db.Teams.AsNoTracking().Select(t => new { t.Id, t.Name }).ToListAsync(ct))
            .Select(t => new AnalyticsVocabularyEntry("team", t.Id.ToString(), t.Name)));
        list.AddRange((await db.Agents.AsNoTracking().Join(db.Users.AsNoTracking(), a => a.UserId, u => u.Id, (a, u) => new { u.Id, u.DisplayName }).ToListAsync(ct))
            .Select(u => new AnalyticsVocabularyEntry("assignee", u.Id.ToString(), u.DisplayName)));
        return list;
    }
}
