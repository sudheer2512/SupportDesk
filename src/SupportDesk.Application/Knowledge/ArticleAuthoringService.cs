using System.Text;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Knowledge;
using SupportDesk.Domain;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Knowledge;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Knowledge;

/// <summary>
/// Authoring helpers around articles: attachments, embeddable images (public asset URLs), editor options, and
/// ticket → article conversion (also the <see cref="IArticleDraftWriter"/> port used by the AI module). Drafts created
/// from tickets are Internal until a reviewer changes the visibility and publishes them.
/// </summary>
public sealed class ArticleAuthoringService(
    DeskDbContext db,
    RequestContext ctx,
    KbAccess access,
    ArticleService articles,
    FileService files,
    HtmlSanitization html,
    TicketHistory history) : IArticleDraftWriter
{
    public const string AssetPurpose = "kb-asset";
    public const string AttachmentPurpose = "kb-attachment";
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp" };

    public static string AssetUrl(Guid fileId) => $"/api/v1/kb-assets/{fileId}";

    // ───────────────────────────── Attachments & assets ─────────────────────────────

    public async Task<ArticleAttachmentDto> AddAttachmentAsync(Guid articleId, Stream content, string fileName, string? contentType, CancellationToken ct = default)
    {
        ctx.Require(Permissions.KbAuthor);
        var a = await articles.LoadAsync(articleId, ct);
        if (await db.ArticleAttachments.CountAsync(x => x.ArticleId == a.Id, ct) >= 50) throw new DomainException(ErrorCodes.Validation, "An article can have at most 50 attachments.");
        var stored = await files.UploadAsync(content, fileName, contentType, AttachmentPurpose, ct);
        var att = new ArticleAttachment
        {
            ArticleId = a.Id, FileId = stored.Id, FileName = stored.FileName, ContentType = stored.ContentType, SizeBytes = stored.SizeBytes, CreatedAt = db.Clock.UtcNow
        };
        db.ArticleAttachments.Add(att);
        a.UpdatedAt = db.Clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return ArticleService.MapAttachment(att);
    }

    public async Task DeleteAttachmentAsync(Guid articleId, Guid attachmentId, CancellationToken ct = default)
    {
        ctx.Require(Permissions.KbAuthor);
        await articles.LoadAsync(articleId, ct);
        var att = await db.ArticleAttachments.FirstOrDefaultAsync(x => x.Id == attachmentId && x.ArticleId == articleId, ct) ?? throw new NotFoundException("Attachment", attachmentId);
        db.ArticleAttachments.Remove(att);
        await db.SaveChangesAsync(ct);
    }

    public async Task<(ArticleAttachment Attachment, Stream Content)> OpenAttachmentAsync(Guid articleId, Guid attachmentId, CancellationToken ct = default)
    {
        await articles.LoadAsync(articleId, ct);
        var att = await db.ArticleAttachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == attachmentId && x.ArticleId == articleId, ct)
                  ?? throw new NotFoundException("Attachment", attachmentId);
        var (_, stream) = await files.OpenAsync(att.FileId, ct);
        return (att, stream);
    }

    /// <summary>Stores an image for embedding in article bodies. The returned URL is public (unguessable id) so portals can render it.</summary>
    public async Task<ArticleAttachmentDto> UploadAssetAsync(Stream content, string fileName, string? contentType, CancellationToken ct = default)
    {
        ctx.RequireAgent();
        ctx.RequireAny(Permissions.KbAuthor, Permissions.CommunityModerate);
        if (!ImageExtensions.Contains(Path.GetExtension(fileName)))
            throw new DomainException("file_type_blocked", "Only PNG, JPEG, GIF or WebP images can be embedded in articles.");
        var stored = await files.UploadAsync(content, fileName, contentType, AssetPurpose, ct);
        if (!FileService.IsInlineSafe(stored.ContentType) || !stored.ContentType.StartsWith("image/", StringComparison.Ordinal))
            throw new DomainException("file_type_blocked", "The file is not a valid image.");
        return new ArticleAttachmentDto(stored.Id, stored.Id, stored.FileName, stored.ContentType, stored.SizeBytes, true, AssetUrl(stored.Id), stored.CreatedAt);
    }

    // ───────────────────────────── Options ─────────────────────────────

    public async Task<KbOptionsDto> OptionsAsync(CancellationToken ct = default)
    {
        access.RequireAgentReader();
        var brands = await db.Brands.AsNoTracking().OrderByDescending(b => b.IsDefault).ThenBy(b => b.Name).Select(b => new IdName(b.Id, b.Name)).ToListAsync(ct);
        var brandLocales = (await db.Brands.AsNoTracking().Select(b => b.SupportedLocalesJson).ToListAsync(ct)).SelectMany(Json.StringList);
        var locales = brandLocales.Concat(KbRules.CommonLocales).Select(KbRules.NormalizeLocale).Where(l => l is not null).Select(l => l!).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Name).Select(p => new IdName(p.Id, p.Name)).ToListAsync(ct);
        var groups = await db.CustomerGroups.AsNoTracking().OrderBy(g => g.Name).Select(g => new IdName(g.Id, g.Name)).ToListAsync(ct);
        var tiers = (await db.Customers.AsNoTracking().Select(c => c.Tier).Distinct().ToListAsync(ct))
            .Concat(["Platinum", "Gold", "Silver", "Bronze", "Standard"]).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var owners = await db.Users.AsNoTracking().Where(u => u.IsActive && u.Kind != UserKind.LightAgent)
            .OrderBy(u => u.DisplayName).Select(u => new IdName(u.Id, u.DisplayName)).Take(500).ToListAsync(ct);
        var tags = (await db.Articles.AsNoTracking().OrderByDescending(a => a.UpdatedAt).Select(a => a.TagsJson).Take(2000).ToListAsync(ct))
            .SelectMany(Json.StringList).GroupBy(t => t).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(200).ToList();
        return new KbOptionsDto(KbRules.ArticleTypes, Enum.GetNames<KbVisibility>(), Enum.GetNames<ArticleStatus>(), locales, brands, products, groups,
            tiers, owners, tags);
    }

    public async Task<List<IdName>> CustomerOptionsAsync(string? q, CancellationToken ct = default)
    {
        access.RequireAgentReader();
        var query = db.Customers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(c => c.Name.Contains(term) || (c.Code != null && c.Code.Contains(term)));
        }
        return await query.OrderBy(c => c.Name).Take(25).Select(c => new IdName(c.Id, c.Name)).ToListAsync(ct);
    }

    // ───────────────────────────── Ticket → article ─────────────────────────────

    public async Task<ArticleDetailDto> CreateFromTicketAsync(Guid ticketId, ArticleFromTicketRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.KbAuthor);
        var t = await db.Tickets.VisibleTo(ctx, db).AsNoTracking().FirstOrDefaultAsync(x => x.Id == ticketId, ct) ?? throw new NotFoundException("Ticket", ticketId);
        if (!t.StatusCategory.IsResolvedOrClosed())
            throw new DomainException(ErrorCodes.Validation, "Resolve the ticket first — articles are drafted from resolved tickets.");
        var body = await DraftBodyFromTicketAsync(t, r.IncludeConversation, ct);
        var tags = await db.TicketTags.AsNoTracking().Where(x => x.TicketId == t.Id).Select(x => x.Tag!.Name).ToListAsync(ct);
        var title = string.IsNullOrWhiteSpace(r.Title) ? CleanSubject(t.Subject) : r.Title.Trim();
        var article = await articles.CreateCoreAsync(new SaveArticleRequest
        {
            Title = title, BodyHtml = body, Summary = TextNormalization.Truncate(TextNormalization.HtmlToText(t.DescriptionHtml), 280),
            CategoryId = r.CategoryId, ArticleType = "Troubleshooting", Visibility = nameof(KbVisibility.Internal), Tags = tags, ProductId = t.ProductId,
            ChangeNote = $"Drafted from ticket #{t.Number}"
        }, t.Id, ct);
        history.Record(t, "KnowledgeArticle", $"Knowledge article draft created: {title}", null, new { articleId = article.Id });
        await db.SaveChangesAsync(ct);
        return await articles.MapDetailAsync(article, ct);
    }

    /// <summary>IArticleDraftWriter: creates an Internal draft (AI module, automations). Agents need kb.author.</summary>
    public async Task<Guid> CreateDraftAsync(string title, string bodyHtml, string? summary, Guid? sourceTicketId, CancellationToken ct)
    {
        if (!ctx.IsSystem) ctx.Require(Permissions.KbAuthor);
        var article = await articles.CreateCoreAsync(new SaveArticleRequest
        {
            Title = string.IsNullOrWhiteSpace(title) ? "Untitled draft" : TextNormalization.Truncate(title.Trim(), ArticleService.MaxTitle),
            BodyHtml = string.IsNullOrWhiteSpace(TextNormalization.HtmlToText(bodyHtml)) ? "<p>(Draft — add content.)</p>" : bodyHtml,
            Summary = summary, ArticleType = "HowTo", Visibility = nameof(KbVisibility.Internal),
            ChangeNote = sourceTicketId is null ? "Draft created automatically" : "Draft created from a ticket"
        }, sourceTicketId, ct);
        return article.Id;
    }

    private async Task<string> DraftBodyFromTicketAsync(Ticket t, bool includeConversation, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("<h2>Problem</h2>");
        sb.Append(string.IsNullOrWhiteSpace(t.DescriptionHtml) ? $"<p>{System.Net.WebUtility.HtmlEncode(t.Subject)}</p>" : html.Sanitize(t.DescriptionHtml));
        // Only public agent replies are used — never internal notes or customer messages (they may contain personal data).
        var replies = await db.TicketThreads.AsNoTracking()
            .Where(x => x.TicketId == t.Id && x.Visibility == Visibility.Public && x.Kind == ThreadKind.AgentReply)
            .OrderBy(x => x.Seq).Select(x => x.BodyHtml).ToListAsync(ct);
        sb.Append("<h2>Solution</h2>");
        if (!string.IsNullOrWhiteSpace(t.ResolutionHtml)) sb.Append(html.Sanitize(t.ResolutionHtml));
        else if (replies.LastOrDefault(r => !string.IsNullOrWhiteSpace(r)) is { } last) sb.Append(html.Sanitize(last));
        else sb.Append("<p>Describe the resolution steps.</p>");
        if (includeConversation && replies.Count > 1)
        {
            sb.Append("<h2>Additional details</h2>");
            foreach (var reply in replies.Take(replies.Count - 1).Where(r => !string.IsNullOrWhiteSpace(r)).Take(5))
                sb.Append("<blockquote>").Append(html.Sanitize(reply)).Append("</blockquote>");
        }
        sb.Append("<p><em>Review this draft: remove customer-specific details before publishing.</em></p>");
        return sb.ToString();
    }

    private static string CleanSubject(string subject)
    {
        var s = subject.Trim();
        foreach (var prefix in new[] { "Re:", "RE:", "Fwd:", "FW:", "Fw:" })
            while (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) s = s[prefix.Length..].Trim();
        return s.Length == 0 ? subject : s;
    }
}
