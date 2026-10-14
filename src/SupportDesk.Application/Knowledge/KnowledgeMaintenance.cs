using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Notifications;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Search;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Knowledge;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Knowledge;

/// <summary>
/// Scheduled review and expiry (every 15 minutes, per tenant): live articles past ExpiresAt are taken off the portal
/// (status Expired) and their owners notified; owners of live articles whose ReviewDueAt has passed get a review
/// reminder (once per due date).
/// </summary>
public sealed class KnowledgeMaintenanceTask(TenantScopeRunner runner, ILogger<KnowledgeMaintenanceTask> logger) : IRecurringTask
{
    public const string TaskName = "kb-maintenance";
    public string Name => TaskName;
    public TimeSpan Interval => TimeSpan.FromMinutes(15);

    public Task RunAsync(CancellationToken ct) =>
        runner.ForEachActiveTenantAsync("Knowledge maintenance", async (sp, tenant, token) =>
        {
            var (expired, reminders) = await sp.GetRequiredService<KnowledgeMaintenance>().RunAsync(token);
            if (expired + reminders > 0)
                logger.LogInformation("Knowledge maintenance for {Tenant}: {Expired} expired, {Reminders} review reminders", tenant.Slug, expired, reminders);
        }, ct);
}

public sealed class KnowledgeMaintenance(DeskDbContext db, ArticleService articles, NotificationService notifications)
{
    public async Task<(int Expired, int Reminders)> RunAsync(CancellationToken ct = default)
    {
        var now = db.Clock.UtcNow;
        var expiring = await db.Articles.Where(a => a.PublishedVersionId != null && a.ExpiresAt != null && a.ExpiresAt <= now).Take(200).ToListAsync(ct);
        foreach (var a in expiring) await articles.ArchiveCoreAsync(a, ArticleStatus.Expired, "Expired automatically", ct);
        if (expiring.Count > 0) await db.SaveChangesAsync(ct);
        foreach (var a in expiring.Where(a => a.OwnerUserId is not null))
            await notifications.NotifyAsync(new NotifyRequest([a.OwnerUserId!.Value], KnowledgeNotificationTypes.ArticleExpired, $"Article expired: {a.Title}",
                "It is no longer shown to customers. Update it and publish again, or leave it archived.", $"/kb/articles/{a.Id}",
                NotificationSeverity.Warning, EntityType: "Article", EntityId: a.Id, DedupKey: $"kb-expired:{a.Id}:{a.ExpiresAt:yyyyMMddHHmm}"), ct);

        var due = await db.Articles.AsNoTracking()
            .Where(a => a.PublishedVersionId != null && a.ReviewDueAt != null && a.ReviewDueAt <= now && a.OwnerUserId != null)
            .Select(a => new { a.Id, a.Title, a.OwnerUserId, a.ReviewDueAt }).Take(500).ToListAsync(ct);
        var reminders = 0;
        foreach (var a in due)
            reminders += await notifications.NotifyAsync(new NotifyRequest([a.OwnerUserId!.Value], KnowledgeNotificationTypes.ReviewDue, $"Review due: {a.Title}",
                "This article is due for its scheduled review. Check it is still accurate, then set the next review date.", $"/kb/articles/{a.Id}",
                EntityType: "Article", EntityId: a.Id, DedupKey: $"kb-review-due:{a.Id}:{a.ReviewDueAt:yyyyMMdd}"), ct);
        return (expiring.Count, reminders);
    }
}

/// <summary>Suggested knowledge articles in the agent workbench context panel, from the ticket's subject, description and product.</summary>
public sealed class KnowledgeContextContributor(DeskDbContext db, RequestContext ctx, KnowledgeSearchService search, KbAccess access) : ITicketContextContributor
{
    public async Task ContributeAsync(Domain.Tickets.Ticket ticket, TicketContextBuilder context, CancellationToken ct)
    {
        if (!ctx.IsAgent && !ctx.IsSystem) return;
        var product = ticket.ProductId is { } p ? await db.Products.AsNoTracking().Where(x => x.Id == p).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
        // Trailing space: no prefix expansion for context queries.
        var query = $"{ticket.Subject} {product} {TextNormalization.Truncate(ticket.DescriptionText, 400)} ";
        var hits = await search.SearchArticlesAsync(query, KbReader.Agent, 10, ct);
        if (hits.Count == 0) return;
        var ids = hits.Select(h => h.ArticleId).ToList();
        var articles = await db.Articles.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        foreach (var h in hits)
        {
            if (!articles.TryGetValue(h.ArticleId, out var a) || !access.AgentCanRead(a)) continue;
            var score = h.Score * (ticket.ProductId is not null && a.ProductId == ticket.ProductId ? 1.3 : 1.0);
            context.Articles.Add(new ArticleSuggestionDto(a.Id, a.Title, a.Summary, a.Visibility.ToString(), Math.Round(score, 3), $"/kb/articles/{a.Id}"));
        }
    }
}
