using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Search;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Knowledge;
using SupportDesk.Domain;
using SupportDesk.Domain.Knowledge;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Knowledge;

/// <summary>
/// Reader signals: article ratings and feedback (helpful / not helpful, 1–5 rating, comment) with a review queue,
/// view counts, the customer search log, and the knowledge analytics built from them. Counters are updated with
/// set-based statements so they never conflict with editors.
/// </summary>
public sealed class KbFeedbackService(DeskDbContext db, RequestContext ctx)
{
    // ───────────────────────────── Reader signals ─────────────────────────────

    /// <summary>Records feedback (caller has already checked the reader may see the article). One per contact per article (updated).</summary>
    public async Task SubmitAsync(Guid articleId, SubmitArticleFeedbackRequest r, Guid? contactId, Guid? userId, CancellationToken ct = default)
    {
        if (r.Rating is { } rating && rating is < 1 or > 5)
            throw new ValidationException(new Dictionary<string, string> { ["rating"] = "Rate from 1 to 5." });
        var comment = string.IsNullOrWhiteSpace(r.Comment) ? null : TextNormalization.Truncate(TextNormalization.HtmlToText(r.Comment).Trim(), 2000);
        var locale = KbRules.NormalizeLocale(r.Locale) ?? "en";
        var now = db.Clock.UtcNow;
        ArticleFeedback? existing = null;
        if (contactId is { } cid) existing = await db.ArticleFeedback.FirstOrDefaultAsync(f => f.ArticleId == articleId && f.ContactId == cid, ct);
        else if (userId is { } uid) existing = await db.ArticleFeedback.FirstOrDefaultAsync(f => f.ArticleId == articleId && f.UserId == uid, ct);
        int helpfulDelta = r.Helpful ? 1 : 0, notHelpfulDelta = r.Helpful ? 0 : 1;
        if (existing is not null)
        {
            if (existing.Helpful) helpfulDelta--; else notHelpfulDelta--;
            existing.Helpful = r.Helpful;
            existing.Rating = r.Rating ?? existing.Rating;
            existing.Comment = comment ?? existing.Comment;
            existing.Locale = locale;
            existing.Reviewed = false;
            existing.CreatedAt = now;
        }
        else
        {
            db.ArticleFeedback.Add(new ArticleFeedback
            {
                ArticleId = articleId, Helpful = r.Helpful, Rating = r.Rating, Comment = comment, Locale = locale, ContactId = contactId, UserId = userId, CreatedAt = now
            });
        }
        await db.SaveChangesAsync(ct);
        var ratings = await db.ArticleFeedback.AsNoTracking().Where(f => f.ArticleId == articleId && f.Rating != null)
            .GroupBy(f => f.ArticleId).Select(g => new { Avg = g.Average(f => (double)f.Rating!.Value), Count = g.Count() }).FirstOrDefaultAsync(ct);
        double? avg = ratings is null ? null : Math.Round(ratings.Avg, 2);
        var count = ratings?.Count ?? 0;
        await db.Articles.Where(a => a.Id == articleId).ExecuteUpdateAsync(u => u
            .SetProperty(a => a.HelpfulCount, a => a.HelpfulCount + helpfulDelta)
            .SetProperty(a => a.NotHelpfulCount, a => a.NotHelpfulCount + notHelpfulDelta)
            .SetProperty(a => a.RatingAverage, avg)
            .SetProperty(a => a.RatingCount, count), ct);
    }

    public Task IncrementViewsAsync(Guid articleId, CancellationToken ct = default) =>
        db.Articles.Where(a => a.Id == articleId).ExecuteUpdateAsync(u => u.SetProperty(a => a.ViewCount, a => a.ViewCount + 1), ct);

    public async Task LogSearchAsync(string query, int results, string source, Guid? brandId, Guid? contactId, string? locale, CancellationToken ct = default)
    {
        var normalized = SearchEngine.NormalizeQuery(query);
        if (normalized.Length < 2) return;
        db.Set<KnowledgeSearchLog>().Add(new KnowledgeSearchLog
        {
            Query = TextNormalization.Truncate(query.Trim(), 300), NormalizedQuery = normalized, ResultCount = results, Source = source, BrandId = brandId,
            ContactId = contactId, Locale = KbRules.NormalizeLocale(locale) ?? "en", CreatedAt = db.Clock.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    // ───────────────────────────── Review queue ─────────────────────────────

    public async Task<PageResult<ArticleFeedbackDto>> ListAsync(ArticleFeedbackQuery q, CancellationToken ct = default)
    {
        ctx.RequireAgent();
        ctx.RequireAny(Permissions.KbAuthor, Permissions.KbReview, Permissions.KbPublish);
        var query = db.ArticleFeedback.AsNoTracking().AsQueryable();
        if (q.ArticleId is { } aid) query = query.Where(f => f.ArticleId == aid);
        if (q.Reviewed is { } reviewed) query = query.Where(f => f.Reviewed == reviewed);
        if (q.Helpful is { } helpful) query = query.Where(f => f.Helpful == helpful);
        if (q.WithComments == true) query = query.Where(f => f.Comment != null);
        var total = await query.CountAsync(ct);
        var page = Math.Max(1, q.Page ?? 1);
        var size = Math.Clamp(q.PageSize ?? 25, 1, 200);
        var rows = await query.OrderByDescending(f => f.CreatedAt).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var articleIds = rows.Select(r => r.ArticleId).Distinct().ToList();
        var contactIds = rows.Where(r => r.ContactId.HasValue).Select(r => r.ContactId!.Value).Distinct().ToList();
        var userIds = rows.Where(r => r.UserId.HasValue).Select(r => r.UserId!.Value).Distinct().ToList();
        var titles = await db.Articles.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(a => articleIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Title, ct);
        var contacts = await db.Contacts.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(c => contactIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.FullName, ct);
        var users = await db.Users.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var items = rows.Select(f => new ArticleFeedbackDto(f.Id, f.ArticleId, titles.GetValueOrDefault(f.ArticleId) ?? "(deleted article)", f.Helpful, f.Rating,
            f.Comment, f.Locale, f.ContactId is { } c ? contacts.GetValueOrDefault(c) : f.UserId is { } u ? users.GetValueOrDefault(u) : null,
            f.ContactId is not null ? "Contact" : f.UserId is not null ? "Agent" : "Anonymous", f.Reviewed, f.CreatedAt)).ToList();
        return new PageResult<ArticleFeedbackDto>(items, page, size, total);
    }

    public async Task MarkReviewedAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        ctx.RequireAgent();
        ctx.RequireAny(Permissions.KbAuthor, Permissions.KbReview, Permissions.KbPublish);
        var list = ids.Distinct().Take(500).ToList();
        await db.ArticleFeedback.Where(f => list.Contains(f.Id)).ExecuteUpdateAsync(u => u.SetProperty(f => f.Reviewed, true), ct);
    }

    // ───────────────────────────── Analytics ─────────────────────────────

    public async Task<KbAnalyticsDto> AnalyticsAsync(int days, CancellationToken ct = default)
    {
        ctx.RequireAgent();
        ctx.RequireAny(Permissions.KbReview, Permissions.KbPublish, Permissions.AdminKnowledge, Permissions.AnalyticsTeam);
        days = Math.Clamp(days, 1, 365);
        var now = db.Clock.UtcNow;
        var from = now.Date.AddDays(-(days - 1));
        var articles = db.Articles.AsNoTracking();
        var stats = await articles.GroupBy(_ => 1).Select(g => new
        {
            Total = g.Count(),
            Live = g.Count(a => a.PublishedVersionId != null),
            Drafts = g.Count(a => a.Status == ArticleStatus.Draft),
            InReview = g.Count(a => a.Status == ArticleStatus.InReview),
            Overdue = g.Count(a => a.ReviewDueAt != null && a.ReviewDueAt <= now && a.PublishedVersionId != null),
            Expiring = g.Count(a => a.ExpiresAt != null && a.ExpiresAt > now && a.ExpiresAt <= now.AddDays(14) && a.PublishedVersionId != null),
            Views = g.Sum(a => a.ViewCount),
            Helpful = g.Sum(a => a.HelpfulCount),
            NotHelpful = g.Sum(a => a.NotHelpfulCount),
            RatingSum = g.Sum(a => (a.RatingAverage ?? 0) * a.RatingCount),
            RatingCount = g.Sum(a => a.RatingCount)
        }).FirstOrDefaultAsync(ct);
        var pending = await db.ArticleFeedback.CountAsync(f => !f.Reviewed && (f.Comment != null || !f.Helpful), ct);
        var logs = db.Set<KnowledgeSearchLog>().AsNoTracking().Where(l => l.CreatedAt >= from);
        var searches = await logs.CountAsync(ct);
        var noResults = await logs.CountAsync(l => l.ResultCount == 0, ct);
        var helpfulTotal = stats?.Helpful ?? 0;
        var notHelpfulTotal = stats?.NotHelpful ?? 0;
        var totals = new KbTotalsDto(stats?.Total ?? 0, stats?.Live ?? 0, stats?.Drafts ?? 0, stats?.InReview ?? 0, stats?.Overdue ?? 0, stats?.Expiring ?? 0,
            stats?.Views ?? 0, helpfulTotal, notHelpfulTotal, Percent(helpfulTotal, notHelpfulTotal),
            stats is { RatingCount: > 0 } ? Math.Round(stats.RatingSum / stats.RatingCount, 2) : null, pending, searches, noResults);

        var topViewed = (await articles.Where(a => a.PublishedVersionId != null).OrderByDescending(a => a.ViewCount).Take(10).ToListAsync(ct)).Select(Stat).ToList();
        var leastHelpful = (await articles.Where(a => a.HelpfulCount + a.NotHelpfulCount >= 3)
                .OrderBy(a => (double)a.HelpfulCount / (a.HelpfulCount + a.NotHelpfulCount)).ThenByDescending(a => a.NotHelpfulCount).Take(10).ToListAsync(ct))
            .Select(Stat).ToList();
        var topSearches = (await logs.GroupBy(l => l.NormalizedQuery)
                .Select(g => new { Query = g.Key, Count = g.Count(), Avg = g.Average(l => (double)l.ResultCount), Last = g.Max(l => l.CreatedAt) })
                .OrderByDescending(x => x.Count).ThenByDescending(x => x.Last).Take(15).ToListAsync(ct))
            .Select(x => new KbSearchTermDto(x.Query, x.Count, Math.Round(x.Avg, 1), x.Last)).ToList();
        var gaps = (await logs.Where(l => l.ResultCount == 0).GroupBy(l => l.NormalizedQuery)
                .Select(g => new { Query = g.Key, Count = g.Count(), Last = g.Max(l => l.CreatedAt) })
                .OrderByDescending(x => x.Count).ThenByDescending(x => x.Last).Take(15).ToListAsync(ct))
            .Select(x => new KbSearchTermDto(x.Query, x.Count, 0, x.Last)).ToList();

        var searchDays = await logs.GroupBy(l => l.CreatedAt.Date).Select(g => new { Day = g.Key, Count = g.Count(), None = g.Count(l => l.ResultCount == 0) }).ToListAsync(ct);
        var feedbackDays = await db.ArticleFeedback.AsNoTracking().Where(f => f.CreatedAt >= from).GroupBy(f => f.CreatedAt.Date)
            .Select(g => new { Day = g.Key, Helpful = g.Count(f => f.Helpful), Not = g.Count(f => !f.Helpful) }).ToListAsync(ct);
        var daily = Enumerable.Range(0, days).Select(i => from.AddDays(i)).Select(d =>
        {
            var s = searchDays.FirstOrDefault(x => x.Day == d);
            var f = feedbackDays.FirstOrDefault(x => x.Day == d);
            return new KbDailyPointDto(DateOnly.FromDateTime(d), s?.Count ?? 0, s?.None ?? 0, f?.Helpful ?? 0, f?.Not ?? 0);
        }).ToList();
        var flows = await db.TroubleshootingFlows.AsNoTracking().OrderByDescending(f => f.StartedCount)
            .Select(f => new FlowUsageStatDto(f.Id, f.Name, f.StartedCount, f.ResolvedCount, f.EscalatedCount)).Take(20).ToListAsync(ct);
        return new KbAnalyticsDto(days, totals, topViewed, leastHelpful, topSearches, gaps, daily, flows);
    }

    private static double? Percent(int helpful, int notHelpful) =>
        helpful + notHelpful == 0 ? null : Math.Round(100.0 * helpful / (helpful + notHelpful), 1);

    private static KbArticleStatDto Stat(Article a) =>
        new(a.Id, a.Title, a.ViewCount, a.HelpfulCount, a.NotHelpfulCount, Percent(a.HelpfulCount, a.NotHelpfulCount), a.RatingAverage);
}
