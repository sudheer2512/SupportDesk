using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Notifications;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Integrations.Engineering;

/// <summary>
/// Sync-back rules for <c>engineering.item_updated</c> (per tenant settings, Administration → Engineering):
/// when the linked item is resolved or released an internal note is added and the ticket assignee is notified;
/// when the fix is released the customer can be told automatically with a reply template ("fix released in version X");
/// orphaned items (deleted/moved in the tracker) are flagged on the ticket. Idempotent per event (client request ids,
/// notification dedup keys, the link's CustomerNotified flag).
/// </summary>
public sealed class EngineeringOutboxHandler(
    DeskDbContext db,
    RequestContext ctx,
    TenantSettingsService settings,
    ConversationService conversation,
    TicketCollaborationService collaboration,
    TicketHistory history,
    NotificationService notifications,
    ILogger<EngineeringOutboxHandler> logger) : IOutboxHandler
{
    public const string NotificationType = "engineering.updated";
    public const string DefaultReleaseReplyHtml =
        "<p>Hi {{contact.name}},</p><p>Good news: the fix for the problem you reported in ticket {{ticket.number}} (“{{ticket.subject}}”) has been " +
        "released in version <strong>{{engineering.fixVersion}}</strong>.</p><p>Please update to this version. If the problem persists afterwards, simply reply " +
        "to this message.</p><p>Kind regards,<br>Support team</p>";

    public string Name => "engineering-sync-rules";
    public int Order => HandlerOrder.Integrations;
    public bool CanHandle(string eventType) => eventType == EventTypes.EngineeringItemUpdated;

    public async Task HandleAsync(OutboxEvent ev, CancellationToken ct)
    {
        if (ev.TenantId is not { } tenantId) return;
        var p = Json.Deserialize<EngineeringItemEventPayload>(ev.PayloadJson);
        if (p is null || p.Action != EngineeringItemEventPayload.Updated) return;
        if (!p.BecameResolved && !p.BecameReleased && !p.BecameOrphaned) return;

        var s = await settings.GetAsync<EngineeringSettings>(EngineeringSettings.Key, ct);
        var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == p.TicketId, ct);
        if (ticket is null || ticket.MergedIntoTicketId is not null || ticket.IsSpam) return;
        var link = await db.EngineeringLinks.FirstOrDefaultAsync(l => l.Id == p.LinkId, ct);
        if (link is null) return; // unlinked meanwhile
        ctx.SetSystem(tenantId, "Engineering integration", ActorType.Integration);

        var version = p.ReleaseName ?? p.FixVersion;
        var shown = await DisplayAsync(ticket, ct);
        var label = $"{p.DisplayId} “{p.Title}”";
        string? noteHtml = null, title = null;
        var severity = NotificationSeverity.Info;
        if (p.BecameReleased && s.AddNoteOnReleased)
        {
            noteHtml = $"<p><strong>Engineering:</strong> the fix for {E(label)} has been released{(version is null ? "" : " in version <strong>" + E(version) + "</strong>")}.</p>" +
                       (s.NotifyCustomerOnRelease ? "<p>The customer is notified automatically.</p>" : "<p>Let the customer know and confirm the fix.</p>");
            title = $"Fix released for {shown}{(version is null ? "" : " (" + version + ")")}";
        }
        else if (p.BecameResolved && !p.BecameReleased && s.AddNoteOnResolved)
        {
            noteHtml = $"<p><strong>Engineering:</strong> {E(label)} was resolved (state: {E(p.State ?? "resolved")}){(p.FixVersion is null ? "" : ", fix version " + E(p.FixVersion))}. " +
                       "The fix ships with the next release.</p>";
            title = $"Engineering resolved {p.DisplayId} for {shown}";
        }
        else if (p.BecameOrphaned)
        {
            noteHtml = $"<p><strong>Engineering:</strong> {E(label)} was deleted or moved in the tracker. The last known state is kept on this ticket; " +
                       "link a replacement work item if engineering still tracks the problem.</p>";
            title = $"Engineering item {p.DisplayId} removed for {shown}";
            severity = NotificationSeverity.Warning;
        }

        if (noteHtml is not null)
        {
            await conversation.AddNoteAsync(ticket.Id, new NoteRequest { BodyHtml = noteHtml, ClientRequestId = $"eng:{ev.Id:N}:note" }, ct);
            if (s.NotifyAssignee && ticket.AssigneeId is { } assignee)
                await notifications.NotifyAsync(new NotifyRequest([assignee], NotificationType, title!, TextNormalization.HtmlToText(noteHtml), $"/tickets/{ticket.Id}",
                    severity, false, "Ticket", ticket.Id, $"eng:{ev.Id:N}"), ct);
        }

        if (p.BecameReleased && s.NotifyCustomerOnRelease && !link.CustomerNotified && ticket.ContactId is not null)
            await NotifyCustomerAsync(ticket, link, p, s, version, ct);
    }

    private async Task NotifyCustomerAsync(Ticket ticket, EngineeringLink link, EngineeringItemEventPayload p, EngineeringSettings s, string? version, CancellationToken ct)
    {
        string body;
        Guid? templateId = null;
        if (s.ReleaseReplyTemplateId is { } tid && await db.ReplyTemplates.AsNoTracking().AnyAsync(t => t.Id == tid && t.IsActive, ct))
        {
            templateId = tid;
            body = await collaboration.RenderTemplateAsync(tid, ticket.Id, ct);
        }
        else
        {
            var contact = await db.Contacts.AsNoTracking().Where(c => c.Id == ticket.ContactId).Select(c => c.FullName).FirstOrDefaultAsync(ct);
            body = DefaultReleaseReplyHtml
                .Replace("{{contact.name}}", E(contact?.Split(' ')[0] ?? "there"))
                .Replace("{{ticket.number}}", E(await DisplayAsync(ticket, ct)))
                .Replace("{{ticket.subject}}", E(ticket.Subject));
        }
        body = body.Replace("{{engineering.fixVersion}}", E(version ?? "the latest version"))
            .Replace("{{engineering.id}}", E(p.DisplayId))
            .Replace("{{engineering.title}}", E(p.Title));

        // The customer-facing reply is authored by the support team, not the integration.
        ctx.SetSystem(ticket.TenantId, "Support team", ActorType.Integration);
        try
        {
            await conversation.ReplyAsync(ticket.Id, new ReplyRequest { BodyHtml = body, Force = true, TemplateId = templateId, ClientRequestId = $"eng-release:{link.Id:N}" }, ct);
        }
        catch (DomainException ex)
        {
            logger.LogWarning(ex, "Release notification for ticket {Ticket} could not be sent", ticket.Id);
            return;
        }
        db.ChangeTracker.Clear();
        var tracked = await db.EngineeringLinks.FirstOrDefaultAsync(l => l.Id == link.Id, ct);
        if (tracked is null) return;
        tracked.CustomerNotified = true;
        history.Record(ticket, TicketEventTypes.EngineeringUpdated, $"Customer notified: fix released{(version is null ? "" : " in " + version)} ({p.DisplayId})",
            [new FieldChange("customerNotified", "false", "true")], new { linkId = link.Id, version });
        await db.SaveChangesAsync(ct);
    }


    private async Task<string> DisplayAsync(Ticket t, CancellationToken ct) => (await settings.TicketsAsync(ct)).NumberPrefix + t.Number;

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
}
