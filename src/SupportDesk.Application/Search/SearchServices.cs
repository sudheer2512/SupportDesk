using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Knowledge;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Knowledge;
using SupportDesk.Domain;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Knowledge;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Search;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Search;

/// <summary>One article hit after authoritative visibility filtering.</summary>
public sealed record ArticleSearchHit(Guid ArticleId, double Score, string? SnippetHtml, string? SnippetText);

/// <summary>
/// Knowledge search for customers, the portal assistant and the AI module (<see cref="IKnowledgeSearch"/>). Candidates
/// come from the BM25 index; every hit is re-checked against <see cref="KbAccess.Readable"/> (live, visibility, brand,
/// category and Restricted-article rules) so the index can never leak an article.
/// </summary>
public sealed class KnowledgeSearchService(DeskDbContext db, SearchEngine engine, KbAccess access) : IKnowledgeSearch
{
    public async Task<List<ArticleSearchHit>> SearchArticlesAsync(string query, KbReader reader, int take, CancellationToken ct = default)
    {
        var parsed = SearchEngine.Parse(query);
        if (parsed.IsEmpty) return [];
        string[] visibilities = reader.Audience switch
        {
            KnowledgeAudience.Public => [SearchVisibility.Public],
            KnowledgeAudience.AuthenticatedCustomer => [SearchVisibility.Public, SearchVisibility.Authenticated, SearchVisibility.Restricted],
            _ => [SearchVisibility.Public, SearchVisibility.Authenticated, SearchVisibility.Restricted, SearchVisibility.Internal]
        };
        var docs = db.SearchDocuments.AsNoTracking().Where(d => d.EntityType == SearchTypes.Article && visibilities.Contains(d.Visibility));
        var scored = await engine.QueryAsync(parsed, docs, Math.Max(take * 3, 15), ct);
        if (scored.Count == 0) return [];
        var ids = scored.Select(s => s.Document.EntityId).ToList();
        var allowed = (await access.Readable(reader).Where(a => ids.Contains(a.Id)).Select(a => a.Id).ToListAsync(ct)).ToHashSet();
        return scored.Where(s => allowed.Contains(s.Document.EntityId)).Take(take)
            .Select(s => new ArticleSearchHit(s.Document.EntityId, s.Score, SearchEngine.Highlight(s.Document.Snippet, parsed),
                TextNormalization.Truncate(s.Document.Snippet, 300)))
            .ToList();
    }

    public async Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, KnowledgeAudience audience, Guid? customerId, int take, CancellationToken ct)
    {
        var reader = audience switch
        {
            KnowledgeAudience.Agent => KbReader.Agent,
            KnowledgeAudience.AuthenticatedCustomer => await access.ForCustomerAsync(customerId, null, ct),
            _ => KbReader.Anonymous(null)
        };
        var hits = await SearchArticlesAsync(query, reader, Math.Clamp(take, 1, 50), ct);
        if (hits.Count == 0) return [];
        var ids = hits.Select(h => h.ArticleId).ToList();
        var articles = await db.Articles.AsNoTracking().Where(a => ids.Contains(a.Id))
            .Select(a => new { a.Id, a.Slug, a.Visibility, Title = db.ArticleVersions.Where(v => v.Id == a.PublishedVersionId).Select(v => v.Title).FirstOrDefault() ?? a.Title })
            .ToDictionaryAsync(a => a.Id, ct);
        var brandSlug = audience == KnowledgeAudience.Agent ? null
            : await db.Brands.AsNoTracking().OrderByDescending(b => b.IsDefault).Select(b => b.Slug).FirstOrDefaultAsync(ct);
        return hits.Where(h => articles.ContainsKey(h.ArticleId)).Select(h =>
        {
            var a = articles[h.ArticleId];
            var url = audience == KnowledgeAudience.Agent ? $"/kb/articles/{a.Id}" : $"/portal/{brandSlug}/kb/{a.Slug}";
            return new KnowledgeHit("Article", a.Id, a.Title, h.SnippetText, url, h.Score, a.Visibility.ToString());
        }).ToList();
    }
}

/// <summary>
/// Agent global search (GET /api/v1/search): tickets within the agent's data scope, articles (drafts/internal only with
/// knowledge permissions), contacts and customers (customers.view; account-managed scope honoured) and community topics.
/// Permission filters are part of the document query, so ranking only ever sees what the agent may open.
/// </summary>
public sealed class GlobalSearchService(DeskDbContext db, RequestContext ctx, SearchEngine engine, TicketLookups lookups)
{
    public async Task<GlobalSearchResultDto> SearchAsync(string? q, string? types, int limit, CancellationToken ct = default)
    {
        ctx.RequireAgent();
        var sw = Stopwatch.StartNew();
        var parsed = SearchEngine.Parse(q);
        var query = (q ?? "").Trim();
        limit = Math.Clamp(limit, 1, 50);
        var requested = (types ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SearchTypes.Normalize).Where(t => t is not null).Select(t => t!).ToHashSet();
        if (requested.Count == 0) requested = SearchTypes.All.ToHashSet();

        var allowed = new HashSet<string>();
        if (requested.Contains(SearchTypes.Ticket) && ctx.Has(Permissions.TicketsView)) allowed.Add(SearchTypes.Ticket);
        if (requested.Contains(SearchTypes.Article)) allowed.Add(SearchTypes.Article);
        if (requested.Contains(SearchTypes.Topic)) allowed.Add(SearchTypes.Topic);
        if (ctx.Has(Permissions.CustomersView))
        {
            if (requested.Contains(SearchTypes.Contact)) allowed.Add(SearchTypes.Contact);
            if (requested.Contains(SearchTypes.Customer)) allowed.Add(SearchTypes.Customer);
        }
        if (parsed.IsEmpty || allowed.Count == 0) return new GlobalSearchResultDto(query, [], [], (int)sw.ElapsedMilliseconds);

        var docs = db.SearchDocuments.AsNoTracking().Where(d => allowed.Contains(d.EntityType));
        // Tickets: the agent's data scope (department / team / own / watched).
        if (allowed.Contains(SearchTypes.Ticket) && !(ctx.IsSystem || ctx.ActorType == ActorType.ApiClient && ctx.UserId is null))
        {
            var visibleTickets = db.Tickets.VisibleTo(ctx, db).Select(t => t.Id);
            docs = docs.Where(d => d.EntityType != SearchTypes.Ticket || visibleTickets.Contains(d.EntityId));
        }
        // Articles: drafts need editorial permissions, internal needs kb.view_internal (or editorial).
        var editor = KbAccess.EditorialPermissions.Any(ctx.Has);
        var articleVis = new List<string> { SearchVisibility.Public, SearchVisibility.Authenticated, SearchVisibility.Restricted };
        if (editor || ctx.Has(Permissions.KbViewInternal)) articleVis.Add(SearchVisibility.Internal);
        if (editor) articleVis.Add(SearchVisibility.Draft);
        docs = docs.Where(d => d.EntityType != SearchTypes.Article || articleVis.Contains(d.Visibility));
        // Topics: hidden ones only for moderators.
        if (!ctx.Has(Permissions.CommunityModerate)) docs = docs.Where(d => d.EntityType != SearchTypes.Topic || d.Visibility != SearchVisibility.Hidden);
        // Customers/contacts: account-managed scope.
        if (ctx.CustomerScope == CustomerScope.AccountManaged && ctx.UserId is { } uid)
        {
            var managed = db.Customers.Where(c => c.AccountManagerUserId == uid).Select(c => c.Id);
            docs = docs.Where(d => (d.EntityType != SearchTypes.Customer && d.EntityType != SearchTypes.Contact) || (d.CustomerId != null && managed.Contains(d.CustomerId.Value)));
        }

        var scored = await engine.QueryAsync(parsed, docs, 100, ct);
        var counts = scored.GroupBy(s => s.Document.EntityType)
            .Select(g => new CountDto(g.Key, Label(g.Key), g.Count())).OrderByDescending(c => c.Count).ToList();
        var top = scored.Take(limit).ToList();
        var hits = await MapAsync(top, parsed, ct);
        return new GlobalSearchResultDto(query, hits, counts, (int)sw.ElapsedMilliseconds);
    }

    public async Task<SearchIndexStatusDto> StatusAsync(CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.AdminKnowledge, Permissions.AdminSecurity);
        var docs = await db.SearchDocuments.AsNoTracking().GroupBy(d => d.EntityType).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var postings = await db.SearchPostings.AsNoTracking().LongCountAsync(ct);
        var last = await db.SearchDocuments.AsNoTracking().MaxAsync(d => (DateTime?)d.UpdatedAt, ct);
        return new SearchIndexStatusDto(docs.Select(d => new CountDto(d.Key, Label(d.Key), d.Count)).ToList(), postings, last);
    }

    private static string Label(string type) => type switch
    {
        SearchTypes.Ticket => "Tickets",
        SearchTypes.Article => "Articles",
        SearchTypes.Contact => "Contacts",
        SearchTypes.Customer => "Customers",
        SearchTypes.Topic => "Community",
        _ => type
    };

    private async Task<List<SearchHitDto>> MapAsync(IReadOnlyList<ScoredDocument> scored, ParsedQuery parsed, CancellationToken ct)
    {
        IEnumerable<Guid> Ids(string type) => scored.Where(s => s.Document.EntityType == type).Select(s => s.Document.EntityId);
        await lookups.LoadAsync(ct);
        var ticketIds = Ids(SearchTypes.Ticket).ToList();
        var tickets = ticketIds.Count == 0 ? [] : await db.Tickets.AsNoTracking().Where(t => ticketIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Number, t.Subject, t.StatusId, t.CustomerId, t.UpdatedAt }).ToDictionaryAsync(t => t.Id, ct);
        var customerIds = tickets.Values.Where(t => t.CustomerId.HasValue).Select(t => t.CustomerId!.Value).Distinct().ToList();
        var ticketCustomers = customerIds.Count == 0 ? [] : await db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var articleIds = Ids(SearchTypes.Article).ToList();
        var articles = articleIds.Count == 0 ? [] : await db.Articles.AsNoTracking().Where(a => articleIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Title, a.Status, a.Visibility, a.PublishedVersionId, a.UpdatedAt }).ToDictionaryAsync(a => a.Id, ct);
        var contactIds = Ids(SearchTypes.Contact).ToList();
        var contacts = contactIds.Count == 0 ? [] : await db.Contacts.AsNoTracking().Where(c => contactIds.Contains(c.Id))
            .Select(c => new { c.Id, c.FullName, c.Email, Customer = db.Customers.Where(x => x.Id == c.CustomerId).Select(x => x.Name).FirstOrDefault(), c.UpdatedAt })
            .ToDictionaryAsync(c => c.Id, ct);
        var custIds = Ids(SearchTypes.Customer).ToList();
        var customers = custIds.Count == 0 ? [] : await db.Customers.AsNoTracking().Where(c => custIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name, c.Code, c.Tier, c.UpdatedAt }).ToDictionaryAsync(c => c.Id, ct);
        var topicIds = Ids(SearchTypes.Topic).ToList();
        var topics = topicIds.Count == 0 ? [] : await db.CommunityTopics.AsNoTracking().Where(t => topicIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Title, t.Status, t.PostCount, t.LastActivityAt }).ToDictionaryAsync(t => t.Id, ct);

        var list = new List<SearchHitDto>();
        foreach (var s in scored)
        {
            var d = s.Document;
            var snippet = SearchEngine.Highlight(d.Snippet, parsed);
            switch (d.EntityType)
            {
                case SearchTypes.Ticket when tickets.TryGetValue(d.EntityId, out var t):
                    list.Add(new SearchHitDto(d.EntityType, t.Id, $"{lookups.Display(t.Number)} {t.Subject}",
                        t.CustomerId is { } c && ticketCustomers.TryGetValue(c, out var cn) ? cn : null, snippet, s.Score, $"/tickets/{t.Id}", lookups.Status(t.StatusId), t.UpdatedAt));
                    break;
                case SearchTypes.Article when articles.TryGetValue(d.EntityId, out var a):
                    list.Add(new SearchHitDto(d.EntityType, a.Id, d.Title, a.Visibility.ToString(), snippet, s.Score, $"/kb/articles/{a.Id}",
                        a.PublishedVersionId is null ? a.Status.ToString() : a.Status == ArticleStatus.Published ? "Published" : $"Live · {a.Status}", a.UpdatedAt));
                    break;
                case SearchTypes.Contact when contacts.TryGetValue(d.EntityId, out var ct2):
                    list.Add(new SearchHitDto(d.EntityType, ct2.Id, ct2.FullName, string.Join(" · ", new[] { ct2.Customer, ct2.Email }.Where(x => x is not null)),
                        snippet, s.Score, $"/customers/contacts/{ct2.Id}", "Contact", ct2.UpdatedAt));
                    break;
                case SearchTypes.Customer when customers.TryGetValue(d.EntityId, out var cu):
                    list.Add(new SearchHitDto(d.EntityType, cu.Id, cu.Name, cu.Code, snippet, s.Score, $"/customers/{cu.Id}", cu.Tier, cu.UpdatedAt));
                    break;
                case SearchTypes.Topic when topics.TryGetValue(d.EntityId, out var tp):
                    list.Add(new SearchHitDto(d.EntityType, tp.Id, tp.Title, $"{tp.PostCount} replies", snippet, s.Score, $"/kb/community/{tp.Id}",
                        tp.Status.ToString(), tp.LastActivityAt));
                    break;
            }
        }
        return list;
    }
}
