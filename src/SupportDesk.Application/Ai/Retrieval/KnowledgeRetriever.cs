using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Ai;
using SupportDesk.Domain.Knowledge;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Ai.Retrieval;

public sealed record RetrievalQuery(
    string Text,
    KnowledgeAudience Audience,
    Guid? CustomerId = null,
    Guid? BrandId = null,
    string? BrandSlug = null,
    IReadOnlyCollection<string>? SourceTypes = null,
    int Take = 5,
    double MinScore = 0.12,
    IReadOnlyCollection<Guid>? ExcludeSourceIds = null,
    string? Locale = null,
    string Feature = AiFeatures.SemanticSearch,
    bool IncludeKnowledgeModuleHits = true);

public sealed record RetrievedChunk(Guid ChunkId, string SourceType, Guid SourceId, string Title, string Text, string? Url, double Score,
    double VectorScore, double LexicalScore, KbVisibility Visibility, string Locale);

/// <summary>
/// Tenant-scoped hybrid retrieval: cosine similarity over stored float32 vectors blended with BM25 lexical scoring,
/// filtered by audience visibility (public / authenticated customer / agent), restricted-article access rules, brand,
/// article publication state and the agent's ticket data scope. Hits from the knowledge module's own search (when that
/// module is installed) are merged in. Chunks of other tenants are never loaded: every query runs under the tenant filter.
/// </summary>
public sealed class KnowledgeRetriever(
    DeskDbContext db,
    RequestContext ctx,
    AiGateway gateway,
    ChunkVectorCache cache,
    IKnowledgeSearch knowledgeSearch,
    ILogger<KnowledgeRetriever> logger)
{
    private const double VectorWeight = 0.75;
    private const double LexicalWeight = 0.25;

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(RetrievalQuery q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q.Text) || !ctx.HasTenant) return [];
        var settings = await gateway.SettingsAsync(ct);
        if (!settings.IsEnabled) return [];
        var text = TextNormalization.Truncate(q.Text, 4000);
        var embedded = await gateway.EmbedAsync([text], q.Feature, ct);
        var queryVector = embedded.Vectors[0];
        var index = await cache.GetAsync(db, ctx.TenantId, embedded.ModelKey, ct);
        var queryTerms = TenantVectorIndex.Terms(text, out _).Keys.ToList();
        var allowed = AllowedVisibilities(q.Audience);
        var types = q.SourceTypes is { Count: > 0 } ? q.SourceTypes.ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        if (q.Audience != KnowledgeAudience.Agent) types = [ChunkSources.Article]; // customers never see ticket-derived knowledge
        var exclude = q.ExcludeSourceIds?.ToHashSet() ?? [];

        // Score every eligible chunk; keep the best chunk per source.
        var best = new Dictionary<(string, Guid), (IndexedChunk Chunk, double Score, double Vec, double Lex)>();
        foreach (var c in index.Chunks)
        {
            if (!allowed.Contains(c.Visibility) || exclude.Contains(c.SourceId)) continue;
            if (types is not null && !types.Contains(c.SourceType)) continue;
            if (c.Vector.Length != queryVector.Length) continue;
            var vec = Math.Max(0, VectorMath.Dot(queryVector, c.Vector));
            var bm25 = queryTerms.Count == 0 ? 0 : index.Bm25(c, queryTerms);
            var lex = bm25 / (bm25 + 4.0);
            var score = VectorWeight * vec + LexicalWeight * lex;
            if (q.Locale is { Length: > 0 } loc && c.Locale.Equals(loc, StringComparison.OrdinalIgnoreCase)) score += 0.02;
            var key = (c.SourceType, c.SourceId);
            if (!best.TryGetValue(key, out var current) || current.Score < score) best[key] = (c, score, vec, lex);
        }
        var candidates = best.Values.Where(x => x.Score >= q.MinScore).OrderByDescending(x => x.Score).Take(Math.Max(q.Take * 4, 20)).ToList();

        // Post-filter against the live sources (publication state, access rules, ticket scope).
        var articleIds = candidates.Where(x => x.Chunk.SourceType == ChunkSources.Article).Select(x => x.Chunk.SourceId).ToList();
        var ticketIds = candidates.Where(x => x.Chunk.SourceType == ChunkSources.Ticket).Select(x => x.Chunk.SourceId).ToList();
        var articles = await AllowedArticlesAsync(articleIds, q, ct);
        var tickets = await AllowedTicketsAsync(ticketIds, q.Audience, ct);
        var kept = candidates.Where(x => x.Chunk.SourceType == ChunkSources.Article ? articles.ContainsKey(x.Chunk.SourceId)
                : x.Chunk.SourceType == ChunkSources.Ticket ? tickets.ContainsKey(x.Chunk.SourceId) : q.Audience == KnowledgeAudience.Agent)
            .Take(q.Take).ToList();
        var chunkIds = kept.Select(x => x.Chunk.Id).ToList();
        var texts = await db.KnowledgeChunks.AsNoTracking().Where(c => chunkIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Title, c.Text }).ToDictionaryAsync(c => c.Id, ct);

        var results = kept.Where(x => texts.ContainsKey(x.Chunk.Id)).Select(x =>
        {
            var row = texts[x.Chunk.Id];
            string? url = x.Chunk.SourceType switch
            {
                ChunkSources.Article => ArticleUrl(q, x.Chunk.SourceId, articles.GetValueOrDefault(x.Chunk.SourceId)),
                ChunkSources.Ticket => $"/tickets/{x.Chunk.SourceId}",
                _ => null
            };
            var title = x.Chunk.SourceType == ChunkSources.Ticket && tickets.TryGetValue(x.Chunk.SourceId, out var number)
                ? $"#{number} {row.Title}" : row.Title;
            return new RetrievedChunk(x.Chunk.Id, x.Chunk.SourceType, x.Chunk.SourceId, title, row.Text, url, Math.Round(x.Score, 4),
                Math.Round(x.Vec, 4), Math.Round(x.Lex, 4), x.Chunk.Visibility, x.Chunk.Locale);
        }).ToList();

        if (q.IncludeKnowledgeModuleHits && (types is null || types.Contains(ChunkSources.Article)))
            results = await MergeKnowledgeModuleHitsAsync(results, q, text, ct);
        return results.OrderByDescending(r => r.Score).Take(q.Take).ToList();
    }

    /// <summary>
    /// Coarse visibility filter on the vector index. Agents may see internal chunks (resolved tickets are internal; their
    /// ticket scope and kb.view_internal for internal articles are enforced in the post-filter).
    /// </summary>
    private static HashSet<KbVisibility> AllowedVisibilities(KnowledgeAudience audience) => audience switch
    {
        KnowledgeAudience.Public => [KbVisibility.Public],
        KnowledgeAudience.AuthenticatedCustomer => [KbVisibility.Public, KbVisibility.Authenticated, KbVisibility.Restricted],
        _ => [KbVisibility.Public, KbVisibility.Authenticated, KbVisibility.Restricted, KbVisibility.Internal]
    };

    private sealed record ArticleInfo(Guid Id, string Slug, KbVisibility Visibility);

    private async Task<Dictionary<Guid, ArticleInfo>> AllowedArticlesAsync(List<Guid> ids, RetrievalQuery q, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var now = db.Clock.UtcNow;
        var rows = await db.Articles.AsNoTracking()
            .Where(a => ids.Contains(a.Id) && a.Status == ArticleStatus.Published && a.PublishedVersionId != null && (a.ExpiresAt == null || a.ExpiresAt > now))
            .Select(a => new { a.Id, a.Slug, a.Visibility, a.BrandIdsJson }).ToListAsync(ct);
        var result = new Dictionary<Guid, ArticleInfo>();
        // Internal articles are for agents with kb.view_internal only.
        var canInternal = q.Audience == KnowledgeAudience.Agent && (ctx.IsSystem || ctx.Has(Permissions.KbViewInternal));
        HashSet<Guid>? restrictedAllowed = null;
        if (q.Audience == KnowledgeAudience.AuthenticatedCustomer && rows.Any(r => r.Visibility == KbVisibility.Restricted))
            restrictedAllowed = await RestrictedAccessAsync(rows.Where(r => r.Visibility == KbVisibility.Restricted).Select(r => r.Id).ToList(), q.CustomerId, ct);
        foreach (var r in rows)
        {
            var visible = r.Visibility switch
            {
                KbVisibility.Public => true,
                KbVisibility.Authenticated => q.Audience != KnowledgeAudience.Public,
                KbVisibility.Restricted => q.Audience == KnowledgeAudience.Agent || restrictedAllowed?.Contains(r.Id) == true,
                KbVisibility.Internal => canInternal,
                _ => false
            };
            if (!visible) continue;
            if (q.BrandId is { } brand && q.Audience != KnowledgeAudience.Agent)
            {
                var brands = Json.GuidList(r.BrandIdsJson);
                if (brands.Count > 0 && !brands.Contains(brand)) continue;
            }
            result[r.Id] = new ArticleInfo(r.Id, r.Slug, r.Visibility);
        }
        return result;
    }

    /// <summary>Restricted articles visible to a customer: any access rule matching the customer, its group, tier or an installed product.</summary>
    private async Task<HashSet<Guid>> RestrictedAccessAsync(List<Guid> articleIds, Guid? customerId, CancellationToken ct)
    {
        if (customerId is not { } cid) return [];
        var customer = await db.Customers.AsNoTracking().Where(c => c.Id == cid).Select(c => new { c.Id, c.CustomerGroupId, c.Tier }).FirstOrDefaultAsync(ct);
        if (customer is null) return [];
        var products = await db.Installations.AsNoTracking().Where(i => i.CustomerId == cid).Select(i => i.ProductId).Distinct().ToListAsync(ct);
        var rules = await db.ArticleAccessRules.AsNoTracking().Where(r => articleIds.Contains(r.ArticleId)).ToListAsync(ct);
        return rules.Where(r => r.CustomerId == customer.Id || (r.CustomerGroupId != null && r.CustomerGroupId == customer.CustomerGroupId) ||
                                (r.CustomerTier != null && r.CustomerTier.Equals(customer.Tier, StringComparison.OrdinalIgnoreCase)) ||
                                (r.ProductId != null && products.Contains(r.ProductId.Value)))
            .Select(r => r.ArticleId).ToHashSet();
    }

    private async Task<Dictionary<Guid, long>> AllowedTicketsAsync(List<Guid> ids, KnowledgeAudience audience, CancellationToken ct)
    {
        if (ids.Count == 0 || audience != KnowledgeAudience.Agent || ctx.IsContact) return [];
        if (!ctx.IsSystem && !ctx.Has(Permissions.TicketsView)) return [];
        return await db.Tickets.AsNoTracking().VisibleTo(ctx, db).Where(t => ids.Contains(t.Id)).Select(t => new { t.Id, t.Number })
            .ToDictionaryAsync(t => t.Id, t => t.Number, ct);
    }

    private static string ArticleUrl(RetrievalQuery q, Guid id, ArticleInfo? info) => q.Audience == KnowledgeAudience.Agent
        ? $"/kb/articles/{id}"
        : q.BrandSlug is { Length: > 0 } slug ? $"/portal/{slug}/kb/{info?.Slug ?? id.ToString()}" : $"/kb/{info?.Slug ?? id.ToString()}";

    private async Task<List<RetrievedChunk>> MergeKnowledgeModuleHitsAsync(List<RetrievedChunk> results, RetrievalQuery q, string text, CancellationToken ct)
    {
        IReadOnlyList<KnowledgeHit> hits;
        try
        {
            hits = await knowledgeSearch.SearchAsync(text, q.Audience, q.CustomerId, q.Take, ct);
        }
        catch (DomainException ex)
        {
            logger.LogDebug("Knowledge search unavailable: {Message}", ex.Message);
            return results;
        }
        if (hits.Count == 0) return results;
        var merged = results.ToList();
        for (var i = 0; i < hits.Count; i++)
        {
            var h = hits[i];
            if (q.ExcludeSourceIds?.Contains(h.SourceId) == true) continue;
            var existing = merged.FindIndex(r => r.SourceId == h.SourceId);
            if (existing >= 0)
            {
                merged[existing] = merged[existing] with { Score = Math.Round(merged[existing].Score + 0.05, 4) };
                continue;
            }
            var visibility = Enum.TryParse<KbVisibility>(h.Visibility, true, out var v) ? v : KbVisibility.Public;
            var score = 0.3 + 0.2 * (1 - (double)i / hits.Count);
            merged.Add(new RetrievedChunk(Guid.Empty, h.SourceType, h.SourceId, h.Title, h.Snippet ?? h.Title, h.Url, Math.Round(score, 4), 0, score,
                visibility, q.Locale ?? "en"));
        }
        return merged;
    }
}
