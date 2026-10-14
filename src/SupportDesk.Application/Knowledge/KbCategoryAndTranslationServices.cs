using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
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

/// <summary>Knowledge categories (a tree per tenant, optionally limited to brands). Managed with kb.publish or admin.knowledge.</summary>
public sealed class KbCategoryService(DeskDbContext db, RequestContext ctx, KbAccess access, IAuditWriter audit)
{
    public async Task<List<KbCategoryDto>> ListAsync(CancellationToken ct = default)
    {
        access.RequireAgentReader();
        var counts = await db.Articles.AsNoTracking().Where(a => a.CategoryId != null).GroupBy(a => a.CategoryId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var rows = await db.KbCategories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
        return rows.Select(c => Map(c, counts.GetValueOrDefault(c.Id))).ToList();
    }

    public async Task<KbCategoryDto> SaveAsync(Guid? id, SaveKbCategoryRequest r, CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.KbPublish, Permissions.AdminKnowledge);
        var errors = new Dictionary<string, string>();
        var name = (r.Name ?? "").Trim();
        if (name.Length is 0 or > 120) errors["name"] = "A name of up to 120 characters is required.";
        if (!KbRules.TryVisibility(r.Visibility, out var visibility)) errors["visibility"] = "Use Public, Authenticated, Internal or Restricted.";
        if (r.ParentId is { } parent)
        {
            if (parent == id) errors["parentId"] = "A category cannot be its own parent.";
            else if (!await db.KbCategories.AnyAsync(c => c.Id == parent, ct)) errors["parentId"] = "Unknown parent category.";
            else if (id is { } self && await IsDescendantAsync(parent, self, ct)) errors["parentId"] = "A category cannot be moved under its own sub-category.";
        }
        var brands = (r.BrandIds ?? []).Distinct().ToList();
        if (brands.Count > 0 && await db.Brands.CountAsync(b => brands.Contains(b.Id), ct) != brands.Count) errors["brandIds"] = "Unknown brand.";
        if (errors.Count > 0) throw new ValidationException(errors);

        var category = id is { } existingId
            ? await db.KbCategories.FirstOrDefaultAsync(c => c.Id == existingId, ct) ?? throw new NotFoundException("Category", existingId)
            : new KbCategory();
        var slug = TextNormalization.Slugify(string.IsNullOrWhiteSpace(r.Slug) ? name : r.Slug, 80);
        if (slug.Length == 0) slug = "category";
        var candidate = slug;
        for (var i = 2; await db.KbCategories.AnyAsync(c => c.Slug == candidate && c.Id != category.Id, ct); i++) candidate = $"{slug}-{i}";
        category.Name = name;
        category.Slug = candidate;
        category.Description = string.IsNullOrWhiteSpace(r.Description) ? null : TextNormalization.Truncate(r.Description.Trim(), 1000);
        category.Icon = string.IsNullOrWhiteSpace(r.Icon) ? null : r.Icon.Trim()[..Math.Min(40, r.Icon.Trim().Length)];
        category.Visibility = visibility;
        category.ParentId = r.ParentId;
        category.BrandIdsJson = Json.Serialize(brands);
        category.SortOrder = r.SortOrder;
        if (id is null) db.KbCategories.Add(category);
        await db.SaveChangesAsync(ct);
        var count = await db.Articles.CountAsync(a => a.CategoryId == category.Id, ct);
        return Map(category, count);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.KbPublish, Permissions.AdminKnowledge);
        var category = await db.KbCategories.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Category", id);
        if (await db.KbCategories.AnyAsync(c => c.ParentId == id, ct))
            throw new ConflictException("category_not_empty", "Move or delete the sub-categories first.");
        if (await db.Articles.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AnyAsync(a => a.CategoryId == id, ct))
            throw new ConflictException("category_not_empty", "Move the articles of this category first.");
        db.KbCategories.Remove(category);
        audit.Write("kb.category_deleted", "KbCategory", id, $"Deleted knowledge category '{category.Name}'");
        await db.SaveChangesAsync(ct);
    }

    private async Task<bool> IsDescendantAsync(Guid candidate, Guid ancestor, CancellationToken ct)
    {
        var all = await db.KbCategories.AsNoTracking().Select(c => new { c.Id, c.ParentId }).ToListAsync(ct);
        var current = all.FirstOrDefault(c => c.Id == candidate);
        for (var depth = 0; current is not null && depth < 50; depth++)
        {
            if (current.Id == ancestor) return true;
            current = current.ParentId is { } p ? all.FirstOrDefault(c => c.Id == p) : null;
        }
        return false;
    }

    public static KbCategoryDto Map(KbCategory c, int count) =>
        new(c.Id, c.ParentId, c.Name, c.Slug, c.Description, c.Icon, c.Visibility.ToString(), Json.GuidList(c.BrandIdsJson), c.SortOrder, count);
}

/// <summary>
/// Article translations with their own workflow (Draft → In translation → In review → Published; Outdated when the
/// source article is republished). Readers get a Published or Outdated translation, else the primary language.
/// </summary>
public sealed class ArticleTranslationService(DeskDbContext db, RequestContext ctx, ArticleService articles, HtmlSanitization html, IEventPublisher events)
{
    public async Task<List<ArticleTranslationDto>> ListAsync(Guid articleId, CancellationToken ct = default)
    {
        var a = await articles.LoadAsync(articleId, ct);
        var published = await PublishedNumberAsync(a, ct);
        var rows = await db.ArticleTranslations.AsNoTracking().Where(t => t.ArticleId == articleId).OrderBy(t => t.Locale).ToListAsync(ct);
        var names = await TranslatorNamesAsync(rows, ct);
        return rows.Select(t => Map(t, published, names)).ToList();
    }

    public async Task<ArticleTranslationDto> SaveAsync(Guid articleId, string locale, SaveTranslationRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.KbAuthor);
        var a = await articles.LoadAsync(articleId, ct);
        var loc = KbRules.NormalizeLocale(locale) ?? throw new ValidationException(new Dictionary<string, string> { ["locale"] = "Use a language code such as hi or ta." });
        if (loc == a.PrimaryLocale) throw new ValidationException(new Dictionary<string, string> { ["locale"] = "This is the article's primary language — edit the article itself." });
        var errors = new Dictionary<string, string>();
        var title = (r.Title ?? "").Trim();
        if (title.Length is 0 or > ArticleService.MaxTitle) errors["title"] = "A title is required.";
        var body = html.Sanitize(r.BodyHtml);
        if (TextNormalization.HtmlToText(body).Length == 0) errors["bodyHtml"] = "Write the translated content.";
        if (errors.Count > 0) throw new ValidationException(errors);

        var t = await db.ArticleTranslations.FirstOrDefaultAsync(x => x.ArticleId == articleId && x.Locale == loc, ct);
        if (t is null)
        {
            t = new ArticleTranslation { ArticleId = articleId, Locale = loc, Status = TranslationStatus.Draft };
            db.ArticleTranslations.Add(t);
        }
        var changed = t.Title != title || t.BodyHtml != body || (t.Summary ?? "") != (r.Summary ?? "");
        t.Title = title;
        t.BodyHtml = body;
        t.Summary = string.IsNullOrWhiteSpace(r.Summary) ? null : TextNormalization.Truncate(r.Summary.Trim(), 1000);
        t.MachineTranslated = r.MachineTranslated;
        t.TranslatorUserId = ctx.UserId;
        // The translation now reflects the current source (published version if any, else the working version).
        t.SourceVersionNumber = await PublishedNumberAsync(a, ct) ?? a.LatestVersionNumber;
        if (changed && t.Status is TranslationStatus.Published or TranslationStatus.Outdated) t.Status = TranslationStatus.InReview;
        events.Publish(KnowledgeEvents.ArticleChanged, "Article", a.Id, new EntityEventPayload(a.Id, a.Title));
        await db.SaveChangesAsync(ct);
        return Map(t, await PublishedNumberAsync(a, ct), await TranslatorNamesAsync([t], ct));
    }

    public async Task<ArticleTranslationDto> TransitionAsync(Guid articleId, string locale, string action, CancellationToken ct = default)
    {
        var a = await articles.LoadAsync(articleId, ct);
        var loc = KbRules.NormalizeLocale(locale) ?? throw new NotFoundException("Translation", locale);
        var t = await db.ArticleTranslations.FirstOrDefaultAsync(x => x.ArticleId == articleId && x.Locale == loc, ct) ?? throw new NotFoundException("Translation", loc);
        var published = await PublishedNumberAsync(a, ct);
        switch ((action ?? "").Trim().ToLowerInvariant())
        {
            case "start":
                ctx.Require(Permissions.KbAuthor);
                t.Status = TranslationStatus.InTranslation;
                t.TranslatorUserId ??= ctx.UserId;
                break;
            case "submit":
                ctx.Require(Permissions.KbAuthor);
                if (t.Status is TranslationStatus.Published) throw new DomainException(ErrorCodes.InvalidTransition, "The translation is already published.");
                t.Status = TranslationStatus.InReview;
                break;
            case "publish":
                ctx.RequireAny(Permissions.KbPublish, Permissions.KbReview);
                if (published is null) throw new DomainException(ErrorCodes.InvalidTransition, "Publish the source article first.");
                t.Status = TranslationStatus.Published;
                t.SourceVersionNumber = published.Value;
                t.PublishedAt = db.Clock.UtcNow;
                events.Publish(EventTypes.ArticleUpdated, "Article", a.Id, new EntityEventPayload(a.Id, a.Title, [new FieldChange("translation", null, t.Locale)]));
                break;
            case "reopen":
                ctx.Require(Permissions.KbAuthor);
                t.Status = TranslationStatus.Draft;
                break;
            default:
                throw new ValidationException(new Dictionary<string, string> { ["action"] = "Use start, submit, publish or reopen." });
        }
        events.Publish(KnowledgeEvents.ArticleChanged, "Article", a.Id, new EntityEventPayload(a.Id, a.Title));
        await db.SaveChangesAsync(ct);
        return Map(t, published, await TranslatorNamesAsync([t], ct));
    }

    public async Task DeleteAsync(Guid articleId, string locale, CancellationToken ct = default)
    {
        ctx.Require(Permissions.KbAuthor);
        var a = await articles.LoadAsync(articleId, ct);
        var loc = KbRules.NormalizeLocale(locale) ?? throw new NotFoundException("Translation", locale);
        var t = await db.ArticleTranslations.FirstOrDefaultAsync(x => x.ArticleId == articleId && x.Locale == loc, ct) ?? throw new NotFoundException("Translation", loc);
        db.ArticleTranslations.Remove(t);
        events.Publish(KnowledgeEvents.ArticleChanged, "Article", a.Id, new EntityEventPayload(a.Id, a.Title));
        await db.SaveChangesAsync(ct);
    }

    private async Task<int?> PublishedNumberAsync(Article a, CancellationToken ct) =>
        a.PublishedVersionId is { } pv ? await db.ArticleVersions.AsNoTracking().Where(v => v.Id == pv).Select(v => (int?)v.VersionNumber).FirstOrDefaultAsync(ct) : null;

    private async Task<Dictionary<Guid, string>> TranslatorNamesAsync(IEnumerable<ArticleTranslation> rows, CancellationToken ct)
    {
        var ids = rows.Where(r => r.TranslatorUserId.HasValue).Select(r => r.TranslatorUserId!.Value).Distinct().ToList();
        return await db.Users.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    private static ArticleTranslationDto Map(ArticleTranslation t, int? published, IReadOnlyDictionary<Guid, string> names) =>
        new(t.Id, t.ArticleId, t.Locale, t.Title, t.Summary, t.BodyHtml, t.Status.ToString(), t.SourceVersionNumber, ArticleService.IsTranslationOutdated(t, published),
            t.MachineTranslated, t.TranslatorUserId, t.TranslatorUserId is { } u && names.TryGetValue(u, out var n) ? n : null, t.PublishedAt, t.UpdatedAt);
}
