using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Seed;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Communication;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Communication;

/// <summary>
/// Omnichannel demo data for tenant "wings": support@ and billing@ file-drop mailboxes, a WhatsApp number and an SMS
/// sender in simulator mode, the brand's chat widget (key wk_wingsdemo, used by /widget/demo.html), a voice line, and
/// realistic conversations created through the real pipeline (e-mail thread with reply threading, WhatsApp exchange,
/// missed call and an answered call with recording), plus channel history for seeded tickets.
/// </summary>
public sealed class CommunicationDemoSeed(IConfiguration config, IHostEnvironment env, ILogger<CommunicationDemoSeed> logger) : IDemoSeedContributor
{
    public const string WidgetKey = "wk_wingsdemo";
    public int Order => 20;

    public async Task SeedAsync(DemoSeedContext c, CancellationToken ct)
    {
        var accounts = new Dictionary<string, Guid>();
        c.At(c.Anchor.AddDays(-60));
        await c.AsSystemAsync(async sp =>
        {
            var svc = sp.GetRequiredService<ChannelAccountService>();
            async Task Add(string key, CreateChannelAccountRequest r) => accounts[key] = (await svc.CreateAsync(r, ct)).Id;
            await Add("support", new CreateChannelAccountRequest
            {
                Type = "Email", Provider = ChannelProviders.FileDrop, Name = "Support mailbox", Address = "support@wings.demo", DisplayName = "Wings Software Support",
                BrandId = c.BrandId, DepartmentId = c.Departments["support"], Settings = Json.Element("""{"pollIntervalSeconds":30,"isDefault":true}"""),
                SignatureHtml = "<p>—<br>Wings Software Support · +91 80 4000 1000 · <a href=\"https://support.wings.demo\">support.wings.demo</a></p>"
            });
            await Add("billing", new CreateChannelAccountRequest
            {
                Type = "Email", Provider = ChannelProviders.FileDrop, Name = "Billing mailbox", Address = "billing@wings.demo", DisplayName = "Wings Software Billing",
                BrandId = c.BrandId, DepartmentId = c.Departments["billing"], Settings = Json.Element("""{"pollIntervalSeconds":60}"""),
                SignatureHtml = "<p>—<br>Wings Software Accounts Receivable</p>"
            });
            await Add("whatsapp", new CreateChannelAccountRequest
            {
                Type = "WhatsApp", Provider = ChannelProviders.Simulator, Name = "WhatsApp support (simulator)", Address = "+91 80 4000 1234", DisplayName = "Wings Support",
                BrandId = c.BrandId, DepartmentId = c.Departments["support"]
            });
            await Add("sms", new CreateChannelAccountRequest
            {
                Type = "Sms", Provider = ChannelProviders.Simulator, Name = "SMS sender (simulator)", Address = "+91 80 4000 5678", BrandId = c.BrandId,
                DepartmentId = c.Departments["support"]
            });
            await Add("chat", new CreateChannelAccountRequest
            {
                Type = "Chat", Provider = ChannelProviders.Widget, Name = "Website chat", Address = WidgetKey, BrandId = c.BrandId, DepartmentId = c.Departments["support"],
                Settings = Json.Element("""{"botEnabled":true,"title":"Chat with Wings Support","greeting":"Hi! I'm the Wings assistant. Ask me anything about Wings ERP, Books or POS.","verifyIdentity":true,"offlineMessage":"Our team is offline right now. We've logged your question and will e-mail you."}""")
            });
            await Add("voice", new CreateChannelAccountRequest
            {
                Type = "Voice", Provider = ChannelProviders.GenericVoice, Name = "Support hotline", Address = "+91 80 4000 1000", BrandId = c.BrandId,
                DepartmentId = c.Departments["support"], Secrets = new() { ["webhookSecret"] = TextNormalization.RandomToken(24) },
                Settings = Json.Element("""{"createTicketsForAnsweredCalls":true}""")
            });

            // Historical seeded replies were "delivered" long ago; CSAT surveys of already resolved tickets were sent then too.
            var db = sp.GetRequiredService<DeskDbContext>();
            await db.TicketThreads.Where(t => t.DeliveryStatus == ThreadDeliveryStatus.Queued).ExecuteUpdateAsync(u => u.SetProperty(t => t.DeliveryStatus, ThreadDeliveryStatus.Sent), ct);
            var resolved = await db.Tickets.AsNoTracking().Where(t => t.StatusCategory >= StatusCategory.Resolved && t.ContactId != null && !db.SatisfactionResponses.Any(r => r.TicketId == t.Id))
                .Select(t => new { t.Id, t.ContactId, t.AssigneeId, t.ResolvedAt, t.Channel }).ToListAsync(ct);
            foreach (var t in resolved)
            {
                var sent = t.ResolvedAt ?? c.Anchor.AddDays(-30);
                db.SatisfactionResponses.Add(new SatisfactionResponse
                {
                    TicketId = t.Id, ContactId = t.ContactId, AgentUserId = t.AssigneeId, TokenHash = TokenHasher.Hash(TextNormalization.RandomToken(24)), Channel = t.Channel,
                    SentAt = sent, ExpiresAt = sent.AddDays(14)
                });
            }
            await db.SaveChangesAsync(ct);
        });

        await LinkSeededTicketsAsync(c, accounts, ct);
        await Try("e-mail thread", () => EmailThreadAsync(c, accounts["support"], ct));
        await Try("WhatsApp exchange", () => WhatsAppExchangeAsync(c, accounts["whatsapp"], ct));
        await Try("calls", () => CallsAsync(c, accounts["voice"], ct));
        await Try("maildrop sample", () => DropSampleMailAsync(c, ct));
    }

    /// <summary>Gives a few seeded tickets their channel history (inbound/outbound messages and conversations).</summary>
    private async Task LinkSeededTicketsAsync(DemoSeedContext c, Dictionary<string, Guid> accounts, CancellationToken ct)
    {
        await c.AsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var domain = (await sp.GetRequiredService<TenantSettingsService>().EmailAsync(ct)).MessageIdDomain;
            foreach (var (story, channel, accountKey) in new[] { ("gst", Channel.Email, "support"), ("export", Channel.Email, "support"), ("pharmacy", Channel.WhatsApp, "whatsapp") })
            {
                if (!c.Tickets.TryGetValue(story, out var ticketId)) continue;
                var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
                if (ticket is null) continue;
                var contact = ticket.ContactId is { } cid ? await db.Contacts.AsNoTracking().FirstAsync(x => x.Id == cid, ct) : null;
                var accountId = accounts[accountKey];
                var phone = OutboundService.RecipientFor(ChannelType.WhatsApp, contact);
                var key = channel == Channel.WhatsApp ? phone ?? $"wa:{ticket.Id:N}" : $"seed-{ticket.Id:N}@mail.{contact?.Email?.Split('@')[^1] ?? "example.com"}";
                var threads = await db.TicketThreads.Where(t => t.TicketId == ticket.Id && t.Visibility == Visibility.Public && t.Kind != ThreadKind.ChatTranscript)
                    .OrderBy(t => t.Seq).ToListAsync(ct);
                if (threads.Count == 0) continue;
                var conv = new Conversation
                {
                    ChannelAccountId = accountId, Channel = channel, ExternalKey = key, ContactId = contact?.Id, TicketId = ticket.Id, Status = ConversationStatus.Open,
                    Subject = ticket.Subject, StartedAt = threads[0].CreatedAt, LastMessageAt = threads[^1].CreatedAt
                };
                db.Conversations.Add(conv);
                ticket.ChannelAccountId = accountId;
                string? previous = null;
                var refs = new List<string>();
                foreach (var th in threads)
                {
                    var inbound = th.Direction == MessageDirection.Inbound;
                    var msg = new Message
                    {
                        ChannelAccountId = accountId, ConversationId = conv.Id, TicketId = ticket.Id, ThreadId = th.Id, Channel = channel,
                        Direction = inbound ? MessageDirection.Inbound : MessageDirection.Outbound, FromAddress = inbound ? th.FromAddress ?? (channel == Channel.WhatsApp ? phone : contact?.Email) : null,
                        FromName = th.AuthorName, Subject = channel == Channel.Email ? (inbound ? ticket.Subject : $"Re: {ticket.Subject} [#{ticket.Number}]") : null,
                        BodyText = th.BodyText, BodyHtml = th.BodyHtml, ContentType = "text", ContactId = inbound ? contact?.Id : null, AuthorUserId = th.AuthorUserId,
                        Status = inbound ? MessageStatus.Processed : MessageStatus.Delivered, OccurredAt = th.CreatedAt, CreatedAt = th.CreatedAt,
                        MetadataJson = Json.Serialize(new { kind = inbound ? null : "reply", seeded = true })
                    };
                    if (channel == Channel.Email)
                    {
                        var id = inbound && previous is null ? key : EmailThreading.NewMessageId(msg.Id, inbound ? $"mail.{contact?.Email?.Split('@')[^1] ?? "example.com"}" : domain);
                        msg.ExternalId = id;
                        msg.InternetMessageId = id;
                        msg.InReplyTo = previous;
                        msg.ReferencesJson = refs.Count > 0 ? Json.Serialize(refs.ToList()) : null;
                        msg.ToJson = Json.Serialize(new[] { inbound ? "support@wings.demo" : contact?.Email ?? "" });
                        previous = id;
                        refs.Add(id);
                    }
                    else
                    {
                        msg.ExternalId = inbound ? $"wamid.seed.{msg.Id:N}" : $"wamid.out.{msg.Id:N}";
                        msg.ToJson = Json.Serialize(new[] { inbound ? "+918040001234" : phone ?? "" });
                        if (inbound) conv.ServiceWindowEndsAt = th.CreatedAt.AddHours(24);
                    }
                    db.Messages.Add(msg);
                    th.MessageId = msg.Id;
                    th.ConversationId = conv.Id;
                    if (!inbound)
                    {
                        th.DeliveryStatus = ThreadDeliveryStatus.Delivered;
                        db.MessageDeliveries.Add(new MessageDelivery
                        {
                            MessageId = msg.Id, Recipient = channel == Channel.Email ? contact?.Email ?? "" : phone ?? "", Status = MessageStatus.Delivered, Attempts = 1,
                            NextAttemptAt = th.CreatedAt, LastAttemptAt = th.CreatedAt, ProviderMessageId = msg.ExternalId, UpdatedAt = th.CreatedAt.AddMinutes(1),
                            StatusHistoryJson = Json.Serialize(new[]
                            {
                                new DeliveryHistory.Entry("Queued", th.CreatedAt, null), new DeliveryHistory.Entry("Sent", th.CreatedAt.AddSeconds(4), null),
                                new DeliveryHistory.Entry("Delivered", th.CreatedAt.AddMinutes(1), null)
                            })
                        });
                    }
                }
                await db.SaveChangesAsync(ct);
            }
        });
    }

    /// <summary>A new e-mail ticket through the real pipeline, an agent answer and a customer reply threaded by In-Reply-To.</summary>
    private async Task EmailThreadAsync(DemoSeedContext c, Guid supportId, CancellationToken ct)
    {
        Guid ticketId = default;
        string? originalId = null;
        c.At(c.Anchor.AddHours(-26));
        await c.AsSystemAsync(async sp =>
        {
            var sim = sp.GetRequiredService<ChannelSimulator>();
            originalId = $"CAF{Guid.NewGuid():N}@mail.sunrisepharma.demo";
            var r = await sim.EmailAsync(supportId, new SimulateEmailRequest
            {
                From = "anita@sunrisepharma.demo", FromName = "Anita Desai", Subject = "Batch expiry report shows wrong dates after upgrade", MessageId = originalId,
                Html = "<p>Hello team,</p><p>Since the 8.0 upgrade the <b>Batch Expiry Report</b> shows expiry dates one month earlier than the batch master for about 40 SKUs " +
                       "(example: batch AMX-2231, master 30-Nov, report 30-Oct). We use this report for FEFO picking, so this is urgent.</p><p>Regards,<br>Anita Desai<br>IT Manager, Sunrise Pharma</p>"
            }, ct);
            ticketId = r.TicketId ?? default;
        });
        if (ticketId == default) return;
        c.At(c.Anchor.AddHours(-25));
        await c.AsAgentAsync("meera", sp => sp.GetRequiredService<ConversationService>().ReplyAsync(ticketId, new ReplyRequest
        {
            BodyHtml = "<p>Hi Anita,</p><p>Thanks for the example. The 8.0 report reads the date format from the regional settings; please run <i>Tools → Repair date formats</i> " +
                       "and re-open the report. If dates are still off, reply with a screenshot of the batch master.</p>", Force = true
        }, ct));
        c.At(c.Anchor.AddHours(-3));
        await c.AsSystemAsync(async sp =>
        {
            await sp.GetRequiredService<ChannelSimulator>().EmailAsync(supportId, new SimulateEmailRequest
            {
                From = "anita@sunrisepharma.demo", FromName = "Anita Desai", Subject = "RE: Batch expiry report shows wrong dates after upgrade", InReplyTo = originalId,
                References = [originalId!],
                Html = "<p>Hi Meera,</p><p>The repair fixed 36 of the 40 SKUs. Four batches imported via the API still show the old dates — screenshot attached.</p>" +
                       "<div class=\"gmail_quote\"><div>On Mon, Meera Iyer wrote:</div><blockquote>Hi Anita, thanks for the example…</blockquote></div>",
                Attachments = [new SimulatedAttachment("batch-master.png", "image/png", Convert.ToBase64String(TinyPng))]
            }, ct);
        });
    }

    private async Task WhatsAppExchangeAsync(DemoSeedContext c, Guid whatsappId, CancellationToken ct)
    {
        Guid ticketId = default;
        c.At(c.Anchor.AddHours(-5));
        await c.AsSystemAsync(async sp =>
        {
            var r = await sp.GetRequiredService<ChannelSimulator>().MessageAsync(whatsappId, new SimulateMessageRequest
            {
                From = "+91 98 1001 0003", FromName = "Joseph Thomas",
                Text = "Hi, the POS at our T. Nagar store is not printing GST invoices since morning. It shows 'Printer offline' but the printer test page works."
            }, ct);
            ticketId = r.TicketId ?? default;
        });
        if (ticketId == default) return;
        c.At(c.Anchor.AddHours(-4.8));
        await c.AsAgentAsync("arjun", sp => sp.GetRequiredService<ConversationService>().ReplyAsync(ticketId, new ReplyRequest
        {
            BodyHtml = "<p>Hi Joseph, please open POS Settings → Devices and re-select the printer (the Windows update renamed it to 'EPSON TM-T82 (Copy 1)').</p>", Force = true,
            Channel = "WhatsApp"
        }, ct));
        c.At(c.Anchor.AddHours(-4.5));
        await c.AsSystemAsync(sp => sp.GetRequiredService<ChannelSimulator>().MessageAsync(whatsappId, new SimulateMessageRequest
        {
            From = "+91 98 1001 0003", FromName = "Joseph Thomas", Text = "That worked, printing again. Thanks Arjun! 👍"
        }, ct));
    }

    private async Task CallsAsync(DemoSeedContext c, Guid voiceId, CancellationToken ct)
    {
        c.At(c.Anchor.AddHours(-2));
        await c.AsSystemAsync(async sp =>
        {
            var sim = sp.GetRequiredService<ChannelSimulator>();
            var missed = $"demo-call-{Guid.NewGuid():N}";
            await sim.CallAsync(voiceId, new SimulateCallRequest { Event = "started", CallId = missed, From = "+91 98 1002 0002" }, ct);
            await sim.CallAsync(voiceId, new SimulateCallRequest { Event = "voicemail", CallId = missed, From = "+91 98 1002 0002",
                RecordingUrl = "https://recordings.wings.demo/vm/2026/suresh-warehouse.mp3", Notes = "Caller asked about stock transfer posting errors." }, ct);
        });
        c.At(c.Anchor.AddHours(-1));
        await c.AsSystemAsync(async sp =>
        {
            var sim = sp.GetRequiredService<ChannelSimulator>();
            var answered = $"demo-call-{Guid.NewGuid():N}";
            await sim.CallAsync(voiceId, new SimulateCallRequest { Event = "started", CallId = answered, From = "+91 98 1003 0001" }, ct);
            await sim.CallAsync(voiceId, new SimulateCallRequest { Event = "answered", CallId = answered, From = "+91 98 1003 0001", AgentEmail = "arjun@wings.demo" }, ct);
            await sim.CallAsync(voiceId, new SimulateCallRequest { Event = "ended", CallId = answered, From = "+91 98 1003 0001", AgentEmail = "arjun@wings.demo", DurationSeconds = 312,
                RecordingUrl = "https://recordings.wings.demo/calls/2026/harpreet-eway.mp3", Disposition = "Resolved on call", Notes = "Explained e-way bill cancellation within 24h." }, ct);
            var missedAgain = $"demo-call-{Guid.NewGuid():N}";
            await sim.CallAsync(voiceId, new SimulateCallRequest { Event = "missed", CallId = missedAgain, From = "+91 98 1006 0001" }, ct);
        });
    }

    /// <summary>Leaves one unread e-mail in the support@ file-drop inbox so the first mailbox poll creates a ticket live.</summary>
    private async Task DropSampleMailAsync(DemoSeedContext c, CancellationToken ct)
    {
        await c.AsSystemAsync(sp =>
        {
            var mime = sp.GetRequiredService<IMimeCodec>();
            var raw = mime.Compose(new OutboundEmail("ritu@blueskyedu.demo", "Ritu Banerjee", ["support@wings.demo"], [], [], null,
                "Fee receipt numbering restarted from 1", "<p>Hello,</p><p>Our fee receipt numbers restarted from 1 after the financial-year rollover. " +
                "Can you help us continue the old series?</p><p>Thanks,<br>Ritu</p>", "Hello, our fee receipt numbers restarted from 1 after the financial-year rollover.",
                $"{Guid.NewGuid():N}@mail.blueskyedu.demo", null, [], [], new Dictionary<string, string>()));
            var inbox = Path.Combine(DataPaths.Root(config, env), "maildrop", "support@wings.demo", "inbox");
            Directory.CreateDirectory(inbox);
            File.WriteAllBytes(Path.Combine(inbox, $"{DateTime.UtcNow:yyyyMMddHHmmss}-welcome.eml"), raw);
            return Task.CompletedTask;
        });
        await Task.CompletedTask;
        _ = ct;
    }

    private async Task Try(string step, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is DomainException or IOException or InvalidOperationException or ChannelConfigurationException)
        {
            logger.LogWarning(ex, "Omnichannel demo step '{Step}' skipped", step);
        }
    }

    /// <summary>1×1 transparent PNG used as a demo attachment.</summary>
    private static readonly byte[] TinyPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
}
