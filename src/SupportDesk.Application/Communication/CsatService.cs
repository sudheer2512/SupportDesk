using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Communication;
using SupportDesk.Domain;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Communication;

/// <summary>
/// Customer satisfaction surveys: when a ticket is resolved a single-use survey token (only its hash is stored) is created
/// and the survey is sent through the ticket's channel (e-mail with one-click ratings, or a WhatsApp/SMS link), respecting
/// survey consent. Public endpoints read and answer the survey until it expires; answers update Ticket.CsatScore and
/// publish satisfaction.received.
/// </summary>
public sealed class CsatService(
    DeskDbContext db,
    RequestContext ctx,
    TenantSettingsService settings,
    OutboundService outbound,
    CommunicationKeys keys,
    TicketHistory history,
    IEventPublisher events,
    ILogger<CsatService> logger,
    Administration.EmailTemplateService templates)
{
    public async Task<SatisfactionResponse?> SendSurveyAsync(Guid ticketId, CancellationToken ct)
    {
        var s = await settings.TicketsAsync(ct);
        if (!s.CsatEnabled) return null;
        var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null || ticket.StatusCategory != StatusCategory.Resolved || ticket.IsSpam || ticket.MergedIntoTicketId is not null || ticket.ContactId is null) return null;
        var cycleStart = ticket.ReopenedAt ?? DateTime.MinValue;
        if (await db.SatisfactionResponses.AnyAsync(r => r.TicketId == ticketId && r.SentAt >= cycleStart, ct)) return null;

        var now = db.Clock.UtcNow;
        var token = TextNormalization.RandomToken(24);
        var response = new SatisfactionResponse
        {
            TicketId = ticket.Id, ContactId = ticket.ContactId, AgentUserId = ticket.AssigneeId, TokenHash = TokenHasher.Hash(token), Channel = ticket.Channel,
            SentAt = now, ExpiresAt = now.AddDays(Math.Max(1, s.CsatExpiryDays))
        };
        db.SatisfactionResponses.Add(response);
        await db.SaveChangesAsync(ct);

        var brand = ticket.BrandId is { } b ? await db.Brands.AsNoTracking().FirstOrDefaultAsync(x => x.Id == b, ct) : await db.Brands.AsNoTracking().FirstOrDefaultAsync(x => x.IsDefault, ct);
        var link = $"{keys.PublicBaseUrl}/portal/{brand?.Slug ?? "help"}/csat/{token}";
        var agent = ticket.AssigneeId is { } a ? await db.Users.AsNoTracking().Where(u => u.Id == a).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) : null;
        var display = $"#{ticket.Number}";
        var stars = string.Join(" ", Enumerable.Range(1, 5).Select(i =>
            $"<a href=\"{link}?rating={i}\" style=\"display:inline-block;padding:8px 12px;margin:2px;border-radius:6px;background:{brand?.PrimaryColor ?? "#2563eb"};color:#fff;text-decoration:none\">{i} ★</a>"));
        // Tenant template (administration → notifications → e-mail templates) or the built-in default, plus the brand footer.
        var (subject, html) = await templates.RenderAsync(Administration.EmailTemplateCatalog.CustomerCsatSurvey, new Dictionary<string, string?>
        {
            ["ticket.number"] = display, ["ticket.subject"] = ticket.Subject, ["agent.name"] = agent ?? "our team", ["survey.link"] = link,
            ["survey.buttons"] = stars, ["survey.expires"] = response.ExpiresAt.ToString("dd MMM yyyy"), ["brand.name"] = brand?.Name
        }, ct);
        html += await templates.BrandFooterAsync(brand?.Id, ct);
        var text = $"Your request {display} was resolved. How did we do? Rate us from 1 to 5: {link}";
        try
        {
            var msg = await outbound.QueueTicketNoticeAsync(ticket, subject, html, text, "csat", ConsentPurpose.Surveys, ct);
            if (msg is null) logger.LogInformation("CSAT survey for ticket {Number} created but no channel was available to send it", ticket.Number);
        }
        catch (Exception ex) when (ex is DomainException or ChannelConfigurationException)
        {
            logger.LogInformation("CSAT survey for ticket {Number} not sent: {Reason}", ticket.Number, ex.Message);
        }
        return response;
    }

    public async Task<CsatSurveyDto> GetAsync(string token, CancellationToken ct)
    {
        var (response, ticket) = await ResolveAsync(token, ct);
        return await MapAsync(response, ticket, ct);
    }

    public Task<CsatSurveyDto> SubmitAsync(string token, CsatSubmitRequest r, CancellationToken ct) => SubmitCoreAsync(token, r, 0, ct);

    private async Task<CsatSurveyDto> SubmitCoreAsync(string token, CsatSubmitRequest r, int attempt, CancellationToken ct)
    {
        if (r.Rating is < 1 or > 5) throw new ValidationException(new Dictionary<string, string> { ["rating"] = "Choose a rating from 1 to 5." });
        var (response, ticket) = await ResolveAsync(token, ct);
        if (response.ExpiresAt < db.Clock.UtcNow) throw new DomainException("survey_expired", "This survey has expired.", 410);
        var comment = string.IsNullOrWhiteSpace(r.Comment) ? null : TextNormalization.Truncate(r.Comment.Trim(), 2000);
        var tracked = await db.SatisfactionResponses.FirstAsync(x => x.Id == response.Id, ct);
        var first = tracked.RespondedAt is null;
        tracked.Rating = r.Rating;
        tracked.Comment = comment;
        tracked.RespondedAt = db.Clock.UtcNow;
        var t = await db.Tickets.FirstAsync(x => x.Id == ticket.Id, ct);
        var before = t.CsatScore;
        t.CsatScore = r.Rating;
        t.CsatComment = comment;
        history.Record(t, TicketEventTypes.Satisfaction, $"Customer rated the support {r.Rating}/5{(comment is null ? "" : $": “{TextNormalization.Truncate(comment, 200)}”")}",
            [new FieldChange("csatScore", before?.ToString(), r.Rating.ToString())], new { responseId = tracked.Id, first });
        events.Publish(EventTypes.SatisfactionReceived, "Ticket", t.Id,
            new SatisfactionPayload(t.Id, t.Number, tracked.Id, r.Rating, comment, t.AssigneeId, t.ContactId));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException) when (attempt < 3)
        {
            // The ticket changed meanwhile; apply the score on the fresh row.
            db.ChangeTracker.Clear();
            return await SubmitCoreAsync(token, r, attempt + 1, ct);
        }
        return await MapAsync(tracked, t, ct);
    }

    private async Task<(SatisfactionResponse Response, Ticket Ticket)> ResolveAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100) throw new NotFoundException("Survey");
        var hash = TokenHasher.Hash(token.Trim());
        var response = await db.AllTenants<SatisfactionResponse>().AsNoTracking().FirstOrDefaultAsync(r => r.TokenHash == hash, ct) ?? throw new NotFoundException("Survey");
        if (!ctx.HasTenant) ctx.SetSystem(response.TenantId, "Customer survey", ActorType.System);
        else if (ctx.TenantId != response.TenantId) throw new NotFoundException("Survey");
        var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == response.TicketId, ct) ?? throw new NotFoundException("Survey");
        return (response, ticket);
    }

    private async Task<CsatSurveyDto> MapAsync(SatisfactionResponse r, Ticket t, CancellationToken ct)
    {
        var agent = r.AgentUserId is { } a ? await db.Users.AsNoTracking().Where(u => u.Id == a).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) : null;
        var brand = t.BrandId is { } b ? await db.Brands.AsNoTracking().Where(x => x.Id == b).Select(x => new { x.Name, x.PrimaryColor }).FirstOrDefaultAsync(ct) : null;
        var prefix = (await settings.TicketsAsync(ct)).NumberPrefix;
        var status = r.RespondedAt is not null ? "answered" : r.ExpiresAt < db.Clock.UtcNow ? "expired" : "open";
        return new CsatSurveyDto(status, $"{(string.IsNullOrEmpty(prefix) ? "#" : prefix)}{t.Number}", t.Subject, agent, brand?.Name, brand?.PrimaryColor, r.Rating, r.Comment, r.ExpiresAt);
    }
}

public sealed record SatisfactionPayload(Guid TicketId, long Number, Guid ResponseId, int Rating, string? Comment, Guid? AgentUserId, Guid? ContactId);

/// <summary>Sends the CSAT survey when a ticket moves to a Resolved status (idempotent per resolution cycle).</summary>
public sealed class CsatSurveyHandler(CsatService csat) : IOutboxHandler
{
    public string Name => "csat-surveys";
    public int Order => HandlerOrder.Integrations;
    public bool CanHandle(string eventType) => eventType == EventTypes.TicketStatusChanged;

    public async Task HandleAsync(OutboxEvent ev, CancellationToken ct)
    {
        var p = Json.Deserialize<TicketEventPayload>(ev.PayloadJson);
        if (p is null || p.Changes?.Any(c => c.Field == "status") != true) return;
        await csat.SendSurveyAsync(p.TicketId, ct);
    }
}
