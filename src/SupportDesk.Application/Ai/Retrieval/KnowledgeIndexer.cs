using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SupportDesk.Domain;
using SupportDesk.Domain.Knowledge;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Ai.Retrieval;

public static class ChunkSources
{
    public const string Article = "Article";
    public const string Ticket = "Ticket";
}

public sealed record BackfillResult(int Articles, int Tickets, int Reembedded, int Removed);

/// <summary>
/// Maintains <see cref="KnowledgeChunk"/> rows: published articles (every published locale) and resolved tickets
/// (problem + resolution + root cause + summary, internal visibility). Chunks are re-embedded only when their content or
/// the tenant's embedding model changes (content hash includes the model key).
/// </summary>
public sealed class KnowledgeIndexer(DeskDbContext db, AiGateway gateway, ILogger<KnowledgeIndexer> logger)
{
    private const int EmbedBatch = 32;

    private sealed record PendingChunk(string Locale, int Index, string Title, string Text);

    public async Task<int> IndexArticleAsync(Guid articleId, CancellationToken ct)
    {
        var settings = await gateway.SettingsAsync(ct);
        if (!settings.IsEnabled) return 0;
        var article = await db.Articles.AsNoTracking().FirstOrDefaultAsync(a => a.Id == articleId, ct);
        var now = db.Clock.UtcNow;
        if (article is null || article.Status != ArticleStatus.Published || article.PublishedVersionId is null || article.ExpiresAt <= now)
            return -await RemoveSourceAsync(ChunkSources.Article, articleId, ct);

        var pending = new List<PendingChunk>();
        var version = await db.ArticleVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == article.PublishedVersionId, ct);
        if (version is not null) AddArticleChunks(pending, version.Locale, version.Title, version.Summary, version.BodyHtml, version.Keywords);
        var translations = await db.ArticleTranslations.AsNoTracking()
            .Where(t => t.ArticleId == articleId && t.Status == TranslationStatus.Published).ToListAsync(ct);
        foreach (var t in translations.Where(t => version is null || !t.Locale.Equals(version.Locale, StringComparison.OrdinalIgnoreCase)))
            AddArticleChunks(pending, t.Locale, t.Title, t.Summary, t.BodyHtml, null);
        return await UpsertAsync(ChunkSources.Article, articleId, article.Visibility, pending, settings.EmbeddingModelKey, ct);
    }

    private static void AddArticleChunks(List<PendingChunk> pending, string locale, string title, string? summary, string bodyHtml, string? keywords)
    {
        var body = TextNormalization.HtmlToText(bodyHtml);
        var text = string.Join("\n\n", new[] { summary, body, keywords is { Length: > 0 } ? "Keywords: " + keywords : null }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var i = pending.Count(p => p.Locale == locale);
        foreach (var chunk in TextChunker.Chunk(text)) pending.Add(new PendingChunk(locale, i++, title, chunk));
        if (i == 0 && title.Length > 0) pending.Add(new PendingChunk(locale, 0, title, title));
    }

    public async Task<int> IndexTicketAsync(Guid ticketId, CancellationToken ct)
    {
        var settings = await gateway.SettingsAsync(ct);
        if (!settings.IsEnabled) return 0;
        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ticketId, ct);
        if (t is null || !t.StatusCategory.IsResolvedOrClosed() || t.MergedIntoTicketId is not null || t.IsSpam)
            return -await RemoveSourceAsync(ChunkSources.Ticket, ticketId, ct);

        var resolution = TextNormalization.HtmlToText(t.ResolutionHtml);
        if (resolution.Length == 0)
        {
            // No explicit resolution: the last public agent reply is the best available description of the fix.
            var lastReply = await db.TicketThreads.AsNoTracking().Where(x => x.TicketId == t.Id && x.Kind == ThreadKind.AgentReply && x.Visibility == Visibility.Public)
                .OrderByDescending(x => x.Seq).Select(x => x.BodyText).FirstOrDefaultAsync(ct);
            resolution = lastReply ?? "";
        }
        var rootCause = t.RootCause is null ? null : await db.LookupValues.AsNoTracking()
            .Where(l => l.Type == "root_cause" && l.Value == t.RootCause).Select(l => l.Label).FirstOrDefaultAsync(ct) ?? t.RootCause;
        if (resolution.Length == 0 && rootCause is null && string.IsNullOrWhiteSpace(t.AiSummary))
            return -await RemoveSourceAsync(ChunkSources.Ticket, ticketId, ct);

        var text = string.Join("\n", new[]
        {
            $"Problem: {TextNormalization.Truncate(t.DescriptionText, 1200)}",
            resolution.Length > 0 ? $"Resolution: {TextNormalization.Truncate(resolution, 1500)}" : null,
            rootCause is null ? null : $"Root cause: {rootCause}",
            t.ResolutionCode is null ? null : $"Resolution code: {t.ResolutionCode}",
            string.IsNullOrWhiteSpace(t.AiSummary) ? null : $"Summary: {TextNormalization.Truncate(t.AiSummary, 600)}"
        }.Where(s => s is not null));
        var pending = TextChunker.Chunk(text, 1400, 120).Select((c, i) => new PendingChunk(t.Language, i, t.Subject, c)).ToList();
        return await UpsertAsync(ChunkSources.Ticket, ticketId, KbVisibility.Internal, pending, settings.EmbeddingModelKey, ct);
    }

    public async Task<int> RemoveSourceAsync(string sourceType, Guid sourceId, CancellationToken ct) =>
        await db.KnowledgeChunks.Where(c => c.SourceType == sourceType && c.SourceId == sourceId).ExecuteDeleteAsync(ct);

    private async Task<int> UpsertAsync(string sourceType, Guid sourceId, KbVisibility visibility, IReadOnlyList<PendingChunk> pending, string modelKey,
        CancellationToken ct)
    {
        var hashes = pending.Select(p => TextNormalization.Sha256Hex($"{modelKey}|{p.Locale}|{p.Title}|{p.Text}")).ToList();
        var existing = await db.KnowledgeChunks.Where(c => c.SourceType == sourceType && c.SourceId == sourceId).ToListAsync(ct);
        if (existing.Count == pending.Count && existing.All(e => e.Visibility == visibility && e.EmbeddingModel == modelKey) &&
            existing.Select(e => e.ContentHash).OrderBy(h => h).SequenceEqual(hashes.OrderBy(h => h)))
            return pending.Count;
        if (pending.Count == 0)
        {
            db.KnowledgeChunks.RemoveRange(existing);
            await db.SaveChangesAsync(ct);
            return 0;
        }
        var vectors = new List<float[]>();
        var dims = 0;
        for (var i = 0; i < pending.Count; i += EmbedBatch)
        {
            var batch = pending.Skip(i).Take(EmbedBatch).Select(p => $"{p.Title}\n{p.Text}").ToList();
            var embedded = await gateway.EmbedAsync(batch, "knowledge_index", ct);
            vectors.AddRange(embedded.Vectors);
            dims = embedded.Dimensions;
        }
        var now = db.Clock.UtcNow;
        db.KnowledgeChunks.RemoveRange(existing);
        for (var i = 0; i < pending.Count; i++)
        {
            var p = pending[i];
            db.KnowledgeChunks.Add(new KnowledgeChunk
            {
                SourceType = sourceType, SourceId = sourceId, Locale = p.Locale, ChunkIndex = p.Index, Title = TextNormalization.Truncate(p.Title, 500),
                Text = p.Text, Visibility = visibility, Embedding = VectorMath.ToBytes(vectors[i]), EmbeddingModel = modelKey, Dimensions = dims,
                ContentHash = hashes[i], UpdatedAt = now
            });
        }
        await db.SaveChangesAsync(ct);
        return pending.Count;
    }

    /// <summary>Re-embeds chunks created with another embedding model (after the tenant switched models).</summary>
    public async Task<int> ReembedStaleAsync(int limit, CancellationToken ct)
    {
        var settings = await gateway.SettingsAsync(ct);
        if (!settings.IsEnabled) return 0;
        var model = settings.EmbeddingModelKey;
        var stale = await db.KnowledgeChunks.Where(c => c.EmbeddingModel != model).OrderBy(c => c.UpdatedAt).Take(limit).ToListAsync(ct);
        var done = 0;
        for (var i = 0; i < stale.Count; i += EmbedBatch)
        {
            var batch = stale.Skip(i).Take(EmbedBatch).ToList();
            var embedded = await gateway.EmbedAsync(batch.Select(c => $"{c.Title}\n{c.Text}").ToList(), "knowledge_index", ct);
            var now = db.Clock.UtcNow;
            for (var j = 0; j < batch.Count; j++)
            {
                var c = batch[j];
                c.Embedding = VectorMath.ToBytes(embedded.Vectors[j]);
                c.EmbeddingModel = model;
                c.Dimensions = embedded.Dimensions;
                c.ContentHash = TextNormalization.Sha256Hex($"{model}|{c.Locale}|{c.Title}|{c.Text}");
                c.UpdatedAt = now;
            }
            await db.SaveChangesAsync(ct);
            done += batch.Count;
        }
        return done;
    }

    /// <summary>Indexes what is missing, removes what is no longer eligible and re-embeds stale chunks (bounded per run).</summary>
    public async Task<BackfillResult> BackfillAsync(int articleLimit, int ticketLimit, int reembedLimit, CancellationToken ct)
    {
        var settings = await gateway.SettingsAsync(ct);
        if (!settings.IsEnabled) return new BackfillResult(0, 0, 0, 0);
        var removed = 0;

        // Chunks whose source is gone or no longer eligible.
        var orphanArticles = await db.KnowledgeChunks.Where(c => c.SourceType == ChunkSources.Article &&
                !db.Articles.Any(a => a.Id == c.SourceId && a.Status == ArticleStatus.Published && a.PublishedVersionId != null))
            .Select(c => c.SourceId).Distinct().OrderBy(id => id).Take(500).ToListAsync(ct);
        foreach (var id in orphanArticles) removed += await RemoveSourceAsync(ChunkSources.Article, id, ct);
        var orphanTickets = await db.KnowledgeChunks.Where(c => c.SourceType == ChunkSources.Ticket &&
                !db.Tickets.Any(t => t.Id == c.SourceId && t.StatusCategory >= StatusCategory.Resolved && t.MergedIntoTicketId == null))
            .Select(c => c.SourceId).Distinct().OrderBy(id => id).Take(500).ToListAsync(ct);
        foreach (var id in orphanTickets) removed += await RemoveSourceAsync(ChunkSources.Ticket, id, ct);

        var articles = 0;
        var articleIds = await db.Articles.AsNoTracking()
            .Where(a => a.Status == ArticleStatus.Published && a.PublishedVersionId != null &&
                        !db.KnowledgeChunks.Any(c => c.SourceType == ChunkSources.Article && c.SourceId == a.Id))
            .OrderByDescending(a => a.PublishedAt).Select(a => a.Id).Take(articleLimit).ToListAsync(ct);
        foreach (var id in articleIds)
        {
            try { if (await IndexArticleAsync(id, ct) > 0) articles++; }
            catch (DomainException ex) { logger.LogWarning("Indexing article {Article} failed: {Message}", id, ex.Message); break; }
        }

        var tickets = 0;
        var since = db.Clock.UtcNow.AddDays(-365);
        var ticketIds = await db.Tickets.AsNoTracking()
            .Where(t => t.StatusCategory >= StatusCategory.Resolved && t.MergedIntoTicketId == null && !t.IsSpam && t.ResolvedAt >= since &&
                        !db.KnowledgeChunks.Any(c => c.SourceType == ChunkSources.Ticket && c.SourceId == t.Id))
            .OrderByDescending(t => t.ResolvedAt).Select(t => t.Id).Take(ticketLimit).ToListAsync(ct);
        foreach (var id in ticketIds)
        {
            try { if (await IndexTicketAsync(id, ct) > 0) tickets++; }
            catch (DomainException ex) { logger.LogWarning("Indexing ticket {Ticket} failed: {Message}", id, ex.Message); break; }
        }

        var reembedded = 0;
        try { reembedded = await ReembedStaleAsync(reembedLimit, ct); }
        catch (DomainException ex) { logger.LogWarning("Re-embedding failed: {Message}", ex.Message); }
        return new BackfillResult(articles, tickets, reembedded, removed);
    }
}
