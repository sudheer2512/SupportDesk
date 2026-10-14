using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Ai.Analytics;
using SupportDesk.Application.Analytics;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Analytics;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Analytics;
using SupportDesk.Domain.Rules;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.Persistence;

namespace SupportDesk.IntegrationTests.Analytics;

/// <summary>
/// Analytics module: metrics maintained from events (business time across a weekend, reopen, SLA breach), data-scope
/// filtering, tenant isolation, exports through the durable job, scheduled delivery and AI parity. Default tenant SLA:
/// business hours Mon–Fri 09:00–18:00 Asia/Kolkata; High = 60 min first response, 480 business minutes resolution.
/// </summary>
[Collection("api")]
public sealed class AnalyticsTests(DeskApiFactory factory)
{
    private static readonly JsonSerializerOptions J = DeskApiFactory.Json;

    /// <summary>October 2026, IST wall-clock time → UTC. 9 Oct 2026 is a Friday.</summary>
    private static DateTime Ist(int day, int hour, int minute = 0) => new DateTime(2026, 10, day, hour, minute, 0, DateTimeKind.Utc).AddMinutes(-330);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(J))!;
    }

    private Task<Guid> CreateTicketAsync(TestTenant t, Guid department, string priority = "High", Guid? assignee = null, string subject = "Analytics ticket") =>
        factory.InTenantAsync(t.Id, async sp => (await sp.GetRequiredService<TicketService>().CreateAsync(new CreateTicketRequest
        {
            Subject = subject, Channel = "Email", Priority = priority, DepartmentId = department, AssigneeId = assignee, SkipRouting = true,
            NewContact = new NewContactDto("Customer", $"c{Guid.NewGuid():N}@x.test", null, null)
        }, new TicketCreationSource(CreateInitialThread: true, EnforceRequiredFields: false), default)).Id);

    private static string Week(string metric, string extra = "") =>
        $"/api/v1/analytics/query?metric={metric}&period=custom&from=2026-10-05&to=2026-10-12{extra}";

    private async Task<TicketMetric> MetricAsync(Guid tenant, Guid ticketId) =>
        await factory.InTenantAsync(tenant, sp => sp.GetRequiredService<DeskDbContext>().TicketMetrics.AsNoTracking().FirstAsync(m => m.TicketId == ticketId));

    // ───────────────────────────── Metric correctness ─────────────────────────────

    [Fact]
    public async Task Metrics_follow_ticket_events_in_business_time_across_a_weekend_with_reopen_and_breach()
    {
        var t = await factory.CreateTenantAsync();
        var clock = factory.Clock;
        Guid ticketId;
        try
        {
            // Friday 17:00 → first reply Monday 10:00 (first response due Friday 18:00: breached).
            clock.Pin(Ist(9, 17));
            ticketId = await CreateTicketAsync(t, t.SupportDepartmentId);
            clock.Pin(Ist(12, 10));
            await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<ConversationService>().ReplyAsync(ticketId, new ReplyRequest { BodyHtml = "<p>On it</p>" }, default));
            // Resolved 11:00, reopened 12:00, resolved again 15:30 (resolution clock 60 + 120 + 210 = 390 ≤ 480: met).
            clock.Pin(Ist(12, 11));
            await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<TicketService>().ResolveAsync(ticketId, new ResolveTicketRequest("fixed", "configuration", null), default));
            clock.Pin(Ist(12, 12));
            await factory.InTenantAsync(t.Id, async sp =>
            {
                var inProgress = await sp.GetRequiredService<DeskDbContext>().TicketStatuses.FirstAsync(s => s.Name == "In Progress");
                await sp.GetRequiredService<TicketService>().ChangeStatusAsync(ticketId, new ChangeStatusRequest(inProgress.Id, null, "Customer says it still fails", null), default);
            });
            clock.Pin(Ist(12, 15, 30));
            await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<TicketService>().ResolveAsync(ticketId, new ResolveTicketRequest("fixed", "product_defect", null), default));
            await factory.DrainOutboxAsync();
        }
        finally
        {
            clock.Unpin();
        }

        var m = await MetricAsync(t.Id, ticketId);
        Assert.Equal(65 * 60, m.FirstResponseMinutes);              // Fri 17:00 → Mon 10:00 calendar
        Assert.Equal(120, m.FirstResponseBusinessMinutes);          // Fri 17–18 + Mon 09–10
        Assert.Equal(70 * 60 + 30, m.ResolutionMinutes);            // Fri 17:00 → Mon 15:30 (final resolution)
        Assert.Equal(60 + 390, m.ResolutionBusinessMinutes);        // Fri 17–18 + Mon 09:00–15:30
        Assert.Equal(1, m.ReopenCount);
        Assert.False(m.FirstContactResolution);
        Assert.True(m.FirstResponseBreached);
        Assert.False(m.FirstResponseSlaMet);
        Assert.True(m.ResolutionSlaMet);
        Assert.True(m.SlaBreached);
        Assert.Equal("product_defect", m.RootCause);
        Assert.Equal(new DateOnly(2026, 10, 9), m.CreatedDay);
        Assert.Equal(new DateOnly(2026, 10, 12), m.ResolvedDay);

        var admin = await t.AdminClientAsync();
        var frt = await ReadAsync<AnalyticsResultDto>(await admin.GetAsync(Week("median_first_response_business_hours")));
        Assert.Equal(2.0, frt.Total);
        var reopen = await ReadAsync<AnalyticsResultDto>(await admin.GetAsync(Week("reopen_rate")));
        Assert.Equal(100, reopen.Total);
        var breaches = await ReadAsync<AnalyticsResultDto>(await admin.GetAsync(Week("sla_breaches", "&dimension=priority")));
        Assert.Equal(1, breaches.Total);
        Assert.Equal("High", Assert.Single(breaches.Rows).Key);
        var series = await ReadAsync<AnalyticsResultDto>(await admin.GetAsync(Week("ticket_count", "&dimension=day")));
        Assert.Equal(8, series.Rows.Count);                         // continuous daily series 5–12 Oct
        Assert.Equal(1, series.Rows.Single(r => r.Key == "2026-10-09").Value);

        var drill = await ReadAsync<AnalyticsDrilldownDto>(await admin.GetAsync("/api/v1/analytics/drilldown?metric=sla_breaches&period=custom&from=2026-10-05&to=2026-10-12&dimension=priority&key=High"));
        Assert.Equal(1, drill.Total);
        Assert.Equal(ticketId, Assert.Single(drill.Items).TicketId);

        // Recomputing (catch-up / backfill) yields the same row.
        await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<MetricsMaintenanceService>().BackfillAsync(30, default));
        var again = await MetricAsync(t.Id, ticketId);
        Assert.Equal(m.Id, again.Id);
        Assert.Equal(m.ResolutionBusinessMinutes, again.ResolutionBusinessMinutes);
        Assert.Equal(1, await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().TicketMetrics.CountAsync()));
    }

    [Fact]
    public async Task Ai_assistant_numbers_match_the_analytics_query_api()
    {
        var t = await factory.CreateTenantAsync();
        var clock = factory.Clock;
        try
        {
            clock.Pin(Ist(5, 10));
            var a = await CreateTicketAsync(t, t.SupportDepartmentId, "Medium");
            await CreateTicketAsync(t, t.BillingDepartmentId, "Low");
            clock.Pin(Ist(5, 11, 17));
            await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<ConversationService>().ReplyAsync(a, new ReplyRequest { BodyHtml = "<p>Hi</p>" }, default));
            clock.Pin(Ist(6, 14, 3));
            await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<TicketService>().ResolveAsync(a, new ResolveTicketRequest("fixed", null, null), default));
            await factory.DrainOutboxAsync();
        }
        finally
        {
            clock.Unpin();
        }

        foreach (var key in AnalyticsCatalog.MetricKeys)
        {
            var (ai, ours) = await factory.InTenantAsync(t.Id, async sp =>
            {
                var tz = WorkingCalendar.ResolveTimeZone("Asia/Kolkata");
                var (rows, _, _) = await sp.GetRequiredService<SafeAnalyticsQuery>().ExecuteAsync(new AnalyticsIntent { Metric = key, Period = "all_time" }, tz, default);
                var result = await sp.GetRequiredService<AnalyticsEngine>().QueryAsync(new AnalyticsQuery { Metric = key, Period = "all_time" }, default);
                return (rows.Single().Value, result.Total ?? 0);
            });
            Assert.True(Math.Abs(ai - ours) < 0.006, $"{key}: AI {ai} vs analytics {ours}");
        }
    }

    // ───────────────────────────── Data scope & isolation ─────────────────────────────

    [Fact]
    public async Task Agents_count_only_their_own_tickets_and_supervisors_their_departments()
    {
        var t = await factory.CreateTenantAsync();
        var (agentId, agentEmail) = await t.CreateAgentAsync("Scope Agent", "Agent", t.SupportDepartmentId);
        var (_, supervisorEmail) = await t.CreateAgentAsync("Scope Supervisor", "Supervisor", t.SupportDepartmentId);
        var (_, analystEmail) = await t.CreateAgentAsync("Scope Analyst", "Analyst", t.SupportDepartmentId);
        var clock = factory.Clock;
        Guid mine;
        try
        {
            clock.Pin(Ist(7, 10));
            mine = await CreateTicketAsync(t, t.SupportDepartmentId, assignee: agentId, subject: "Assigned to the agent");
            await CreateTicketAsync(t, t.SupportDepartmentId, subject: "Support queue");
            await CreateTicketAsync(t, t.BillingDepartmentId, subject: "Billing queue");
            await factory.DrainOutboxAsync();
        }
        finally
        {
            clock.Unpin();
        }

        async Task<double?> Count(string email) =>
            (await ReadAsync<AnalyticsResultDto>(await (await t.AgentClientAsync(email)).GetAsync(Week("ticket_count")))).Total;

        Assert.Equal(1, await Count(agentEmail));       // personal scope (no analytics.team)
        Assert.Equal(2, await Count(supervisorEmail));  // Support department
        Assert.Equal(3, await Count(analystEmail));     // all tickets
        Assert.Equal(3, await Count(t.AdminEmail));

        var supervisor = await t.AgentClientAsync(supervisorEmail);
        var byDept = await ReadAsync<AnalyticsResultDto>(await supervisor.GetAsync(Week("ticket_count", "&dimension=department")));
        Assert.Equal(t.SupportDepartmentId.ToString(), Assert.Single(byDept.Rows).Key);

        var agent = await t.AgentClientAsync(agentEmail);
        var drill = await ReadAsync<AnalyticsDrilldownDto>(await agent.GetAsync("/api/v1/analytics/drilldown?metric=ticket_count&period=custom&from=2026-10-05&to=2026-10-12"));
        Assert.Equal(mine, Assert.Single(drill.Items).TicketId);
        var report = await ReadAsync<ReportResultDto>(await agent.GetAsync("/api/v1/analytics/reports/volume?period=custom&from=2026-10-05&to=2026-10-12"));
        Assert.Equal(1, report.Kpis.Single(k => k.Metric == "ticket_count").Value);
        Assert.Contains("Scope: my tickets", report.AppliedFilters);

        // Agents cannot export or schedule; injection-shaped parameters are rejected.
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync("/api/v1/analytics/exports", new CreateReportExportRequest { ReportKey = "volume", Format = "Csv" }, J)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.GetAsync("/api/v1/analytics/query?metric=ticket_count;DROP TABLE Tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.GetAsync("/api/v1/analytics/query?metric=ticket_count&customer=1 OR 1=1")).StatusCode);
    }

    [Fact]
    public async Task Tenants_never_see_each_others_metrics_dashboards_exports_or_schedules()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        var clock = factory.Clock;
        try
        {
            clock.Pin(Ist(8, 10));
            await CreateTicketAsync(a, a.SupportDepartmentId);
            await CreateTicketAsync(a, a.SupportDepartmentId);
            await CreateTicketAsync(b, b.SupportDepartmentId);
            await factory.DrainOutboxAsync();
        }
        finally
        {
            clock.Unpin();
        }
        var adminA = await a.AdminClientAsync();
        var adminB = await b.AdminClientAsync();
        Assert.Equal(2, (await ReadAsync<AnalyticsResultDto>(await adminA.GetAsync(Week("ticket_count")))).Total);
        Assert.Equal(1, (await ReadAsync<AnalyticsResultDto>(await adminB.GetAsync(Week("ticket_count")))).Total);

        var dashboard = await ReadAsync<DashboardDto>(await adminA.PostAsJsonAsync("/api/v1/analytics/dashboards", new SaveDashboardRequest
        {
            Name = "Tenant A board", Widgets = [new DashboardWidgetDto { Title = "Created", Type = "kpi", Metric = "ticket_count" }]
        }, J));
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/v1/analytics/dashboards/{dashboard.Id}")).StatusCode);
        Assert.DoesNotContain(await ReadAsync<List<DashboardSummaryDto>>(await adminB.GetAsync("/api/v1/analytics/dashboards")), d => d.Id == dashboard.Id);

        var export = await ReadAsync<ReportExportDto>(await adminA.PostAsJsonAsync("/api/v1/analytics/exports",
            new CreateReportExportRequest { ReportKey = "volume", Format = "Csv", Parameters = new Dictionary<string, string> { ["period"] = "last_7_days" } }, J));
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/v1/analytics/exports/{export.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/v1/analytics/exports/{export.Id}/download")).StatusCode);

        var schedule = await ReadAsync<ScheduledReportDto>(await adminA.PostAsJsonAsync("/api/v1/analytics/schedules", new SaveScheduledReportRequest
        {
            Name = "A weekly", ReportKey = "sla", Format = "Pdf", Frequency = "Weekly", DayOfWeek = "Monday", RunAtLocal = "08:00", Recipients = ["ops@a.test"]
        }, J));
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/v1/analytics/schedules/{schedule.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PostAsync($"/api/v1/analytics/schedules/{schedule.Id}/run", null)).StatusCode);
        // Each tenant gets its own role defaults.
        var defaultsB = (await ReadAsync<List<DashboardSummaryDto>>(await adminB.GetAsync("/api/v1/analytics/dashboards"))).Where(d => d.IsDefault).ToList();
        Assert.Equal(new[] { "agent", "executive", "manager", "supervisor" }, defaultsB.Select(d => d.Audience).OrderBy(x => x).ToArray());
    }

    // ───────────────────────────── Dashboards ─────────────────────────────

    [Fact]
    public async Task Dashboards_are_personal_or_shared_with_role_defaults_protected()
    {
        var t = await factory.CreateTenantAsync();
        var (_, agentEmail) = await t.CreateAgentAsync("Board Agent", "Agent", t.SupportDepartmentId);
        var (_, supervisorEmail) = await t.CreateAgentAsync("Board Supervisor", "Supervisor", t.SupportDepartmentId);
        var agent = await t.AgentClientAsync(agentEmail);
        var supervisor = await t.AgentClientAsync(supervisorEmail);

        var list = await ReadAsync<List<DashboardSummaryDto>>(await agent.GetAsync("/api/v1/analytics/dashboards"));
        Assert.Equal("agent", list[0].Audience);                       // the caller's natural default first
        var defaultBoard = list.First(d => d.Audience == "supervisor");
        Assert.False(defaultBoard.CanEdit);
        var full = await ReadAsync<DashboardDto>(await agent.GetAsync($"/api/v1/analytics/dashboards/{defaultBoard.Id}"));
        var edit = new SaveDashboardRequest { Name = full.Name, Period = full.Period, IsShared = true, Widgets = full.Widgets, RowVersion = full.RowVersion };
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PutAsJsonAsync($"/api/v1/analytics/dashboards/{full.Id}", edit, J)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync("/api/v1/analytics/dashboards", edit with { Name = "Shared by agent" }, J)).StatusCode);

        var updated = await ReadAsync<DashboardDto>(await supervisor.PutAsJsonAsync($"/api/v1/analytics/dashboards/{full.Id}", edit with { Name = "Team operations (ours)" }, J));
        Assert.Equal("Team operations (ours)", updated.Name);
        // Stale row version → 409.
        Assert.Equal(HttpStatusCode.Conflict, (await supervisor.PutAsJsonAsync($"/api/v1/analytics/dashboards/{full.Id}", edit, J)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await supervisor.DeleteAsync($"/api/v1/analytics/dashboards/{full.Id}")).StatusCode);

        var mine = await ReadAsync<DashboardDto>(await agent.PostAsync($"/api/v1/analytics/dashboards/{full.Id}/duplicate", null));
        Assert.True(mine.IsMine && mine.CanEdit && !mine.IsShared);
        var invalid = await agent.PutAsJsonAsync($"/api/v1/analytics/dashboards/{mine.Id}", new SaveDashboardRequest
        {
            Name = "Mine", RowVersion = mine.RowVersion, Widgets = [new DashboardWidgetDto { Title = "x", Type = "kpi", Metric = "salary_total" }]
        }, J);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await supervisor.GetAsync($"/api/v1/analytics/dashboards/{mine.Id}")).StatusCode);   // personal
        Assert.Equal(HttpStatusCode.NoContent, (await agent.DeleteAsync($"/api/v1/analytics/dashboards/{mine.Id}")).StatusCode);
    }

    // ───────────────────────────── Exports ─────────────────────────────

    [Fact]
    public async Task Exports_are_rendered_by_the_durable_job_as_csv_excel_and_pdf()
    {
        var t = await factory.CreateTenantAsync();
        var clock = factory.Clock;
        try
        {
            clock.Pin(Ist(6, 11));
            await CreateTicketAsync(t, t.SupportDepartmentId, "Critical");
            await CreateTicketAsync(t, t.BillingDepartmentId, "Low");
            await factory.DrainOutboxAsync();
        }
        finally
        {
            clock.Unpin();
        }
        var admin = await t.AdminClientAsync();
        var parameters = new Dictionary<string, string> { ["period"] = "custom", ["from"] = "2026-10-05", ["to"] = "2026-10-12", ["compare"] = "previous" };
        var expected = new Dictionary<string, string> { ["Csv"] = "Report,", ["Xlsx"] = "PK", ["Pdf"] = "%PDF" };
        foreach (var (format, magic) in expected)
        {
            var response = await admin.PostAsJsonAsync("/api/v1/analytics/exports", new CreateReportExportRequest { ReportKey = "volume", Format = format, Parameters = parameters }, J);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var export = await ReadAsync<ReportExportDto>(response);
            Assert.Equal("Queued", export.Status);
            Assert.Equal(HttpStatusCode.Conflict, (await admin.GetAsync($"/api/v1/analytics/exports/{export.Id}/download")).StatusCode);

            await factory.DrainJobsAsync();
            var done = await ReadAsync<ReportExportDto>(await admin.GetAsync($"/api/v1/analytics/exports/{export.Id}"));
            Assert.True(done.Status == "Succeeded", done.Error);
            Assert.True(done.RowCount > 0);
            var bytes = await (await admin.GetAsync(done.DownloadUrl)).Content.ReadAsByteArrayAsync();
            var text = Encoding.UTF8.GetString(bytes.AsSpan(format == "Csv" ? 3 : 0, Math.Min(bytes.Length, 20) - (format == "Csv" ? 3 : 0)));
            Assert.StartsWith(magic, text);
            if (format == "Csv")
            {
                var csv = Encoding.UTF8.GetString(bytes);
                Assert.Contains("Tickets created,2,tickets", csv);
                Assert.Contains("By department", csv);
            }
        }
        var list = await ReadAsync<List<ReportExportDto>>(await admin.GetAsync("/api/v1/analytics/exports"));
        Assert.Equal(3, list.Count(e => e.Status == "Succeeded"));
        Assert.True(await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().AuditLogs.AnyAsync(l => l.Action == "data.exported" && l.EntityType == "ReportExport")));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/analytics/exports",
            new CreateReportExportRequest { ReportKey = "volume", Format = "Docx" }, J)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/analytics/exports",
            new CreateReportExportRequest { ReportKey = "volume", Format = "Csv", Parameters = new Dictionary<string, string> { ["sql"] = "select 1" } }, J)).StatusCode);
    }

    // ───────────────────────────── Scheduled reports ─────────────────────────────

    [Fact]
    public async Task Scheduled_report_runs_once_per_slot_and_is_emailed_as_an_attachment()
    {
        var t = await factory.CreateTenantAsync();
        await CreateTicketAsync(t, t.SupportDepartmentId);
        await factory.DrainOutboxAsync();
        var admin = await t.AdminClientAsync();
        var recipient = $"sla-{Guid.NewGuid():N}@reports.test";
        var created = await admin.PostAsJsonAsync("/api/v1/analytics/schedules", new SaveScheduledReportRequest
        {
            Name = "Daily SLA digest", ReportKey = "sla", Format = "Xlsx", Frequency = "Daily", RunAtLocal = "07:00", Recipients = [recipient, "not-an-email"],
            Parameters = new Dictionary<string, string> { ["period"] = "last_7_days" }
        }, J);
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        var schedule = await ReadAsync<ScheduledReportDto>(await admin.PostAsJsonAsync("/api/v1/analytics/schedules", new SaveScheduledReportRequest
        {
            Name = "Daily SLA digest", ReportKey = "sla", Format = "Xlsx", Frequency = "Daily", RunAtLocal = "07:00", Recipients = [recipient],
            Parameters = new Dictionary<string, string> { ["period"] = "last_7_days" }
        }, J));
        Assert.True(schedule.NextRunAt > DateTime.UtcNow);

        // Make it due, run the scheduler twice (one run per slot), then the delivery job.
        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            await db.Set<ScheduledReport>().Where(s => s.Id == schedule.Id).ExecuteUpdateAsync(u => u.SetProperty(s => s.NextRunAt, DateTime.UtcNow.AddMinutes(-5)));
        });
        await factory.RunRecurringAsync(ScheduledReportTask.TaskName);
        await factory.RunRecurringAsync(ScheduledReportTask.TaskName);
        await factory.DrainJobsAsync();

        var runs = await ReadAsync<List<ScheduledReportRunDto>>(await admin.GetAsync($"/api/v1/analytics/schedules/{schedule.Id}/runs"));
        var run = Assert.Single(runs);
        Assert.True(run.Status == "Succeeded", run.Error);
        Assert.Equal(1, run.Delivered);
        Assert.False(run.Manual);
        Assert.EndsWith(".xlsx", run.FileName);
        var after = await ReadAsync<ScheduledReportDto>(await admin.GetAsync($"/api/v1/analytics/schedules/{schedule.Id}"));
        Assert.True(after.NextRunAt > DateTime.UtcNow);
        Assert.StartsWith("Delivered to 1", after.LastStatus);

        var outbox = Path.Combine(factory.Services.GetRequiredService<IConfiguration>()["Storage:DataPath"]!, "mail-outbox");
        var mail = Directory.GetFiles(outbox, "*.eml").Select(File.ReadAllText).Single(m => m.Contains(recipient));
        Assert.Contains("spreadsheetml.sheet", mail);       // the rendered report is attached
        Assert.Contains("attachment", mail);

        // Run now → a manual run in the history; deleting the schedule removes it.
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync($"/api/v1/analytics/schedules/{schedule.Id}/run", null)).StatusCode);
        await factory.DrainJobsAsync();
        runs = await ReadAsync<List<ScheduledReportRunDto>>(await admin.GetAsync($"/api/v1/analytics/schedules/{schedule.Id}/runs"));
        Assert.Equal(2, runs.Count);
        Assert.Contains(runs, r => r.Manual && r.Status == "Succeeded");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/analytics/schedules/{schedule.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/analytics/schedules/{schedule.Id}")).StatusCode);
    }

    // ───────────────────────────── Roll-ups & reports ─────────────────────────────

    [Fact]
    public async Task Daily_rollups_and_every_standard_report_build()
    {
        var t = await factory.CreateTenantAsync();
        var (agentId, _) = await t.CreateAgentAsync("Rollup Agent", "Agent", t.SupportDepartmentId);
        var ticket = await CreateTicketAsync(t, t.SupportDepartmentId, assignee: agentId);
        await factory.InTenantAsync(t.Id, async sp =>
        {
            var loader = sp.GetRequiredService<SupportDesk.Application.Identity.PrincipalLoader>();
            await loader.ApplyAgentAsync(t.Id, agentId);
            await sp.GetRequiredService<ConversationService>().ReplyAsync(ticket, new ReplyRequest { BodyHtml = "<p>Looking</p>" }, default);
        });
        await factory.DrainOutboxAsync();
        await factory.RunRecurringAsync(AnalyticsRollupTask.TaskName);

        var rows = await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().AgentMetrics.AsNoTracking().Where(m => m.UserId == agentId).ToListAsync());
        Assert.Equal(1, rows.Sum(r => r.Replies));
        Assert.Equal(1, rows.Sum(r => r.FirstResponses));
        Assert.True(await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().BacklogSnapshots.AnyAsync(s => s.OpenCount == 1)));

        var admin = await t.AdminClientAsync();
        var status = await ReadAsync<MetricsStatusDto>(await admin.GetAsync("/api/v1/analytics/metrics/status"));
        Assert.Equal(0, status.Missing);
        Assert.Equal(0, status.Stale);
        foreach (var report in ReportCatalog.All)
        {
            var result = await ReadAsync<ReportResultDto>(await admin.GetAsync($"/api/v1/analytics/reports/{report.Key}?period=last_7_days&compare=previous"));
            Assert.Equal(report.Key, result.Key);
            Assert.NotEmpty(result.Kpis);
        }
        var agents = await ReadAsync<ReportResultDto>(await admin.GetAsync("/api/v1/analytics/reports/agents?period=last_7_days"));
        var row = agents.Tables.Single(x => x.Id == "agents").Rows.Single(r => ((JsonElement)r["_key"]!).GetString() == agentId.ToString());
        Assert.Equal(1, ((JsonElement)row["agent_replies"]!).GetInt32());
        Assert.Equal(1, ((JsonElement)row["open"]!).GetDouble());

        foreach (var path in new[] { "pareto?metric=ticket_count&dimension=priority", "cohorts?period=last_30_days", "ageing?dimension=department", "breakdown?metrics=ticket_count,backlog&dimension=assignee" })
            Assert.True((await admin.GetAsync($"/api/v1/analytics/{path}")).IsSuccessStatusCode, path);
        var rebuild = await ReadAsync<MetricsStatusDto>(await admin.PostAsJsonAsync("/api/v1/analytics/metrics/rebuild", new RebuildMetricsRequest { Days = 7 }, J));
        Assert.True(rebuild.RebuildQueued);
        await factory.DrainJobsAsync();
        Assert.False((await ReadAsync<MetricsStatusDto>(await admin.GetAsync("/api/v1/analytics/metrics/status"))).RebuildQueued);
    }
}
