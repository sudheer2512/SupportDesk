using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Notifications;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Ai;
using SupportDesk.Domain;
using SupportDesk.Domain.Ai;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Ai.Anomalies;

public sealed record DayCount(Guid Key, DateTime Day, int Count);

/// <summary>
/// Daily ticket-volume anomaly detection per product, version, customer and category: today's (or a given day's) count
/// against the trailing 28-day mean and standard deviation. A z-score of at least 3 with at least 5 tickets raises an
/// <see cref="AnomalyAlert"/> (one per dimension value per day — idempotent) and notifies supervisors once.
/// </summary>
public sealed class AnomalyDetectionService(DeskDbContext db, RequestContext ctx, AiGateway gateway, NotificationService notifications, TicketLookups lookups)
{
    public const int WindowDays = 28;
    public const int MinCount = 5;
    public const double ZThreshold = 3.0;

    public static readonly string[] Dimensions = ["product", "version", "customer", "category"];

    /// <summary>z-score with a Poisson floor on the deviation so sparse series (mean ≈ 0) do not explode.</summary>
    public static double ZScore(int count, double mean, double stdDev) => (count - mean) / Math.Max(stdDev, Math.Sqrt(Math.Max(mean, 1.0)));

    public static (double Mean, double StdDev) Baseline(IReadOnlyList<int> history)
    {
        if (history.Count == 0) return (0, 0);
        var mean = history.Average();
        var variance = history.Sum(x => (x - mean) * (x - mean)) / history.Count;
        return (mean, Math.Sqrt(variance));
    }

    public async Task<int> DetectAsync(DateOnly day, CancellationToken ct)
    {
        if (!await gateway.IsAvailableAsync(AiFeatures.AnomalyDetection, interactive: false, ct)) return 0;
        var dayStart = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var from = dayStart.AddDays(-WindowDays);
        var to = dayStart.AddDays(1);
        var q = db.Tickets.AsNoTracking().Where(t => t.CreatedAt >= from && t.CreatedAt < to && !t.IsSpam && t.MergedIntoTicketId == null);
        await lookups.LoadAsync(ct);
        var created = new List<AnomalyAlert>();
        foreach (var dimension in Dimensions)
        {
            var rows = dimension switch
            {
                "product" => await q.Where(t => t.ProductId != null).GroupBy(t => new { K = t.ProductId!.Value, D = t.CreatedAt.Date })
                    .Select(g => new DayCount(g.Key.K, g.Key.D, g.Count())).ToListAsync(ct),
                "version" => await q.Where(t => t.VersionId != null).GroupBy(t => new { K = t.VersionId!.Value, D = t.CreatedAt.Date })
                    .Select(g => new DayCount(g.Key.K, g.Key.D, g.Count())).ToListAsync(ct),
                "customer" => await q.Where(t => t.CustomerId != null).GroupBy(t => new { K = t.CustomerId!.Value, D = t.CreatedAt.Date })
                    .Select(g => new DayCount(g.Key.K, g.Key.D, g.Count())).ToListAsync(ct),
                _ => await q.Where(t => t.CategoryId != null).GroupBy(t => new { K = t.CategoryId!.Value, D = t.CreatedAt.Date })
                    .Select(g => new DayCount(g.Key.K, g.Key.D, g.Count())).ToListAsync(ct)
            };
            foreach (var group in rows.GroupBy(r => r.Key))
            {
                var count = group.Where(r => r.Day == dayStart).Sum(r => r.Count);
                if (count < MinCount) continue;
                var byDay = group.Where(r => r.Day < dayStart).ToDictionary(r => r.Day, r => r.Count);
                var history = Enumerable.Range(1, WindowDays).Select(i => byDay.GetValueOrDefault(dayStart.AddDays(-i))).ToList();
                var (mean, std) = Baseline(history);
                var z = ZScore(count, mean, std);
                if (z < ZThreshold) continue;
                var existing = await db.AnomalyAlerts.FirstOrDefaultAsync(a => a.Dimension == dimension && a.DimensionId == group.Key && a.Day == day, ct);
                if (existing is not null)
                {
                    if (count > existing.Count)
                    {
                        existing.Count = count;
                        existing.Score = Math.Round(z, 3);
                        existing.Baseline = Math.Round(mean, 3);
                        existing.StdDev = Math.Round(std, 3);
                    }
                    continue;
                }
                var alert = new AnomalyAlert
                {
                    Dimension = dimension, DimensionId = group.Key, DimensionLabel = await LabelAsync(dimension, group.Key, ct), Day = day, Count = count,
                    Baseline = Math.Round(mean, 3), StdDev = Math.Round(std, 3), Score = Math.Round(z, 3), CreatedAt = db.Clock.UtcNow
                };
                db.AnomalyAlerts.Add(alert);
                created.Add(alert);
            }
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another instance raised the same alert concurrently (unique per dimension/value/day).
            db.ChangeTracker.Clear();
            return 0;
        }
        if (created.Count == 0) return 0;

        var supervisors = await db.UserRoles.AsNoTracking()
            .Where(ur => db.RolePermissions.Any(p => p.RoleId == ur.RoleId && p.PermissionCode == Permissions.SupervisorMonitor))
            .Select(ur => ur.UserId).Distinct().ToListAsync(ct);
        foreach (var a in created)
            await notifications.NotifyAsync(new NotifyRequest(supervisors, NotificationTypes.AnomalyDetected,
                $"Unusual ticket volume: {a.DimensionLabel}", $"{a.Count} tickets on {a.Day:yyyy-MM-dd} vs. a typical {a.Baseline:0.#} per day (z = {a.Score:0.0}).",
                "/analytics/anomalies", NotificationSeverity.Warning, false, "AnomalyAlert", a.Id, $"anomaly:{a.Id:N}"), ct);
        await gateway.RunBuiltinAsync(new AiCall { Feature = AiFeatures.AnomalyDetection, EntityType = "AnomalyAlert" }, () =>
            Task.FromResult(new AiOutput<object>(created.Select(a => new { a.Id, a.Dimension, a.DimensionLabel, a.Day, a.Count, a.Baseline, a.Score }).ToList(),
                Math.Min(1, created.Max(a => a.Score) / 6))), ct);
        return created.Count;
    }

    private async Task<string> LabelAsync(string dimension, Guid id, CancellationToken ct) => dimension switch
    {
        "product" => lookups.Product(id) ?? "Product",
        "version" => await db.ProductVersions.AsNoTracking().Where(v => v.Id == id)
            .Join(db.Products.AsNoTracking(), v => v.ProductId, p => p.Id, (v, p) => p.Name + " " + v.Version).FirstOrDefaultAsync(ct) ?? "Version",
        "customer" => await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(c => c.Id == id).Select(c => c.Name)
            .FirstOrDefaultAsync(ct) ?? "Customer",
        _ => lookups.Category(id) ?? "Category"
    };

    public async Task<List<AnomalyAlertDto>> ListAsync(string? status, int days, CancellationToken ct)
    {
        ctx.RequireAny(Permissions.SupervisorMonitor, Permissions.AnalyticsTeam, Permissions.AdminAi);
        var since = DateOnly.FromDateTime(db.Clock.UtcNow.AddDays(-Math.Clamp(days, 1, 365)));
        var q = db.AnomalyAlerts.AsNoTracking().Where(a => a.Day >= since);
        if (Enum.TryParse<AnomalyStatus>(status, true, out var st)) q = q.Where(a => a.Status == st);
        return (await q.OrderByDescending(a => a.Day).ThenByDescending(a => a.Score).Take(200).ToListAsync(ct)).Select(Map).ToList();
    }

    public async Task<AnomalyAlertDto> SetStatusAsync(Guid id, AnomalyStatus status, CancellationToken ct)
    {
        ctx.RequireAny(Permissions.SupervisorMonitor, Permissions.AdminAi);
        var a = await db.AnomalyAlerts.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Anomaly alert", id);
        a.Status = status;
        a.AcknowledgedById = ctx.UserId;
        await db.SaveChangesAsync(ct);
        return Map(a);
    }

    public static AnomalyAlertDto Map(AnomalyAlert a) =>
        new(a.Id, a.Dimension, a.DimensionId, a.DimensionLabel, a.Day, a.Count, a.Baseline, a.StdDev, a.Score, a.Status.ToString(), a.CreatedAt);
}

/// <summary>Hourly: evaluates yesterday (complete) and today (so far) for every active tenant.</summary>
public sealed class AnomalyDetectionTask(TenantScopeRunner tenants, IClock clock) : IRecurringTask
{
    public string Name => "ai-anomaly-detection";
    public TimeSpan Interval => TimeSpan.FromHours(1);

    public Task RunAsync(CancellationToken ct) => tenants.ForEachActiveTenantAsync("AI anomaly detection", async (sp, _, token) =>
    {
        var service = sp.GetRequiredService<AnomalyDetectionService>();
        var today = DateOnly.FromDateTime(clock.UtcNow);
        await service.DetectAsync(today.AddDays(-1), token);
        await service.DetectAsync(today, token);
    }, ct);
}
