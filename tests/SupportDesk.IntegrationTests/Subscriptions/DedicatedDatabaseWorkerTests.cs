using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Subscriptions;
using SupportDesk.Contracts.Platform;
using SupportDesk.Contracts.Tickets;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.IntegrationTests.Subscriptions;

/// <summary>
/// A tenant moved to a dedicated database keeps working in the background: its outbox events, jobs, audit sealing and
/// cross-tenant scans run against its own database, and work that was pending in the shared database at the move runs once.
/// </summary>
[Collection("api")]
public sealed class DedicatedDatabaseWorkerTests(DeskApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions J = DeskApiFactory.Json;

    [Fact]
    public async Task Background_work_follows_a_tenant_into_its_dedicated_database()
    {
        var t = await factory.CreateTenantAsync();

        // A job queued while the tenant still lives in the shared database.
        var beforeMove = $"before-move-{Guid.NewGuid():N}";
        var pendingJobId = await factory.InTenantAsync(t.Id, async sp =>
        {
            var job = sp.GetRequiredService<IJobQueue>().Enqueue(TestCounterJobHandler.JobType, beforeMove);
            await sp.GetRequiredService<DeskDbContext>().SaveChangesAsync();
            return job.Id;
        });

        // Move the tenant (platform operator).
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<RequestContext>().SetPlatformOperator(Guid.NewGuid(), "Test operator");
            await scope.ServiceProvider.GetRequiredService<DedicatedDatabaseProvisioner>()
                .ProvisionAsync(t.Id, new ProvisionDedicatedRequest($"d{Guid.NewGuid():N}"[..12]));
        }

        // The shared original of the pending job is retired; the copy runs exactly once in the dedicated database.
        Assert.Equal("Cancelled", await SharedScalarAsync<string>("SELECT Status FROM dbo.BackgroundJobs WHERE Id = @id", pendingJobId));
        await factory.DrainJobsAsync();
        Assert.Equal(1, Runs(beforeMove));
        await factory.DrainJobsAsync();
        Assert.Equal(1, Runs(beforeMove));

        // New work after the move: a ticket created through the API lands in the dedicated database, and its events are
        // processed there (the analytics handler builds the ticket's metric row).
        var admin = await t.AdminClientAsync();
        var created = await admin.PostAsJsonAsync("/api/v1/tickets",
            new CreateTicketRequest { Subject = "Dedicated database ticket", Channel = "Phone", SkipRouting = true }, J);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var ticket = (await created.Content.ReadFromJsonAsync<TicketDetailDto>(J))!;
        Assert.Equal(0, await SharedScalarAsync<int>("SELECT COUNT(*) FROM dbo.Tickets WHERE Id = @id", ticket.Id));

        await factory.DrainOutboxAsync();
        var (pendingEvents, hasMetric) = await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            return (await db.OutboxEvents.CountAsync(e => e.TenantId == t.Id && e.Status != Domain.Platform.OutboxStatus.Processed),
                await db.TicketMetrics.AnyAsync(m => m.TicketId == ticket.Id));
        });
        Assert.Equal(0, pendingEvents);
        Assert.True(hasMetric, "The ticket.created handlers did not run against the dedicated database.");

        // A job queued after the move runs from the dedicated database.
        var afterMove = $"after-move-{Guid.NewGuid():N}";
        await factory.InTenantAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<IJobQueue>().Enqueue(TestCounterJobHandler.JobType, afterMove);
            await sp.GetRequiredService<DeskDbContext>().SaveChangesAsync();
        });
        await factory.DrainJobsAsync();
        Assert.Equal(1, Runs(afterMove));

        // Recurring work reaches the dedicated database too: audit rows written there get sealed, cross-tenant scans run.
        await factory.RunRecurringAsync("audit-sealer");
        await factory.RunRecurringAsync("sla-monitor");
        await factory.RunRecurringAsync("contract-lifecycle");
        var unsealed = await factory.InTenantAsync(t.Id, sp =>
            sp.GetRequiredService<DeskDbContext>().AuditLogs.CountAsync(a => a.TenantId == t.Id && a.Hash == ""));
        Assert.Equal(0, unsealed);
    }

    /// <summary>How often the test job ran for a payload (the handler records the payload as JSON).</summary>
    private static int Runs(string payload) => TestCounterJobHandler.Executions.GetValueOrDefault($"\"{payload}\"");

    private async Task<T> SharedScalarAsync<T>(string sql, Guid id)
    {
        await using var conn = new SqlConnection(factory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@id", id);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}
