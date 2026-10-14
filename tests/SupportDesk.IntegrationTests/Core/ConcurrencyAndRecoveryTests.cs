using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Platform;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain.Platform;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.Persistence;
using SupportDesk.Workers;

namespace SupportDesk.IntegrationTests.Core;

[Collection("api")]
public sealed class ConcurrencyAndRecoveryTests(DeskApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions J = DeskApiFactory.Json;

    [Fact]
    public async Task Concurrent_replies_get_unique_gap_free_sequence_numbers()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var ticket = (await (await admin.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest { Subject = "Busy ticket", Channel = "Phone", SkipRouting = true }, J))
            .Content.ReadFromJsonAsync<TicketDetailDto>(J))!;

        var replies = await Task.WhenAll(Enumerable.Range(1, 8).Select(i =>
            admin.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/replies", new ReplyRequest { BodyHtml = $"<p>Reply {i}</p>" }, J)));
        Assert.All(replies, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var seqs = await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().TicketThreads
            .Where(x => x.TicketId == ticket.Id).Select(x => x.Seq).OrderBy(x => x).ToListAsync());
        Assert.Equal(Enumerable.Range(1, 8), seqs);
    }

    [Fact]
    public async Task Concurrent_ticket_creation_gets_unique_numbers()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var created = await Task.WhenAll(Enumerable.Range(1, 12).Select(async i =>
            (await (await admin.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest { Subject = $"Parallel {i}", Channel = "Phone", SkipRouting = true }, J))
                .Content.ReadFromJsonAsync<TicketDetailDto>(J))!.Number));
        Assert.Equal(12, created.Distinct().Count());
        Assert.Equal(created.Min() + 11, created.Max());
    }

    [Fact]
    public async Task Concurrent_edits_with_the_same_version_let_exactly_one_win()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var ticket = (await (await admin.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest { Subject = "Contended", Channel = "Phone", SkipRouting = true }, J))
            .Content.ReadFromJsonAsync<TicketDetailDto>(J))!;
        var results = await Task.WhenAll(
            admin.PatchAsJsonAsync($"/api/v1/tickets/{ticket.Id}", new UpdateTicketRequest { RowVersion = ticket.RowVersion, Priority = "High" }, J),
            admin.PatchAsJsonAsync($"/api/v1/tickets/{ticket.Id}", new UpdateTicketRequest { RowVersion = ticket.RowVersion, Priority = "Low" }, J));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Jobs_abandoned_by_a_crashed_worker_are_reclaimed_and_run_once()
    {
        var t = await factory.CreateTenantAsync();
        var key = $"\"recovery-{Guid.NewGuid():N}\"";
        await factory.InTenantAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<IJobQueue>().Enqueue(TestCounterJobHandler.JobType, key.Trim('"'));
            await sp.GetRequiredService<DeskDbContext>().SaveChangesAsync();
        });
        var processor = factory.Services.GetRequiredService<JobProcessor>();
        // A worker claims the job (lease taken) and then "crashes" without completing it.
        var claimed = await processor.ClaimAsync(100, default);
        Assert.NotEmpty(claimed);
        Assert.False(TestCounterJobHandler.Executions.ContainsKey(key));
        // While the lease is valid nobody else may take it.
        Assert.DoesNotContain(await processor.ClaimAsync(100, default), claimed.Contains);
        // After the lease expires (simulated restart later), the job is reclaimed and executed exactly once.
        factory.Clock.Pin(DateTime.UtcNow.Add(JobProcessor.Lease).AddMinutes(1));
        try
        {
            await processor.DrainAsync(default);
            await processor.DrainAsync(default);
        }
        finally
        {
            factory.Clock.Unpin();
        }
        Assert.Equal(1, TestCounterJobHandler.Executions[key]);
    }

    [Fact]
    public async Task Outbox_events_stuck_in_processing_are_recovered_and_handlers_not_repeated()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var ticket = (await (await admin.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest { Subject = "Outbox crash", Channel = "Phone", SkipRouting = true }, J))
            .Content.ReadFromJsonAsync<TicketDetailDto>(J))!;
        // Simulate an instance that claimed the events and died: Processing with a lease in the past.
        await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().OutboxEvents.Where(e => e.AggregateId == ticket.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.Status, OutboxStatus.Processing).SetProperty(e => e.LockedUntil, DateTime.UtcNow.AddMinutes(-1))));
        await factory.DrainOutboxAsync();
        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var events = await db.OutboxEvents.Where(e => e.AggregateId == ticket.Id).ToListAsync();
            Assert.All(events, e => Assert.Equal(OutboxStatus.Processed, e.Status));
            var ids = events.Select(e => e.Id).ToList();
            var deliveries = await db.OutboxDeliveries.Where(d => ids.Contains(d.EventId)).ToListAsync();
            Assert.All(deliveries.GroupBy(d => new { d.EventId, d.Handler }), g => Assert.Single(g));
            Assert.All(deliveries, d => Assert.True(d.Succeeded));
        });
    }

    [Fact]
    public async Task Audit_chain_is_sealed_verifiable_and_tamper_evident()
    {
        var t = await factory.CreateTenantAsync();
        await factory.LoginAsync(t.Slug, t.AdminEmail); // produces audit records
        await factory.RunRecurringAsync("audit-sealer");
        var result = await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<AuditVerifier>().VerifyAsync(t.Id, default));
        Assert.True(result.Valid, result.Message);
        Assert.True(result.Checked > 0);

        // The database refuses to modify sealed rows…
        await using (var conn = new SqlConnection(factory.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE dbo.AuditLogs SET Summary = N'covering tracks' WHERE TenantId = @t";
            cmd.Parameters.AddWithValue("@t", t.Id);
            await Assert.ThrowsAsync<SqlException>(() => cmd.ExecuteNonQueryAsync());
        }
        // …and even a privileged bypass of the trigger is detected by chain verification.
        await using (var conn = new SqlConnection(factory.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXEC sp_set_session_context N'audit_purge', 1; UPDATE TOP (1) dbo.AuditLogs SET Summary = N'covering tracks' WHERE TenantId = @t AND [Hash] <> ''";
            cmd.Parameters.AddWithValue("@t", t.Id);
            await cmd.ExecuteNonQueryAsync();
        }
        var tampered = await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<AuditVerifier>().VerifyAsync(t.Id, default));
        Assert.False(tampered.Valid);
    }
}
