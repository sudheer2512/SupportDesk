using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Administration.ConfigTransfer;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Seed;
using SupportDesk.Application.Tenancy;
using SupportDesk.Contracts.Subscriptions;
using SupportDesk.Domain;
using SupportDesk.Domain.Platform;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Subscriptions;

/// <summary>
/// Demo data for subscriptions, the platform console and compliance: the "wings" billing history (paid invoices and the
/// current open one), 60 days of usage history, a billing contact, a pending support-access request, retention policies,
/// a legal hold, a configuration baseline snapshot, plus two extra tenants for the platform console (a trial ending soon
/// and a tenant suspended for an unpaid invoice).
/// </summary>
public sealed class SubscriptionsDemoSeed(IServiceScopeFactory scopes) : IDemoSeedContributor
{
    public int Order => 990;

    public async Task SeedAsync(DemoSeedContext c, CancellationToken ct)
    {
        var now = c.Anchor;
        c.Clock.Pin(now);
        try
        {
            await SeedWingsBillingAsync(c, now, ct);
            await SeedComplianceAsync(c, now, ct);
            await SeedExtraTenantsAsync(c, now, ct);
        }
        finally
        {
            c.Clock.Unpin();
        }
    }

    private static async Task SeedWingsBillingAsync(DemoSeedContext c, DateTime now, CancellationToken ct)
    {
        Dictionary<string, long> current = [];
        await c.AsSystemAsync(async sp => current = await sp.GetRequiredService<UsageCollector>().RecomputeAsync(ct));
        await c.AsSystemAsync(async sp =>
        {
            var catalog = sp.GetRequiredService<CatalogStore>().Db;
            var tenant = await catalog.Tenants.FirstAsync(t => t.Id == c.TenantId, ct);
            var sub = await catalog.AllTenants<Subscription>().FirstAsync(s => s.TenantId == c.TenantId, ct);
            var plan = await catalog.Plans.FirstAsync(p => p.Id == sub.PlanId, ct);
            var start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-4);
            sub.StartedAt = start;
            sub.BillingContactName = "Asha Menon";
            sub.BillingContactEmail = "accounts@wings.demo";
            sub.BillingAddress = "Wings Software Pvt Ltd, 4th Floor, Tidel Park, Chennai 600113, India";
            sub.BillingTaxId = "33AABCW1234F1Z5";
            var issuer = sp.GetRequiredService<InvoiceIssuer>();
            var agents = Math.Max(current.GetValueOrDefault(UsageMetrics.Agents), 1);
            for (var period = start; period <= now; period = period.AddMonths(1))
            {
                sub.CurrentPeriodStart = period;
                sub.CurrentPeriodEnd = period.AddMonths(1);
                var invoice = await issuer.EnsurePeriodInvoiceAsync(catalog, tenant, sub, plan, agents, period.AddHours(2), ct);
                if (sub.CurrentPeriodEnd <= now)
                {
                    invoice.Status = InvoiceStatus.Paid;
                    invoice.PaidAt = period.AddDays(6);
                    invoice.PaymentReference = $"NEFT/{period:yyyyMM}/WINGS";
                }
                else invoice.DueAt = now.AddDays(9);
                await catalog.SaveChangesAsync(ct);
            }

            // 60 days of usage history, trending up to today's real values.
            var rng = new Random(42);
            for (var d = 59; d >= 0; d--)
            {
                var day = now.Date.AddDays(-d);
                var factor = 1 - d / 140.0;
                foreach (var (metric, value) in current)
                {
                    var monthly = metric is UsageMetrics.AiTokens or UsageMetrics.ApiCalls;
                    long q = monthly
                        ? (long)(value * Math.Min(1, day.Day / (double)Math.Max(1, now.Day)) * (0.9 + rng.NextDouble() * 0.2))
                        : metric is UsageMetrics.Agents or UsageMetrics.LightAgents or UsageMetrics.Channels
                            ? Math.Max(0, value - (d > 40 ? 2 : d > 20 ? 1 : 0))
                            : (long)(value * factor);
                    if (metric == UsageMetrics.ApiCalls && q == 0) q = 400 + day.Day * 37 + rng.Next(0, 90);
                    if (metric == UsageMetrics.AiTokens && q == 0) q = 2500 + day.Day * 820 + rng.Next(0, 600);
                    catalog.Add(new UsageSnapshot { TenantId = c.TenantId, Day = day, Metric = metric, Quantity = Math.Max(0, q), UpdatedAt = day.AddHours(23) });
                }
            }

            var op = await catalog.PlatformOperators.AsNoTracking().OrderBy(o => o.CreatedAt).FirstOrDefaultAsync(ct);
            catalog.Add(new SupportAccessGrant
            {
                TenantId = c.TenantId, OperatorId = op?.Id ?? Guid.Empty, OperatorName = op?.DisplayName ?? "Platform Operator", DurationMinutes = 120, Status = SupportAccessStatus.Pending,
                Reason = "Investigating slow ticket list loads reported by your team (case PLAT-2041). Read-only diagnostics only."
            });
            await catalog.SaveChangesAsync(ct);
        });
    }

    private static async Task SeedComplianceAsync(DemoSeedContext c, DateTime now, CancellationToken ct)
    {
        await c.AsAgentAsync("admin", async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            db.RetentionPolicies.Add(new RetentionPolicy { Name = "Archive closed tickets after 2 years", EntityType = "ClosedTickets", Action = RetentionAction.Archive, AfterDays = 730 });
            db.RetentionPolicies.Add(new RetentionPolicy { Name = "Purge AI execution logs after 180 days", EntityType = "AiLogs", Action = RetentionAction.Delete, AfterDays = 180, IsActive = false });
            db.RetentionPolicies.Add(new RetentionPolicy { Name = "Delete audit records after 7 years", EntityType = "AuditLogs", Action = RetentionAction.Delete, AfterDays = 7 * 365 });
            if (c.Customers.TryGetValue("coastal", out var coastal))
                db.LegalHolds.Add(new LegalHold
                {
                    Name = "Coastal Foods contract dispute", Reason = "Preserve all support records for arbitration matter ARB/2026/114 (legal: R. Iyer).",
                    CustomerId = coastal, FromDate = now.AddDays(-365)
                });
            await db.SaveChangesAsync(ct);
            await sp.GetRequiredService<ConfigTransferService>().CreateSnapshotAsync(new Contracts.ConfigTransfer.CreateSnapshotRequest("Baseline after go-live"), ct);
        });
    }

    private async Task SeedExtraTenantsAsync(DemoSeedContext c, DateTime now, CancellationToken ct)
    {
        using (var scope = scopes.CreateScope())
        {
            var sp = scope.ServiceProvider;
            await sp.GetRequiredService<TenantProvisioningService>().ProvisionAsync(new ProvisionTenantRequest(
                "acme-trial", "Acme Logistics", "trial", "admin@acme-trial.demo", "Alex Carter (Admin)", Seed.DemoSeeder.AgentPassword, "Europe/Berlin", "DE",
                Trial: true, Seats: 3, DemoMode: true), ct);
            var db = sp.GetRequiredService<DeskDbContext>();
            var sub = await db.Subscriptions.FirstAsync(ct);
            sub.StartedAt = now.AddDays(-25);
            sub.CurrentPeriodStart = now.AddDays(-25);
            sub.CurrentPeriodEnd = now.AddDays(5);
            sub.TrialEndsAt = now.AddDays(5);
            await db.SaveChangesAsync(ct);
        }
        using (var scope = scopes.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var provisioned = await sp.GetRequiredService<TenantProvisioningService>().ProvisionAsync(new ProvisionTenantRequest(
                "globex", "Globex Retail", "starter", "admin@globex.demo", "Grace Lee (Admin)", Seed.DemoSeeder.AgentPassword, "America/New_York", "US",
                Trial: false, Seats: 4, DemoMode: true), ct);
            var db = sp.GetRequiredService<DeskDbContext>();
            var tenant = await db.Tenants.FirstAsync(t => t.Id == provisioned.TenantId, ct);
            var sub = await db.Subscriptions.FirstAsync(ct);
            var plan = await db.Plans.FirstAsync(p => p.Id == sub.PlanId, ct);
            sub.StartedAt = now.AddDays(-70);
            sub.CurrentPeriodStart = now.AddDays(-40);
            sub.CurrentPeriodEnd = now.AddDays(-10);
            sub.BillingContactEmail = "ap@globex.demo";
            await db.SaveChangesAsync(ct);
            var invoice = await sp.GetRequiredService<InvoiceIssuer>().EnsurePeriodInvoiceAsync(db, tenant, sub, plan, 4, now.AddDays(-40), ct);
            invoice.DueAt = now.AddDays(-26);
            sub.GraceEndsAt = now.AddDays(-19);
            sp.GetRequiredService<BillingLifecycle>().Suspend(db, tenant, sub, "Billing: an invoice remained unpaid after the grace period.", now.AddDays(-19));
            await db.SaveChangesAsync(ct);
        }
        _ = c;
    }
}
