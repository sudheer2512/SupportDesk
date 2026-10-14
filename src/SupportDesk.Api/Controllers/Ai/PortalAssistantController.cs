using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Api.Infrastructure;
using SupportDesk.Application.Ai.Features;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Ai;
using SupportDesk.Domain;
using SupportDesk.Domain.Platform;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Api.Controllers.Ai;

/// <summary>
/// Help-center assistant. Anonymous visitors get answers from public articles only; signed-in contacts also from
/// articles for authenticated customers and restricted articles their organisation may read. The brand slug is resolved
/// server-side to its tenant; a contact token for another tenant is refused. Rate limited per client address.
/// </summary>
[Route("api/v1/portal/{brand}/assistant")]
[AllowAnonymous]
[EnableRateLimiting(HostingSetup.PublicLimiter)]
public sealed class PortalAssistantController(DeskDbContext db, RequestContext ctx, AiAssistantService assistant, ILogger<PortalAssistantController> logger)
    : DeskControllerBase
{
    private const string Fallback = "The assistant can't answer right now. Search the help center or raise a ticket and our team will help.";

    [HttpPost]
    public async Task<ActionResult<PortalAssistantResponse>> Ask(string brand, PortalAssistantRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 2000)
            throw new ValidationException(new Dictionary<string, string> { ["message"] = "Ask a question (up to 2000 characters)." });
        var slug = brand.Trim().ToLowerInvariant();
        var b = await db.AllTenants<Brand>().AsNoTracking().FirstOrDefaultAsync(x => x.Slug == slug && x.PortalEnabled, ct)
                ?? throw new NotFoundException("Help center", brand);
        var tenantStatus = await db.Tenants.AsNoTracking().Where(t => t.Id == b.TenantId).Select(t => (TenantStatus?)t.Status).FirstOrDefaultAsync(ct);
        if (tenantStatus is null or TenantStatus.Cancelled or TenantStatus.PendingDeletion or TenantStatus.Suspended) throw new NotFoundException("Help center", brand);
        if (ctx.HasTenant && !ctx.IsContact) throw new ForbiddenException("Agents and API clients use the agent AI endpoints.");
        if (ctx.IsContact && ctx.TenantId != b.TenantId) throw new NotFoundException("Help center", brand);
        if (!ctx.HasTenant) ctx.SetTenant(b.TenantId);
        if (!b.AiAssistantEnabled) throw new DomainException("assistant_disabled", "The assistant is not enabled for this help center.", 404);

        Guid? customerId = ctx.ContactId is { } contactId
            ? await db.Contacts.AsNoTracking().Where(c => c.Id == contactId).Select(c => c.CustomerId).FirstOrDefaultAsync(ct)
            : null;
        try
        {
            var reply = await assistant.AnswerAsync(request.Message, request.Locale ?? b.DefaultLocale, customerId, b.Id, b.Slug, request.ConversationId, ct);
            if (reply is null) return new PortalAssistantResponse(Fallback, [], true, 0);
            return new PortalAssistantResponse(reply.Text, reply.Citations.Select(c => new CitationDto(c.Index, c.SourceType, c.SourceId, c.Title, c.Url, c.Snippet)).ToList(),
                reply.SuggestHandoff, reply.Confidence);
        }
        catch (DomainException ex) when (ex.StatusCode is 402 or 409 or 502)
        {
            // Budget exhausted, feature switched off or provider down: hand the visitor to a person.
            logger.LogInformation("Portal assistant unavailable for {Brand}: {Message}", slug, ex.Message);
            return new PortalAssistantResponse(Fallback, [], true, 0);
        }
    }
}
