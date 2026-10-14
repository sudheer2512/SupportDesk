using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Domain;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Communication;

/// <summary>
/// "Notify the customer" on Resolve: when the agent resolves a ticket with <c>notifyCustomer = true</c> the customer gets
/// a resolution notice with the resolution notes — through the ticket's channel (e-mail on the ticket's mailbox, or the
/// messaging conversation) and as a public system entry on the ticket, which the portal shows and pushes live to the
/// contact. With <c>notifyCustomer = false</c> nothing is sent (the CSAT survey is skipped too, see CsatSurveyHandler).
/// Idempotent per event: the public entry is keyed by the event id and the message is queued only once.
/// </summary>
public sealed class ResolutionNoticeHandler(
    DeskDbContext db,
    TenantSettingsService settings,
    OutboundService outbound,
    ThreadAppender threads,
    IRealtimePublisher realtime,
    ILogger<ResolutionNoticeHandler> logger) : IOutboxHandler
{
    public const string MessageKind = "resolution";

    public string Name => "resolution-notice";
    public int Order => HandlerOrder.Integrations;
    public bool CanHandle(string eventType) => eventType == EventTypes.TicketStatusChanged;

    public async Task HandleAsync(OutboxEvent ev, CancellationToken ct)
    {
        var p = Json.Deserialize<TicketEventPayload>(ev.PayloadJson);
        if (p?.NotifyCustomer != true || ev.TenantId is not { } tenantId) return;
        var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == p.TicketId, ct);
        // Reopened (or merged / spam) before the notice went out: nothing to announce any more.
        if (ticket is null || ticket.StatusCategory != StatusCategory.Resolved || ticket.IsSpam || ticket.MergedIntoTicketId is not null || ticket.ContactId is null) return;

        var key = $"resolution-notice:{ev.Id:N}";
        if (await db.TicketThreads.AnyAsync(t => t.ClientRequestId == key, ct)) return;

        var s = await settings.TicketsAsync(ct);
        var display = $"{s.NumberPrefix}{ticket.Number}";
        var contactName = await db.Contacts.AsNoTracking().Where(c => c.Id == ticket.ContactId).Select(c => c.FirstName ?? c.FullName).FirstOrDefaultAsync(ct);
        var agent = ticket.AssigneeId is { } a ? await db.Users.AsNoTracking().Where(u => u.Id == a).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) : null;
        var (html, text) = Compose(display, ticket.Subject, contactName, agent, ticket.ResolutionHtml, ticket.AwaitingCustomerConfirmation);

        var thread = await threads.AppendAsync(ticket.Id, _ => new TicketThread
        {
            Kind = ThreadKind.SystemMessage, Visibility = Visibility.Public, Channel = ticket.Channel, Direction = MessageDirection.Outbound,
            AuthorType = AuthorType.System, AuthorName = "Resolution notice", Subject = $"Resolved: {ticket.Subject}", BodyHtml = html, BodyText = text,
            DeliveryStatus = ThreadDeliveryStatus.Delivered, ChannelMetadataJson = Json.Serialize(new { kind = MessageKind, eventId = ev.Id })
        }, key, ct);

        try
        {
            var msg = await outbound.QueueTicketNoticeAsync(ticket, $"Resolved: {ticket.Subject} [{display}]", html, text, MessageKind, ConsentPurpose.Transactional, ct);
            if (msg is null) logger.LogInformation("Resolution notice for ticket {Number} recorded on the ticket; no channel route to send it", ticket.Number);
        }
        catch (Exception ex) when (ex is DomainException or ChannelConfigurationException)
        {
            logger.LogInformation("Resolution notice for ticket {Number} not sent: {Reason}", ticket.Number, ex.Message);
        }

        var payload = new { ticketId = ticket.Id, threadId = thread.Id, seq = thread.Seq, kind = thread.Kind.ToString(), visibility = thread.Visibility.ToString() };
        await realtime.ToTicketAsync(tenantId, ticket.Id, "ticket.thread", payload, ct);
        await realtime.ToContactAsync(tenantId, ticket.ContactId!.Value, "ticket.thread", payload, ct);
    }

    /// <summary>Notice body (HTML + plain text). <paramref name="resolutionHtml"/> is already sanitised (TicketService.ResolveAsync).</summary>
    public static (string Html, string Text) Compose(string display, string subject, string? contactName, string? agent, string? resolutionHtml, bool awaitingConfirmation)
    {
        var greeting = $"<p>Hello {WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(contactName) ? "there" : contactName)},</p>";
        var head = $"<p>Your request <strong>{WebUtility.HtmlEncode(display)} — {WebUtility.HtmlEncode(subject)}</strong> has been resolved" +
                   (agent is null ? "." : $" by {WebUtility.HtmlEncode(agent)}.") + "</p>";
        var notes = string.IsNullOrWhiteSpace(TextNormalization.HtmlToText(resolutionHtml)) ? "" : $"<p><strong>Resolution</strong></p>{resolutionHtml}";
        var tail = awaitingConfirmation
            ? "<p>Please confirm that the problem is solved, or reply to this message if it is not — we will keep the request open until we hear from you.</p>"
            : "<p>If the problem comes back, simply reply to this message and we will reopen the request.</p>";
        var html = greeting + head + notes + tail;
        return (html, TextNormalization.HtmlToText(html));
    }
}
