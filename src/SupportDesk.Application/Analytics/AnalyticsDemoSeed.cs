using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Seed;
using SupportDesk.Domain.Analytics;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Analytics;

/// <summary>
/// Demo analytics: computes ticket metrics and 130 days of agent/backlog roll-ups from the seeded history, creates the
/// role-default dashboards and a weekly SLA report e-mailed to the support supervisor.
/// </summary>
public sealed class AnalyticsDemoSeed(ILogger<AnalyticsDemoSeed> logger) : IDemoSeedContributor
{
    public int Order => 950;

    public async Task SeedAsync(DemoSeedContext c, CancellationToken ct)
    {
        c.At(c.Anchor);
        try
        {
            await c.AsSystemAsync(async sp =>
            {
                await sp.GetRequiredService<MetricsMaintenanceService>().BackfillAsync(130, ct);
                if (!c.Users.TryGetValue("priya", out var priya)) return;
                var db = sp.GetRequiredService<DeskDbContext>();
                if (await db.Set<ScheduledReport>().AnyAsync(ct)) return;
                var tz = await db.Tenants.AsNoTracking().Where(t => t.Id == c.TenantId).Select(t => t.DefaultTimeZone).FirstAsync(ct);
                var run = new TimeOnly(8, 0);
                db.Add(new ScheduledReport
                {
                    Name = "Weekly SLA review", ReportKey = "sla", Format = ReportFormat.Pdf, Frequency = ReportFrequency.Weekly, RunAtLocal = run,
                    DayOfWeek = DayOfWeek.Monday, TimeZone = tz, OwnerUserId = priya, IsActive = true,
                    ParametersJson = Json.Serialize(new Dictionary<string, string> { ["period"] = "last_week", ["compare"] = "previous" }),
                    RecipientsJson = Json.Serialize(new[] { "priya@wings.demo" }),
                    NextRunAt = ScheduleCalculator.Next(ReportFrequency.Weekly, run, DayOfWeek.Monday, null, tz, c.Anchor)
                });
                await db.SaveChangesAsync(ct);
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Demo data only: the roll-up task and the backfill job recompute everything later.
            logger.LogWarning(ex, "Analytics demo seed incomplete");
        }
    }
}
