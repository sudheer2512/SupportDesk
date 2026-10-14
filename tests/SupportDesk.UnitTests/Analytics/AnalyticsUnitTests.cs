using System.Text;
using SupportDesk.Application.Ai.Analytics;
using SupportDesk.Application.Analytics;
using SupportDesk.Contracts.Analytics;
using SupportDesk.Domain;
using SupportDesk.Domain.Analytics;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Sla;
using SupportDesk.Domain.Tickets;

namespace SupportDesk.UnitTests.Analytics;

/// <summary>Metric definitions on known tickets (business calendar Mon–Fri 09:00–18:00 Asia/Kolkata).</summary>
public sealed class TicketMetricCalculatorTests
{
    private static readonly TimeZoneInfo Ist = WorkingCalendar.ResolveTimeZone("Asia/Kolkata");
    private static readonly WorkingCalendar Business = WorkingCalendar.WeekdaysCalendar("Asia/Kolkata", TimeSpan.FromHours(9), TimeSpan.FromHours(18));

    /// <summary>October 2026 local IST time → UTC (9 Oct 2026 is a Friday).</summary>
    private static DateTime At(int day, int hour, int minute = 0) => new DateTime(2026, 10, day, hour, minute, 0, DateTimeKind.Utc).AddMinutes(-330);

    private static Ticket Ticket(DateTime created) => new()
    {
        TenantId = Guid.NewGuid(), Number = 42, CreatedAt = created, Channel = Channel.Email, Priority = TicketPriority.High, DepartmentId = Guid.NewGuid(),
        StatusCategory = StatusCategory.Open
    };

    private static TicketMetric Compute(Ticket t, IReadOnlyList<ThreadFact>? threads = null, IReadOnlyList<ActivityFact>? activity = null,
        IReadOnlyList<MilestoneFact>? milestones = null, int logged = 0)
    {
        var m = new TicketMetric();
        TicketMetricCalculator.Apply(m, new TicketFacts(t, threads ?? [], activity ?? [], milestones ?? [], logged, 0, 0m, "Gold"), Business, Ist, At(14, 12));
        return m;
    }

    [Fact]
    public void First_response_and_resolution_count_business_time_across_a_weekend()
    {
        // Created Friday 17:00, first reply Monday 10:00, resolved Monday 17:30.
        var t = Ticket(At(9, 17));
        t.FirstResponseAt = At(12, 10);
        t.ResolvedAt = At(12, 17, 30);
        t.StatusCategory = StatusCategory.Resolved;
        t.AgentReplyCount = 1;
        var agent = Guid.NewGuid();
        var m = Compute(t, [new ThreadFact(ThreadKind.CustomerMessage, Visibility.Public, null, At(9, 17)), new ThreadFact(ThreadKind.AgentReply, Visibility.Public, agent, At(12, 10))]);

        Assert.Equal(65 * 60, m.FirstResponseMinutes);           // calendar: Fri 17:00 → Mon 10:00
        Assert.Equal(120, m.FirstResponseBusinessMinutes);       // Fri 17–18 + Mon 09–10
        Assert.Equal(72 * 60 + 30, m.ResolutionMinutes);         // Fri 17:00 → Mon 17:30
        Assert.Equal(60 + 510, m.ResolutionBusinessMinutes);     // Fri 17–18 + Mon 09:00–17:30
        Assert.Equal(agent, m.FirstResponderId);
        Assert.True(m.FirstContactResolution);
        Assert.Equal(new DateOnly(2026, 10, 9), m.CreatedDay);
        Assert.Equal(new DateOnly(2026, 10, 12), m.ResolvedDay);
        Assert.Equal("Gold", m.CustomerTier);
    }

    [Fact]
    public void Reopened_ticket_uses_the_final_resolution_and_is_not_first_contact_resolution()
    {
        var t = Ticket(At(12, 9));
        t.FirstResponseAt = At(12, 9, 30);
        t.ReopenCount = 1;
        t.ResolvedAt = At(13, 11);
        t.StatusCategory = StatusCategory.Resolved;
        t.AgentReplyCount = 1;
        var m = Compute(t, activity: [new ActivityFact("Resolved", At(12, 10)), new ActivityFact("Reopened", At(12, 15))]);
        Assert.Equal(1, m.ReopenCount);
        Assert.False(m.FirstContactResolution);
        Assert.Equal(26 * 60, m.ResolutionMinutes);              // Mon 09:00 → Tue 11:00
        Assert.Equal(9 * 60 + 2 * 60, m.ResolutionBusinessMinutes);

        // While reopened (open) there is no resolution at all.
        t.StatusCategory = StatusCategory.InProgress;
        t.ResolvedAt = null;
        var open = Compute(t);
        Assert.Null(open.ResolvedAt);
        Assert.Null(open.ResolutionMinutes);
        Assert.False(open.FirstContactResolution);
    }

    [Fact]
    public void Sla_outcomes_are_tracked_per_milestone()
    {
        var t = Ticket(At(12, 9));
        t.SlaPolicyId = Guid.NewGuid();
        t.SlaState = SlaState.Breached;
        var m = Compute(t, milestones:
        [
            new MilestoneFact(SlaMilestoneKind.FirstResponse, MilestoneStatus.Met, At(12, 9, 30), null),
            new MilestoneFact(SlaMilestoneKind.NextResponse, MilestoneStatus.Breached, At(12, 15), At(12, 14)),
            new MilestoneFact(SlaMilestoneKind.NextResponse, MilestoneStatus.Met, At(13, 10), null),
            new MilestoneFact(SlaMilestoneKind.Resolution, MilestoneStatus.Breached, null, At(12, 17))
        ]);
        Assert.True(m.HasSla);
        Assert.True(m.FirstResponseSlaMet);
        Assert.False(m.FirstResponseBreached);
        Assert.True(m.ResolutionBreached);
        Assert.False(m.ResolutionSlaMet);
        Assert.Equal(1, m.NextResponseBreaches);
        Assert.Equal(1, m.NextResponseMet);
        Assert.True(m.SlaBreached);

        var compliance = MetricCatalog.Get("resolution_sla_compliance");
        Assert.Equal(0, MetricCatalog.Aggregate(compliance, [m], At(14, 0)).Value);
        Assert.Equal(100, MetricCatalog.Aggregate(MetricCatalog.Get("first_response_sla_compliance"), [m], At(14, 0)).Value);
        Assert.Equal(1, MetricCatalog.Aggregate(MetricCatalog.Get("sla_breaches"), [m], At(14, 0)).Value);
    }

    [Fact]
    public void Spam_merged_and_deleted_tickets_are_excluded()
    {
        var spam = Ticket(At(12, 9));
        spam.IsSpam = true;
        Assert.True(Compute(spam).Excluded);
        var merged = Ticket(At(12, 9));
        merged.MergedIntoTicketId = Guid.NewGuid();
        Assert.True(Compute(merged).Excluded);
        Assert.False(Compute(Ticket(At(12, 9))).Excluded);
    }

    [Theory]
    [InlineData("10:00:59", "10:01:00", 1)]
    [InlineData("10:00:00", "10:00:59", 0)]
    [InlineData("10:00:30", "11:00:10", 60)]
    public void Calendar_minutes_follow_sql_datediff_semantics(string from, string to, int expected)
    {
        var d = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, TicketMetricCalculator.CalendarMinutes(d + TimeSpan.Parse(from), d + TimeSpan.Parse(to)));
    }
}

public sealed class MetricCatalogTests
{
    private static TicketMetric Row(int? frtMinutes = null, bool resolved = false, int reopen = 0, bool fcr = false, int? csat = null) => new()
    {
        CreatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), FirstResponseMinutes = frtMinutes, ReopenCount = reopen,
        ResolvedAt = resolved ? new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc) : null, FirstContactResolution = fcr, CsatScore = csat,
        StatusCategory = resolved ? StatusCategory.Resolved : StatusCategory.Open
    };

    [Fact]
    public void Median_and_p90_use_linear_interpolation()
    {
        Assert.Equal(2.5, MetricCatalog.Percentile([1, 2, 3, 4], 0.5));
        Assert.Equal(3.7, MetricCatalog.Percentile([1, 2, 3, 4], 0.9)!.Value, 6);
        Assert.Null(MetricCatalog.Percentile([], 0.5));
        var rows = new[] { Row(60), Row(120), Row(180), Row(240), Row(null) };
        var at = DateTime.UtcNow;
        Assert.Equal(2.5, MetricCatalog.Aggregate(MetricCatalog.Get("median_first_response_hours"), rows, at).Value);
        Assert.Equal(2.5, MetricCatalog.Aggregate(MetricCatalog.Get("avg_first_response_hours"), rows, at).Value);
        Assert.Equal(3.7, MetricCatalog.Aggregate(MetricCatalog.Get("p90_first_response_hours"), rows, at).Value);
        Assert.Equal(4, MetricCatalog.Aggregate(MetricCatalog.Get("avg_first_response_hours"), rows, at).Count);
    }

    [Fact]
    public void Rates_are_percentages_of_their_population()
    {
        var rows = new[] { Row(resolved: true, fcr: true), Row(resolved: true, reopen: 1), Row(resolved: true), Row(resolved: false), Row(csat: 5), Row(csat: 3) };
        var at = DateTime.UtcNow;
        Assert.Equal(33.3, MetricCatalog.Aggregate(MetricCatalog.Get("fcr_rate"), rows, at).Value);
        Assert.Equal(33.3, MetricCatalog.Aggregate(MetricCatalog.Get("reopen_rate"), rows, at).Value);
        Assert.Equal(3, MetricCatalog.Aggregate(MetricCatalog.Get("resolved_tickets"), rows, at).Value);
        Assert.Equal(4, MetricCatalog.Aggregate(MetricCatalog.Get("csat_avg"), rows, at).Value);
        Assert.Equal(50, MetricCatalog.Aggregate(MetricCatalog.Get("csat_positive_rate"), rows, at).Value);
        Assert.Equal(-50, MetricCatalog.Delta(5, 10));
        Assert.Null(MetricCatalog.Delta(5, 0));
    }

    [Fact]
    public void Utilisation_divides_logged_by_scheduled_minutes()
    {
        var u = MetricCatalog.Get("utilisation");
        Assert.Equal(50, MetricCatalog.AggregateAgents(u, [new AgentMetric { HandleMinutes = 120, OnlineMinutes = 480 }, new AgentMetric { HandleMinutes = 360, OnlineMinutes = 480 }]).Value);
        Assert.Null(MetricCatalog.AggregateAgents(u, [new AgentMetric { HandleMinutes = 30 }]).Value);
    }

    [Fact]
    public void Ai_metrics_take_their_definitions_from_the_catalogue()
    {
        foreach (var (key, ai) in AnalyticsCatalog.Metrics)
        {
            var m = MetricCatalog.Get(key);
            Assert.Equal(m.Label, ai.Label);
            Assert.Equal(m.Unit, ai.Unit);
            Assert.Equal(m.Basis == DateBasis.Resolved, ai.ByResolvedDate);
            Assert.Equal(m.Aggregation == MetricAggregation.Count ? MetricKind.Count : MetricKind.Average, ai.Kind);
        }
        var nowUtc = new DateTime(2026, 10, 14, 6, 0, 0, DateTimeKind.Utc);
        var tz = WorkingCalendar.ResolveTimeZone("Asia/Kolkata");
        foreach (var p in AnalyticsCatalog.Periods) Assert.Equal(AnalyticsPeriods.Resolve(p, nowUtc, tz), AnalyticsCatalog.ResolvePeriod(p, nowUtc, tz));
    }

    [Fact]
    public void Every_catalogue_metric_is_documented()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SupportDesk.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var doc = File.ReadAllText(Path.Combine(dir!.FullName, "docs", "METRICS.md"));
        foreach (var m in MetricCatalog.List) Assert.Contains($"`{m.Key}`", doc);
        foreach (var d in MetricCatalog.Dimensions.Where(d => d.Key != MetricCatalog.NoneKey)) Assert.Contains($"`{d.Key}`", doc);
    }

    [Fact]
    public void Periods_and_comparisons_resolve_on_the_tenant_calendar()
    {
        var tz = WorkingCalendar.ResolveTimeZone("Asia/Kolkata");
        var now = new DateTime(2026, 10, 14, 6, 0, 0, DateTimeKind.Utc); // Wed 14 Oct 11:30 IST
        var custom = AnalyticsPeriods.Build("custom", "2026-10-01", "2026-10-07", now, tz);
        Assert.Equal(new DateTime(2026, 9, 30, 18, 30, 0, DateTimeKind.Utc), custom.From);
        Assert.Equal(new DateTime(2026, 10, 7, 18, 30, 0, DateTimeKind.Utc), custom.To);
        var prev = AnalyticsPeriods.Comparison(custom, "previous", tz)!;
        Assert.Equal(new DateOnly(2026, 9, 24), prev.FromDay);
        Assert.Equal(new DateOnly(2026, 9, 30), prev.ToDay);
        var ly = AnalyticsPeriods.Comparison(custom, "last_year", tz)!;
        Assert.Equal(new DateOnly(2025, 10, 1), ly.FromDay);
        Assert.Throws<SupportDesk.Application.Tickets.ValidationException>(() => AnalyticsPeriods.Build("custom", "2026-10-07", "2026-10-01", now, tz));
        Assert.Throws<SupportDesk.Application.Tickets.ValidationException>(() => AnalyticsPeriods.Build("since 1990", null, null, now, tz));
    }

    [Fact]
    public void Filters_are_whitelisted_and_typed()
    {
        var f = AnalyticsFilters.Parse(new Dictionary<string, string?> { ["priority"] = "high,Critical", ["channel"] = "Email", ["tier"] = "Gold" });
        Assert.Equal(new[] { TicketPriority.High, TicketPriority.Critical }, f.Priorities);
        Assert.True(f.HasTicketOnlyFilters);
        Assert.Throws<SupportDesk.Application.Tickets.ValidationException>(() => AnalyticsFilters.Parse(new Dictionary<string, string?> { ["subject"] = "x' OR 1=1" }));
        Assert.Throws<SupportDesk.Application.Tickets.ValidationException>(() => AnalyticsFilters.Parse(new Dictionary<string, string?> { ["customer"] = "ABC Retail" }));
        Assert.Throws<SupportDesk.Application.Tickets.ValidationException>(() => AnalyticsFilters.Parse(new Dictionary<string, string?> { ["priority"] = "Urgentest" }));
        Assert.Throws<DomainException>(() => MetricCatalog.Get("revenue"));
    }
}

public sealed class ScheduleCalculatorTests
{
    private static DateTime Ist(int month, int day, int hour, int minute = 0) => new DateTime(2026, month, day, hour, minute, 0, DateTimeKind.Utc).AddMinutes(-330);

    [Fact]
    public void Daily_weekly_and_monthly_runs_fall_on_local_time()
    {
        var at = new TimeOnly(7, 0);
        Assert.Equal(Ist(10, 14, 7), ScheduleCalculator.Next(ReportFrequency.Daily, at, null, null, "Asia/Kolkata", Ist(10, 14, 6)));
        Assert.Equal(Ist(10, 15, 7), ScheduleCalculator.Next(ReportFrequency.Daily, at, null, null, "Asia/Kolkata", Ist(10, 14, 7)));
        // Wednesday 14 Oct → next Monday 19 Oct.
        Assert.Equal(Ist(10, 19, 7), ScheduleCalculator.Next(ReportFrequency.Weekly, at, DayOfWeek.Monday, null, "Asia/Kolkata", Ist(10, 14, 12)));
        Assert.Equal(Ist(11, 1, 7), ScheduleCalculator.Next(ReportFrequency.Monthly, at, null, 1, "Asia/Kolkata", Ist(10, 14, 12)));
        Assert.Equal(Ist(10, 28, 7), ScheduleCalculator.Next(ReportFrequency.Monthly, at, null, 31, "Asia/Kolkata", Ist(10, 14, 12))); // clamped to 28
    }

    [Fact]
    public void Daylight_saving_gaps_do_not_break_the_schedule()
    {
        // Europe/London springs forward on 29 Mar 2026 at 01:00 → 02:00; a 01:30 run moves to the first valid instant.
        var next = ScheduleCalculator.Next(ReportFrequency.Daily, new TimeOnly(1, 30), null, null, "Europe/London", new DateTime(2026, 3, 28, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 3, 29, 1, 0, 0, DateTimeKind.Utc), next);
    }
}

public sealed class ReportRenderingTests
{
    private static ReportResultDto Sample() => new("volume", "Ticket volume", "desc",
        new PeriodRangeDto("custom", "1 Oct – 7 Oct", DateTime.UtcNow.AddDays(-7), DateTime.UtcNow, "2026-10-01", "2026-10-07"), null,
        [new ReportKpiDto("ticket_count", "Tickets created", "tickets", 12, 10, 20, true), new ReportKpiDto("median_first_response_hours", "Median FRT", "hours", 1.5, null, null, false)],
        [],
        [new ReportTableDto("t", "By department", [new("label", "Department", "text"), new("ticket_count", "Tickets", "number"), new("fcr_rate", "FCR", "percent")],
            [new Dictionary<string, object?> { ["label"] = "=HYPERLINK(\"http://x\")", ["ticket_count"] = 12.0, ["fcr_rate"] = 50.0 }], "department", "ticket_count")],
        ["Priority: High"], "Asia/Kolkata", DateTime.UtcNow);

    [Fact]
    public void Csv_neutralises_formula_injection_and_quotes_cells()
    {
        var csv = Encoding.UTF8.GetString(ReportRenderer.Csv(Sample()));
        Assert.Contains("\"'=HYPERLINK(\"\"http://x\"\")\"", csv);
        Assert.Contains("Tickets created,12,tickets,10,20", csv);
        Assert.Equal("'-1+1", ReportRenderer.Escape("-1+1"));
        Assert.Equal("-12.5", ReportRenderer.Escape("-12.5"));
    }

    [Fact]
    public void Excel_and_pdf_files_are_produced()
    {
        var xlsx = ReportRenderer.Xlsx(Sample());
        Assert.Equal((byte)'P', xlsx[0]);
        Assert.Equal((byte)'K', xlsx[1]);
        var pdf = ReportRenderer.Pdf(Sample());
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.Equal("3h 15m", ReportFormatting.Hours(3.25));
        Assert.Equal("30m", ReportFormatting.Hours(0.5));
    }

    [Fact]
    public void Customer_health_score_penalises_breaches_reopens_escalations_and_low_csat()
    {
        Assert.Equal(100, ReportService.HealthScore(10, 0, 0, 0, 4.5));
        Assert.Equal(100 - 40 * 0.5 - 20 * 0.1 - 10 * 1.0, ReportService.HealthScore(10, 5, 1, 0, 3.0));
        Assert.Equal(100, ReportService.HealthScore(0, 0, 0, 0, null));
    }
}

public sealed class DashboardWidgetValidationTests
{
    [Fact]
    public void Widgets_must_reference_catalogue_metrics_and_fit_the_grid()
    {
        var ok = DashboardService.ValidateWidgets([new DashboardWidgetDto { Title = "Backlog", Type = "bar", Metric = "backlog", Dimension = "assignee", X = 10, W = 6, H = 20 }]);
        Assert.Equal(6, ok[0].X);   // pulled back inside the 12-column grid
        Assert.Equal(8, ok[0].H);   // capped
        Assert.Equal(10, ok[0].Limit);
        Assert.Throws<SupportDesk.Application.Tickets.ValidationException>(() =>
            DashboardService.ValidateWidgets([new DashboardWidgetDto { Title = "x", Type = "kpi", Metric = "drop table" }]));
        Assert.Throws<SupportDesk.Application.Tickets.ValidationException>(() =>
            DashboardService.ValidateWidgets([new DashboardWidgetDto { Title = "x", Type = "line", Metric = "ticket_count", Dimension = "priority" }]));
        Assert.Throws<SupportDesk.Application.Tickets.ValidationException>(() =>
            DashboardService.ValidateWidgets([new DashboardWidgetDto { Title = "x", Type = "bar", Metric = "utilisation", Dimension = "channel" }]));
        Assert.Throws<SupportDesk.Application.Tickets.ValidationException>(() =>
            DashboardService.ValidateWidgets([new DashboardWidgetDto { Title = "x", Type = "kpi", Metric = "ticket_count", Filters = new Dictionary<string, string> { ["subject"] = "a" } }]));
        foreach (var d in DefaultDashboards.All) Assert.Equal(d.Widgets.Count, DashboardService.ValidateWidgets(d.Widgets).Count);
    }
}
