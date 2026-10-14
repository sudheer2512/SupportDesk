using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Knowledge;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Search;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Knowledge;
using SupportDesk.Contracts.Portal;
using SupportDesk.Domain;
using SupportDesk.Domain.Knowledge;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Portal;

/// <summary>
/// Portal knowledge content: home page, category tree, category pages, article pages (locale with fallback, related
/// articles, attachments, view counts), feedback, search with logging, deflection suggestions, guided troubleshooting
/// and support contacts. Every article goes through <see cref="KbAccess.Readable"/> for the current reader.
/// </summary>
public sealed class PortalKnowledgeService(
    DeskDbContext db,
    PortalContext portal,
    KbAccess access,
    KnowledgeSearchService search,
    KbFeedbackService feedback,
    CommunityService community,
    TroubleshootingService flows,
    FileService files)
{
    /// <summary>Live article with its published title/summary (member-init so EF can keep composing ORDER BY / WHERE on it).</summary>
    private sealed class ArticleRow
    {
        public Guid Id { get; init; }
        public string Slug { get; init; } = "";
        public string Title { get; init; } = "";
        public string? Summary { get; init; }
        public string ArticleType { get; init; } = "";
        public Guid? CategoryId { get; init; }
        public DateTime? PublishedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
        public long ViewCount { get; init; }
        public int HelpfulCount { get; init; }
        public int NotHelpfulCount { get; init; }
        public string TagsJson { get; init; } = "[]";
    }

    private IQueryable<ArticleRow> Rows(IQueryable<Article> readable) =>
        from a in readable
        join v in db.ArticleVersions.AsNoTracking() on a.PublishedVersionId equals (Guid?)v.Id
        select new ArticleRow
        {
            Id = a.Id, Slug = a.Slug, Title = v.Title, Summary = v.Summary, ArticleType = a.ArticleType, CategoryId = a.CategoryId, PublishedAt = a.PublishedAt,
            UpdatedAt = a.UpdatedAt, ViewCount = a.ViewCount, HelpfulCount = a.HelpfulCount, NotHelpfulCount = a.NotHelpfulCount, TagsJson = a.TagsJson
        };

    private async Task<List<PortalArticleSummaryDto>> MapAsync(IReadOnlyList<ArticleRow> rows, IReadOnlyDictionary<Guid, string?>? snippets, CancellationToken ct)
    {
        var categoryIds = rows.Where(r => r.CategoryId.HasValue).Select(r => r.CategoryId!.Value).Distinct().ToList();
        var categories = await db.KbCategories.AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => new { c.Name, c.Slug }, ct);
        return rows.Select(r =>
        {
            var cat = r.CategoryId is { } c && categories.TryGetValue(c, out var x) ? x : null;
            return new PortalArticleSummaryDto(r.Id, r.Slug, r.Title, r.Summary, r.ArticleType, r.CategoryId, cat?.Name, cat?.Slug, r.PublishedAt, r.UpdatedAt,
                r.ViewCount, HelpfulPercent(r.HelpfulCount, r.NotHelpfulCount), Json.StringList(r.TagsJson), snippets?.GetValueOrDefault(r.Id));
        }).ToList();
    }

    private static double? HelpfulPercent(int helpful, int notHelpful) =>
        helpful + notHelpful == 0 ? null : Math.Round(100.0 * helpful / (helpful + notHelpful), 0);

    private void RequireKb()
    {
        if (!portal.Brand.KnowledgeBaseEnabled) throw new NotFoundException("Knowledge base");
    }

    // ───────────────────────────── Home ─────────────────────────────

    public async Task<PortalHomeDto> HomeAsync(PortalTicketService tickets, CancellationToken ct = default)
    {
        var reader = await portal.ReaderAsync(ct);
        var brand = portal.Brand;
        List<PortalCategoryDto> categories = [];
        List<PortalArticleSummaryDto> popular = [], recent = [], announcements = [];
        if (brand.KnowledgeBaseEnabled)
        {
            categories = await CategoryTreeAsync(reader, ct);
            var readable = access.Readable(reader);
            popular = await MapAsync(await Rows(readable).OrderByDescending(a => a.ViewCount).Take(6).ToListAsync(ct), null, ct);
            recent = await MapAsync(await Rows(readable).OrderByDescending(a => a.PublishedAt).Take(6).ToListAsync(ct), null, ct);
            var since = db.Clock.UtcNow.AddDays(-120);
            announcements = await MapAsync(await Rows(readable.Where(a => (a.ArticleType == "ReleaseNote" || a.TagsJson.Contains("\"announcement\"")) && a.PublishedAt >= since))
                .OrderByDescending(a => a.PublishedAt).Take(3).ToListAsync(ct), null, ct);
        }
        var flowList = await flows.PublishedFor(reader).OrderByDescending(f => f.StartedCount).ThenBy(f => f.Name).Take(6)
            .Select(f => new PortalFlowSummaryDto(f.Id, f.Name, f.Description, db.Products.Where(p => p.Id == f.ProductId).Select(p => p.Name).FirstOrDefault()))
            .ToListAsync(ct);
        var topics = brand.CommunityEnabled
            ? (await community.ListAsync(new TopicQuery { PageSize = 5 }, brand.Id, ct)).Items
            : [];
        var stats = portal.SignedIn ? await tickets.StatsAsync(ct) : null;
        return new PortalHomeDto(categories, popular, recent, announcements, flowList, topics, stats);
    }

    // ───────────────────────────── Categories ─────────────────────────────

    public async Task<List<PortalCategoryDto>> CategoriesAsync(CancellationToken ct = default)
    {
        RequireKb();
        return await CategoryTreeAsync(await portal.ReaderAsync(ct), ct);
    }

    private async Task<List<PortalCategoryDto>> CategoryTreeAsync(KbReader reader, CancellationToken ct)
    {
        var cats = await access.ReadableCategories(reader).OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
        var counts = await access.Readable(reader).Where(a => a.CategoryId != null).GroupBy(a => a.CategoryId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var visibleIds = cats.Select(c => c.Id).ToHashSet();
        PortalCategoryDto Build(KbCategory c)
        {
            var children = cats.Where(x => x.ParentId == c.Id).Select(Build).Where(x => x.ArticleCount > 0).ToList();
            var total = counts.GetValueOrDefault(c.Id) + children.Sum(x => x.ArticleCount);
            return new PortalCategoryDto(c.Id, c.ParentId, c.Name, c.Slug, c.Description, c.Icon, total, children);
        }
        return cats.Where(c => c.ParentId is null || !visibleIds.Contains(c.ParentId.Value)).Select(Build).Where(c => c.ArticleCount > 0).ToList();
    }

    public async Task<PortalCategoryPageDto> CategoryAsync(string slug, CancellationToken ct = default)
    {
        RequireKb();
        var reader = await portal.ReaderAsync(ct);
        var tree = await CategoryTreeAsync(reader, ct);
        var flat = Flatten(tree).ToList();
        var category = flat.FirstOrDefault(c => c.Slug == slug) ?? throw new NotFoundException("Category", slug);
        var breadcrumb = new List<PortalCategoryRefDto>();
        for (var c = category; c is not null; c = c.ParentId is { } p ? flat.FirstOrDefault(x => x.Id == p) : null)
            breadcrumb.Insert(0, new PortalCategoryRefDto(c.Id, c.Name, c.Slug));
        var rows = await Rows(access.Readable(reader).Where(a => a.CategoryId == category.Id)).OrderBy(a => a.Title).Take(200).ToListAsync(ct);
        return new PortalCategoryPageDto(category, breadcrumb, await MapAsync(rows, null, ct));
    }

    private static IEnumerable<PortalCategoryDto> Flatten(IEnumerable<PortalCategoryDto> nodes) =>
        nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));

    // ───────────────────────────── Articles ─────────────────────────────

    public async Task<List<PortalArticleSummaryDto>> ArticlesAsync(string? category, string? tag, string? sort, int? limit, CancellationToken ct = default)
    {
        RequireKb();
        var reader = await portal.ReaderAsync(ct);
        var readable = access.Readable(reader);
        if (!string.IsNullOrWhiteSpace(category))
        {
            var cat = await access.ReadableCategories(reader).Where(c => c.Slug == category).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
            if (cat is null) return [];
            readable = readable.Where(a => a.CategoryId == cat);
        }
        if (KbRules.NormalizeTags([tag ?? ""]).FirstOrDefault() is { } t) readable = readable.Where(a => a.TagsJson.Contains("\"" + t + "\""));
        var rows = Rows(readable);
        rows = (sort ?? "popular").ToLowerInvariant() switch
        {
            "recent" => rows.OrderByDescending(a => a.PublishedAt),
            "title" => rows.OrderBy(a => a.Title),
            _ => rows.OrderByDescending(a => a.ViewCount)
        };
        return await MapAsync(await rows.Take(Math.Clamp(limit ?? 20, 1, 100)).ToListAsync(ct), null, ct);
    }

    public async Task<PortalArticleDto> ArticleAsync(string slugOrId, string? locale, CancellationToken ct = default)
    {
        RequireKb();
        var reader = await portal.ReaderAsync(ct);
        var readable = access.Readable(reader);
        var article = Guid.TryParse(slugOrId, out var id)
            ? await readable.FirstOrDefaultAsync(a => a.Id == id, ct)
            : await readable.FirstOrDefaultAsync(a => a.Slug == slugOrId, ct);
        if (article is null) throw new NotFoundException("Article", slugOrId);
        var version = await db.ArticleVersions.AsNoTracking().FirstAsync(v => v.Id == article.PublishedVersionId, ct);
        var translations = await db.ArticleTranslations.AsNoTracking()
            .Where(t => t.ArticleId == article.Id && (t.Status == TranslationStatus.Published || t.Status == TranslationStatus.Outdated)).ToListAsync(ct);
        var requested = KbRules.NormalizeLocale(locale);
        var translation = requested is null || requested == article.PrimaryLocale ? null : translations.FirstOrDefault(t => t.Locale == requested)
                          ?? translations.FirstOrDefault(t => t.Locale.Split('-')[0] == requested.Split('-')[0]);
        var outdated = translation is not null && ArticleService.IsTranslationOutdated(translation, version.VersionNumber);

        var catRef = article.CategoryId is { } cid ? await db.KbCategories.AsNoTracking().Where(c => c.Id == cid).Select(c => new { c.Name, c.Slug }).FirstOrDefaultAsync(ct) : null;
        var breadcrumb = await BreadcrumbAsync(article.CategoryId, ct);
        var relatedIds = Json.GuidList(article.RelatedArticleIdsJson);
        var related = relatedIds.Count == 0 ? [] : await MapAsync(await Rows(readable.Where(a => relatedIds.Contains(a.Id))).Take(6).ToListAsync(ct), null, ct);
        if (related.Count < 3)
        {
            // Fill with articles from the same category.
            var more = await Rows(readable.Where(a => a.Id != article.Id && a.CategoryId == article.CategoryId && !relatedIds.Contains(a.Id)))
                .OrderByDescending(a => a.ViewCount).Take(3 - related.Count).ToListAsync(ct);
            related.AddRange(await MapAsync(more, null, ct));
        }
        var attachments = await db.ArticleAttachments.AsNoTracking().Where(x => x.ArticleId == article.Id).OrderBy(x => x.CreatedAt)
            .Select(x => new PortalAttachmentDto(x.Id, x.FileName, x.ContentType, x.SizeBytes, $"{portal.ApiBase}/kb/articles/{article.Id}/attachments/{x.Id}", x.CreatedAt))
            .ToListAsync(ct);
        var product = article.ProductId is { } pid ? await db.Products.AsNoTracking().Where(p => p.Id == pid).Select(p => p.Name).FirstOrDefaultAsync(ct) : null;
        await feedback.IncrementViewsAsync(article.Id, ct);
        return new PortalArticleDto(
            article.Id, article.Slug, translation?.Title ?? version.Title, translation is null ? version.Summary : translation.Summary, translation?.BodyHtml ?? version.BodyHtml,
            article.ArticleType, translation?.Locale ?? article.PrimaryLocale, article.PrimaryLocale,
            new[] { article.PrimaryLocale }.Concat(translations.Select(t => t.Locale)).Distinct().ToList(), outdated,
            requested is not null && requested != article.PrimaryLocale && translation is null, article.CategoryId, catRef?.Name, catRef?.Slug, breadcrumb,
            Json.StringList(article.TagsJson), product, article.PublishedAt, article.UpdatedAt, article.ViewCount + 1, article.HelpfulCount, article.NotHelpfulCount,
            article.RatingAverage, related, attachments);
    }

    private async Task<List<PortalCategoryRefDto>> BreadcrumbAsync(Guid? categoryId, CancellationToken ct)
    {
        var list = new List<PortalCategoryRefDto>();
        if (categoryId is null) return list;
        var all = await db.KbCategories.AsNoTracking().Select(c => new { c.Id, c.ParentId, c.Name, c.Slug }).ToListAsync(ct);
        var current = all.FirstOrDefault(c => c.Id == categoryId);
        for (var depth = 0; current is not null && depth < 10; depth++)
        {
            list.Insert(0, new PortalCategoryRefDto(current.Id, current.Name, current.Slug));
            current = current.ParentId is { } p ? all.FirstOrDefault(c => c.Id == p) : null;
        }
        return list;
    }

    public async Task SubmitFeedbackAsync(Guid articleId, SubmitArticleFeedbackRequest r, CancellationToken ct = default)
    {
        RequireKb();
        if (!await access.CanReadAsync(articleId, await portal.ReaderAsync(ct), ct)) throw new NotFoundException("Article", articleId);
        await feedback.SubmitAsync(articleId, r, portal.Contact?.Id, null, ct);
    }

    public async Task<(ArticleAttachment Attachment, Stream Content)> OpenAttachmentAsync(Guid articleId, Guid attachmentId, CancellationToken ct = default)
    {
        RequireKb();
        if (!await access.CanReadAsync(articleId, await portal.ReaderAsync(ct), ct)) throw new NotFoundException("Article", articleId);
        var att = await db.ArticleAttachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == attachmentId && x.ArticleId == articleId, ct)
                  ?? throw new NotFoundException("Attachment", attachmentId);
        var (_, stream) = await files.OpenAsync(att.FileId, ct);
        return (att, stream);
    }

    // ───────────────────────────── Search ─────────────────────────────

    public async Task<PortalSearchResultDto> SearchAsync(string? q, CancellationToken ct = default)
    {
        var query = (q ?? "").Trim();
        if (query.Length < 2) return new PortalSearchResultDto(query, [], [], 0);
        if (query.Length > 200) query = query[..200];
        var reader = await portal.ReaderAsync(ct);
        List<PortalArticleSummaryDto> articles = [];
        if (portal.Brand.KnowledgeBaseEnabled)
        {
            var hits = await search.SearchArticlesAsync(query, reader, 25, ct);
            var ids = hits.Select(h => h.ArticleId).ToList();
            var rows = await Rows(access.Readable(reader).Where(a => ids.Contains(a.Id))).ToListAsync(ct);
            var ordered = hits.Select(h => rows.FirstOrDefault(r => r.Id == h.ArticleId)).Where(r => r is not null).Select(r => r!).ToList();
            articles = await MapAsync(ordered, hits.ToDictionary(h => h.ArticleId, h => h.SnippetHtml), ct);
        }
        var topics = portal.Brand.CommunityEnabled ? (await community.ListAsync(new TopicQuery { Q = query, PageSize = 5 }, portal.Brand.Id, ct)).Items : [];
        await feedback.LogSearchAsync(query, articles.Count, "portal", portal.Brand.Id, portal.Contact?.Id, portal.Contact?.PreferredLanguage, ct);
        return new PortalSearchResultDto(query, articles, topics, articles.Count + topics.Count);
    }

    /// <summary>Deflection suggestions while a customer types a ticket (not logged).</summary>
    public async Task<List<PortalArticleSummaryDto>> SuggestAsync(string? q, int limit, CancellationToken ct = default)
    {
        if (!portal.Brand.KnowledgeBaseEnabled || string.IsNullOrWhiteSpace(q) || q.Trim().Length < 3) return [];
        var reader = await portal.ReaderAsync(ct);
        var hits = await search.SearchArticlesAsync(q.Length > 300 ? q[..300] : q, reader, Math.Clamp(limit, 1, 8), ct);
        var ids = hits.Select(h => h.ArticleId).ToList();
        var rows = await Rows(access.Readable(reader).Where(a => ids.Contains(a.Id))).ToListAsync(ct);
        var ordered = hits.Select(h => rows.FirstOrDefault(r => r.Id == h.ArticleId)).Where(r => r is not null).Select(r => r!).ToList();
        return await MapAsync(ordered, hits.ToDictionary(h => h.ArticleId, h => h.SnippetHtml), ct);
    }

    // ───────────────────────────── Guided troubleshooting ─────────────────────────────

    public async Task<List<PortalFlowSummaryDto>> FlowsAsync(CancellationToken ct = default)
    {
        var reader = await portal.ReaderAsync(ct);
        return await flows.PublishedFor(reader).OrderBy(f => f.Name)
            .Select(f => new PortalFlowSummaryDto(f.Id, f.Name, f.Description, db.Products.Where(p => p.Id == f.ProductId).Select(p => p.Name).FirstOrDefault()))
            .ToListAsync(ct);
    }

    public async Task<PortalFlowDto> FlowAsync(Guid id, CancellationToken ct = default)
    {
        var reader = await portal.ReaderAsync(ct);
        var flow = await flows.PublishedFor(reader).FirstOrDefaultAsync(f => f.Id == id, ct) ?? throw new NotFoundException("Guide", id);
        var definition = TroubleshootingService.Parse(flow.DefinitionJson);
        var articleIds = definition.Nodes.Where(n => n.ArticleId is not null).Select(n => n.ArticleId!.Value).Distinct().ToList();
        var rows = articleIds.Count == 0 ? [] : await Rows(access.Readable(reader).Where(a => articleIds.Contains(a.Id))).ToListAsync(ct);
        var product = flow.ProductId is { } pid ? await db.Products.AsNoTracking().Where(p => p.Id == pid).Select(p => p.Name).FirstOrDefaultAsync(ct) : null;
        return new PortalFlowDto(flow.Id, flow.Name, flow.Description, product, definition, await MapAsync(rows, null, ct));
    }

    public async Task RecordFlowUsageAsync(Guid id, FlowUsageRequest r, CancellationToken ct = default) =>
        await flows.RecordUsageAsync(id, await portal.ReaderAsync(ct), r.Outcome, ct);

    // ───────────────────────────── Support contacts ─────────────────────────────

    public async Task<PortalSupportInfoDto> SupportAsync(CancellationToken ct = default)
    {
        PortalPersonDto? manager = null;
        if (portal.Contact?.CustomerId is { } cid)
        {
            var managerId = await db.Customers.AsNoTracking().Where(c => c.Id == cid).Select(c => c.AccountManagerUserId).FirstOrDefaultAsync(ct);
            if (managerId is { } mid)
                manager = await db.Users.AsNoTracking().Where(u => u.Id == mid && u.IsActive)
                    .Select(u => new PortalPersonDto(u.DisplayName, u.Email, u.Phone, db.Agents.Where(a => a.UserId == u.Id).Select(a => a.Title).FirstOrDefault()))
                    .FirstOrDefaultAsync(ct);
        }
        var departments = await db.Departments.AsNoTracking().Where(d => d.IsActive && d.VisibleInPortal).OrderBy(d => d.SortOrder)
            .Select(d => new PortalDepartmentContactDto(d.Name, d.Description, d.Email)).ToListAsync(ct);
        return new PortalSupportInfoDto(portal.Brand.SupportEmail, portal.Brand.SupportPhone, manager, departments);
    }
}
