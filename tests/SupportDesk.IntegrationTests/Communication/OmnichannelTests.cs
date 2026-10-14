using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using SupportDesk.Application.Communication;
using SupportDesk.Contracts.Communication;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Tickets;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.Persistence;

namespace SupportDesk.IntegrationTests.Communication;

[Collection("api")]
public sealed class OmnichannelTests(DeskApiFactory factory)
{
    private static readonly JsonSerializerOptions J = DeskApiFactory.Json;

    // ───────────────────────────── helpers ─────────────────────────────

    private static async Task<ChannelAccountDto> CreateChannelAsync(HttpClient admin, CreateChannelAccountRequest request)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/channels", request, J);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ChannelAccountDto>(J))!;
    }

    private static Task<ChannelAccountDto> MailboxAsync(HttpClient admin, TestTenant t, string local = "support") => CreateChannelAsync(admin, new CreateChannelAccountRequest
    {
        Type = "Email", Provider = ChannelProviders.FileDrop, Name = $"{local} mailbox", Address = $"{local}@{t.Slug}.test",
        Settings = JsonSerializer.SerializeToElement(new { pollIntervalSeconds = 0 })
    });

    private static async Task<InboundResultDto> SimulateEmailAsync(HttpClient admin, Guid channelId, SimulateEmailRequest request)
    {
        var response = await admin.PostAsJsonAsync($"/api/v1/channels/{channelId}/simulator/email", request, J);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<InboundResultDto>(J))!;
    }

    private string MaildropFolder(string address, string folder) =>
        Path.Combine(factory.Services.GetRequiredService<IConfiguration>()["Storage:DataPath"]!, "maildrop", address, folder);

    private static string Eml(string from, string to, string subject, string messageId, string body, string? inReplyTo = null) =>
        $"From: Customer <{from}>\r\nTo: {to}\r\nSubject: {subject}\r\nMessage-ID: <{messageId}>\r\nDate: {DateTime.UtcNow:R}\r\n" +
        (inReplyTo is null ? "" : $"In-Reply-To: <{inReplyTo}>\r\nReferences: <{inReplyTo}>\r\n") +
        $"MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n{body}\r\n";

    private Task<T> Db<T>(TestTenant t, Func<DeskDbContext, Task<T>> query) =>
        factory.InTenantAsync(t.Id, sp => query(sp.GetRequiredService<DeskDbContext>()));

    private static async Task<TicketDetailDto> TicketAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<TicketDetailDto>($"/api/v1/tickets/{id}", J))!;

    private static async Task ReplyAsync(HttpClient client, Guid ticketId, string html)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/tickets/{ticketId}/replies", new ReplyRequest { BodyHtml = html, Force = true }, J);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    // ───────────────────────────── tests ─────────────────────────────

    [Fact]
    public async Task Duplicate_inbound_email_creates_exactly_one_ticket_and_one_thread()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var mailbox = await MailboxAsync(admin, t);
        var messageId = $"dup-{Guid.NewGuid():N}@customer.example";
        var subject = $"Duplicate delivery {Guid.NewGuid():N}";
        var eml = Eml("buyer@customer.example", mailbox.Address, subject, messageId, "The invoice PDF is blank.");

        var inbox = MaildropFolder(mailbox.Address, "inbox");
        Directory.CreateDirectory(inbox);
        await File.WriteAllTextAsync(Path.Combine(inbox, "first.eml"), eml);
        await factory.RunRecurringAsync("mailbox-poller");
        // The provider delivers the same message again (e.g. a retry after a timeout) — through the poller and the simulator.
        await File.WriteAllTextAsync(Path.Combine(inbox, "second-copy.eml"), eml);
        await factory.RunRecurringAsync("mailbox-poller");
        var again = await SimulateEmailAsync(admin, mailbox.Id, new SimulateEmailRequest
        {
            From = "buyer@customer.example", Subject = subject, MessageId = messageId, Text = "The invoice PDF is blank."
        });
        Assert.Equal(InboundOutcomes.Duplicate, again.Outcome);

        var (tickets, threads, messages) = await Db(t, async db =>
        {
            var ids = await db.Tickets.Where(x => x.Subject == subject).Select(x => x.Id).ToListAsync();
            var threadCount = await db.TicketThreads.CountAsync(x => ids.Contains(x.TicketId) && x.Kind == ThreadKind.CustomerMessage);
            var messageCount = await db.Messages.CountAsync(m => m.Direction == MessageDirection.Inbound && m.ExternalId == messageId);
            return (ids.Count, threadCount, messageCount);
        });
        Assert.Equal(1, tickets);
        Assert.Equal(1, threads);
        Assert.Equal(1, messages);
        Assert.Empty(Directory.GetFiles(inbox));
        Assert.Equal(2, Directory.GetFiles(MaildropFolder(mailbox.Address, "processed")).Length);
    }

    [Fact]
    public async Task Reply_with_in_reply_to_threads_into_the_ticket_and_reopens_it_when_resolved()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var mailbox = await MailboxAsync(admin, t);
        var rootId = $"root-{Guid.NewGuid():N}@customer.example";
        var created = await SimulateEmailAsync(admin, mailbox.Id, new SimulateEmailRequest
        {
            From = "kavya@customer.example", FromName = "Kavya Reddy", Subject = "Payroll PF calculation wrong", MessageId = rootId, Text = "PF is computed on gross."
        });
        Assert.Equal(InboundOutcomes.Created, created.Outcome);
        var ticketId = created.TicketId!.Value;

        var resolve = await admin.PostAsJsonAsync($"/api/v1/tickets/{ticketId}/resolve", new ResolveTicketRequest("fixed", "configuration", "<p>Rule fixed</p>"), J);
        Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);
        Assert.Equal("Resolved", (await TicketAsync(admin, ticketId)).StatusCategory);

        var reply = await SimulateEmailAsync(admin, mailbox.Id, new SimulateEmailRequest
        {
            From = "kavya@customer.example", FromName = "Kavya Reddy", Subject = "RE: something unrelated", InReplyTo = rootId, References = [rootId],
            Html = "<div>It is still wrong for 3 employees.</div><div class=\"gmail_quote\"><div>On Mon, Support wrote:</div><blockquote>Rule fixed</blockquote></div>"
        });

        Assert.Equal(InboundOutcomes.Threaded, reply.Outcome);
        Assert.Equal(ticketId, reply.TicketId);
        var ticket = await TicketAsync(admin, ticketId);
        Assert.NotEqual("Resolved", ticket.StatusCategory);
        Assert.Equal(1, ticket.ReopenCount);
        var timeline = (await admin.GetFromJsonAsync<List<TimelineItemDto>>($"/api/v1/tickets/{ticketId}/timeline", J))!;
        var customerThreads = timeline.Where(i => i.Thread?.Kind == "CustomerMessage").Select(i => i.Thread!).ToList();
        Assert.Equal(2, customerThreads.Count);
        Assert.Contains("still wrong", customerThreads[^1].BodyText);
        Assert.DoesNotContain("Rule fixed", customerThreads[^1].BodyText); // quoted history stripped (kept in the raw .eml)
        Assert.Equal(1, await Db(t, db => db.Tickets.CountAsync(x => x.Subject == "Payroll PF calculation wrong")));
    }

    [Fact]
    public async Task Agent_reply_is_sent_by_file_drop_with_correct_threading_headers()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var mailbox = await MailboxAsync(admin, t);
        var rootId = $"root-{Guid.NewGuid():N}@customer.example";
        var created = await SimulateEmailAsync(admin, mailbox.Id, new SimulateEmailRequest
        {
            From = "nikhil@customer.example", FromName = "Nikhil Joshi", Subject = "E-way bill error 2172", MessageId = rootId, Text = "Error 2172 on every e-way bill."
        });
        var ticketId = created.TicketId!.Value;
        await factory.DrainOutboxAsync();
        await factory.DrainJobsAsync(); // sends the automatic acknowledgement

        await ReplyAsync(admin, ticketId, "<p>Please check the ship-to state code.</p>");
        await factory.DrainOutboxAsync();
        await factory.DrainJobsAsync();

        var sent = Directory.GetFiles(MaildropFolder(mailbox.Address, "sent"), "*.eml")
            .Select(f => MimeMessage.Load(f)).ToList();
        var reply = Assert.Single(sent, m => (m.TextBody ?? "").Contains("ship-to state code"));
        var ack = Assert.Single(sent, m => m.Headers[HeaderId.AutoSubmitted] == "auto-replied");
        Assert.Equal(rootId, ack.InReplyTo);

        var number = (await TicketAsync(admin, ticketId)).Number;
        Assert.Equal(mailbox.Address, reply.From.Mailboxes.Single().Address);
        Assert.Equal("nikhil@customer.example", reply.To.Mailboxes.Single().Address);
        Assert.Equal(rootId, reply.InReplyTo);
        Assert.Contains(rootId, reply.References);
        Assert.Contains($"[#{number}]", reply.Subject);
        Assert.StartsWith("Re:", reply.Subject);
        Assert.Matches(@"^support\+t-\d+x[0-9a-f]{10}@", reply.ReplyTo.Mailboxes.Single().Address);
        Assert.EndsWith("@supportdesk.local", reply.MessageId);

        var (deliveryStatus, history, threadStatus) = await Db(t, async db =>
        {
            var msg = await db.Messages.FirstAsync(m => m.InternetMessageId == reply.MessageId);
            var delivery = await db.MessageDeliveries.FirstAsync(d => d.MessageId == msg.Id);
            var thread = await db.TicketThreads.FirstAsync(x => x.Id == msg.ThreadId);
            return (delivery.Status, delivery.StatusHistoryJson, thread.DeliveryStatus);
        });
        Assert.Equal(MessageStatus.Sent, deliveryStatus);
        Assert.Contains("Queued", history);
        Assert.Contains("Sent", history);
        Assert.Equal("Sent", threadStatus);

        // The customer answers our reply: its In-Reply-To is our Message-ID → same ticket.
        var followUp = await SimulateEmailAsync(admin, mailbox.Id, new SimulateEmailRequest
        {
            From = "nikhil@customer.example", Subject = "Re: thanks", InReplyTo = reply.MessageId, Text = "That fixed it."
        });
        Assert.Equal(InboundOutcomes.Threaded, followUp.Outcome);
        Assert.Equal(ticketId, followUp.TicketId);
    }

    [Fact]
    public async Task WhatsApp_webhook_rejects_bad_signatures_and_creates_a_ticket_for_a_valid_one()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var channel = await CreateChannelAsync(admin, new CreateChannelAccountRequest
        {
            Type = "WhatsApp", Provider = ChannelProviders.MetaCloud, Name = "WhatsApp", Address = "+91 80 1234 5678",
            Settings = JsonSerializer.SerializeToElement(new { phoneNumberId = "PNID-1", verifyToken = "vt-123" }),
            Secrets = new() { ["appSecret"] = "wa-app-secret", ["accessToken"] = "test-token" }
        });
        Assert.NotNull(channel.WebhookPath);
        Assert.Equal(new[] { "accessToken", "appSecret" }, channel.SecretKeys);
        var anonymous = factory.CreateClient();

        var handshake = await anonymous.GetAsync($"{channel.WebhookPath}?hub.mode=subscribe&hub.verify_token=vt-123&hub.challenge=987654");
        Assert.Equal(HttpStatusCode.OK, handshake.StatusCode);
        Assert.Equal("987654", await handshake.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.GetAsync($"{channel.WebhookPath}?hub.mode=subscribe&hub.verify_token=nope&hub.challenge=1")).StatusCode);

        var wamid = $"wamid.{Guid.NewGuid():N}";
        var payload = Encoding.UTF8.GetBytes($$$"""
            {"object":"whatsapp_business_account","entry":[{"id":"WABA","changes":[{"field":"messages","value":{"messaging_product":"whatsapp",
            "metadata":{"display_phone_number":"918012345678","phone_number_id":"PNID-1"},
            "contacts":[{"profile":{"name":"Rekha Nair"},"wa_id":"919810050002"}],
            "messages":[{"from":"919810050002","id":"{{{wamid}}}","timestamp":"{{{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}}}","type":"text","text":{"body":"The pharmacy POS froze again"}}]}}]}]}
            """);
        HttpRequestMessage Signed(string signature)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, channel.WebhookPath) { Content = new ByteArrayContent(payload) };
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            req.Headers.Add("X-Hub-Signature-256", signature);
            return req;
        }

        var rejected = await anonymous.SendAsync(Signed("sha256=" + WebhookSignatures.HmacSha256Hex("wrong-secret", payload)));
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Equal(0, await Db(t, db => db.Messages.CountAsync(m => m.ExternalId == wamid)));

        var good = "sha256=" + WebhookSignatures.HmacSha256Hex("wa-app-secret", payload);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.SendAsync(Signed(good))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.SendAsync(Signed(good))).StatusCode); // Meta redelivers

        var (ticketCount, ticketChannel, contactPhone, windowEnds, events) = await Db(t, async db =>
        {
            var msg = await db.Messages.SingleAsync(m => m.ExternalId == wamid);
            var tickets = await db.Tickets.Where(x => x.Id == msg.TicketId).ToListAsync();
            var contact = await db.Contacts.FirstAsync(c => c.Id == msg.ContactId);
            var conv = await db.Conversations.FirstAsync(c => c.Id == msg.ConversationId);
            var inbound = await db.InboundEvents.CountAsync(e => e.ChannelAccountId == channel.Id);
            return (tickets.Count, tickets[0].Channel, contact.PhoneNormalized, conv.ServiceWindowEndsAt, inbound);
        });
        Assert.Equal(1, ticketCount);
        Assert.Equal(Channel.WhatsApp, ticketChannel);
        Assert.Equal("919810050002", contactPhone);
        Assert.NotNull(windowEnds);
        Assert.Equal(1, events);
    }

    [Fact]
    public async Task Sms_reply_to_an_opted_out_contact_fails_with_a_clear_reason()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var sms = await CreateChannelAsync(admin, new CreateChannelAccountRequest
        {
            Type = "Sms", Provider = ChannelProviders.Simulator, Name = "SMS", Address = "+91 80 5555 0000"
        });
        var inbound = await admin.PostAsJsonAsync($"/api/v1/channels/{sms.Id}/simulator/message", new SimulateMessageRequest
        {
            From = "+91 98765 43210", FromName = "Imran Sheikh", Text = "Need a copy of invoice 2231"
        }, J);
        var result = (await inbound.Content.ReadFromJsonAsync<InboundResultDto>(J))!;
        Assert.Equal(InboundOutcomes.Created, result.Outcome);
        var ticketId = result.TicketId!.Value;

        // While opted in, replies go out through the channel.
        await ReplyAsync(admin, ticketId, "<p>Sure, sending it shortly.</p>");
        await factory.DrainOutboxAsync();
        await factory.DrainJobsAsync();

        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var contactId = (await db.Tickets.FirstAsync(x => x.Id == ticketId)).ContactId!.Value;
            db.ContactConsents.Add(new ContactChannelConsent
            {
                ContactId = contactId, Channel = Channel.Sms, Purpose = ConsentPurpose.Transactional, Status = ConsentStatus.OptedOut, Source = "STOP keyword",
                RecordedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        });
        await ReplyAsync(admin, ticketId, "<p>Here is the invoice link.</p>");
        await factory.DrainOutboxAsync();
        await factory.DrainJobsAsync();

        var outbound = (await admin.GetFromJsonAsync<List<OutboundMessageDto>>($"/api/v1/channels/{sms.Id}/outbound", J))!;
        var first = Assert.Single(outbound, m => m.BodyText!.Contains("sending it shortly"));
        Assert.Equal("Sent", first.Status);
        var blocked = Assert.Single(outbound, m => m.BodyText!.Contains("invoice link"));
        Assert.Equal("Failed", blocked.Status);
        var delivery = Assert.Single(blocked.Deliveries);
        Assert.Equal("Failed", delivery.Status);
        Assert.Contains("opted out", delivery.Error);
        Assert.Equal(0, delivery.Attempts); // never handed to the provider
        var timeline = (await admin.GetFromJsonAsync<List<TimelineItemDto>>($"/api/v1/tickets/{ticketId}/timeline", J))!;
        Assert.Equal("Failed", timeline.Single(i => i.Thread?.BodyText?.Contains("invoice link") == true).Thread!.DeliveryStatus);
    }

    [Fact]
    public async Task Csat_survey_token_flow_updates_the_ticket_and_publishes_the_event()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var mailbox = await MailboxAsync(admin, t);
        var created = await SimulateEmailAsync(admin, mailbox.Id, new SimulateEmailRequest
        {
            From = "lata@customer.example", FromName = "Lata Menon", Subject = "Loyalty points not credited", Text = "Points missing for bill 554."
        });
        var ticketId = created.TicketId!.Value;
        var resolve = await admin.PostAsJsonAsync($"/api/v1/tickets/{ticketId}/resolve", new ResolveTicketRequest("fixed", "data_issue", "<p>Points credited</p>"), J);
        Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);
        await factory.DrainOutboxAsync();
        await factory.DrainOutboxAsync(); // idempotent: a second pass sends nothing new

        var (surveys, body) = await Db(t, async db =>
        {
            var count = await db.SatisfactionResponses.CountAsync(r => r.TicketId == ticketId);
            var msg = await db.Messages.Where(m => m.TicketId == ticketId && m.Direction == MessageDirection.Outbound && m.MetadataJson!.Contains("\"kind\":\"csat\""))
                .Select(m => m.BodyHtml).SingleAsync();
            return (count, msg!);
        });
        Assert.Equal(1, surveys);
        var token = Regex.Match(body, @"/csat/([A-Za-z0-9_\-]+)").Groups[1].Value;
        Assert.NotEmpty(token);

        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/v1/public/csat/not-a-real-token")).StatusCode);
        var survey = (await anonymous.GetFromJsonAsync<CsatSurveyDto>($"/api/v1/public/csat/{token}", J))!;
        Assert.Equal("open", survey.Status);
        Assert.Equal("Loyalty points not credited", survey.Subject);
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsJsonAsync($"/api/v1/public/csat/{token}", new CsatSubmitRequest(9, null), J)).StatusCode);

        var submit = await anonymous.PostAsJsonAsync($"/api/v1/public/csat/{token}", new CsatSubmitRequest(5, "Quick and helpful"), J);
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        Assert.Equal("answered", (await submit.Content.ReadFromJsonAsync<CsatSurveyDto>(J))!.Status);
        var ticket = await TicketAsync(admin, ticketId);
        Assert.Equal(5, ticket.CsatScore);
        Assert.True(await Db(t, db => db.OutboxEvents.AnyAsync(e => e.Type == EventTypes.SatisfactionReceived && e.AggregateId == ticketId)));

        // Expired surveys can no longer be answered.
        await factory.InTenantAsync(t.Id, async sp => await sp.GetRequiredService<DeskDbContext>().SatisfactionResponses
            .Where(r => r.TicketId == ticketId).ExecuteUpdateAsync(u => u.SetProperty(r => r.ExpiresAt, DateTime.UtcNow.AddDays(-1))));
        Assert.Equal(HttpStatusCode.Gone, (await anonymous.PostAsJsonAsync($"/api/v1/public/csat/{token}", new CsatSubmitRequest(1, null), J)).StatusCode);
    }

    [Fact]
    public async Task Live_chat_hands_off_to_a_ticket_and_agent_replies_reach_the_visitor()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var widget = await CreateChannelAsync(admin, new CreateChannelAccountRequest
        {
            Type = "Chat", Provider = ChannelProviders.Widget, Name = "Website chat", Settings = JsonSerializer.SerializeToElement(new { botEnabled = true })
        });
        var visitor = factory.CreateClient();
        var start = await visitor.PostAsJsonAsync($"/api/v1/chat/{widget.Address}/sessions", new StartChatRequest { Name = "Guest", Message = "My POS will not print" }, J);
        Assert.Equal(HttpStatusCode.Created, start.StatusCode);
        var session = (await start.Content.ReadFromJsonAsync<ChatSessionDto>(J))!;
        // No assistant is configured → immediate handoff to a human with a ticket.
        Assert.Equal("WaitingForAgent", session.Status);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/conversations/{session.ConversationId}/accept", null)).StatusCode);
        var send = await admin.PostAsJsonAsync($"/api/v1/conversations/{session.ConversationId}/messages", new AgentChatMessageRequest("Please re-select the printer."), J);
        Assert.True(send.IsSuccessStatusCode, await send.Content.ReadAsStringAsync());
        await factory.DrainOutboxAsync();

        visitor.DefaultRequestHeaders.Add("X-Visitor-Token", session.VisitorToken);
        var state = (await visitor.GetFromJsonAsync<ChatStateDto>($"/api/v1/chat/{widget.Address}/sessions/{session.ConversationId}", J))!;
        Assert.Equal("AgentActive", state.Status);
        Assert.Single(state.Messages, m => m.Author == "agent" && m.Text == "Please re-select the printer.");
        Assert.NotNull(state.TicketId);

        var other = factory.CreateClient();
        other.DefaultRequestHeaders.Add("X-Visitor-Token", session.VisitorToken[..^2] + "xx");
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/v1/chat/{widget.Address}/sessions/{session.ConversationId}")).StatusCode);

        var close = await visitor.PostAsync($"/api/v1/chat/{widget.Address}/sessions/{session.ConversationId}/close", null);
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        var timeline = (await admin.GetFromJsonAsync<List<TimelineItemDto>>($"/api/v1/tickets/{state.TicketId}/timeline", J))!;
        Assert.Contains(timeline, i => i.Thread?.Kind == "ChatTranscript");
        Assert.Single(timeline, i => i.Thread?.Kind == "AgentReply" && i.Thread.DeliveryStatus == "Delivered");
    }
}
