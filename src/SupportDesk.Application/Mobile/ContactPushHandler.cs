using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Mobile;

/// <summary>
/// Mobile push for portal contacts: a public agent reply on the contact's ticket, and the ticket being resolved
/// (so they can confirm or rate it). Only the ticket's own contact is notified, only for public entries, and only on
/// devices registered from the customer app. Best effort: delivery problems are logged by the fan-out and never
/// retried (a duplicate phone alert is worse than a missed one — the portal and e-mail remain the record).
/// </summary>
public sealed class ContactPushHandler(DeskDbContext db, PushFanout fanout, TicketLookups lookups) : IOutboxHandler
{
    public string Name => "mobile-contact-push";
    public int Order => HandlerOrder.Notifications + 5;

    public bool CanHandle(string eventType) => eventType is EventTypes.TicketReplyAdded or EventTypes.TicketStatusChanged;

    public async Task HandleAsync(OutboxEvent ev, CancellationToken ct)
    {
        var p = Json.Deserialize<TicketEventPayload>(ev.PayloadJson);
        if (p is null) return;
        var t = await db.Tickets.AsNoTracking().Where(x => x.Id == p.TicketId)
            .Select(x => new { x.Id, x.Number, x.Subject, x.ContactId, x.StatusCategory, x.StatusId }).FirstOrDefaultAsync(ct);
        if (t?.ContactId is not { } contactId) return;

        string title, body, kind;
        if (ev.Type == EventTypes.TicketReplyAdded)
        {
            if (p.ThreadId is not { } threadId) return;
            var thread = await db.TicketThreads.AsNoTracking().Where(x => x.Id == threadId)
                .Select(x => new { x.Visibility, x.Kind, x.AuthorName, x.BodyText }).FirstOrDefaultAsync(ct);
            if (thread is null || thread.Visibility != Visibility.Public || thread.Kind != ThreadKind.AgentReply) return;
            await lookups.LoadAsync(ct);
            title = $"New reply on {lookups.Display(t.Number)}";
            body = TextNormalization.Truncate($"{thread.AuthorName ?? "Support"}: {thread.BodyText}", 180);
            kind = "ticket.reply";
        }
        else
        {
            // Status changes: only the move into Resolved is worth a phone alert (confirm / rate).
            var toResolved = p.Changes?.Any(c => c.Field == "status") == true && t.StatusCategory == StatusCategory.Resolved;
            if (!toResolved) return;
            await lookups.LoadAsync(ct);
            title = $"{lookups.Display(t.Number)} was resolved";
            body = TextNormalization.Truncate($"{t.Subject} — let us know if this solved it.", 180);
            kind = "ticket.resolved";
        }

        var devices = await fanout.ContactDevicesAsync(contactId, ct);
        if (devices.Count == 0) return;
        await fanout.SendAsync(devices, title, body, new Dictionary<string, string>
        {
            ["type"] = kind, ["ticketId"] = t.Id.ToString(), ["link"] = $"/customer/tickets/{t.Id}"
        }, ct);
    }
}
