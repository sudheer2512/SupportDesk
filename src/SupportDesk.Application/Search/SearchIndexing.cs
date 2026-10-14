using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Knowledge;
using SupportDesk.Application.Platform;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Platform;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Search;

/// <summary>
/// Keeps the search index in step with committed changes: ticket.*, contact.*, customer.*, article.* and the
/// knowledge module's internal kb.article_changed / community.topic_changed events. Idempotent (re-indexing the same
/// content is a no-op thanks to content hashes).
/// </summary>
public sealed class SearchOutboxHandler(SearchIndexer indexer) : IOutboxHandler
{
    public string Name => "search-index";
    public int Order => HandlerOrder.Search;

    public bool CanHandle(string eventType) =>
        eventType.StartsWith("ticket.", StringComparison.Ordinal) && eventType != EventTypes.TicketViewed
        || eventType is EventTypes.ContactCreated or EventTypes.ContactUpdated or EventTypes.ContactMerged
            or EventTypes.CustomerCreated or EventTypes.CustomerUpdated
            or EventTypes.ArticlePublished or EventTypes.ArticleUpdated
            or KnowledgeEvents.ArticleChanged or KnowledgeEvents.TopicChanged;

    public async Task HandleAsync(OutboxEvent ev, CancellationToken ct)
    {
        if (ev.TenantId is null) return;
        if (ev.Type.StartsWith("ticket.", StringComparison.Ordinal))
        {
            var p = Json.Deserialize<TicketEventPayload>(ev.PayloadJson);
            var id = p?.TicketId ?? ev.AggregateId;
            if (id is { } ticketId && ticketId != Guid.Empty) await indexer.IndexAsync(SearchTypes.Ticket, ticketId, ct);
            // A merge also changes the source ticket(s).
            if (ev.Type == EventTypes.TicketMerged && p?.Changes is { } changes)
                foreach (var c in changes)
                    if (Guid.TryParse(c.From, out var from)) await indexer.IndexAsync(SearchTypes.Ticket, from, ct);
            return;
        }
        if (ev.AggregateId is not { } aggregate) return;
        var type = ev.Type switch
        {
            EventTypes.ContactCreated or EventTypes.ContactUpdated or EventTypes.ContactMerged => SearchTypes.Contact,
            EventTypes.CustomerCreated or EventTypes.CustomerUpdated => SearchTypes.Customer,
            KnowledgeEvents.TopicChanged => SearchTypes.Topic,
            _ => SearchTypes.Article
        };
        await indexer.IndexAsync(type, aggregate, ct);
        if (ev.Type == EventTypes.ContactMerged && Json.Deserialize<EntityEventPayload>(ev.PayloadJson)?.Changes is { } merged)
            foreach (var c in merged)
                if (Guid.TryParse(c.From, out var from)) await indexer.IndexAsync(SearchTypes.Contact, from, ct);
    }
}

/// <summary>
/// Backfill / reconciliation (every 10 minutes, per tenant): indexes records that have no document or changed since
/// they were indexed (e.g. data imported directly, events processed before this module existed) and removes documents
/// whose record is gone. Bounded batches per run keep each tick short.
/// </summary>
public sealed class SearchBackfillTask(TenantScopeRunner runner, ILogger<SearchBackfillTask> logger) : IRecurringTask
{
    public const string TaskName = "search-backfill";
    public string Name => TaskName;
    public TimeSpan Interval => TimeSpan.FromMinutes(10);

    public Task RunAsync(CancellationToken ct) =>
        runner.ForEachActiveTenantAsync("Search backfill", async (sp, tenant, token) =>
        {
            var n = await sp.GetRequiredService<SearchBackfill>().RunAsync(500, token);
            if (n > 0) logger.LogInformation("Search backfill indexed {Count} records for tenant {Tenant}", n, tenant.Slug);
        }, ct);
}

/// <summary>The per-tenant backfill step (also used by POST /search/reindex and the demo seed).</summary>
public sealed class SearchBackfill(DeskDbContext db, SearchIndexer indexer)
{
    /// <summary>Indexes up to <paramref name="batch"/> stale records per entity type; returns how many were processed.</summary>
    public async Task<int> RunAsync(int batch = 500, CancellationToken ct = default)
    {
        var total = 0;
        var docs = db.SearchDocuments.AsNoTracking();

        var tickets = await db.Tickets.AsNoTracking().Where(t => !t.IsSpam && !docs.Any(d => d.EntityType == SearchTypes.Ticket && d.EntityId == t.Id && d.UpdatedAt >= t.UpdatedAt))
            .OrderByDescending(t => t.UpdatedAt).Select(t => t.Id).Take(batch).ToListAsync(ct);
        foreach (var id in tickets) await indexer.IndexAsync(SearchTypes.Ticket, id, ct);
        total += tickets.Count;

        var articles = await db.Articles.AsNoTracking().Where(a => !docs.Any(d => d.EntityType == SearchTypes.Article && d.EntityId == a.Id && d.UpdatedAt >= a.UpdatedAt))
            .Select(a => a.Id).Take(batch).ToListAsync(ct);
        foreach (var id in articles) await indexer.IndexAsync(SearchTypes.Article, id, ct);
        total += articles.Count;

        var contacts = await db.Contacts.AsNoTracking()
            .Where(c => c.Status != Domain.Customers.ContactStatus.Erased && c.Status != Domain.Customers.ContactStatus.Merged
                        && !docs.Any(d => d.EntityType == SearchTypes.Contact && d.EntityId == c.Id && d.UpdatedAt >= c.UpdatedAt))
            .Select(c => c.Id).Take(batch).ToListAsync(ct);
        foreach (var id in contacts) await indexer.IndexAsync(SearchTypes.Contact, id, ct);
        total += contacts.Count;

        var customers = await db.Customers.AsNoTracking().Where(c => !docs.Any(d => d.EntityType == SearchTypes.Customer && d.EntityId == c.Id && d.UpdatedAt >= c.UpdatedAt))
            .Select(c => c.Id).Take(batch).ToListAsync(ct);
        foreach (var id in customers) await indexer.IndexAsync(SearchTypes.Customer, id, ct);
        total += customers.Count;

        var topics = await db.CommunityTopics.AsNoTracking().Where(t => !docs.Any(d => d.EntityType == SearchTypes.Topic && d.EntityId == t.Id && d.UpdatedAt >= t.UpdatedAt))
            .Select(t => t.Id).Take(batch).ToListAsync(ct);
        foreach (var id in topics) await indexer.IndexAsync(SearchTypes.Topic, id, ct);
        total += topics.Count;

        // Orphans: documents whose record no longer exists (hard/soft deleted, spam).
        var orphans = await docs.Where(d =>
                (d.EntityType == SearchTypes.Ticket && !db.Tickets.Any(t => t.Id == d.EntityId && !t.IsSpam)) ||
                (d.EntityType == SearchTypes.Article && !db.Articles.Any(a => a.Id == d.EntityId)) ||
                (d.EntityType == SearchTypes.Contact && !db.Contacts.Any(c => c.Id == d.EntityId && c.Status != Domain.Customers.ContactStatus.Erased
                                                                            && c.Status != Domain.Customers.ContactStatus.Merged)) ||
                (d.EntityType == SearchTypes.Customer && !db.Customers.Any(c => c.Id == d.EntityId)) ||
                (d.EntityType == SearchTypes.Topic && !db.CommunityTopics.Any(t => t.Id == d.EntityId)))
            .Select(d => new { d.EntityType, d.EntityId }).Take(batch).ToListAsync(ct);
        foreach (var o in orphans) await indexer.RemoveAsync(o.EntityType, o.EntityId, ct);
        return total + orphans.Count;
    }

    /// <summary>Re-indexes everything of the tenant (admin "rebuild"), in batches.</summary>
    public async Task<int> RebuildAsync(CancellationToken ct = default)
    {
        await db.SearchDocuments.ExecuteUpdateAsync(u => u.SetProperty(d => d.UpdatedAt, DateTime.UnixEpoch).SetProperty(d => d.ContentHash, ""), ct);
        var total = 0;
        for (var i = 0; i < 200; i++)
        {
            var n = await RunAsync(500, ct);
            total += n;
            if (n == 0) break;
        }
        return total;
    }
}
