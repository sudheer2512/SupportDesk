using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Communication;
using SupportDesk.Application.Mobile;
using SupportDesk.Application.Notifications;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Mobile;
using SupportDesk.Contracts.Portal;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Events;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.IntegrationTests.Portal;
using SupportDesk.Persistence;

namespace SupportDesk.IntegrationTests.Mobile;

/// <summary>
/// Mobile app backend: push-device registration (tenant + user isolation), offline-sync idempotency of ticket
/// creation and replies (clientRequestId), and push dispatch (preferences, every device, dead tokens, customer push).
/// </summary>
[Collection("api")]
public sealed class MobileTests(DeskApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions J = DeskApiFactory.Json;

    // ───── Device registration ─────

    [Fact]
    public async Task Devices_are_isolated_per_user_and_tenant()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        var (u1, e1) = await a.CreateAgentAsync("Asha");
        var (u2, e2) = await a.CreateAgentAsync("Bilal");
        var agent1 = await a.AgentClientAsync(e1);
        var agent2 = await a.AgentClientAsync(e2);
        var token = "fcm-" + Guid.NewGuid().ToString("N") + ":APA91b" + new string('x', 40);

        var registered = await Register(agent1, "/api/v1/me/devices", new RegisterDeviceRequest("Android", token, "1.4.0"));
        Assert.Equal("Android", registered.Platform);
        Assert.Equal("agent", registered.AppKind);
        Assert.Equal("1.4.0", registered.AppVersion);
        Assert.True(registered.IsActive);
        Assert.Equal("…" + token[^6..], registered.TokenHint);
        Assert.DoesNotContain(token, await (await agent1.GetAsync("/api/v1/me/devices")).Content.ReadAsStringAsync());

        // Another agent of the same tenant neither sees nor can switch off the first agent's device.
        Assert.Empty(await agent2.GetFromJsonAsync<List<PushDeviceDto>>("/api/v1/me/devices", J) ?? []);
        Assert.Equal(HttpStatusCode.NoContent, (await agent2.PostAsJsonAsync("/api/v1/me/devices/unregister", new UnregisterDeviceRequest(token), J)).StatusCode);
        Assert.True(Assert.Single(await agent1.GetFromJsonAsync<List<PushDeviceDto>>("/api/v1/me/devices", J) ?? []).IsActive);

        // The same install token in another tenant is a separate registration that does not touch tenant A.
        var adminB = await b.AdminClientAsync();
        await Register(adminB, "/api/v1/me/devices", new RegisterDeviceRequest("Android", token));
        var inA = await factory.InTenantAsync(a.Id, sp => sp.GetRequiredService<DeskDbContext>().PushDevices.AsNoTracking().Where(d => d.Token == token).ToListAsync());
        Assert.Equal(u1, Assert.Single(inA).UserId);
        var inB = await factory.InTenantAsync(b.Id, sp => sp.GetRequiredService<DeskDbContext>().PushDevices.AsNoTracking().Where(d => d.Token == token).ToListAsync());
        Assert.NotEqual(u1, Assert.Single(inB).UserId);

        // The install changes hands inside tenant A (sign-in as another agent): the token moves, never duplicates.
        await Register(agent2, "/api/v1/me/devices", new RegisterDeviceRequest("Android", token, "1.4.1"));
        Assert.Empty(await agent1.GetFromJsonAsync<List<PushDeviceDto>>("/api/v1/me/devices", J) ?? []);
        var moved = await factory.InTenantAsync(a.Id, sp => sp.GetRequiredService<DeskDbContext>().PushDevices.AsNoTracking().SingleAsync(d => d.Token == token));
        Assert.Equal(u2, moved.UserId);

        // Unregister by the owner switches it off.
        Assert.Equal(HttpStatusCode.NoContent, (await agent2.PostAsJsonAsync("/api/v1/me/devices/unregister", new UnregisterDeviceRequest(token), J)).StatusCode);
        Assert.False(Assert.Single(await agent2.GetFromJsonAsync<List<PushDeviceDto>>("/api/v1/me/devices", J) ?? []).IsActive);

        // Validation.
        var bad = await agent1.PostAsJsonAsync("/api/v1/me/devices", new RegisterDeviceRequest("Symbian", "abc"), J);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("platform", await bad.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await agent1.PostAsJsonAsync("/api/v1/me/devices", new RegisterDeviceRequest("iOS", "has space"), J)).StatusCode);
    }

    [Fact]
    public async Task Portal_contacts_register_their_own_devices_on_the_portal_endpoint_only()
    {
        var t = await factory.CreateTenantAsync();
        var customer = await factory.CustomerAsync(t, "Kestrel Foods");
        var (contactId, contact) = await factory.ContactAsync(t, customer.Id, customer.HeadOffice, PortalAccessScope.Own, "Ravi Contact");
        var (_, other) = await factory.ContactAsync(t, customer.Id, customer.HeadOffice, PortalAccessScope.Own, "Other Contact");
        var token = new string('a', 64);

        var dto = await Register(contact, $"/api/v1/portal/{t.Slug}/devices", new RegisterDeviceRequest("iOS", token, "2.0.0"));
        Assert.Equal("customer", dto.AppKind);
        Assert.Equal("iOS", dto.Platform);
        Assert.Single(await contact.GetFromJsonAsync<List<PushDeviceDto>>($"/api/v1/portal/{t.Slug}/devices", J) ?? []);
        Assert.Empty(await other.GetFromJsonAsync<List<PushDeviceDto>>($"/api/v1/portal/{t.Slug}/devices", J) ?? []);
        var row = await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().PushDevices.AsNoTracking().SingleAsync(d => d.Token == token));
        Assert.Equal(contactId, row.ContactId);
        Assert.Null(row.UserId);

        // Contact tokens cannot use the agent endpoint and vice versa.
        var asAgent = await contact.PostAsJsonAsync("/api/v1/me/devices", new RegisterDeviceRequest("iOS", token), J);
        Assert.Contains(asAgent.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized });
        var admin = await t.AdminClientAsync();
        var asContact = await admin.PostAsJsonAsync($"/api/v1/portal/{t.Slug}/devices", new RegisterDeviceRequest("iOS", token), J);
        Assert.Contains(asContact.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized });
        Assert.Equal(contactId, (await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().PushDevices.AsNoTracking().SingleAsync(d => d.Token == token))).ContactId);
    }

    // ───── Offline sync: idempotent create / reply ─────

    [Fact]
    public async Task Agent_ticket_creation_and_replies_are_idempotent_by_client_request_id()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var (_, email) = await t.CreateAgentAsync("Mobile Agent");
        var agent = await t.AgentClientAsync(email);
        var createKey = "mobile:" + Guid.NewGuid().ToString("N");
        var create = new CreateTicketRequest
        {
            Subject = "Printer offline at branch", Channel = "MobileApp", SkipRouting = true, ClientRequestId = createKey,
            NewContact = new NewContactDto("Walk In", $"walkin.{Guid.NewGuid():N}@cust.test", null, null)
        };
        var first = await ReadOk<TicketDetailDto>(await agent.PostAsJsonAsync("/api/v1/tickets", create, J));
        var retry = await ReadOk<TicketDetailDto>(await agent.PostAsJsonAsync("/api/v1/tickets", create, J));
        Assert.Equal(first.Id, retry.Id);
        Assert.Equal(1, await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().Tickets.CountAsync(x => x.Subject == "Printer offline at branch")));

        // A reply sent twice (response lost on a flaky network) is stored once.
        var replyKey = "mobile:" + Guid.NewGuid().ToString("N");
        var reply = new ReplyRequest { BodyHtml = "<p>We are on it.</p>", BasedOnSeq = first.LastThreadSeq, ClientRequestId = replyKey };
        var r1 = await ReadOk<ThreadDto>(await agent.PostAsJsonAsync($"/api/v1/tickets/{first.Id}/replies", reply, J));
        var r2 = await ReadOk<ThreadDto>(await agent.PostAsJsonAsync($"/api/v1/tickets/{first.Id}/replies", reply, J));
        Assert.Equal(r1.Id, r2.Id);

        // Queued offline while someone else replied: the replay collides (409 with the new entries) …
        await ReadOk<ThreadDto>(await admin.PostAsJsonAsync($"/api/v1/tickets/{first.Id}/replies", new ReplyRequest { BodyHtml = "<p>Admin update</p>" }, J));
        var queuedKey = "mobile:" + Guid.NewGuid().ToString("N");
        var queued = new ReplyRequest { BodyHtml = "<p>Written offline</p>", BasedOnSeq = r1.Seq, ClientRequestId = queuedKey };
        var collision = await agent.PostAsJsonAsync($"/api/v1/tickets/{first.Id}/replies", queued, J);
        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);
        var problem = await collision.Content.ReadAsStringAsync();
        Assert.Contains("reply_collision", problem);
        Assert.Contains("Admin update", problem);

        // … "Send anyway" forces it with the same key, and a retry of that send is still a single entry.
        var forced = queued with { Force = true };
        var f1 = await ReadOk<ThreadDto>(await agent.PostAsJsonAsync($"/api/v1/tickets/{first.Id}/replies", forced, J));
        var f2 = await ReadOk<ThreadDto>(await agent.PostAsJsonAsync($"/api/v1/tickets/{first.Id}/replies", forced, J));
        Assert.Equal(f1.Id, f2.Id);
        // A replay of an already-stored key is answered with the stored entry even without Force (no false collision).
        Assert.Equal(f1.Id, (await ReadOk<ThreadDto>(await agent.PostAsJsonAsync($"/api/v1/tickets/{first.Id}/replies", queued, J))).Id);

        // Internal notes are idempotent too.
        var note = new NoteRequest { BodyHtml = "<p>Called the branch.</p>", ClientRequestId = "mobile:" + Guid.NewGuid().ToString("N") };
        var n1 = await ReadOk<ThreadDto>(await agent.PostAsJsonAsync($"/api/v1/tickets/{first.Id}/notes", note, J));
        var n2 = await ReadOk<ThreadDto>(await agent.PostAsJsonAsync($"/api/v1/tickets/{first.Id}/notes", note, J));
        Assert.Equal(n1.Id, n2.Id);

        var bodies = await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().TicketThreads.AsNoTracking()
            .Where(x => x.TicketId == first.Id).Select(x => x.BodyText).ToListAsync());
        Assert.Single(bodies, b => b!.Contains("We are on it"));
        Assert.Single(bodies, b => b!.Contains("Written offline"));
        Assert.Single(bodies, b => b!.Contains("Called the branch"));
    }

    [Fact]
    public async Task Portal_ticket_creation_and_replies_are_idempotent_per_contact()
    {
        var t = await factory.CreateTenantAsync();
        var customer = await factory.CustomerAsync(t, "Osprey Retail");
        var (_, c1) = await factory.ContactAsync(t, customer.Id, customer.HeadOffice, PortalAccessScope.Own, "First Contact");
        var (_, c2) = await factory.ContactAsync(t, customer.Id, customer.HeadOffice, PortalAccessScope.Own, "Second Contact");
        var key = Guid.NewGuid().ToString("N");
        var body = new PortalCreateTicketRequest { Subject = "Invoice print is blank", DescriptionHtml = "<p>Since the update.</p>", ClientRequestId = key };

        var a = await ReadOk<PortalTicketCreatedDto>(await c1.PostAsJsonAsync($"/api/v1/portal/{t.Slug}/tickets", body, J));
        var again = await ReadOk<PortalTicketCreatedDto>(await c1.PostAsJsonAsync($"/api/v1/portal/{t.Slug}/tickets", body, J));
        Assert.Equal(a.Id, again.Id);
        // The key is scoped to the contact: another contact using the same key gets their own ticket.
        var other = await ReadOk<PortalTicketCreatedDto>(await c2.PostAsJsonAsync($"/api/v1/portal/{t.Slug}/tickets", body, J));
        Assert.NotEqual(a.Id, other.Id);

        var reply = new PortalReplyRequest("<p>Attached a screenshot.</p>", null, Guid.NewGuid().ToString("N"));
        var m1 = await ReadOk<PortalMessageDto>(await c1.PostAsJsonAsync($"/api/v1/portal/{t.Slug}/tickets/{a.Id}/replies", reply, J));
        var m2 = await ReadOk<PortalMessageDto>(await c1.PostAsJsonAsync($"/api/v1/portal/{t.Slug}/tickets/{a.Id}/replies", reply, J));
        Assert.Equal(m1.Id, m2.Id);
        Assert.Equal(1, await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().TicketThreads
            .CountAsync(x => x.TicketId == a.Id && x.BodyText!.Contains("Attached a screenshot"))));
    }

    // ───── Push dispatch ─────

    [Fact]
    public async Task Agent_push_follows_preferences_and_reaches_every_active_device()
    {
        var t = await factory.CreateTenantAsync();
        var (u1, e1) = await t.CreateAgentAsync("Pushy");
        var (u2, e2) = await t.CreateAgentAsync("Quiet");
        var phone = "fcm-phone-" + Guid.NewGuid().ToString("N");
        var tablet = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"); // 64 hex chars: an APNs token
        var quiet = "fcm-quiet-" + Guid.NewGuid().ToString("N");
        var c1 = await t.AgentClientAsync(e1);
        await Register(c1, "/api/v1/me/devices", new RegisterDeviceRequest("Android", phone));
        await Register(c1, "/api/v1/me/devices", new RegisterDeviceRequest("iOS", tablet));
        await Register(await t.AgentClientAsync(e2), "/api/v1/me/devices", new RegisterDeviceRequest("Android", quiet));

        // Quiet turned push off for SLA warnings; Pushy keeps the default (push on, e-mail off).
        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            db.NotificationPreferences.Add(new NotificationPreference { UserId = u2, EventType = NotificationTypes.SlaWarning, InApp = true, Push = false });
            await db.SaveChangesAsync();
        });
        var created = await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<NotificationService>().NotifyAsync(
            new NotifyRequest([u1, u2], NotificationTypes.SlaWarning, "#42 first response due in 10 min", "Printer offline", $"/tickets/{Guid.NewGuid()}",
                DedupKey: Guid.NewGuid().ToString("N"))));
        Assert.Equal(2, created);

        var channels = await factory.InTenantAsync(t.Id, sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            return (from d in db.NotificationDeliveries
                    join n in db.Notifications on d.NotificationId equals n.Id
                    select new { n.UserId, d.Channel }).ToListAsync();
        });
        Assert.Contains(channels, c => c.UserId == u1 && c.Channel == NotificationChannel.Push);
        Assert.DoesNotContain(channels, c => c.UserId == u2 && c.Channel == NotificationChannel.Push);

        // Deliver with a fake provider: the tablet's token turns out to be dead (app uninstalled).
        var sender = new RecordingPushSender { DeadTokens = { tablet } };
        await factory.InTenantAsync(t.Id, async sp =>
        {
            var fanout = ActivatorUtilities.CreateInstance<PushFanout>(sp, (IPushSender)sender);
            await ActivatorUtilities.CreateInstance<NotificationDeliveryProcessor>(sp, fanout).ProcessDueAsync(default);
        });
        Assert.Equal(2, sender.Sent.Count);
        Assert.Contains(sender.Sent, s => s.Token == phone && s.Platform == DevicePlatform.Android && s.Data["type"] == NotificationTypes.SlaWarning);
        Assert.Contains(sender.Sent, s => s.Token == tablet && s.Platform == DevicePlatform.iOS);
        Assert.DoesNotContain(sender.Sent, s => s.Token == quiet);

        var state = await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var push = await (from d in db.NotificationDeliveries
                              join n in db.Notifications on d.NotificationId equals n.Id
                              where n.UserId == u1 && d.Channel == NotificationChannel.Push
                              select d.Status).SingleAsync();
            var devices = await db.PushDevices.AsNoTracking().Where(d => d.UserId == u1).ToDictionaryAsync(d => d.Token, d => d.IsActive);
            return (push, devices);
        });
        Assert.Equal(DeliveryStatus.Succeeded, state.push);
        Assert.True(state.devices[phone]);
        Assert.False(state.devices[tablet]);
    }

    [Fact]
    public async Task Customers_get_push_for_public_replies_and_resolution_only()
    {
        var t = await factory.CreateTenantAsync();
        var customer = await factory.CustomerAsync(t, "Heron Logistics");
        var (_, contact) = await factory.ContactAsync(t, customer.Id, customer.HeadOffice, PortalAccessScope.Own, "Mobile Customer");
        var (_, bystander) = await factory.ContactAsync(t, customer.Id, customer.HeadOffice, PortalAccessScope.Own, "Bystander");
        var token = "fcm-cust-" + Guid.NewGuid().ToString("N");
        var bystanderToken = "fcm-bys-" + Guid.NewGuid().ToString("N");
        await Register(contact, $"/api/v1/portal/{t.Slug}/devices", new RegisterDeviceRequest("Android", token));
        await Register(bystander, $"/api/v1/portal/{t.Slug}/devices", new RegisterDeviceRequest("Android", bystanderToken));
        var ticket = await contact.CreatePortalTicketAsync(t.Slug, "Scanner keeps disconnecting");

        var admin = await t.AdminClientAsync();
        await ReadOk<ThreadDto>(await admin.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/notes", new NoteRequest { BodyHtml = "<p>Internal: check driver</p>" }, J));
        await ReadOk<ThreadDto>(await admin.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/replies", new ReplyRequest { BodyHtml = "<p>Please update the driver.</p>" }, J));
        await ReadOk<TicketDetailDto>(await admin.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/resolve", new ResolveTicketRequest("fixed", null, "<p>Driver updated.</p>"), J));

        var sender = new RecordingPushSender();
        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var events = await db.OutboxEvents.AsNoTracking().Where(e => e.TenantId == t.Id && e.AggregateId == ticket.Id).OrderBy(e => e.OccurredAt).ToListAsync();
            var handler = ActivatorUtilities.CreateInstance<ContactPushHandler>(sp, ActivatorUtilities.CreateInstance<PushFanout>(sp, (IPushSender)sender));
            Assert.Contains(events, e => e.Type == EventTypes.TicketNoteAdded);
            foreach (var ev in events.Where(e => handler.CanHandle(e.Type))) await handler.HandleAsync(ev, default);
        });

        // One push for the public reply, one for the resolution — none for the note, the customer's own message or other contacts.
        Assert.Equal(2, sender.Sent.Count);
        Assert.All(sender.Sent, s => Assert.Equal(token, s.Token));
        Assert.Contains(sender.Sent, s => s.Data["type"] == "ticket.reply" && s.Body.Contains("update the driver") && s.Data["link"] == $"/customer/tickets/{ticket.Id}");
        Assert.Contains(sender.Sent, s => s.Data["type"] == "ticket.resolved" && s.Title.Contains("resolved"));
        Assert.DoesNotContain(sender.Sent, s => s.Body.Contains("Internal"));
    }

    // ───── Helpers ─────

    private static async Task<PushDeviceDto> Register(HttpClient client, string url, RegisterDeviceRequest request) =>
        await ReadOk<PushDeviceDto>(await client.PostAsJsonAsync(url, request, J));

    private static async Task<T> ReadOk<T>(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(J))!;
    }

    /// <summary>Fake push provider: records every send; tokens in <see cref="DeadTokens"/> are rejected as unregistered.</summary>
    private sealed class RecordingPushSender : IPushSender
    {
        public sealed record PushCall(DevicePlatform? Platform, string Token, string Title, string Body, IReadOnlyDictionary<string, string> Data);

        public ConcurrentQueue<PushCall> SentQueue { get; } = new();
        public IReadOnlyCollection<PushCall> Sent => SentQueue;
        public HashSet<string> DeadTokens { get; } = [];
        public bool IsConfigured => true;

        public Task<SendResult> SendAsync(string deviceToken, string title, string body, IReadOnlyDictionary<string, string> data, CancellationToken ct) =>
            Record(null, deviceToken, title, body, data);

        public Task<SendResult> SendAsync(DevicePlatform platform, string deviceToken, string title, string body, IReadOnlyDictionary<string, string> data,
            CancellationToken ct) => Record(platform, deviceToken, title, body, data);

        private Task<SendResult> Record(DevicePlatform? platform, string token, string title, string body, IReadOnlyDictionary<string, string> data)
        {
            SentQueue.Enqueue(new PushCall(platform, token, title, body, data));
            return Task.FromResult(DeadTokens.Contains(token) ? SendResult.Dead("Unregistered") : SendResult.Ok("fake"));
        }
    }
}
