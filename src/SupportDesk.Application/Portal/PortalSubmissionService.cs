using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using SupportDesk.Application.Knowledge;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Search;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Portal;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Portal;

/// <summary>
/// Anonymous ticket submission and the embeddable web form (only when the brand allows anonymous tickets).
/// Captcha-free abuse protection: a honeypot field and a time trap (bots get a fake success and nothing is created),
/// the public rate limiter on the endpoint, and a per-IP hourly cap per brand.
/// </summary>
public sealed class PortalSubmissionService(
    DeskDbContext db,
    PortalContext portal,
    TicketService tickets,
    TicketLookups lookups,
    IDistributedCache cache,
    HtmlSanitization html)
{
    public const int MaxPerIpPerHour = 5;
    private const string Received = "Thank you — we have received your request and will reply by e-mail.";

    public async Task<PortalSubmissionResultDto> SubmitAsync(PortalAnonymousTicketRequest r, string? ip, Channel channel, CancellationToken ct = default)
    {
        var brand = portal.Brand;
        if (!brand.AllowAnonymousTickets) throw new NotFoundException("Ticket form");
        // Honeypot and time trap: answer like a success so bots learn nothing.
        if (!string.IsNullOrWhiteSpace(r.Website)) return new PortalSubmissionResultDto(true, null, Received);
        if (r.FormStartedAt is { } started && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - started is >= 0 and < 2500)
            return new PortalSubmissionResultDto(true, null, Received);

        var errors = new Dictionary<string, string>();
        var name = (r.Name ?? "").Trim();
        if (name.Length is < 2 or > 120) errors["name"] = "Enter your name.";
        var email = TextNormalization.NormalizeEmail(r.Email);
        if (email is null || email.Length > 254) errors["email"] = "Enter a valid e-mail address.";
        var subject = (r.Subject ?? "").Trim();
        if (subject.Length is < 3 or > 250) errors["subject"] = "Tell us briefly what the problem is.";
        var description = r.DescriptionHtml is { Length: > 0 } d ? html.Sanitize(d) : TextNormalization.TextToHtml(r.Description);
        if (description.Length > 50_000) errors["description"] = "The description is too long.";
        if (r.DepartmentId is { } dept && !await db.Departments.AnyAsync(x => x.Id == dept && x.IsActive && x.VisibleInPortal, ct)) errors["departmentId"] = "Choose one of the listed departments.";
        if (r.CategoryId is { } cat && !await db.TicketCategories.AnyAsync(x => x.Id == cat && x.IsActive && x.VisibleInPortal, ct)) errors["categoryId"] = "Choose one of the listed categories.";
        if (r.ProductId is { } prod && !await db.Products.AnyAsync(x => x.Id == prod && x.IsActive, ct)) errors["productId"] = "Choose one of the listed products.";
        if (errors.Count > 0) throw new ValidationException(errors);

        var key = $"portal-submit:{brand.Id:N}:{ip ?? "unknown"}:{DateTime.UtcNow.ToString("yyyyMMddHH", CultureInfo.InvariantCulture)}";
        var count = int.TryParse(await cache.GetStringAsync(key, ct), out var n) ? n : 0;
        if (count >= MaxPerIpPerHour) throw new DomainException("rate_limited", "Too many requests from your network. Please try again later or sign in.", 429);
        await cache.SetStringAsync(key, (count + 1).ToString(CultureInfo.InvariantCulture), new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1) }, ct);

        // The ticket core matches an existing contact by e-mail/phone or creates one (same unit of work as the ticket).
        var ticket = await tickets.CreateAsync(new CreateTicketRequest
        {
            Subject = subject, DescriptionHtml = description, Channel = channel.ToString(), BrandId = brand.Id,
            NewContact = new NewContactDto(name, email, string.IsNullOrWhiteSpace(r.Phone) ? null : r.Phone.Trim(), null),
            DepartmentId = r.DepartmentId ?? brand.DefaultDepartmentId, CategoryId = r.CategoryId, ProductId = r.ProductId, CustomFields = r.CustomFields
        }, new TicketCreationSource(CreateInitialThread: true, PortalSubmission: true, AuthorName: name, FromAddress: email, EnforceRequiredFields: false), ct);
        await lookups.LoadAsync(ct);
        return new PortalSubmissionResultDto(true, lookups.Display(ticket.Number), $"Thank you — your ticket {lookups.Display(ticket.Number)} has been created. We will reply by e-mail.");
    }
}

/// <summary>
/// Portal assistant: asks the AI module's <see cref="IConversationAssistant"/> first; when it is not configured or not
/// confident, answers from the knowledge base (top articles for the reader, with citations) and suggests a ticket when
/// nothing relevant is found. Every assistant question is logged as a knowledge search (analytics).
/// </summary>
public sealed class PortalAssistantService(
    DeskDbContext db,
    PortalContext portal,
    IConversationAssistant assistant,
    KnowledgeSearchService search,
    KbFeedbackService feedback)
{
    public async Task<PortalAssistantReplyDto> AskAsync(PortalAssistantRequest r, CancellationToken ct = default)
    {
        var brand = portal.Brand;
        if (!brand.AiAssistantEnabled) throw new NotFoundException("Assistant");
        var message = (r.Message ?? "").Trim();
        if (message.Length < 2) throw new ValidationException(new Dictionary<string, string> { ["message"] = "Ask a question." });
        if (message.Length > 1000) message = message[..1000];
        var locale = KbRules.NormalizeLocale(r.Locale) ?? portal.Contact?.PreferredLanguage ?? brand.DefaultLocale;

        AssistantReply? ai = null;
        try { ai = await assistant.ReplyAsync(null, message, locale, portal.SignedIn, ct); }
        catch (DomainException) { ai = null; }
        if (ai is not null && !string.IsNullOrWhiteSpace(ai.Text))
        {
            var citations = ai.Citations.Select(c => new PortalCitationDto(c.Index, c.SourceType, c.SourceId, c.Title, PortalUrl(c.Url), c.Snippet)).ToList();
            await feedback.LogSearchAsync(message, citations.Count, "assistant", brand.Id, portal.Contact?.Id, locale, ct);
            return new PortalAssistantReplyDto(ai.Text, citations, ai.SuggestHandoff, ai.Confidence, "ai");
        }

        var reader = await portal.ReaderAsync(ct);
        var hits = brand.KnowledgeBaseEnabled ? await search.SearchArticlesAsync(message + " ", reader, 3, ct) : [];
        await feedback.LogSearchAsync(message, hits.Count, "assistant", brand.Id, portal.Contact?.Id, locale, ct);
        if (hits.Count == 0)
            return new PortalAssistantReplyDto(
                "I couldn't find an article that answers this. You can create a ticket and our team will help you directly.", [], true, 0, "kb");
        var ids = hits.Select(h => h.ArticleId).ToList();
        var articles = await db.Articles.AsNoTracking().Where(a => ids.Contains(a.Id))
            .Select(a => new { a.Id, a.Slug, Title = db.ArticleVersions.Where(v => v.Id == a.PublishedVersionId).Select(v => v.Title).FirstOrDefault() ?? a.Title })
            .ToDictionaryAsync(a => a.Id, ct);
        var cites = hits.Where(h => articles.ContainsKey(h.ArticleId)).Select((h, i) =>
        {
            var a = articles[h.ArticleId];
            return new PortalCitationDto(i + 1, "Article", a.Id, a.Title, $"/{brand.Slug}/kb/{a.Slug}", h.SnippetText);
        }).ToList();
        var text = new StringBuilder("Here is what I found in our help articles:\n");
        foreach (var c in cites) text.Append('\n').Append('[').Append(c.Index).Append("] ").Append(c.Title);
        text.Append("\n\nOpen an article for the step-by-step answer. If it doesn't solve your problem, create a ticket.");
        var top = hits[0].Score;
        var confidence = Math.Round(Math.Min(0.9, top / (top + 4)), 2);
        return new PortalAssistantReplyDto(text.ToString(), cites, confidence < 0.5, confidence, "kb");
    }

    /// <summary>Portal app route for citation URLs ("/portal/{brand}/kb/x" → "/{brand}/kb/x").</summary>
    private static string? PortalUrl(string? url) =>
        url is not null && url.StartsWith("/portal/", StringComparison.OrdinalIgnoreCase) ? url["/portal".Length..] : url;
}
