using System.Linq.Expressions;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Ai.Retrieval;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Ai;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Ai;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Ai.Features;

/// <summary>Semantic search over tickets and articles, similar resolved tickets, and duplicate detection (with merge).</summary>
public sealed class AiSearchService(
    DeskDbContext db,
    RequestContext ctx,
    AiGateway gateway,
    KnowledgeRetriever retriever,
    TicketLookups lookups,
    TicketService tickets)
{
    public async Task<SemanticSearchResultDto> SearchAsync(SemanticSearchRequest r, CancellationToken ct)
    {
        ctx.RequireAny(Permissions.TicketsView, Permissions.KbViewInternal);
        var query = (r.Query ?? "").Trim();
        if (query.Length < 2) throw new ValidationException(new Dictionary<string, string> { ["query"] = "Enter at least two characters." });
        await gateway.EnsureAvailableAsync(AiFeatures.SemanticSearch, interactive: true, ct);
        var take = Math.Clamp(r.Take ?? 20, 1, 50);
        var scope = (r.Scope ?? "all").ToLowerInvariant();
        var items = new List<SemanticSearchHitDto>();
        await lookups.LoadAsync(ct);

        if (scope is "all" or "articles" or "tickets")
        {
            var types = scope switch { "articles" => new[] { ChunkSources.Article }, "tickets" => [ChunkSources.Ticket], _ => [ChunkSources.Article, ChunkSources.Ticket] };
            var hits = await retriever.SearchAsync(new RetrievalQuery(query, KnowledgeAudience.Agent, SourceTypes: types, Take: take, MinScore: 0.1), ct);
            var ticketIds = hits.Where(h => h.SourceType == ChunkSources.Ticket).Select(h => h.SourceId).ToList();
            var info = await db.Tickets.AsNoTracking().Where(t => ticketIds.Contains(t.Id)).Select(t => new { t.Id, t.Number, t.StatusId }).ToDictionaryAsync(t => t.Id, ct);
            items.AddRange(hits.Select(h => new SemanticSearchHitDto(h.SourceType, h.SourceId, h.Title, TextNormalization.Truncate(h.Text, 300), h.Url, h.Score,
                info.TryGetValue(h.SourceId, out var t) ? t.Number : null, info.TryGetValue(h.SourceId, out var t2) ? lookups.Status(t2.StatusId) : null,
                h.Visibility.ToString())));
        }
        if (scope is "all" or "tickets" && ctx.Has(Permissions.TicketsView))
        {
            // Open tickets are not in the knowledge index: rank recent and keyword-matching open tickets by embedding similarity.
            var open = await RankOpenTicketsAsync(query, null, take, ct);
            items.AddRange(open.Select(o => new SemanticSearchHitDto("OpenTicket", o.Ticket.Id, $"{lookups.Display(o.Ticket.Number)} {o.Ticket.Subject}",
                TextNormalization.Truncate(o.Ticket.DescriptionText, 300), $"/tickets/{o.Ticket.Id}", Math.Round(o.Score, 4), o.Ticket.Number,
                lookups.Status(o.Ticket.StatusId), "Internal")));
        }
        var settings = await gateway.SettingsAsync(ct);
        return new SemanticSearchResultDto(query, items.OrderByDescending(i => i.Score).Take(take).ToList(), settings.EmbeddingModelKey);
    }

    public sealed record RankedTicket(Ticket Ticket, double Score, double Similarity);

    /// <summary>Embeds candidate open tickets and ranks them against the query (bounded candidate set).</summary>
    public async Task<List<RankedTicket>> RankOpenTicketsAsync(string query, Expression<Func<Ticket, bool>>? extraCandidates, int take, CancellationToken ct,
        Guid? excludeId = null, DateTime? createdFrom = null, DateTime? createdTo = null)
    {
        var words = query.Split([' ', '\n', '\t', ',', '.', ':', ';', '?', '!', '"', '\'', '(', ')', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim().ToLowerInvariant()).Where(w => w.Length >= 4 && TextNormalization.Tokenize(w).Any())
            .Distinct().OrderByDescending(w => w.Length).Take(4).ToList();
        var baseQuery = db.Tickets.AsNoTracking().VisibleTo(ctx, db)
            .Where(t => t.StatusCategory < StatusCategory.Resolved && t.MergedIntoTicketId == null && !t.IsSpam);
        if (excludeId is { } ex) baseQuery = baseQuery.Where(t => t.Id != ex);
        if (createdFrom is { } from) baseQuery = baseQuery.Where(t => t.CreatedAt >= from);
        if (createdTo is { } to) baseQuery = baseQuery.Where(t => t.CreatedAt <= to);
        var candidates = new Dictionary<Guid, Ticket>();
        if (words.Count > 0)
            foreach (var t in await baseQuery.Where(SubjectContainsAny(words)).OrderByDescending(t => t.LastActivityAt).Take(60).ToListAsync(ct)) candidates[t.Id] = t;
        if (extraCandidates is not null)
            foreach (var t in await baseQuery.Where(extraCandidates).OrderByDescending(t => t.LastActivityAt).Take(40).ToListAsync(ct)) candidates[t.Id] = t;
        foreach (var t in await baseQuery.OrderByDescending(t => t.LastActivityAt).Take(30).ToListAsync(ct)) candidates.TryAdd(t.Id, t);
        if (candidates.Count == 0) return [];
        var list = candidates.Values.ToList();
        var texts = new List<string> { query };
        texts.AddRange(list.Select(t => $"{t.Subject}\n{TextNormalization.Truncate(t.DescriptionText, 600)}"));
        var vectors = (await gateway.EmbedAsync(texts, AiFeatures.SemanticSearch, ct)).Vectors;
        var queryTerms = TextNormalization.Tokenize(query).ToHashSet();
        return list.Select((t, i) =>
        {
            var sim = Math.Max(0, VectorMath.Dot(vectors[0], vectors[i + 1]));
            var overlap = queryTerms.Count == 0 ? 0 : TextNormalization.Tokenize(t.Subject).Distinct().Count(queryTerms.Contains) / (double)queryTerms.Count;
            return new RankedTicket(t, 0.8 * sim + 0.2 * overlap, sim);
        }).Where(x => x.Score >= 0.15).OrderByDescending(x => x.Score).Take(take).ToList();
    }

    private static Expression<Func<Ticket, bool>> SubjectContainsAny(IReadOnlyList<string> words)
    {
        var p = Expression.Parameter(typeof(Ticket), "t");
        var subject = Expression.Property(p, nameof(Ticket.Subject));
        var contains = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
        Expression body = Expression.Constant(false);
        foreach (var w in words) body = Expression.OrElse(body, Expression.Call(subject, contains, Expression.Constant(w)));
        return Expression.Lambda<Func<Ticket, bool>>(body, p);
    }

    /// <summary>Similar resolved tickets (knowledge index), used by the workbench panel, replies and root-cause analysis.</summary>
    public async Task<List<SimilarTicketDto>> SimilarResolvedAsync(Guid ticketId, string text, int take, CancellationToken ct, string feature = AiFeatures.SemanticSearch)
    {
        var hits = await retriever.SearchAsync(new RetrievalQuery(TextNormalization.Truncate(text, 3000), KnowledgeAudience.Agent,
            SourceTypes: [ChunkSources.Ticket], Take: take, MinScore: 0.18, ExcludeSourceIds: [ticketId], Feature: feature, IncludeKnowledgeModuleHits: false), ct);
        if (hits.Count == 0) return [];
        await lookups.LoadAsync(ct);
        var ids = hits.Select(h => h.SourceId).ToList();
        var rows = await db.Tickets.AsNoTracking().Where(t => ids.Contains(t.Id))
            .Select(t => new { t.Id, t.Number, t.Subject, t.StatusId, t.StatusCategory, t.ResolutionHtml, t.RootCause, t.CreatedAt, t.ResolvedAt }).ToDictionaryAsync(t => t.Id, ct);
        var labels = await RootCauseLabelsAsync(ct);
        return hits.Where(h => rows.ContainsKey(h.SourceId)).Select(h =>
        {
            var t = rows[h.SourceId];
            return new SimilarTicketDto(t.Id, t.Number, lookups.Display(t.Number), t.Subject, lookups.Status(t.StatusId), t.StatusCategory.ToString(), h.Score,
                TextNormalization.Truncate(TextNormalization.HtmlToText(t.ResolutionHtml) is { Length: > 0 } res ? res : h.Text, 240),
                t.RootCause is null ? null : labels.GetValueOrDefault(t.RootCause, t.RootCause), t.CreatedAt, t.ResolvedAt);
        }).ToList();
    }

    public async Task<List<SimilarTicketDto>> SimilarAsync(Guid ticketId, CancellationToken ct)
    {
        ctx.Require(Permissions.TicketsView);
        await gateway.EnsureAvailableAsync(AiFeatures.SemanticSearch, interactive: true, ct);
        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ticketId, ct) ?? throw new NotFoundException("Ticket", ticketId);
        await tickets.EnsureVisibleAsync(t, ct);
        return await SimilarResolvedAsync(t.Id, $"{t.Subject}\n{t.DescriptionText}", 8, ct);
    }

    public async Task<Dictionary<string, string>> RootCauseLabelsAsync(CancellationToken ct) =>
        await db.LookupValues.AsNoTracking().Where(l => l.Type == "root_cause").GroupBy(l => l.Value).Select(g => new { g.Key, Label = g.First().Label })
            .ToDictionaryAsync(x => x.Key, x => x.Label, ct);

    // ───────────────────────────── Duplicates ─────────────────────────────

    /// <summary>Similar open tickets from the same contact/customer (or with near-identical text) within 14 days.</summary>
    public async Task<DuplicateCheckDto> DuplicatesAsync(Guid ticketId, bool interactive, CancellationToken ct)
    {
        if (interactive) ctx.Require(Permissions.TicketsView);
        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ticketId, ct) ?? throw new NotFoundException("Ticket", ticketId);
        await tickets.EnsureVisibleAsync(t, ct);
        var result = await gateway.RunBuiltinAsync(new AiCall
        {
            Feature = AiFeatures.DuplicateDetection, EntityType = "Ticket", EntityId = t.Id, Interactive = interactive
        }, async () =>
        {
            var candidates = await FindDuplicatesAsync(t, ct);
            return new AiOutput<List<DuplicateTicketCandidateDto>>(candidates, candidates.FirstOrDefault()?.Similarity,
                Logged: candidates.Select(c => new { c.TicketId, c.Number, c.Similarity, c.Reason }));
        }, ct);
        return new DuplicateCheckDto(result.ExecutionId, t.Id, result.Value);
    }

    public async Task<List<DuplicateTicketCandidateDto>> FindDuplicatesAsync(Ticket t, CancellationToken ct)
    {
        await lookups.LoadAsync(ct);
        Expression<Func<Ticket, bool>> sameParty = x => (t.ContactId != null && x.ContactId == t.ContactId) || (t.CustomerId != null && x.CustomerId == t.CustomerId);
        var ranked = await RankOpenTicketsAsync($"{t.Subject}\n{TextNormalization.Truncate(t.DescriptionText, 800)}", sameParty, 20, ct, excludeId: t.Id,
            createdFrom: t.CreatedAt.AddDays(-14), createdTo: t.CreatedAt.AddDays(14));
        var canMerge = ctx.IsSystem || ctx.Has(Permissions.TicketsMerge);
        var list = new List<DuplicateTicketCandidateDto>();
        foreach (var r in ranked)
        {
            var sameContact = t.ContactId is not null && r.Ticket.ContactId == t.ContactId;
            var sameCustomer = t.CustomerId is not null && r.Ticket.CustomerId == t.CustomerId;
            var score = r.Similarity + (sameContact ? 0.1 : sameCustomer ? 0.05 : 0);
            // Different requesters need near-identical text to be called duplicates.
            var threshold = sameContact ? 0.55 : sameCustomer ? 0.62 : 0.8;
            if (score < threshold) continue;
            var reason = $"{Math.Round(r.Similarity * 100)}% similar text" + (sameContact ? ", same contact" : sameCustomer ? ", same customer" : "") +
                         $", created {Math.Abs((r.Ticket.CreatedAt - t.CreatedAt).TotalDays):0.#} days apart";
            list.Add(new DuplicateTicketCandidateDto(r.Ticket.Id, r.Ticket.Number, lookups.Display(r.Ticket.Number), r.Ticket.Subject, lookups.Status(r.Ticket.StatusId),
                r.Ticket.CreatedAt, Math.Round(Math.Min(1, score), 4), sameContact, sameCustomer, reason, canMerge));
        }
        return list.OrderByDescending(x => x.Similarity).Take(5).ToList();
    }

    /// <summary>Merges this ticket into the chosen (surviving) duplicate through the normal merge flow.</summary>
    public async Task<Guid> MergeDuplicateAsync(Guid ticketId, MergeDuplicateRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.TicketsMerge);
        var target = await tickets.MergeAsync(r.TargetTicketId, new MergeTicketsRequest([ticketId], r.Note ?? "Merged as a duplicate suggested by AI"), ct);
        var exec = await db.AiExecutions.AsNoTracking().Where(e => e.Feature == AiFeatures.DuplicateDetection && e.EntityId == ticketId)
            .OrderByDescending(e => e.CreatedAt).Select(e => (Guid?)e.Id).FirstOrDefaultAsync(ct);
        if (exec is { } id) await gateway.MarkOutcomeAsync(id, AiOutcome.Accepted, ct);
        return target.Id;
    }
}

/// <summary>Root-cause assistance, resolution-time prediction, knowledge-article drafts and translation.</summary>
public sealed class AiInsightService(
    DeskDbContext db,
    RequestContext ctx,
    AiGateway gateway,
    AiTicketLoader loader,
    AiSearchService search,
    TicketLookups lookups,
    TicketService tickets,
    IArticleDraftWriter drafts,
    HtmlSanitization html)
{
    // ───────────────────────────── Root cause ─────────────────────────────

    public async Task<RootCauseDto> RootCauseAsync(Guid ticketId, CancellationToken ct)
    {
        ctx.Require(Permissions.TicketsView);
        var (ticket, snapshot) = await loader.LoadAsync(ticketId, internalView: true, ct);
        await gateway.EnsureAvailableAsync(AiFeatures.RootCause, interactive: true, ct);
        var similar = await search.SimilarResolvedAsync(ticket.Id, $"{snapshot.Subject}\n{snapshot.Description}", 12, ct, AiFeatures.RootCause);
        var evidence = new Dictionary<Guid, RootCauseEvidence>();
        var similarIds = similar.Select(s => s.TicketId).ToList();
        var codes = await db.Tickets.AsNoTracking().Where(t => similarIds.Contains(t.Id)).Select(t => new { t.Id, t.RootCause })
            .ToDictionaryAsync(t => t.Id, t => t.RootCause, ct);
        var labels = await search.RootCauseLabelsAsync(ct);
        foreach (var s in similar)
            evidence[s.TicketId] = new RootCauseEvidence(s.TicketId, s.Number, s.Subject, codes.GetValueOrDefault(s.TicketId), s.RootCause, s.ResolutionSnippet, s.Similarity);
        // Same product/category history adds weaker evidence even when wording differs.
        var since = db.Clock.UtcNow.AddDays(-365);
        var sameArea = await db.Tickets.AsNoTracking().VisibleTo(ctx, db)
            .Where(t => t.Id != ticket.Id && t.RootCause != null && t.ResolvedAt >= since && t.CategoryId == ticket.CategoryId &&
                        (ticket.ProductId == null || t.ProductId == ticket.ProductId))
            .OrderByDescending(t => t.ResolvedAt).Take(30)
            .Select(t => new { t.Id, t.Number, t.Subject, t.RootCause, t.ResolutionHtml, t.SubcategoryId }).ToListAsync(ct);
        foreach (var t in sameArea.Where(t => !evidence.ContainsKey(t.Id)))
            evidence[t.Id] = new RootCauseEvidence(t.Id, t.Number, t.Subject, t.RootCause, labels.GetValueOrDefault(t.RootCause!, t.RootCause!),
                TextNormalization.Truncate(TextNormalization.HtmlToText(t.ResolutionHtml), 240), t.SubcategoryId == ticket.SubcategoryId && t.SubcategoryId != null ? 0.3 : 0.2);
        var list = evidence.Values.OrderByDescending(e => e.Similarity).Take(25).ToList();
        var evidenceText = new StringBuilder();
        foreach (var e in list)
            evidenceText.AppendLine($"- id {e.TicketId} (#{e.Number}, similarity {e.Similarity:0.00}) \"{e.Subject}\" root cause: {e.RootCauseLabel ?? "unknown"} ({e.RootCauseCode ?? "-"}); resolution: {e.Resolution}");
        var result = await gateway.CompleteAsync(new AiCall
        {
            Feature = AiFeatures.RootCause, PromptKey = PromptKeys.RootCause, EntityType = "Ticket", EntityId = ticket.Id, Interactive = true,
            Untrusted = new Dictionary<string, string>
            {
                ["ticket"] = AiTicketLoader.RenderMetadata(snapshot) + "\n" + AiTicketLoader.RenderConversation(snapshot, true, 6000),
                ["evidence"] = list.Count == 0 ? "(no similar resolved tickets)" : evidenceText.ToString()
            },
            LocalTask = new RootCauseTask(snapshot, list), MaxTokens = 700
        }, raw => ParseCauses(raw, list), ct);
        await lookups.LoadAsync(ct);
        return new RootCauseDto(result.ExecutionId, ticket.Id, result.Value, list.Count, result.Provider);
    }

    private AiOutput<List<RootCauseCandidateDto>> ParseCauses(string raw, IReadOnlyList<RootCauseEvidence> evidence)
    {
        var json = AiJson.ParseObject(raw);
        var byId = evidence.ToDictionary(e => e.TicketId);
        var causes = new List<RootCauseCandidateDto>();
        if (AiJson.Prop(json, "causes") is { ValueKind: System.Text.Json.JsonValueKind.Array } arr)
        {
            foreach (var c in arr.EnumerateArray().Take(3))
            {
                var cause = AiJson.Str(c, "cause")?.Trim();
                if (string.IsNullOrWhiteSpace(cause)) continue;
                var ids = AiJson.StrList(c, "evidence", 10, 60).Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(byId.ContainsKey).ToList();
                var code = AiJson.Str(c, "code");
                if (code is not null && evidence.All(e => !string.Equals(e.RootCauseCode, code, StringComparison.OrdinalIgnoreCase))) code = null;
                causes.Add(new RootCauseCandidateDto(TextNormalization.Truncate(cause, 200), code, Math.Round(AiJson.Clamp01(AiJson.Num(c, "likelihood"), 0.3), 3),
                    AiJson.Str(c, "explanation") is { } ex ? TextNormalization.Truncate(ex, 400) : null,
                    ids.Select(id => byId[id]).Select(e => new RootCauseEvidenceDto(e.TicketId, e.Number, e.Subject, e.Resolution, Math.Round(e.Similarity, 3))).ToList()));
            }
        }
        var top = causes.FirstOrDefault()?.Likelihood;
        return new AiOutput<List<RootCauseCandidateDto>>(causes, top, causes.SelectMany(c => c.Evidence.Select(e => e.TicketId)).Distinct().ToList(),
            causes.Select(c => new { c.Cause, c.Code, c.Likelihood, evidence = c.Evidence.Select(e => e.TicketId) }));
    }

    // ───────────────────────────── Resolution prediction ─────────────────────────────

    public async Task<ResolutionPredictionDto> PredictAsync(Guid ticketId, bool interactive, CancellationToken ct, bool log = true)
    {
        if (interactive) ctx.Require(Permissions.TicketsView);
        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ticketId, ct) ?? throw new NotFoundException("Ticket", ticketId);
        await tickets.EnsureVisibleAsync(t, ct);
        if (!log)
        {
            if (!await gateway.IsAvailableAsync(AiFeatures.ResolutionPrediction, interactive, ct)) throw new DomainException("ai_feature_disabled", "Resolution prediction is turned off.", 409);
            return await ComputePredictionAsync(t, null, ct);
        }
        var result = await gateway.RunBuiltinAsync(new AiCall
        {
            Feature = AiFeatures.ResolutionPrediction, EntityType = "Ticket", EntityId = t.Id, Interactive = interactive
        }, async () =>
        {
            var p = await ComputePredictionAsync(t, null, ct);
            return new AiOutput<ResolutionPredictionDto>(p, p.Confidence);
        }, ct);
        return result.Value with { ExecutionId = result.ExecutionId };
    }

    private async Task<ResolutionPredictionDto> ComputePredictionAsync(Ticket t, Guid? executionId, CancellationToken ct)
    {
        var since = db.Clock.UtcNow.AddDays(-365);
        var history = db.Tickets.AsNoTracking().Where(x => x.Id != t.Id && x.ResolvedAt != null && x.ResolvedAt >= since && x.MergedIntoTicketId == null && !x.IsSpam);
        var levels = new List<(string Basis, double Weight, IQueryable<Ticket> Query)>();
        if (t.CategoryId is { } cat && t.ProductId is { } prod)
            levels.Add(("same category, priority and product", 1.0, history.Where(x => x.CategoryId == cat && x.Priority == t.Priority && x.ProductId == prod)));
        if (t.CategoryId is { } cat2) levels.Add(("same category and priority", 0.9, history.Where(x => x.CategoryId == cat2 && x.Priority == t.Priority)));
        if (t.ProductId is { } prod2) levels.Add(("same priority and product", 0.8, history.Where(x => x.Priority == t.Priority && x.ProductId == prod2)));
        levels.Add(("same priority", 0.65, history.Where(x => x.Priority == t.Priority)));
        levels.Add(("all resolved tickets", 0.4, history));

        List<int> minutes = [];
        var basis = "no history";
        var weight = 0.0;
        foreach (var (b, w, q) in levels)
        {
            minutes = await q.OrderByDescending(x => x.ResolvedAt).Take(1000)
                .Select(x => EF.Functions.DateDiffMinute(x.CreatedAt, x.ResolvedAt!.Value)).ToListAsync(ct);
            basis = b;
            weight = w;
            if (minutes.Count >= 8) break;
        }
        minutes = minutes.Where(m => m >= 0).OrderBy(m => m).ToList();
        if (minutes.Count == 0)
            return new ResolutionPredictionDto(executionId, t.Id, null, null, null, 0, basis, true, 0, null, "There are no resolved tickets to learn from yet.");
        double Pct(double p)
        {
            var pos = (minutes.Count - 1) * p;
            var lo = (int)Math.Floor(pos);
            var hi = (int)Math.Ceiling(pos);
            return (minutes[lo] + (minutes[hi] - minutes[lo]) * (pos - lo)) / 60.0;
        }
        var median = Pct(0.5);
        var p25 = Pct(0.25);
        var p75 = Pct(0.75);
        var low = minutes.Count < 10 || weight < 0.65;
        if (low)
        {
            // Few comparable tickets: widen the range to reflect the uncertainty.
            p25 *= 0.75;
            p75 *= 1.35;
        }
        var spread = median <= 0 ? 1 : Math.Min(1, (p75 - p25) / (2 * median));
        var confidence = Math.Round(Math.Min(1, minutes.Count / 40.0) * weight * (1 - 0.4 * spread), 3);
        DateTime? eta = t.StatusCategory.IsOpen() ? t.CreatedAt.AddHours(median) : null;
        if (eta < db.Clock.UtcNow) eta = db.Clock.UtcNow.AddHours(Math.Max(1, p75 - median));
        var explanation = $"Based on {minutes.Count} resolved ticket(s) with {basis}: typically resolved in {Hours(median)} (middle half {Hours(p25)}–{Hours(p75)})." +
                          (low ? " Few comparable tickets — treat as a rough estimate." : "");
        return new ResolutionPredictionDto(executionId, t.Id, Math.Round(median, 2), Math.Round(p25, 2), Math.Round(p75, 2), minutes.Count, basis, low, confidence, eta, explanation);
    }

    private static string Hours(double h) => h < 1 ? $"{Math.Max(1, Math.Round(h * 60))} min" : h < 48 ? $"{h:0.#} h" : $"{h / 24:0.#} days";

    // ───────────────────────────── Knowledge drafts ─────────────────────────────

    public async Task<KnowledgeDraftDto> DraftArticleAsync(Guid ticketId, CancellationToken ct)
    {
        ctx.Require(Permissions.KbAuthor);
        var (ticket, snapshot) = await loader.LoadAsync(ticketId, internalView: true, ct);
        var result = await gateway.CompleteAsync(new AiCall
        {
            Feature = AiFeatures.KnowledgeGeneration, PromptKey = PromptKeys.KnowledgeGeneration, EntityType = "Ticket", EntityId = ticket.Id, Interactive = true,
            Untrusted = new Dictionary<string, string> { ["ticket"] = AiTicketLoader.RenderMetadata(snapshot) + "\n" + AiTicketLoader.RenderConversation(snapshot, true, 10_000) },
            LocalTask = new KnowledgeDraftTask(snapshot), MaxTokens = 1500, RestorePii = false
        }, raw =>
        {
            var json = AiJson.ParseObject(raw);
            var title = AiJson.Str(json, "title")?.Trim();
            var body = AiJson.Str(json, "bodyHtml") ?? AiJson.Str(json, "body");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body)) throw new AiOutputException("Missing title or body.");
            var value = (TextNormalization.Truncate(title, 200), AiJson.Str(json, "summary") is { } s ? TextNormalization.Truncate(s, 500) : null, html.Sanitize(body));
            return new AiOutput<(string, string?, string)>(value, null, Logged: new { title = value.Item1, summary = value.Item2, bodyHtml = value.Item3 });
        }, ct);
        var (articleTitle, summary, bodyHtml) = result.Value;
        try
        {
            var articleId = await drafts.CreateDraftAsync(articleTitle, bodyHtml, summary, ticket.Id, ct);
            await gateway.MarkOutcomeAsync(result.ExecutionId, AiOutcome.Accepted, ct);
            return new KnowledgeDraftDto(result.ExecutionId, ticket.Id, articleId, true, articleTitle, summary, bodyHtml, "A draft article was created for review.");
        }
        catch (DomainException ex)
        {
            return new KnowledgeDraftDto(result.ExecutionId, ticket.Id, null, false, articleTitle, summary, bodyHtml,
                $"{ex.Message} The generated draft is shown below so it can be copied into the knowledge base.");
        }
    }

    // ───────────────────────────── Translation ─────────────────────────────

    public async Task<TranslationDto> TranslateAsync(TranslateRequest r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Text)) throw new ValidationException(new Dictionary<string, string> { ["text"] = "Enter text to translate." });
        if (string.IsNullOrWhiteSpace(r.TargetLocale) || r.TargetLocale.Length > 20)
            throw new ValidationException(new Dictionary<string, string> { ["targetLocale"] = "Choose a target language." });
        var text = TextNormalization.Truncate(r.Text, 8000);
        var result = await gateway.CompleteAsync(new AiCall
        {
            Feature = AiFeatures.Translation, PromptKey = PromptKeys.Translation, Interactive = true,
            Values = new Dictionary<string, string> { ["target_locale"] = r.TargetLocale.Trim(), ["source_locale"] = r.SourceLocale?.Trim() ?? "auto-detect" },
            Untrusted = new Dictionary<string, string> { ["text"] = text },
            LocalTask = new TranslationTask(text, r.TargetLocale.Trim(), r.SourceLocale), MaxTokens = Math.Min(4000, text.Length / 2 + 200), Temperature = 0
        }, raw =>
        {
            var json = AiJson.ParseObject(raw);
            var supported = AiJson.Bool(json, "supported") ?? true;
            var translated = AiJson.Str(json, "text");
            if (supported && string.IsNullOrWhiteSpace(translated)) throw new AiOutputException("Missing 'text'.");
            return new AiOutput<(bool, string?, string?)>((supported, translated, AiJson.Str(json, "message")), null);
        }, ct);
        var (ok, output, message) = result.Value;
        return new TranslationDto(result.ExecutionId, ok, ok ? output : null, r.TargetLocale, result.Provider,
            ok ? null : message ?? "Translation is not supported by the built-in provider. Configure an external model in AI settings.");
    }
}
