using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Ai.Agents;
using SupportDesk.Application.Ai.Features;
using SupportDesk.Application.Ai.Retrieval;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Ai;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Ai;

// ───────────────────────────── Workbench context panel ─────────────────────────────

/// <summary>
/// Adds AI content to the agent workbench context panel: suggested articles, similar resolved tickets, pending AI
/// proposals, possible duplicates, a resolution-time estimate, the AI summary and a frustration warning. Only cheap,
/// read-only work runs here (no model calls are logged per panel view); failures never break the panel.
/// </summary>
public sealed class AiTicketContextContributor(
    RequestContext ctx,
    AiGateway gateway,
    KnowledgeRetriever retriever,
    AiSearchService search,
    AiInsightService insights,
    AiProposalService proposals,
    ILogger<AiTicketContextContributor> logger) : ITicketContextContributor
{
    public async Task ContributeAsync(Ticket ticket, TicketContextBuilder context, CancellationToken ct)
    {
        var settings = await gateway.SettingsAsync(ct);
        if (!settings.IsEnabled || !(ctx.IsSystem || ctx.Has(Permissions.AiUse))) return;
        var text = $"{ticket.Subject}\n{TextNormalization.Truncate(ticket.DescriptionText, 1500)}";

        if (await gateway.IsAvailableAsync(AiFeatures.SemanticSearch, interactive: true, ct))
        {
            var articles = await retriever.SearchAsync(new RetrievalQuery(text, KnowledgeAudience.Agent, SourceTypes: [ChunkSources.Article], Take: 5, MinScore: 0.2), ct);
            context.Articles.AddRange(articles.Select(a => new ArticleSuggestionDto(a.SourceId, a.Title, TextNormalization.Truncate(a.Text, 200), a.Visibility.ToString(),
                a.Score, a.Url ?? $"/kb/articles/{a.SourceId}")));
            foreach (var s in await search.SimilarResolvedAsync(ticket.Id, text, 3, ct))
                context.AiRecommendations.Add(new AiRecommendationDto("similar_ticket", $"Similar resolved ticket {s.DisplayNumber}: {s.Subject}",
                    s.ResolutionSnippet ?? "", s.Similarity, null, Json.ToElement(new { s.TicketId, s.Number, s.RootCause })));
        }

        var pending = await proposals.ListAsync(ticket.Id, ProposalStatus.Proposed.ToString(), 10, ct);
        foreach (var p in pending)
            context.AiRecommendations.Add(new AiRecommendationDto("proposal", p.ActionLabel, p.Rationale ?? p.ActionLabel, p.Confidence, p.ExecutionId,
                Json.ToElement(new { proposalId = p.Id, p.ActionType, p.Risk, p.RequiresApproval, payload = p.Payload })));

        if (ticket.StatusCategory.IsOpen() && await gateway.IsAvailableAsync(AiFeatures.DuplicateDetection, interactive: true, ct))
        {
            foreach (var d in (await search.FindDuplicatesAsync(ticket, ct)).Take(2))
                context.AiRecommendations.Add(new AiRecommendationDto("duplicate", $"Possible duplicate of {d.DisplayNumber}: {d.Subject}", d.Reason, d.Similarity, null,
                    Json.ToElement(new { d.TicketId, d.Number, d.CanMerge })));
        }

        if (ticket.StatusCategory.IsOpen() && await gateway.IsAvailableAsync(AiFeatures.ResolutionPrediction, interactive: true, ct))
        {
            try
            {
                var p = await insights.PredictAsync(ticket.Id, interactive: true, ct, log: false);
                if (p.MedianHours is not null)
                    context.AiRecommendations.Add(new AiRecommendationDto("resolution_prediction", "Expected resolution time", p.Explanation, p.Confidence, null,
                        Json.ToElement(new { p.MedianHours, p.P25Hours, p.P75Hours, p.SampleSize, p.LowConfidence, p.PredictedResolutionAt })));
            }
            catch (DomainException ex) { logger.LogDebug("Resolution prediction skipped: {Message}", ex.Message); }
        }

        if (!string.IsNullOrWhiteSpace(ticket.AiSummary))
            context.AiRecommendations.Add(new AiRecommendationDto("summary", "AI summary", ticket.AiSummary, null, null,
                Json.ToElement(new { generatedAt = ticket.AiSummaryAt })));
        if (ticket.Sentiment == Sentiment.Frustrated)
            context.AiRecommendations.Add(new AiRecommendationDto("sentiment", "Customer appears frustrated",
                "Recent messages show frustration. Acknowledge the impact, give a concrete next step and consider escalating.", ticket.SentimentScore is { } s ? Math.Abs(s) : null,
                null, Json.ToElement(new { sentiment = ticket.Sentiment.ToString(), score = ticket.SentimentScore })));
    }
}

// ───────────────────────────── Conversation assistant & automation gateway ─────────────────────────────

/// <summary>Chat bot / portal assistant: grounded answers with citations, handing off to a person when unsure.</summary>
public sealed class AiConversationAssistant(DeskDbContext db, RequestContext ctx, AiAssistantService assistant, ILogger<AiConversationAssistant> logger)
    : IConversationAssistant
{
    public async Task<AssistantReply?> ReplyAsync(Guid? conversationId, string customerMessage, string? locale, bool authenticatedCustomer, CancellationToken ct)
    {
        Guid? customerId = null;
        Guid? brandId = null;
        if (conversationId is { } cid && await db.Conversations.AsNoTracking().Where(c => c.Id == cid).Select(c => new { c.ContactId, c.ChannelAccountId }).FirstOrDefaultAsync(ct) is { } conv)
        {
            if (authenticatedCustomer && conv.ContactId is { } contact)
                customerId = await db.Contacts.AsNoTracking().Where(c => c.Id == contact).Select(c => c.CustomerId).FirstOrDefaultAsync(ct);
            brandId = await db.ChannelAccounts.AsNoTracking().Where(a => a.Id == conv.ChannelAccountId).Select(a => a.BrandId).FirstOrDefaultAsync(ct);
        }
        else if (authenticatedCustomer && ctx.ContactId is { } me)
        {
            customerId = await db.Contacts.AsNoTracking().Where(c => c.Id == me).Select(c => c.CustomerId).FirstOrDefaultAsync(ct);
        }
        try
        {
            return await assistant.AnswerAsync(customerMessage, locale, customerId, brandId, null, conversationId, ct);
        }
        catch (DomainException ex)
        {
            // Budget exhausted, provider down, feature off: the conversation goes to a person.
            logger.LogInformation("Conversation assistant unavailable: {Message}", ex.Message);
            return null;
        }
    }
}

/// <summary>AI operations for automation rules (classify/apply, summarise).</summary>
public sealed class AiActionGatewayService(AiGateway gateway, AiTicketLoader loader, AiClassificationService classifier, AiSummaryService summaries)
    : IAiActionGateway
{
    public async Task<string> ClassifyTicketAsync(Guid ticketId, bool apply, CancellationToken ct)
    {
        try
        {
            if (!await gateway.IsAvailableAsync(AiFeatures.Classification, interactive: false, ct)) return "AI classification is turned off.";
            var (ticket, snapshot) = await loader.LoadAsync(ticketId, internalView: false, ct);
            var result = await classifier.ClassifyAsync(ticket, snapshot, interactive: false, AiFeatures.Classification, null, ct);
            var settings = await gateway.SettingsAsync(ct);
            var min = settings.Feature(AiFeatures.Classification).MinConfidence;
            var confident = result.Fields.Where(f => f.Value.Value is not null && f.Value.Confidence >= min).ToDictionary(f => f.Key, f => f.Value.Value);
            var described = string.Join(", ", result.Fields.Select(f => $"{f.Key} {f.Value.Confidence:P0}"));
            if (!apply) return $"Classification suggested ({described}).";
            if (confident.Count == 0) return $"No field reached the {min:P0} confidence threshold ({described}).";
            var (applied, _, _) = await classifier.ApplyValuesAsync(ticketId, confident, "AI classification (automation rule)", result.ExecutionId, ct);
            if (applied.Count > 0) await gateway.MarkOutcomeAsync(result.ExecutionId, AiOutcome.Applied, ct);
            return applied.Count == 0 ? "Classification already up to date." : $"Applied AI classification: {string.Join(", ", applied)}.";
        }
        catch (DomainException ex)
        {
            return $"AI classification failed: {ex.Message}";
        }
    }

    public async Task<string> SummarizeTicketAsync(Guid ticketId, CancellationToken ct)
    {
        try
        {
            var s = await summaries.SummarizeAsync(ticketId, interactive: false, ct);
            return s.Summary;
        }
        catch (DomainException ex)
        {
            return $"AI summary failed: {ex.Message}";
        }
    }
}

// ───────────────────────────── Outbox handlers ─────────────────────────────

/// <summary>Keeps the knowledge index current: published/updated articles and resolved (or reopened/merged/deleted) tickets.</summary>
public sealed class AiKnowledgeIndexHandler(KnowledgeIndexer indexer, AiGateway gateway, ILogger<AiKnowledgeIndexHandler> logger) : IOutboxHandler
{
    public string Name => "ai-knowledge-index";
    public int Order => HandlerOrder.Ai;

    public bool CanHandle(string eventType) => eventType is EventTypes.ArticlePublished or EventTypes.ArticleUpdated or EventTypes.TicketStatusChanged
        or EventTypes.TicketMerged or EventTypes.TicketDeleted;

    public async Task HandleAsync(OutboxEvent ev, CancellationToken ct)
    {
        if (ev.TenantId is null || !(await gateway.SettingsAsync(ct)).IsEnabled) return;
        var id = ev.AggregateId ?? PayloadId(ev.PayloadJson);
        if (id is null) return;
        try
        {
            switch (ev.Type)
            {
                case EventTypes.ArticlePublished or EventTypes.ArticleUpdated:
                    await indexer.IndexArticleAsync(id.Value, ct);
                    break;
                case EventTypes.TicketStatusChanged:
                    await indexer.IndexTicketAsync(id.Value, ct);
                    break;
                default:
                    await indexer.RemoveSourceAsync(ChunkSources.Ticket, id.Value, ct);
                    break;
            }
        }
        catch (DomainException ex) when (ex.Code is "ai_disabled" or "feature_not_in_plan" or "ai_budget_exceeded" or "limit_exceeded")
        {
            // Not retryable now; the backfill task catches up once allowed again.
            logger.LogInformation("Knowledge indexing skipped for {Type} {Id}: {Message}", ev.Type, id, ex.Message);
        }
    }

    private static Guid? PayloadId(string json)
    {
        var o = Json.Object(json);
        foreach (var key in new[] { "ticketId", "articleId", "id" })
            if (o.TryGetValue(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String && Guid.TryParse(v.GetString(), out var g)) return g;
        return null;
    }
}

/// <summary>
/// Per-ticket AI on ticket.created / ticket.customer_replied: sentiment for inbound messages (frustration flag),
/// optional classification auto-apply for empty fields, then the tenant's AI agents. Each step is isolated so one failure
/// (provider down, budget) does not block the others; agents ignore stale (replayed/seeded) events.
/// </summary>
public sealed class AiTicketEventsHandler(
    AiGateway gateway,
    AiSentimentService sentiment,
    AiTicketLoader loader,
    AiClassificationService classifier,
    AiAgentRunner agents,
    DeskDbContext db,
    AiOptions options,
    ILogger<AiTicketEventsHandler> logger) : IOutboxHandler
{
    public const int MaxChainDepth = 4;
    public string Name => "ai-ticket-events";
    public int Order => HandlerOrder.Ai;

    public bool CanHandle(string eventType) => eventType is EventTypes.TicketCreated or EventTypes.TicketCustomerReplied;

    public async Task HandleAsync(OutboxEvent ev, CancellationToken ct)
    {
        if (ev.TenantId is null) return;
        var settings = await gateway.SettingsAsync(ct);
        if (!settings.IsEnabled) return;
        var payload = Json.Deserialize<TicketEventPayload>(ev.PayloadJson);
        var ticketId = payload?.TicketId ?? ev.AggregateId;
        if (ticketId is not { } id || !await db.Tickets.AnyAsync(t => t.Id == id, ct)) return;

        if (await gateway.IsAvailableAsync(AiFeatures.Sentiment, interactive: false, ct))
            await StepAsync("sentiment", () => sentiment.AnalyzeTicketAsync(id, interactive: false, ct));

        var classification = settings.Feature(AiFeatures.Classification);
        if (ev.Type == EventTypes.TicketCreated && classification.AutoApply && await gateway.IsAvailableAsync(AiFeatures.Classification, interactive: false, ct))
            await StepAsync("classification", () => AutoClassifyAsync(id, classification.MinConfidence, ct));

        var fresh = db.Clock.UtcNow - ev.OccurredAt <= TimeSpan.FromMinutes(Math.Max(1, options.AgentMaxEventAgeMinutes));
        if (fresh && ev.ChainDepth < MaxChainDepth)
            await StepAsync("agents", () => agents.RunForTicketAsync(id, ev.Type == EventTypes.TicketCreated ? AgentTriggers.Created : AgentTriggers.CustomerReplied,
                ev.ActorType, ct));
    }

    /// <summary>Auto-apply fills only EMPTY category/product fields above the threshold; it never overrides a person's choice.</summary>
    private async Task<int> AutoClassifyAsync(Guid ticketId, double minConfidence, CancellationToken ct)
    {
        var (ticket, snapshot) = await loader.LoadAsync(ticketId, internalView: false, ct);
        if (ticket.CategoryId is not null && ticket.ProductId is not null) return 0;
        var result = await classifier.ClassifyAsync(ticket, snapshot, interactive: false, AiFeatures.Classification, null, ct);
        var values = new Dictionary<string, string?>();
        if (ticket.CategoryId is null && result.Fields.TryGetValue(ClassificationFields.Category, out var c) && c.Confidence >= minConfidence) values[ClassificationFields.Category] = c.Value;
        if (ticket.ProductId is null && result.Fields.TryGetValue(ClassificationFields.Product, out var p) && p.Confidence >= minConfidence) values[ClassificationFields.Product] = p.Value;
        if (values.Count == 0) return 0;
        var (applied, _, _) = await classifier.ApplyValuesAsync(ticketId, values, "AI classification auto-applied", result.ExecutionId, ct);
        if (applied.Count > 0) await gateway.MarkOutcomeAsync(result.ExecutionId, AiOutcome.Applied, ct);
        return applied.Count;
    }

    private async Task StepAsync<T>(string step, Func<Task<T>> action)
    {
        try { await action(); }
        catch (DomainException ex)
        {
            db.ChangeTracker.Clear();
            logger.LogInformation("AI step {Step} skipped: {Message}", step, ex.Message);
        }
    }
}

// ───────────────────────────── Background work ─────────────────────────────

public static class AiJobTypes
{
    public const string Reembed = "ai.reembed";
}

public sealed record ReembedPayload(string ModelKey);

/// <summary>Re-embeds every chunk after the tenant switched embedding models (bounded per run; the backfill task finishes the rest).</summary>
public sealed class AiReembedJobHandler(KnowledgeIndexer indexer) : JobHandler<ReembedPayload>
{
    public override string Type => AiJobTypes.Reembed;

    protected override async Task ExecuteAsync(ReembedPayload payload, BackgroundJob job, CancellationToken ct)
    {
        for (var i = 0; i < 40; i++)
            if (await indexer.ReembedStaleAsync(500, ct) == 0) return;
    }
}

/// <summary>Every 10 minutes: indexes missing articles/resolved tickets, removes ineligible chunks, re-embeds stale ones.</summary>
public sealed class AiKnowledgeBackfillTask(TenantScopeRunner tenants) : IRecurringTask
{
    public string Name => "ai-knowledge-backfill";
    public TimeSpan Interval => TimeSpan.FromMinutes(10);

    public Task RunAsync(CancellationToken ct) => tenants.ForEachActiveTenantAsync("AI knowledge indexer", async (sp, _, token) =>
    {
        await sp.GetRequiredService<KnowledgeIndexer>().BackfillAsync(50, 200, 500, token);
    }, ct);
}

/// <summary>Every 6 hours: applies the AI execution retention period and expires proposals nobody decided on within 14 days.</summary>
public sealed class AiHousekeepingTask(TenantScopeRunner tenants) : IRecurringTask
{
    public string Name => "ai-housekeeping";
    public TimeSpan Interval => TimeSpan.FromHours(6);

    public Task RunAsync(CancellationToken ct) => tenants.ForEachActiveTenantAsync("AI housekeeping", async (sp, _, token) =>
    {
        var db = sp.GetRequiredService<DeskDbContext>();
        var retention = await db.AiConfigurations.AsNoTracking().OrderBy(c => c.CreatedAt).Select(c => (int?)c.ExecutionRetentionDays).FirstOrDefaultAsync(token) ?? 180;
        var cutoff = db.Clock.UtcNow.AddDays(-Math.Max(7, retention));
        await db.AiExecutions.Where(e => e.CreatedAt < cutoff).ExecuteDeleteAsync(token);
        var stale = db.Clock.UtcNow.AddDays(-14);
        await db.AiActionProposals.Where(p => p.Status == ProposalStatus.Proposed && p.CreatedAt < stale)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, ProposalStatus.Expired), token);
    }, ct);
}
