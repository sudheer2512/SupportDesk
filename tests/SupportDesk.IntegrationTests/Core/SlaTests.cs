using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Sla;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Sla;
using SupportDesk.Domain.Tickets;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.Persistence;

namespace SupportDesk.IntegrationTests.Core;

/// <summary>
/// SLA calculations on the default policy (business hours Mon–Fri 09:00–18:00 Asia/Kolkata; High = 60 min first
/// response, 8 business hours resolution; Critical = 24x7, 15 min / 4 h). All times below are IST unless noted.
/// </summary>
[Collection("api")]
public sealed class SlaTests(DeskApiFactory factory)
{
    private static DateTime Ist(int day, int hour, int minute = 0) => new DateTime(2026, 10, day, hour, minute, 0, DateTimeKind.Utc).AddMinutes(-330);

    private async Task<(Ticket Ticket, List<SlaMilestone> Milestones)> LoadAsync(Guid tenant, Guid ticketId) =>
        await factory.InTenantAsync(tenant, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var t = await db.Tickets.AsNoTracking().FirstAsync(x => x.Id == ticketId);
            var ms = await db.SlaMilestones.AsNoTracking().Where(m => m.TicketId == ticketId).OrderBy(m => m.Kind).ThenBy(m => m.Cycle).ToListAsync();
            return (t, ms);
        });

    [Fact]
    public async Task Business_hours_pause_retarget_breach_and_escalation()
    {
        var tenant = await factory.CreateTenantAsync();
        var clock = factory.Clock;
        Guid ticketId = default, waiting = default;
        try
        {
            // Monday 12 Oct 10:00 — High priority ticket.
            clock.Pin(Ist(12, 10));
            await factory.InTenantAsync(tenant.Id, async sp =>
            {
                var t = await sp.GetRequiredService<TicketService>().CreateAsync(new CreateTicketRequest
                {
                    Subject = "SLA story", Channel = "Email", Priority = "High", SkipRouting = true,
                    NewContact = new NewContactDto("Customer", $"c{Guid.NewGuid():N}@x.test", null, null)
                }, new TicketCreationSource(CreateInitialThread: true, EnforceRequiredFields: false), default);
                ticketId = t.Id;
                waiting = (await sp.GetRequiredService<DeskDbContext>().TicketStatuses.FirstAsync(s => s.Name == "Waiting on Customer")).Id;
            });
            var (ticket, ms) = await LoadAsync(tenant.Id, ticketId);
            Assert.Equal(Ist(12, 11), ticket.FirstResponseDueAt);   // +60 business minutes
            Assert.Equal(Ist(12, 18), ticket.DueAt);                // +480 business minutes = end of Monday
            Assert.Equal(SlaState.OnTrack, ticket.SlaState);

            // 10:30 agent replies → first response met. 11:00 waiting on customer → resolution clock paused (60 min used).
            clock.Pin(Ist(12, 10, 30));
            await factory.InTenantAsync(tenant.Id, sp => sp.GetRequiredService<ConversationService>().ReplyAsync(ticketId, new ReplyRequest { BodyHtml = "<p>Looking</p>" }, default));
            // Met on time → an "sla.met" event for webhooks, automation and analytics.
            Assert.True(await factory.InTenantAsync(tenant.Id, sp => sp.GetRequiredService<DeskDbContext>().OutboxEvents
                .AnyAsync(e => e.Type == EventTypes.SlaMet && e.AggregateId == ticketId)), "sla.met was not published");
            clock.Pin(Ist(12, 11));
            await factory.InTenantAsync(tenant.Id, sp => sp.GetRequiredService<TicketService>().ChangeStatusAsync(ticketId, new ChangeStatusRequest(waiting, null, null, null), default));
            (ticket, ms) = await LoadAsync(tenant.Id, ticketId);
            Assert.Equal(MilestoneStatus.Met, ms.Single(m => m.Kind == SlaMilestoneKind.FirstResponse).Status);
            var resolution = ms.Single(m => m.Kind == SlaMilestoneKind.Resolution);
            Assert.Equal(MilestoneStatus.Paused, resolution.Status);
            Assert.Equal(60, resolution.ElapsedMinutesBeforeSegment, 3);
            Assert.Equal(SlaState.Paused, ticket.SlaState);

            // Tuesday 10:00 customer replies → resumes; 420 business minutes left → Tuesday 17:00. A next-response cycle starts (240 min → 14:00).
            clock.Pin(Ist(13, 10));
            await factory.InTenantAsync(tenant.Id, sp => sp.GetRequiredService<ConversationService>().AddCustomerMessageAsync(ticketId,
                new CustomerMessageInput("<p>Here are the details</p>", null, Channel.Email, null, "Customer"), default));
            (ticket, ms) = await LoadAsync(tenant.Id, ticketId);
            Assert.Equal(Ist(13, 17), ticket.DueAt);
            Assert.Equal(Ist(13, 14), ticket.NextResponseDueAt);
            Assert.Equal(MilestoneStatus.Active, ms.Single(m => m.Kind == SlaMilestoneKind.Resolution).Status);

            // Priority → Critical (24x7, 4 h): elapsed 60 min is kept, 180 calendar minutes remain → 13:00.
            await factory.InTenantAsync(tenant.Id, async sp =>
            {
                var t = await sp.GetRequiredService<DeskDbContext>().Tickets.AsNoTracking().FirstAsync(x => x.Id == ticketId);
                await sp.GetRequiredService<TicketService>().UpdateAsync(ticketId, new UpdateTicketRequest { RowVersion = Convert.ToBase64String(t.RowVersion), Priority = "Critical" }, default);
            });
            (ticket, _) = await LoadAsync(tenant.Id, ticketId);
            Assert.Equal(Ist(13, 13), ticket.DueAt);

            // 13:05 — monitor marks the breach and fires escalation level 1.
            clock.Pin(Ist(13, 13, 5));
            await factory.InTenantAsync(tenant.Id, sp => sp.GetRequiredService<SlaMonitorProcessor>().ProcessAsync(default));
            (ticket, ms) = await LoadAsync(tenant.Id, ticketId);
            resolution = ms.Single(m => m.Kind == SlaMilestoneKind.Resolution);
            Assert.Equal(MilestoneStatus.Breached, resolution.Status);
            Assert.Equal(Ist(13, 13), resolution.BreachedAt);
            Assert.Equal(SlaState.Breached, ticket.SlaState);
            Assert.True(ticket.IsEscalated);
            await factory.InTenantAsync(tenant.Id, async sp =>
            {
                var db = sp.GetRequiredService<DeskDbContext>();
                Assert.True(await db.SlaEvents.AnyAsync(e => e.TicketId == ticketId && e.Type == SlaEventTypes.Breached));
                Assert.True(await db.SlaEvents.AnyAsync(e => e.TicketId == ticketId && e.Type == SlaEventTypes.Escalated));
                Assert.True(await db.OutboxEvents.AnyAsync(e => e.AggregateId == ticketId && e.Type == EventTypes.SlaBreached));
            });

            // Running the monitor again is idempotent (no additional breach/escalation events).
            Func<Task<int>> slaEventCount = () => factory.InTenantAsync(tenant.Id, sp =>
                sp.GetRequiredService<DeskDbContext>().SlaEvents.CountAsync(e => e.TicketId == ticketId && (e.Type == SlaEventTypes.Breached || e.Type == SlaEventTypes.Escalated)));
            var before = await slaEventCount();
            await factory.InTenantAsync(tenant.Id, sp => sp.GetRequiredService<SlaMonitorProcessor>().ProcessAsync(default));
            Assert.Equal(before, await slaEventCount());

            // Resolution after the breach keeps the milestone breached (late) and completes the instance.
            clock.Pin(Ist(13, 13, 30));
            await factory.InTenantAsync(tenant.Id, sp => sp.GetRequiredService<TicketService>().ResolveAsync(ticketId, new ResolveTicketRequest("fixed", null, null), default));
            (ticket, ms) = await LoadAsync(tenant.Id, ticketId);
            resolution = ms.Single(m => m.Kind == SlaMilestoneKind.Resolution);
            Assert.Equal(MilestoneStatus.Breached, resolution.Status);
            Assert.NotNull(resolution.CompletedAt);
            Assert.Equal(SlaState.Breached, ticket.SlaState);
        }
        finally
        {
            clock.Unpin();
        }
    }

    [Fact]
    public async Task Reopened_ticket_resumes_resolution_clock_without_counting_resolved_time()
    {
        var tenant = await factory.CreateTenantAsync();
        var clock = factory.Clock;
        Guid ticketId = default;
        try
        {
            clock.Pin(Ist(14, 9));
            await factory.InTenantAsync(tenant.Id, async sp => ticketId = (await sp.GetRequiredService<TicketService>().CreateAsync(new CreateTicketRequest
            {
                Subject = "Reopen", Channel = "Phone", Priority = "High", SkipRouting = true
            }, TicketCreationSource.Agent with { EnforceRequiredFields = false }, default)).Id);
            clock.Pin(Ist(14, 11)); // 120 business minutes used
            await factory.InTenantAsync(tenant.Id, sp => sp.GetRequiredService<TicketService>().ResolveAsync(ticketId, new ResolveTicketRequest("fixed", null, null), default));
            var (_, ms) = await LoadAsync(tenant.Id, ticketId);
            Assert.Equal(MilestoneStatus.Met, ms.Single(m => m.Kind == SlaMilestoneKind.Resolution).Status);

            clock.Pin(Ist(15, 9)); // reopened next morning: overnight resolved time must not count
            await factory.InTenantAsync(tenant.Id, sp => sp.GetRequiredService<TicketService>().ReopenAsync(ticketId, new ReopenTicketRequest("Still broken"), default));
            var (ticket, ms2) = await LoadAsync(tenant.Id, ticketId);
            var resolution = ms2.Single(m => m.Kind == SlaMilestoneKind.Resolution);
            Assert.Equal(MilestoneStatus.Active, resolution.Status);
            Assert.Equal(120, resolution.ElapsedMinutesBeforeSegment, 3);
            Assert.Equal(Ist(15, 15), ticket.DueAt); // 480 − 120 = 360 business minutes from 09:00
            Assert.Equal(1, ticket.ReopenCount);
        }
        finally
        {
            clock.Unpin();
        }
    }
}
