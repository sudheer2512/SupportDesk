using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Notifications;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Knowledge;
using SupportDesk.Domain;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Knowledge;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Knowledge;

/// <summary>
/// Knowledge articles: list/detail, create/update (every content change is a new immutable <see cref="ArticleVersion"/>),
/// workflow Draft → In review → Approved → Published → Archived/Expired with permission-checked transitions and
/// reviewer notes, version history with diff and restore, and soft delete. Publishing marks published translations
/// of older source versions Outdated and raises article.published / article.updated.
/// </summary>
public sealed class ArticleService(
    DeskDbContext db,
    RequestContext ctx,
    KbAccess access,
    HtmlSanitization html,
    IEventPublisher events,
    IAuditWriter audit,
    NotificationService notifications)
{
    public const int MaxTitle = 300;

    // ───────────────────────────── Queries ─────────────────────────────

    public async Task<PageResult<ArticleListItemDto>> ListAsync(ArticleQuery q, CancellationToken ct = default)
    {
        access.RequireAgentReader();
        var now = db.Clock.UtcNow;
        var query = access.AgentReadable(db.Articles.AsNoTracking());
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = q.Q.Trim();
            query = query.Where(a => a.Title.Contains(term) || a.Slug.Contains(term) || (a.Summary != null && a.Summary.Contains(term)) || a.TagsJson.Contains(term));
        }
        if (q.Status is { Length: > 0 } s && Enum.TryParse<ArticleStatus>(s, true, out var status)) query = query.Where(a => a.Status == status);
        if (KbRules.TryVisibility(q.Visibility, out var vis) && !string.IsNullOrWhiteSpace(q.Visibility)) query = query.Where(a => a.Visibility == vis);
        if (q.CategoryId is { } cat)
        {
            var children = await db.KbCategories.AsNoTracking().Where(c => c.ParentId == cat).Select(c => c.Id).ToListAsync(ct);
            children.Add(cat);
            query = query.Where(a => a.CategoryId != null && children.Contains(a.CategoryId.Value));
        }
        if (KbRules.NormalizeLocale(q.Locale) is { } locale)
            query = query.Where(a => a.PrimaryLocale == locale || db.ArticleTranslations.Any(t => t.ArticleId == a.Id && t.Locale == locale));
        if (q.OwnerUserId is { } owner) query = query.Where(a => a.OwnerUserId == owner);
        if (KbRules.NormalizeArticleType(q.ArticleType) is { } type) query = query.Where(a => a.ArticleType == type);
        if (q.ProductId is { } product) query = query.Where(a => a.ProductId == product);
        if (KbRules.NormalizeTags([q.Tag ?? ""]).FirstOrDefault() is { } tag) query = query.Where(a => a.TagsJson.Contains("\"" + tag + "\""));
        if (q.ReviewDue == true) query = query.Where(a => a.ReviewDueAt != null && a.ReviewDueAt <= now);
        if (q.Live == true) query = query.Where(a => a.PublishedVersionId != null && (a.ExpiresAt == null || a.ExpiresAt > now));
        if (q.Live == false) query = query.Where(a => a.PublishedVersionId == null);

        var total = await query.CountAsync(ct);
        query = (q.Sort ?? "updated").ToLowerInvariant() switch
        {
            "title" => query.OrderBy(a => a.Title),
            "views" => query.OrderByDescending(a => a.ViewCount).ThenBy(a => a.Title),
            "helpful" => query.OrderByDescending(a => a.HelpfulCount - a.NotHelpfulCount).ThenBy(a => a.Title),
            "reviewdue" => query.OrderBy(a => a.ReviewDueAt == null).ThenBy(a => a.ReviewDueAt),
            _ => query.OrderByDescending(a => a.UpdatedAt)
        };
        var page = Math.Max(1, q.Page ?? 1);
        var size = Math.Clamp(q.PageSize ?? 25, 1, 200);
        var rows = await query.Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PageResult<ArticleListItemDto>(await MapListAsync(rows, ct), page, size, total);
    }

    public async Task<List<ArticleListItemDto>> MapListAsync(IReadOnlyList<Article> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.Id).ToList();
        var categoryIds = rows.Where(r => r.CategoryId.HasValue).Select(r => r.CategoryId!.Value).Distinct().ToList();
        var categories = await db.KbCategories.AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var userIds = rows.Where(r => r.OwnerUserId.HasValue).Select(r => r.OwnerUserId!.Value).Distinct().ToList();
        var users = await db.Users.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var productIds = rows.Where(r => r.ProductId.HasValue).Select(r => r.ProductId!.Value).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var locales = (await db.ArticleTranslations.AsNoTracking().Where(t => ids.Contains(t.ArticleId)).Select(t => new { t.ArticleId, t.Locale }).ToListAsync(ct))
            .GroupBy(t => t.ArticleId).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Locale).OrderBy(x => x).ToList());
        var publishedIds = rows.Where(r => r.PublishedVersionId.HasValue).Select(r => r.PublishedVersionId!.Value).ToList();
        var publishedNumbers = await db.ArticleVersions.AsNoTracking().Where(v => publishedIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.VersionNumber, ct);
        var now = db.Clock.UtcNow;
        return rows.Select(a => new ArticleListItemDto(
            a.Id, a.Slug, a.Title, a.Summary, a.Status.ToString(), a.Visibility.ToString(), a.ArticleType, a.CategoryId,
            a.CategoryId is { } c && categories.TryGetValue(c, out var cn) ? cn : null, a.OwnerUserId,
            a.OwnerUserId is { } o && users.TryGetValue(o, out var on) ? on : null, a.PrimaryLocale,
            locales.TryGetValue(a.Id, out var l) ? l : [], Json.StringList(a.TagsJson), a.ProductId,
            a.ProductId is { } p && products.TryGetValue(p, out var pn) ? pn : null, KbAccess.IsLive(a, now),
            a.PublishedVersionId is not null && a.CurrentVersionId != a.PublishedVersionId, a.LatestVersionNumber,
            a.PublishedVersionId is { } pv && publishedNumbers.TryGetValue(pv, out var pvn) ? pvn : null,
            a.PublishedAt, a.ReviewDueAt, a.ExpiresAt, a.ViewCount, a.HelpfulCount, a.NotHelpfulCount, a.RatingAverage, a.RatingCount, a.UpdatedAt)).ToList();
    }

    public async Task<ArticleDetailDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        access.RequireAgentReader();
        var a = await db.Articles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Article", id);
        if (!access.AgentCanRead(a)) throw new NotFoundException("Article", id);
        return await MapDetailAsync(a, ct);
    }

    public async Task<ArticleDetailDto> MapDetailAsync(Article a, CancellationToken ct)
    {
        var current = a.CurrentVersionId is { } cv ? await db.ArticleVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == cv, ct) : null;
        var publishedNumber = a.PublishedVersionId is { } pv
            ? await db.ArticleVersions.AsNoTracking().Where(v => v.Id == pv).Select(v => (int?)v.VersionNumber).FirstOrDefaultAsync(ct)
            : null;
        var category = a.CategoryId is { } c ? await db.KbCategories.AsNoTracking().Where(x => x.Id == c).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
        var owner = a.OwnerUserId is { } o ? await db.Users.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(u => u.Id == o).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) : null;
        var product = a.ProductId is { } p ? await db.Products.AsNoTracking().Where(x => x.Id == p).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
        var relatedIds = Json.GuidList(a.RelatedArticleIdsJson);
        var now = db.Clock.UtcNow;
        var related = (await db.Articles.AsNoTracking().Where(x => relatedIds.Contains(x.Id)).ToListAsync(ct))
            .OrderBy(x => relatedIds.IndexOf(x.Id)).Select(x => new ArticleRefDto(x.Id, x.Title, x.Slug, x.Status.ToString(), KbAccess.IsLive(x, now))).ToList();
        var rules = await (from r in db.ArticleAccessRules.AsNoTracking()
                           where r.ArticleId == a.Id
                           select new ArticleAccessRuleDto(r.Id, r.CustomerId, db.Customers.Where(x => x.Id == r.CustomerId).Select(x => x.Name).FirstOrDefault(),
                               r.CustomerGroupId, db.CustomerGroups.Where(x => x.Id == r.CustomerGroupId).Select(x => x.Name).FirstOrDefault(), r.CustomerTier,
                               r.ProductId, db.Products.Where(x => x.Id == r.ProductId).Select(x => x.Name).FirstOrDefault())).ToListAsync(ct);
        var attachments = (await db.ArticleAttachments.AsNoTracking().Where(x => x.ArticleId == a.Id).OrderBy(x => x.CreatedAt).ToListAsync(ct))
            .Select(x => MapAttachment(x)).ToList();
        var translations = (await db.ArticleTranslations.AsNoTracking().Where(t => t.ArticleId == a.Id).OrderBy(t => t.Locale).ToListAsync(ct))
            .Select(t => new ArticleTranslationSummaryDto(t.Id, t.Locale, t.Title, t.Status.ToString(), t.SourceVersionNumber,
                IsTranslationOutdated(t, publishedNumber), t.UpdatedAt)).ToList();
        var lastNote = await db.ArticleVersions.AsNoTracking().Where(v => v.ArticleId == a.Id && v.ReviewNote != null)
            .OrderByDescending(v => v.ReviewedAt).Select(v => v.ReviewNote).FirstOrDefaultAsync(ct);
        string? ticketNumber = null;
        if (a.SourceTicketId is { } tid)
        {
            var number = await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(t => t.Id == tid).Select(t => (long?)t.Number).FirstOrDefaultAsync(ct);
            if (number is { } n) ticketNumber = "#" + n;
        }
        return new ArticleDetailDto(
            a.Id, a.Slug, current?.Title ?? a.Title, current?.Summary ?? a.Summary, current?.BodyHtml ?? "", current?.Keywords, a.Status.ToString(),
            a.Visibility.ToString(), a.ArticleType, a.CategoryId, category, a.OwnerUserId, owner, a.PrimaryLocale, Json.StringList(a.TagsJson), related,
            Json.GuidList(a.BrandIdsJson), a.ProductId, product, KbAccess.IsLive(a, now),
            a.PublishedVersionId is not null && a.CurrentVersionId != a.PublishedVersionId, current?.VersionNumber ?? a.LatestVersionNumber, publishedNumber,
            a.PublishedAt, a.ReviewDueAt, a.ExpiresAt, a.ViewCount, a.HelpfulCount, a.NotHelpfulCount, a.RatingAverage, a.RatingCount, a.SourceTicketId,
            ticketNumber, rules, attachments, translations, Actions(a), lastNote, a.CreatedAt, a.UpdatedAt, EditToken(a));
    }

    public static bool IsTranslationOutdated(ArticleTranslation t, int? publishedNumber) =>
        t.Status == TranslationStatus.Outdated || (publishedNumber is { } p && t.SourceVersionNumber < p);

    public static ArticleAttachmentDto MapAttachment(ArticleAttachment x) =>
        new(x.Id, x.FileId, x.FileName, x.ContentType, x.SizeBytes, x.ContentType.StartsWith("image/", StringComparison.Ordinal),
            $"/api/v1/kb/articles/{x.ArticleId}/attachments/{x.Id}/download", x.CreatedAt);

    public ArticleActionsDto Actions(Article a)
    {
        var author = ctx.Has(Permissions.KbAuthor);
        var reviewer = ctx.Has(Permissions.KbReview);
        var publisher = ctx.Has(Permissions.KbPublish);
        var pending = a.CurrentVersionId is not null && a.CurrentVersionId != a.PublishedVersionId;
        return new ArticleActionsDto(
            Edit: author,
            Submit: author && a.Status == ArticleStatus.Draft,
            Approve: reviewer && a.Status == ArticleStatus.InReview,
            Reject: reviewer && a.Status == ArticleStatus.InReview,
            Publish: publisher && pending && (a.Status == ArticleStatus.Approved || (reviewer && a.Status is ArticleStatus.Draft or ArticleStatus.InReview)),
            Archive: publisher && a.PublishedVersionId is not null,
            Reopen: author && a.Status is ArticleStatus.Archived or ArticleStatus.Expired,
            Delete: publisher,
            ManageTranslations: author);
    }

    // ───────────────────────────── Create / update ─────────────────────────────

    public async Task<ArticleDetailDto> CreateAsync(SaveArticleRequest r, CancellationToken ct = default)
    {
        var article = await CreateCoreAsync(r, null, ct);
        return await MapDetailAsync(article, ct);
    }

    /// <summary>Creates a Draft article with version 1 (used by the API, ticket→article, import and the demo seed).</summary>
    public async Task<Article> CreateCoreAsync(SaveArticleRequest r, Guid? sourceTicketId, CancellationToken ct)
    {
        if (!ctx.IsSystem) ctx.Require(Permissions.KbAuthor);
        var v = await ValidateAsync(r, null, ct);
        var a = new Article
        {
            Slug = await UniqueSlugAsync(r.Slug, v.Title, null, ct),
            PrimaryLocale = v.Locale,
            OwnerUserId = v.OwnerId ?? ctx.UserId,
            SourceTicketId = sourceTicketId,
            Status = ArticleStatus.Draft
        };
        ApplyMetadata(a, r, v);
        db.Articles.Add(a);
        AddVersion(a, v, r.ChangeNote ?? (sourceTicketId is null ? "Created" : "Created from a ticket"));
        if (r.AccessRules is not null) ReplaceRules(a.Id, v.Rules);
        events.Publish(KnowledgeEvents.ArticleChanged, "Article", a.Id, new EntityEventPayload(a.Id, a.Title));
        await db.SaveChangesAsync(ct);
        return a;
    }

    public async Task<ArticleDetailDto> UpdateAsync(Guid id, SaveArticleRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.KbAuthor);
        var a = await LoadAsync(id, ct);
        CheckEditToken(a, r.RowVersion);
        var v = await ValidateAsync(r, a.Id, ct);
        var current = a.CurrentVersionId is { } cv ? await db.ArticleVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == cv, ct) : null;
        var contentChanged = current is null || current.Title != v.Title || current.BodyHtml != v.Body || (current.Summary ?? "") != (v.Summary ?? "")
                             || (current.Keywords ?? "") != (v.Keywords ?? "") || current.Locale != v.Locale;
        if (r.Slug is { Length: > 0 } slug && TextNormalization.Slugify(slug) != a.Slug) a.Slug = await UniqueSlugAsync(slug, v.Title, a.Id, ct);
        a.PrimaryLocale = v.Locale;
        ApplyMetadata(a, r, v);
        if (v.OwnerId is { } owner) a.OwnerUserId = owner;
        if (contentChanged)
        {
            AddVersion(a, v, r.ChangeNote);
            a.Status = ArticleStatus.Draft;
        }
        if (r.AccessRules is not null) ReplaceRules(a.Id, v.Rules);
        events.Publish(KnowledgeEvents.ArticleChanged, "Article", a.Id, new EntityEventPayload(a.Id, a.Title));
        await SaveAsync(ct);
        return await MapDetailAsync(a, ct);
    }

    private void ApplyMetadata(Article a, SaveArticleRequest r, ValidatedArticle v)
    {
        a.Title = v.Title;
        a.Summary = v.Summary;
        a.ArticleType = v.Type;
        a.Visibility = v.Visibility;
        a.CategoryId = r.CategoryId;
        a.ProductId = r.ProductId;
        a.TagsJson = Json.Serialize(v.Tags);
        a.RelatedArticleIdsJson = Json.Serialize(v.Related);
        a.BrandIdsJson = Json.Serialize(v.Brands);
        a.ReviewDueAt = r.ReviewDueAt;
        a.ExpiresAt = r.ExpiresAt;
    }

    private void AddVersion(Article a, ValidatedArticle v, string? note)
    {
        var version = new ArticleVersion
        {
            ArticleId = a.Id,
            VersionNumber = a.LatestVersionNumber + 1,
            Locale = v.Locale,
            Title = v.Title,
            BodyHtml = v.Body,
            Summary = v.Summary,
            Keywords = v.Keywords,
            Status = ArticleStatus.Draft,
            ChangeNote = string.IsNullOrWhiteSpace(note) ? null : TextNormalization.Truncate(note.Trim(), 1000),
            AuthorId = ctx.UserId,
            CreatedAt = db.Clock.UtcNow
        };
        db.ArticleVersions.Add(version);
        a.LatestVersionNumber = version.VersionNumber;
        a.CurrentVersionId = version.Id;
    }

    private void ReplaceRules(Guid articleId, IReadOnlyList<ArticleAccessRuleInput> rules)
    {
        db.ArticleAccessRules.RemoveRange(db.ArticleAccessRules.Where(x => x.ArticleId == articleId));
        foreach (var r in rules)
            db.ArticleAccessRules.Add(new ArticleAccessRule
            {
                ArticleId = articleId, CustomerId = r.CustomerId, CustomerGroupId = r.CustomerGroupId,
                CustomerTier = string.IsNullOrWhiteSpace(r.CustomerTier) ? null : r.CustomerTier.Trim(), ProductId = r.ProductId
            });
    }

    private sealed record ValidatedArticle(string Title, string Body, string? Summary, string? Keywords, string Type, KbVisibility Visibility,
        string Locale, List<string> Tags, List<Guid> Related, List<Guid> Brands, Guid? OwnerId, IReadOnlyList<ArticleAccessRuleInput> Rules);

    private async Task<ValidatedArticle> ValidateAsync(SaveArticleRequest r, Guid? articleId, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        var title = (r.Title ?? "").Trim();
        if (title.Length == 0) errors["title"] = "A title is required.";
        else if (title.Length > MaxTitle) errors["title"] = $"At most {MaxTitle} characters.";
        var body = html.Sanitize(r.BodyHtml);
        if (TextNormalization.HtmlToText(body).Length == 0 && !body.Contains("<img", StringComparison.OrdinalIgnoreCase) && !body.Contains("<iframe", StringComparison.OrdinalIgnoreCase))
            errors["bodyHtml"] = "Write the article content.";
        if (body.Length > 1_000_000) errors["bodyHtml"] = "The article is too long.";
        var type = KbRules.NormalizeArticleType(r.ArticleType);
        if (type is null) errors["articleType"] = $"Use one of: {string.Join(", ", KbRules.ArticleTypes)}.";
        if (!KbRules.TryVisibility(r.Visibility, out var visibility)) errors["visibility"] = "Use Public, Authenticated, Internal or Restricted.";
        var locale = KbRules.NormalizeLocale(r.PrimaryLocale ?? "en");
        if (locale is null) errors["primaryLocale"] = "Use a language code such as en, hi or ta.";
        if (r.CategoryId is { } cat && !await db.KbCategories.AnyAsync(c => c.Id == cat, ct)) errors["categoryId"] = "Unknown category.";
        if (r.ProductId is { } prod && !await db.Products.AnyAsync(p => p.Id == prod, ct)) errors["productId"] = "Unknown product.";
        Guid? owner = null;
        if (r.OwnerUserId is { } o)
        {
            if (!await db.Users.AnyAsync(u => u.Id == o && u.IsActive, ct)) errors["ownerUserId"] = "Unknown or inactive user.";
            else owner = o;
        }
        var related = (r.RelatedArticleIds ?? []).Where(x => x != articleId).Distinct().Take(20).ToList();
        if (related.Count > 0)
        {
            var found = await db.Articles.Where(x => related.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct);
            related = related.Where(found.Contains).ToList();
        }
        var brands = (r.BrandIds ?? []).Distinct().ToList();
        if (brands.Count > 0 && await db.Brands.CountAsync(b => brands.Contains(b.Id), ct) != brands.Count) errors["brandIds"] = "Unknown brand.";
        if (r.ExpiresAt is { } exp && exp <= db.Clock.UtcNow) errors["expiresAt"] = "The expiry date must be in the future.";
        var rules = (r.AccessRules ?? []).ToList();
        if (rules.Count > 100) errors["accessRules"] = "At most 100 access rules.";
        foreach (var rule in rules)
        {
            if (rule.CustomerId is null && rule.CustomerGroupId is null && string.IsNullOrWhiteSpace(rule.CustomerTier) && rule.ProductId is null)
                errors["accessRules"] = "Each access rule needs a customer, customer group, tier or product.";
            if (rule.CustomerId is { } cid && !await db.Customers.AnyAsync(x => x.Id == cid, ct)) errors["accessRules"] = "Unknown customer in an access rule.";
            if (rule.CustomerGroupId is { } gid && !await db.CustomerGroups.AnyAsync(x => x.Id == gid, ct)) errors["accessRules"] = "Unknown customer group in an access rule.";
            if (rule.ProductId is { } pid && !await db.Products.AnyAsync(x => x.Id == pid, ct)) errors["accessRules"] = "Unknown product in an access rule.";
        }
        if (visibility == KbVisibility.Restricted && errors.Count == 0)
        {
            var hasRules = r.AccessRules is not null ? rules.Count > 0 : articleId is { } aid && await db.ArticleAccessRules.AnyAsync(x => x.ArticleId == aid, ct);
            if (!hasRules) errors["accessRules"] = "Restricted articles need at least one access rule (customer, group, tier or product).";
        }
        if (errors.Count > 0) throw new ValidationException(errors);
        var summary = string.IsNullOrWhiteSpace(r.Summary) ? null : TextNormalization.Truncate(TextNormalization.HtmlToText(r.Summary).Trim(), 1000);
        var keywords = string.IsNullOrWhiteSpace(r.Keywords) ? null : TextNormalization.Truncate(r.Keywords.Trim(), 1000);
        return new ValidatedArticle(title, body, summary, keywords, type!, visibility, locale!, KbRules.NormalizeTags(r.Tags), related, brands, owner, rules);
    }

    private async Task<string> UniqueSlugAsync(string? requested, string title, Guid? articleId, CancellationToken ct)
    {
        var baseSlug = TextNormalization.Slugify(string.IsNullOrWhiteSpace(requested) ? title : requested, 120);
        if (baseSlug.Length == 0) baseSlug = "article";
        var slug = baseSlug;
        for (var i = 2; await db.Articles.AnyAsync(a => a.Slug == slug && a.Id != articleId, ct); i++) slug = $"{baseSlug}-{i}";
        return slug;
    }

    // ───────────────────────────── Workflow ─────────────────────────────

    public async Task<ArticleDetailDto> TransitionAsync(Guid id, ArticleTransitionRequest r, CancellationToken ct = default)
    {
        var a = await LoadAsync(id, ct);
        var action = (r.Action ?? "").Trim().ToLowerInvariant();
        var note = string.IsNullOrWhiteSpace(r.Note) ? null : TextNormalization.Truncate(r.Note.Trim(), 2000);
        var now = db.Clock.UtcNow;
        var current = a.CurrentVersionId is { } cv ? await db.ArticleVersions.FirstOrDefaultAsync(v => v.Id == cv, ct) : null;
        if (current is null) throw new DomainException(ErrorCodes.InvalidTransition, "The article has no content yet.");
        var notify = new List<NotifyRequest>();
        var link = $"/kb/articles/{a.Id}";

        switch (action)
        {
            case "submit":
                ctx.Require(Permissions.KbAuthor);
                Expect(a, ArticleStatus.Draft, "submit for review");
                a.Status = current.Status = ArticleStatus.InReview;
                if (note is not null) current.ChangeNote ??= note;
                notify.Add(new NotifyRequest(await UsersWithPermissionAsync(Permissions.KbReview, ct), KnowledgeNotificationTypes.ReviewRequested,
                    $"Review requested: {current.Title}", note ?? $"{ctx.ActorName} submitted version {current.VersionNumber} for review.", link,
                    EntityType: "Article", EntityId: a.Id, DedupKey: $"kb-review:{current.Id}"));
                break;
            case "approve":
                ctx.Require(Permissions.KbReview);
                Expect(a, ArticleStatus.InReview, "approve");
                a.Status = current.Status = ArticleStatus.Approved;
                Review(current, note, now);
                notify.Add(AuthorNotice(a, current, "approved", note, link));
                break;
            case "reject":
                ctx.Require(Permissions.KbReview);
                Expect(a, ArticleStatus.InReview, "send back");
                if (note is null) throw new ValidationException(new Dictionary<string, string> { ["note"] = "Tell the author what to change." });
                a.Status = current.Status = ArticleStatus.Draft;
                Review(current, note, now);
                notify.Add(AuthorNotice(a, current, "returned for changes", note, link));
                break;
            case "publish":
            {
                ctx.Require(Permissions.KbPublish);
                if (a.PublishedVersionId == current.Id && a.Status == ArticleStatus.Published)
                    throw new DomainException(ErrorCodes.InvalidTransition, "This version is already published.");
                if (a.Status != ArticleStatus.Approved)
                {
                    if (a.Status is not (ArticleStatus.Draft or ArticleStatus.InReview) || !ctx.Has(Permissions.KbReview))
                        throw new DomainException(ErrorCodes.InvalidTransition, "Only approved articles can be published. Submit it for review first.");
                    Review(current, note ?? "Approved on publish", now);
                }
                if (a.ExpiresAt is { } exp && exp <= now) throw new DomainException(ErrorCodes.Validation, "The expiry date has passed. Change or clear it before publishing.");
                var first = a.PublishedAt is null;
                if (a.PublishedVersionId is { } previous && previous != current.Id &&
                    await db.ArticleVersions.FirstOrDefaultAsync(v => v.Id == previous, ct) is { } superseded)
                    superseded.Status = ArticleStatus.Archived;
                current.Status = ArticleStatus.Published;
                a.Status = ArticleStatus.Published;
                a.PublishedVersionId = current.Id;
                a.PublishedAt = now;
                a.Title = current.Title;
                a.Summary = current.Summary;
                a.ReviewDueAt ??= now.AddMonths(6);
                // Published translations made from an older source version are now outdated.
                var outdated = await db.ArticleTranslations.Where(t => t.ArticleId == a.Id && t.Status == TranslationStatus.Published && t.SourceVersionNumber < current.VersionNumber).ToListAsync(ct);
                foreach (var t in outdated) t.Status = TranslationStatus.Outdated;
                events.Publish(first ? EventTypes.ArticlePublished : EventTypes.ArticleUpdated, "Article", a.Id,
                    new EntityEventPayload(a.Id, a.Title, [new FieldChange("version", null, current.VersionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture))]));
                audit.Write("kb.article_published", "Article", a.Id, $"Published '{a.Title}' (v{current.VersionNumber})", new { version = current.VersionNumber, outdatedTranslations = outdated.Count });
                if (a.OwnerUserId is { } owner && owner != ctx.UserId)
                    notify.Add(new NotifyRequest([owner], KnowledgeNotificationTypes.ArticlePublished, $"Published: {a.Title}", $"Version {current.VersionNumber} is live.", link,
                        EntityType: "Article", EntityId: a.Id, DedupKey: $"kb-published:{current.Id}"));
                break;
            }
            case "archive":
                ctx.Require(Permissions.KbPublish);
                if (a.PublishedVersionId is null) throw new DomainException(ErrorCodes.InvalidTransition, "Only live articles can be archived.");
                await ArchiveCoreAsync(a, ArticleStatus.Archived, note, ct);
                break;
            case "reopen":
                ctx.Require(Permissions.KbAuthor);
                if (a.Status is not (ArticleStatus.Archived or ArticleStatus.Expired))
                    throw new DomainException(ErrorCodes.InvalidTransition, "Only archived or expired articles can be reopened.");
                a.Status = ArticleStatus.Draft;
                current.Status = ArticleStatus.Draft;
                break;
            default:
                throw new ValidationException(new Dictionary<string, string> { ["action"] = "Use submit, approve, reject, publish, archive or reopen." });
        }
        events.Publish(KnowledgeEvents.ArticleChanged, "Article", a.Id, new EntityEventPayload(a.Id, a.Title));
        await SaveAsync(ct);
        foreach (var n in notify.Where(n => n.UserIds.Count > 0)) await notifications.NotifyAsync(n, ct);
        return await MapDetailAsync(a, ct);
    }

    /// <summary>Takes a live article off the portal (archive or expiry); readers no longer see any version.</summary>
    public async Task ArchiveCoreAsync(Article a, ArticleStatus status, string? note, CancellationToken ct)
    {
        if (a.PublishedVersionId is { } pv && await db.ArticleVersions.FirstOrDefaultAsync(v => v.Id == pv, ct) is { } published)
            published.Status = status;
        a.PublishedVersionId = null;
        a.Status = status;
        events.Publish(EventTypes.ArticleUpdated, "Article", a.Id, new EntityEventPayload(a.Id, a.Title, [new FieldChange("status", null, status.ToString())]));
        audit.Write(status == ArticleStatus.Expired ? "kb.article_expired" : "kb.article_archived", "Article", a.Id, $"{status} '{a.Title}'", new { note });
    }

    private static void Expect(Article a, ArticleStatus status, string verb)
    {
        if (a.Status != status) throw new DomainException(ErrorCodes.InvalidTransition, $"An article in status {a.Status} cannot {verb}.");
    }

    private void Review(ArticleVersion v, string? note, DateTime now)
    {
        v.ReviewerId = ctx.UserId;
        v.ReviewedAt = now;
        v.ReviewNote = note;
    }

    private NotifyRequest AuthorNotice(Article a, ArticleVersion v, string what, string? note, string link)
    {
        var to = new[] { v.AuthorId, a.OwnerUserId }.Where(x => x is not null && x != ctx.UserId).Select(x => x!.Value).Distinct().ToList();
        return new NotifyRequest(to, KnowledgeNotificationTypes.ArticleReviewed, $"Article {what}: {v.Title}", note, link,
            EntityType: "Article", EntityId: a.Id, DedupKey: $"kb-reviewed:{v.Id}:{what}");
    }

    private async Task<List<Guid>> UsersWithPermissionAsync(string permission, CancellationToken ct) =>
        await db.UserRoles.AsNoTracking()
            .Where(ur => db.RolePermissions.Any(rp => rp.RoleId == ur.RoleId && rp.PermissionCode == permission) && ur.UserId != ctx.UserId)
            .Where(ur => db.Users.Any(u => u.Id == ur.UserId && u.IsActive))
            .Select(ur => ur.UserId).Distinct().OrderBy(id => id).Take(50).ToListAsync(ct);

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.KbPublish);
        var a = await LoadAsync(id, ct);
        a.IsDeleted = true;
        a.DeletedAt = db.Clock.UtcNow;
        a.Slug = TextNormalization.Truncate($"{a.Slug}-deleted-{a.Id:N}", 440);
        if (a.PublishedVersionId is not null)
            events.Publish(EventTypes.ArticleUpdated, "Article", a.Id, new EntityEventPayload(a.Id, a.Title, [new FieldChange("deleted", null, "true")]));
        events.Publish(KnowledgeEvents.ArticleChanged, "Article", a.Id, new EntityEventPayload(a.Id, a.Title));
        audit.Write("kb.article_deleted", "Article", a.Id, $"Deleted article '{a.Title}'");
        await SaveAsync(ct);
    }

    // ───────────────────────────── Versions ─────────────────────────────

    public async Task<List<ArticleVersionDto>> VersionsAsync(Guid id, CancellationToken ct = default)
    {
        var a = await GetReadableAsync(id, ct);
        var versions = await db.ArticleVersions.AsNoTracking().Where(v => v.ArticleId == id).OrderByDescending(v => v.VersionNumber).ToListAsync(ct);
        var userIds = versions.SelectMany(v => new[] { v.AuthorId, v.ReviewerId }).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
        var users = await db.Users.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return versions.Select(v => MapVersion(v, a, users)).ToList();
    }

    public async Task<ArticleVersionContentDto> VersionAsync(Guid id, int number, CancellationToken ct = default)
    {
        var a = await GetReadableAsync(id, ct);
        var v = await db.ArticleVersions.AsNoTracking().FirstOrDefaultAsync(x => x.ArticleId == id && x.VersionNumber == number, ct)
                ?? throw new NotFoundException("Version", number);
        var ids = new[] { v.AuthorId, v.ReviewerId }.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        var users = await db.Users.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return new ArticleVersionContentDto(MapVersion(v, a, users), v.BodyHtml, v.Keywords);
    }

    public async Task<ArticleDiffDto> DiffAsync(Guid id, int from, int to, CancellationToken ct = default)
    {
        await GetReadableAsync(id, ct);
        var versions = await db.ArticleVersions.AsNoTracking().Where(v => v.ArticleId == id && (v.VersionNumber == from || v.VersionNumber == to)).ToListAsync(ct);
        var a = versions.FirstOrDefault(v => v.VersionNumber == from) ?? throw new NotFoundException("Version", from);
        var b = versions.FirstOrDefault(v => v.VersionNumber == to) ?? throw new NotFoundException("Version", to);
        var lines = TextDiff.Lines(TextNormalization.HtmlToText(a.BodyHtml), TextNormalization.HtmlToText(b.BodyHtml));
        static FieldDiffDto? Field(string? x, string? y) => (x ?? "") == (y ?? "") ? null : new FieldDiffDto(x, y);
        return new ArticleDiffDto(from, to, Field(a.Title, b.Title), Field(a.Summary, b.Summary), Field(a.Keywords, b.Keywords), lines,
            lines.Count(l => l.Op == "insert"), lines.Count(l => l.Op == "delete"));
    }

    public async Task<ArticleDetailDto> RestoreAsync(Guid id, int number, string? changeNote, CancellationToken ct = default)
    {
        ctx.Require(Permissions.KbAuthor);
        var a = await LoadAsync(id, ct);
        var source = await db.ArticleVersions.AsNoTracking().FirstOrDefaultAsync(v => v.ArticleId == id && v.VersionNumber == number, ct)
                     ?? throw new NotFoundException("Version", number);
        var note = $"Restored from version {number}" + (string.IsNullOrWhiteSpace(changeNote) ? "" : $": {changeNote.Trim()}");
        AddVersion(a, new ValidatedArticle(source.Title, source.BodyHtml, source.Summary, source.Keywords, a.ArticleType, a.Visibility, source.Locale,
            [], [], [], null, []), note);
        a.Title = source.Title;
        a.Summary = source.Summary;
        a.Status = ArticleStatus.Draft;
        events.Publish(KnowledgeEvents.ArticleChanged, "Article", a.Id, new EntityEventPayload(a.Id, a.Title));
        await SaveAsync(ct);
        return await MapDetailAsync(a, ct);
    }

    private ArticleVersionDto MapVersion(ArticleVersion v, Article a, IReadOnlyDictionary<Guid, string> users) =>
        new(v.Id, v.VersionNumber, v.Locale, v.Title, v.Summary, v.Status.ToString(), v.ChangeNote, v.AuthorId,
            v.AuthorId is { } au && users.TryGetValue(au, out var an) ? an : null, v.CreatedAt, v.ReviewerId,
            v.ReviewerId is { } r && users.TryGetValue(r, out var rn) ? rn : null, v.ReviewedAt, v.ReviewNote, v.Id == a.CurrentVersionId, v.Id == a.PublishedVersionId);

    // ───────────────────────────── Helpers ─────────────────────────────

    public async Task<Article> LoadAsync(Guid id, CancellationToken ct)
    {
        access.RequireAgentReader();
        var a = await db.Articles.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Article", id);
        if (!access.AgentCanRead(a)) throw new NotFoundException("Article", id);
        return a;
    }

    private async Task<Article> GetReadableAsync(Guid id, CancellationToken ct)
    {
        access.RequireAgentReader();
        var a = await db.Articles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Article", id);
        if (!access.AgentCanRead(a)) throw new NotFoundException("Article", id);
        return a;
    }

    /// <summary>
    /// Editing concurrency token: latest version number + last edit time. Unlike the SQL rowversion it ignores reader
    /// counters (views, feedback), so busy articles do not produce false edit conflicts.
    /// </summary>
    public static string EditToken(Article a) => $"{a.LatestVersionNumber}.{a.UpdatedAt.Ticks}";

    private static void CheckEditToken(Article a, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ValidationException(new Dictionary<string, string> { ["rowVersion"] = "rowVersion is required for updates." });
        if (!string.Equals(token.Trim(), EditToken(a), StringComparison.Ordinal))
            throw new ConflictException(ErrorCodes.ConcurrencyConflict, "Someone else changed this article. Reload it to see the latest version, then apply your changes again.");
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(ErrorCodes.ConcurrencyConflict, "Someone else changed this article. Reload it to see the latest version, then apply your changes again.");
        }
    }

    /// <summary>Raw JSON helper for import/export and seed.</summary>
    public static JsonElement ToJson<T>(T value) => Json.ToElement(value);

    public string SanitizeForImport(string? body) => html.Sanitize(body);
}
