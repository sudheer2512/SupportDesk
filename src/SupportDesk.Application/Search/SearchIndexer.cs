using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Domain;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Knowledge;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Search;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Search;

/// <summary>Entity types held in the tenant search index.</summary>
public static class SearchTypes
{
    public const string Ticket = "Ticket";
    public const string Article = "Article";
    public const string Contact = "Contact";
    public const string Customer = "Customer";
    public const string Topic = "Topic";

    public static readonly string[] All = [Ticket, Article, Contact, Customer, Topic];

    public static string? Normalize(string? type) => All.FirstOrDefault(t => t.Equals(type?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Visibility tags stored on search documents.</summary>
public static class SearchVisibility
{
    public const string Public = "public";
    public const string Authenticated = "authenticated";
    public const string Internal = "internal";
    public const string Restricted = "restricted";
    /// <summary>Knowledge article that is not live (draft / in review / archived / expired).</summary>
    public const string Draft = "draft";
    /// <summary>Moderated (hidden) community topic.</summary>
    public const string Hidden = "hidden";

    public static string For(KbVisibility v) => v switch
    {
        KbVisibility.Public => Public,
        KbVisibility.Authenticated => Authenticated,
        KbVisibility.Restricted => Restricted,
        _ => Internal
    };
}

/// <summary>Text of one record split into weighted fields (title ×3, keywords ×2, body ×1).</summary>
public sealed record IndexContent(
    string Title,
    string? Keywords,
    string Body,
    string Visibility,
    string Locale,
    Guid? ScopeId,
    Guid? CustomerId,
    string? Snippet);

/// <summary>
/// Maintains the tenant's inverted index (<see cref="SearchDocument"/> + <see cref="SearchPosting"/>). Each record is
/// tokenised with <see cref="TextNormalization.Tokenize"/>; a posting's weight is the field-weighted term frequency.
/// Unchanged content (same hash) is not re-indexed. Missing/deleted records are removed from the index.
/// </summary>
public sealed class SearchIndexer(DeskDbContext db)
{
    public const float TitleWeight = 3f;
    public const float KeywordWeight = 2f;
    public const float BodyWeight = 1f;
    private const int MaxBodyChars = 24_000;
    private const int MaxBodyTokens = 3_000;
    private const int MaxSnippetChars = 1_200;

    public async Task IndexAsync(string entityType, Guid id, CancellationToken ct = default)
    {
        var content = entityType switch
        {
            SearchTypes.Ticket => await TicketAsync(id, ct),
            SearchTypes.Article => await ArticleAsync(id, ct),
            SearchTypes.Contact => await ContactAsync(id, ct),
            SearchTypes.Customer => await CustomerAsync(id, ct),
            SearchTypes.Topic => await TopicAsync(id, ct),
            _ => null
        };
        if (content is null) await RemoveAsync(entityType, id, ct);
        else await UpsertAsync(entityType, id, content, ct);
    }

    public async Task RemoveAsync(string entityType, Guid id, CancellationToken ct = default)
    {
        var docId = await db.SearchDocuments.Where(d => d.EntityType == entityType && d.EntityId == id).Select(d => (Guid?)d.Id).FirstOrDefaultAsync(ct);
        if (docId is null) return;
        await db.InTransactionAsync(async () =>
        {
            await db.SearchPostings.Where(p => p.DocumentId == docId).ExecuteDeleteAsync(ct);
            await db.SearchDocuments.Where(d => d.Id == docId).ExecuteDeleteAsync(ct);
        }, ct);
    }

    public async Task UpsertAsync(string entityType, Guid id, IndexContent content, CancellationToken ct = default)
    {
        var now = db.Clock.UtcNow;
        var hash = TextNormalization.Sha256Hex(string.Join("\u001f", entityType, content.Title, content.Keywords, content.Body, content.Visibility,
            content.Locale, content.ScopeId, content.CustomerId, content.Snippet));
        var doc = await db.SearchDocuments.FirstOrDefaultAsync(d => d.EntityType == entityType && d.EntityId == id, ct);
        if (doc is not null && doc.ContentHash == hash)
        {
            doc.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            return;
        }

        var (weights, termCount) = Weigh(content);
        await db.InTransactionAsync(async () =>
        {
            if (doc is null)
            {
                doc = new SearchDocument { EntityType = entityType, EntityId = id };
                db.SearchDocuments.Add(doc);
            }
            else
            {
                await db.SearchPostings.Where(p => p.DocumentId == doc.Id).ExecuteDeleteAsync(ct);
            }
            doc.Title = TextNormalization.Truncate(content.Title, 500);
            doc.Snippet = content.Snippet is null ? null : TextNormalization.Truncate(content.Snippet, MaxSnippetChars);
            doc.Visibility = content.Visibility;
            doc.Locale = content.Locale;
            doc.ScopeId = content.ScopeId;
            doc.CustomerId = content.CustomerId;
            doc.TermCount = termCount;
            doc.ContentHash = hash;
            doc.UpdatedAt = now;
            db.SearchPostings.AddRange(weights.Select(w => new SearchPosting { DocumentId = doc.Id, Term = w.Key, Weight = w.Value }));
            await db.SaveChangesAsync(ct);
        }, ct);
    }

    /// <summary>Field-weighted term frequencies and the (unweighted) document length.</summary>
    public static (Dictionary<string, float> Weights, int TermCount) Weigh(IndexContent c)
    {
        var weights = new Dictionary<string, float>(StringComparer.Ordinal);
        var count = 0;
        void Add(string? text, float factor, int maxTokens)
        {
            var n = 0;
            foreach (var term in TextNormalization.Tokenize(text))
            {
                if (n++ >= maxTokens) break;
                weights[term] = weights.GetValueOrDefault(term) + factor;
                count++;
            }
        }
        Add(c.Title, TitleWeight, 200);
        Add(c.Keywords, KeywordWeight, 600);
        Add(c.Body.Length > MaxBodyChars ? c.Body[..MaxBodyChars] : c.Body, BodyWeight, MaxBodyTokens);
        return (weights, Math.Max(1, count));
    }

    // ───────────────────────────── Content builders ─────────────────────────────

    private async Task<IndexContent?> TicketAsync(Guid id, CancellationToken ct)
    {
        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null || t.IsSpam) return null;
        var customer = t.CustomerId is null ? null : await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(c => c.Id == t.CustomerId).Select(c => c.Name).FirstOrDefaultAsync(ct);
        var contact = t.ContactId is null ? null : await db.Contacts.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(c => c.Id == t.ContactId).Select(c => new { c.FullName, c.Email }).FirstOrDefaultAsync(ct);
        var tags = await db.TicketTags.AsNoTracking().Where(x => x.TicketId == id).Select(x => x.Tag!.Name).ToListAsync(ct);
        var threads = await db.TicketThreads.AsNoTracking().Where(x => x.TicketId == id && x.BodyText != null)
            .OrderBy(x => x.Seq).Select(x => x.BodyText!).Take(60).ToListAsync(ct);
        var body = string.Join("\n", new[] { t.DescriptionText ?? "" }.Concat(threads.Where(b => b != t.DescriptionText)));
        var keywords = string.Join(' ', new[] { t.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), customer, contact?.FullName, contact?.Email }
            .Concat(tags).Where(s => !string.IsNullOrWhiteSpace(s)));
        return new IndexContent(t.Subject, keywords, body, SearchVisibility.Internal, t.Language, t.DepartmentId, t.CustomerId,
            TextNormalization.Truncate(t.DescriptionText ?? threads.FirstOrDefault(), MaxSnippetChars));
    }

    private async Task<IndexContent?> ArticleAsync(Guid id, CancellationToken ct)
    {
        var a = await db.Articles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return null;
        var versionId = a.PublishedVersionId ?? a.CurrentVersionId;
        var v = versionId is null ? null : await db.ArticleVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == versionId, ct);
        var title = v?.Title ?? a.Title;
        var translations = await db.ArticleTranslations.AsNoTracking()
            .Where(x => x.ArticleId == id && (x.Status == TranslationStatus.Published || x.Status == TranslationStatus.Outdated))
            .Select(x => new { x.Title, x.Summary, x.BodyHtml }).ToListAsync(ct);
        var bodyText = TextNormalization.HtmlToText(v?.BodyHtml);
        var keywords = string.Join(' ', new[] { v?.Keywords, string.Join(' ', Json.StringList(a.TagsJson)) }
            .Concat(translations.Select(x => x.Title)).Where(s => !string.IsNullOrWhiteSpace(s)));
        var body = string.Join("\n", new[] { v?.Summary ?? a.Summary, bodyText }
            .Concat(translations.Select(x => x.Summary + "\n" + TextNormalization.HtmlToText(x.BodyHtml))).Where(s => !string.IsNullOrWhiteSpace(s)));
        var live = a.PublishedVersionId is not null && a.Status is not (ArticleStatus.Archived or ArticleStatus.Expired);
        var brands = Json.GuidList(a.BrandIdsJson);
        return new IndexContent(title, keywords, body, live ? SearchVisibility.For(a.Visibility) : SearchVisibility.Draft, a.PrimaryLocale,
            brands.Count > 0 ? brands[0] : null, null, (v?.Summary ?? a.Summary) is { Length: > 0 } s ? s + "\n" + bodyText : bodyText);
    }

    private async Task<IndexContent?> ContactAsync(Guid id, CancellationToken ct)
    {
        var c = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null || c.Status is ContactStatus.Erased or ContactStatus.Merged) return null;
        var customer = c.CustomerId is null ? null : await db.Customers.AsNoTracking().Where(x => x.Id == c.CustomerId).Select(x => x.Name).FirstOrDefaultAsync(ct);
        var keywords = string.Join(' ', new[] { c.Email, c.Phone, c.Mobile, c.WhatsAppNumber, c.PhoneNormalized, c.JobTitle, customer }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        return new IndexContent(c.FullName, keywords, "", SearchVisibility.Internal, c.PreferredLanguage, null, c.CustomerId,
            string.Join(" · ", new[] { c.JobTitle, customer, c.Email }.Where(s => !string.IsNullOrWhiteSpace(s))));
    }

    private async Task<IndexContent?> CustomerAsync(Guid id, CancellationToken ct)
    {
        var c = await db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return null;
        var cities = await db.CustomerLocations.AsNoTracking().Where(l => l.CustomerId == id).Select(l => l.City ?? l.Name).ToListAsync(ct);
        var keywords = string.Join(' ', new[] { c.Code, c.ErpCode, c.Industry, c.Tier, c.Email, c.Phone, c.Website, c.ServiceRegion }
            .Concat(Json.StringList(c.DomainsJson)).Concat(cities).Where(s => !string.IsNullOrWhiteSpace(s)));
        return new IndexContent(c.Name, keywords, c.Notes ?? "", SearchVisibility.Internal, c.PreferredLanguage, null, c.Id,
            string.Join(" · ", new[] { c.Code, c.Tier, c.Industry, cities.FirstOrDefault() }.Where(s => !string.IsNullOrWhiteSpace(s))));
    }

    private async Task<IndexContent?> TopicAsync(Guid id, CancellationToken ct)
    {
        var t = await db.CommunityTopics.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return null;
        var posts = await db.CommunityPosts.AsNoTracking().Where(p => p.TopicId == id && !p.IsHidden).OrderBy(p => p.CreatedAt)
            .Select(p => p.BodyHtml).Take(100).ToListAsync(ct);
        var text = TextNormalization.HtmlToText(t.BodyHtml);
        var body = string.Join("\n", new[] { text }.Concat(posts.Select(TextNormalization.HtmlToText)));
        return new IndexContent(t.Title, string.Join(' ', Json.StringList(t.TagsJson)), body,
            t.Status == TopicStatus.Hidden ? SearchVisibility.Hidden : SearchVisibility.Public, "en", t.BrandId, null, text);
    }
}
