using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Ai.Retrieval;
using SupportDesk.Application.Common;
using SupportDesk.Application.Notifications;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Ai;
using SupportDesk.Domain;
using SupportDesk.Domain.Ai;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Knowledge;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Ai.Features;

/// <summary>Conversation summary, current status and pending actions → Ticket.AiSummary/AiSummaryAt.</summary>
public sealed class AiSummaryService(DeskDbContext db, RequestContext ctx, AiGateway gateway, AiTicketLoader loader)
{
    public async Task<TicketSummaryDto> SummarizeAsync(Guid ticketId, bool interactive, CancellationToken ct)
    {
        if (interactive) ctx.Require(Permissions.TicketsView);
        var (ticket, snapshot) = await loader.LoadAsync(ticketId, internalView: true, ct);
        var result = await gateway.CompleteAsync(new AiCall
        {
            Feature = AiFeatures.Summarization, PromptKey = PromptKeys.Summarization, EntityType = "Ticket", EntityId = ticket.Id, Interactive = interactive,
            Values = new Dictionary<string, string> { ["metadata"] = AiTicketLoader.RenderMetadata(snapshot) },
            Untrusted = new Dictionary<string, string> { ["conversation"] = AiTicketLoader.RenderConversation(snapshot, includeInternal: true) },
            LocalTask = new SummaryTask(snapshot), MaxTokens = 700
        }, Parse, ct);
        var (summary, status, actions) = result.Value;
        var now = db.Clock.UtcNow;
        var stored = new StringBuilder(summary);
        if (status.Length > 0) stored.Append("\n\nCurrent status: ").Append(status);
        if (actions.Count > 0) stored.Append("\n\nPending actions:\n").Append(string.Join("\n", actions.Select(a => "• " + a)));
        var text = stored.ToString();
        await db.Tickets.Where(t => t.Id == ticket.Id).ExecuteUpdateAsync(u => u.SetProperty(t => t.AiSummary, text).SetProperty(t => t.AiSummaryAt, now), ct);
        return new TicketSummaryDto(result.ExecutionId, ticket.Id, summary, status, actions, now, result.Provider);
    }

    private static AiOutput<(string Summary, string Status, List<string> Actions)> Parse(string raw)
    {
        var json = AiJson.ParseObject(raw);
        var summary = AiJson.Str(json, "summary")?.Trim();
        if (string.IsNullOrWhiteSpace(summary)) throw new AiOutputException("Missing 'summary'.");
        var status = AiJson.Str(json, "currentStatus")?.Trim() ?? AiJson.Str(json, "current_status")?.Trim() ?? "";
        var actions = AiJson.StrList(json, "pendingActions", 8, 300);
        if (actions.Count == 0) actions = AiJson.StrList(json, "pending_actions", 8, 300);
        var value = (TextNormalization.Truncate(summary, 2000), TextNormalization.Truncate(status, 500), actions);
        return new AiOutput<(string, string, List<string>)>(value, null, Logged: new { summary = value.Item1, currentStatus = value.Item2, pendingActions = actions });
    }
}

/// <summary>Per-message sentiment (inbound customer messages) rolled up to the ticket; frustrated customers are flagged.</summary>
public sealed class AiSentimentService(DeskDbContext db, RequestContext ctx, AiGateway gateway, TicketHistory history, NotificationService notifications,
    TicketService tickets)
{
    public static double ScoreOf(Sentiment s) => s switch
    {
        Sentiment.Positive => 0.6, Sentiment.Negative => -0.5, Sentiment.Frustrated => -0.85, _ => 0
    };

    public async Task<SentimentDto> GetAsync(Guid ticketId, CancellationToken ct)
    {
        ctx.Require(Permissions.TicketsView);
        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ticketId, ct) ?? throw new NotFoundException("Ticket", ticketId);
        await tickets.EnsureVisibleAsync(t, ct);
        var messages = await db.TicketThreads.AsNoTracking().Where(x => x.TicketId == ticketId && x.Kind == ThreadKind.CustomerMessage)
            .OrderBy(x => x.Seq).Select(x => new MessageSentimentDto(x.Id, x.Seq, x.Sentiment.ToString(), x.CreatedAt)).ToListAsync(ct);
        return new SentimentDto(t.Id, t.Sentiment.ToString(), t.SentimentScore, t.Sentiment == Sentiment.Frustrated, messages);
    }

    /// <summary>Analyses not-yet-scored inbound messages (at most five) and updates the ticket roll-up. Returns messages analysed.</summary>
    public async Task<int> AnalyzeTicketAsync(Guid ticketId, bool interactive, CancellationToken ct)
    {
        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ticketId, ct) ?? throw new NotFoundException("Ticket", ticketId);
        if (interactive) await tickets.EnsureVisibleAsync(t, ct);
        var pending = await db.TicketThreads.AsNoTracking()
            .Where(x => x.TicketId == ticketId && x.Kind == ThreadKind.CustomerMessage && x.Sentiment == Sentiment.Unknown)
            .OrderByDescending(x => x.Seq).Take(5).ToListAsync(ct);
        var analysed = 0;
        double? latestScore = null;
        Sentiment? latest = null;
        foreach (var thread in pending.OrderBy(x => x.Seq))
        {
            var text = thread.BodyText ?? TextNormalization.HtmlToText(thread.BodyHtml);
            if (string.IsNullOrWhiteSpace(text)) continue;
            var (sentiment, score) = await AnalyzeTextAsync(text, ticketId, interactive, ct);
            await db.TicketThreads.Where(x => x.Id == thread.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.Sentiment, sentiment), ct);
            latest = sentiment;
            latestScore = score;
            analysed++;
        }
        if (analysed == 0 && t.Sentiment == Sentiment.Unknown && !await db.TicketThreads.AnyAsync(x => x.TicketId == ticketId && x.Kind == ThreadKind.CustomerMessage, ct)
            && !string.IsNullOrWhiteSpace(t.DescriptionText))
        {
            // Ticket logged without a customer message (phone, agent-created): score the description itself.
            (latest, latestScore) = await AnalyzeTextAsync(t.Subject + "\n" + t.DescriptionText, ticketId, interactive, ct);
            analysed++;
        }
        if (analysed > 0) await RollUpAsync(t, latest, latestScore, ct);
        return analysed;
    }

    private async Task<(Sentiment, double)> AnalyzeTextAsync(string text, Guid ticketId, bool interactive, CancellationToken ct)
    {
        var result = await gateway.CompleteAsync(new AiCall
        {
            Feature = AiFeatures.Sentiment, PromptKey = PromptKeys.Sentiment, EntityType = "Ticket", EntityId = ticketId, Interactive = interactive,
            Untrusted = new Dictionary<string, string> { ["message"] = TextNormalization.Truncate(text, 4000) },
            LocalTask = new SentimentTask(text), MaxTokens = 150, Temperature = 0
        }, raw =>
        {
            var json = AiJson.ParseObject(raw);
            var score = Math.Clamp(AiJson.Num(json, "score") ?? 0, -1, 1);
            var frustrated = AiJson.Bool(json, "frustrated") ?? false;
            var label = AiJson.Str(json, "sentiment");
            var sentiment = Enum.TryParse<Sentiment>(label, true, out var s) && s != Sentiment.Unknown ? s
                : score <= -0.2 ? Sentiment.Negative : score >= 0.2 ? Sentiment.Positive : Sentiment.Neutral;
            if (frustrated) sentiment = Sentiment.Frustrated;
            var cues = AiJson.StrList(json, "cues", 6, 80);
            return new AiOutput<(Sentiment, double)>((sentiment, score), Math.Abs(score), Logged: new { sentiment = sentiment.ToString(), score, frustrated, cues });
        }, ct);
        return result.Value;
    }

    private async Task RollUpAsync(Ticket t, Sentiment? latest, double? latestScore, CancellationToken ct)
    {
        var recent = await db.TicketThreads.AsNoTracking().Where(x => x.TicketId == t.Id && x.Kind == ThreadKind.CustomerMessage && x.Sentiment != Sentiment.Unknown)
            .OrderByDescending(x => x.Seq).Take(3).Select(x => x.Sentiment).ToListAsync(ct);
        var scores = recent.Select(ScoreOf).ToList();
        if (scores.Count == 0 && latestScore is { } only) scores.Add(only);
        else if (scores.Count > 0 && latestScore is { } ls) scores[0] = ls;
        double[] weights = [1.0, 0.6, 0.35];
        var avg = scores.Count == 0 ? 0 : scores.Select((s, i) => s * weights[i]).Sum() / weights.Take(scores.Count).Sum();
        var negatives = recent.Count(s => s is Sentiment.Negative or Sentiment.Frustrated);
        var rolled = latest == Sentiment.Frustrated || (negatives >= 2 && avg <= -0.45) ? Sentiment.Frustrated
            : avg <= -0.2 ? Sentiment.Negative : avg >= 0.25 ? Sentiment.Positive : Sentiment.Neutral;
        var score = Math.Round(avg, 3);
        await db.Tickets.Where(x => x.Id == t.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.Sentiment, rolled).SetProperty(x => x.SentimentScore, score), ct);
        if (rolled == Sentiment.Frustrated && t.Sentiment != Sentiment.Frustrated)
        {
            history.Record(t, TicketEventTypes.AiAction, "AI flagged the customer as frustrated", [new Domain.Events.FieldChange("sentiment", t.Sentiment.ToString(), rolled.ToString())],
                new { kind = "sentiment", score });
            await db.SaveChangesAsync(ct);
            var recipients = t.AssigneeId is { } a ? [a]
                : await db.AgentDepartments.AsNoTracking().Where(d => d.DepartmentId == t.DepartmentId && d.IsSupervisor).Select(d => d.UserId).ToListAsync(ct);
            await notifications.NotifyAsync(new NotifyRequest(recipients, NotificationTypes.AiProposal, $"#{t.Number}: customer appears frustrated", t.Subject,
                $"/tickets/{t.Id}", NotificationSeverity.Warning, false, "Ticket", t.Id, $"ai-frustrated:{t.Id:N}:{db.Clock.UtcNow:yyyyMMdd}"), ct);
        }
    }
}

public sealed record GroundedReply(Guid ExecutionId, string BodyText, string BodyHtml, IReadOnlyList<CitationDto> Citations, double Confidence, bool Grounded,
    IReadOnlyList<RetrievedSource> Sources, string Provider, IReadOnlyList<string> InjectionFlags);

/// <summary>Suggested replies grounded in published knowledge and similar resolved tickets, with numbered citations.</summary>
public sealed partial class AiReplyService(
    DeskDbContext db,
    RequestContext ctx,
    AiGateway gateway,
    AiTicketLoader loader,
    KnowledgeRetriever retriever,
    HtmlSanitization html)
{
    public async Task<SuggestedReplyDto> SuggestAsync(Guid ticketId, SuggestReplyRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.TicketsView);
        var (ticket, snapshot) = await loader.LoadAsync(ticketId, internalView: true, ct);
        var reply = await ComposeAsync(ticket, snapshot, customerFacingOnly: false, AiFeatures.ReplySuggestion, interactive: true, null, r.Instructions, ct);
        return new SuggestedReplyDto(reply.ExecutionId, ticket.Id, reply.BodyHtml, reply.BodyText, reply.Citations, reply.Confidence, reply.Grounded, reply.Provider);
    }

    /// <summary>
    /// Retrieves sources and composes a reply. Customer-facing-only replies (auto-responder) are grounded exclusively in
    /// published articles visible to the customer; agent suggestions may also use similar resolved tickets.
    /// </summary>
    public async Task<GroundedReply> ComposeAsync(Ticket ticket, TicketSnapshot snapshot, bool customerFacingOnly, string feature, bool interactive,
        Guid? agentId, string? instructions, CancellationToken ct)
    {
        var query = $"{snapshot.Subject}\n{AiTicketLoader.LatestCustomerText(snapshot)}";
        var articleHits = await retriever.SearchAsync(new RetrievalQuery(query,
            customerFacingOnly ? KnowledgeAudience.AuthenticatedCustomer : KnowledgeAudience.Agent, ticket.CustomerId, ticket.BrandId,
            SourceTypes: [ChunkSources.Article], Take: 4, MinScore: 0.15, Feature: feature, Locale: snapshot.Language), ct);
        if (customerFacingOnly) articleHits = articleHits.Where(h => h.Visibility != KbVisibility.Internal).ToList();
        var ticketHits = customerFacingOnly ? [] : await retriever.SearchAsync(new RetrievalQuery(query, KnowledgeAudience.Agent, SourceTypes: [ChunkSources.Ticket],
            Take: 2, MinScore: 0.2, ExcludeSourceIds: [ticket.Id], Feature: feature, IncludeKnowledgeModuleHits: false), ct);
        var sources = articleHits.Concat(ticketHits).Select((h, i) => new RetrievedSource(i + 1, h.SourceType, h.SourceId, h.Title,
            TextNormalization.Truncate(h.Text, 1500), h.Url, h.Score, h.Visibility.ToString())).ToList();
        var agentName = ctx.IsAgent ? ctx.ActorName : null;
        var result = await gateway.CompleteAsync(new AiCall
        {
            Feature = feature, PromptKey = PromptKeys.ReplySuggestion, EntityType = "Ticket", EntityId = ticket.Id, Interactive = interactive, AgentId = agentId,
            RequiresAgentsPlan = feature == AiFeatures.Agents,
            Values = new Dictionary<string, string>
            {
                ["instructions"] = string.IsNullOrWhiteSpace(instructions) ? "(none)" : TextNormalization.Truncate(instructions, 500),
                ["agent"] = agentName ?? "Support team"
            },
            Untrusted = new Dictionary<string, string>
            {
                ["ticket"] = AiTicketLoader.RenderMetadata(snapshot) + "\n\n" + AiTicketLoader.RenderConversation(snapshot, includeInternal: false, 8000),
                ["sources"] = RenderSources(sources)
            },
            LocalTask = new ReplyTask(snapshot, sources, agentName, instructions, customerFacingOnly), MaxTokens = 900, Temperature = 0.3
        }, raw => ParseReply(raw, sources), ct);
        var (body, cited, confidence, grounded) = result.Value;
        var citations = cited.Select(i => sources[i - 1]).Select(s => new CitationDto(s.Index, s.SourceType, s.SourceId, s.Title, s.Url,
            TextNormalization.Truncate(s.Text, 240))).ToList();
        var bodyHtml = html.Sanitize(TextNormalization.TextToHtml(body));
        return new GroundedReply(result.ExecutionId, body, bodyHtml, citations, confidence, grounded && citations.Count > 0, sources, result.Provider, result.InjectionFlags);
    }

    public static string RenderSources(IReadOnlyList<RetrievedSource> sources)
    {
        if (sources.Count == 0) return "(no relevant sources were found)";
        var sb = new StringBuilder();
        foreach (var s in sources)
        {
            sb.AppendLine($"[{s.Index}] {s.SourceType} ({s.Visibility}): {s.Title}");
            sb.AppendLine(s.Text);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Validates citations: only numbers of provided sources survive; stray [n] markers are removed from the text.</summary>
    public static AiOutput<(string Body, List<int> Citations, double Confidence, bool Grounded)> ParseReply(string raw, IReadOnlyList<RetrievedSource> sources,
        string bodyField = "body")
    {
        var json = AiJson.ParseObject(raw);
        var body = AiJson.Str(json, bodyField)?.Trim();
        if (string.IsNullOrWhiteSpace(body)) throw new AiOutputException($"Missing '{bodyField}'.");
        var valid = sources.Select(s => s.Index).ToHashSet();
        body = CitationMarker().Replace(body, m => int.TryParse(m.Groups[1].Value, out var n) && valid.Contains(n) ? m.Value : "");
        var inText = CitationMarker().Matches(body).Select(m => int.Parse(m.Groups[1].Value)).ToList();
        var cited = AiJson.IntList(json, "citations").Concat(inText).Where(valid.Contains).Distinct().OrderBy(i => i).ToList();
        var confidence = AiJson.Clamp01(AiJson.Num(json, "confidence"), cited.Count > 0 ? 0.6 : 0.3);
        var grounded = (AiJson.Bool(json, "grounded") ?? cited.Count > 0) && cited.Count > 0;
        body = TextNormalization.Truncate(body, 6000);
        return new AiOutput<(string, List<int>, double, bool)>((body, cited, confidence, grounded), confidence, cited,
            new { body, citations = cited, confidence, grounded });
    }

    [GeneratedRegex(@"\[(\d{1,2})\]")] internal static partial Regex CitationMarker();
}

/// <summary>Customer-facing assistant (portal and chat bot) answering from customer-visible knowledge only.</summary>
public sealed partial class AiAssistantService(RequestContext ctx, AiGateway gateway, KnowledgeRetriever retriever)
{
    public const double HandoffThreshold = 0.45;

    public async Task<AssistantReply?> AnswerAsync(string question, string? locale, Guid? customerId, Guid? brandId, string? brandSlug, Guid? conversationId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(question)) return null;
        if (!await gateway.IsAvailableAsync(AiFeatures.PortalAssistant, interactive: false, ct)) return null;
        var text = TextNormalization.Truncate(question.Trim(), 2000);
        var authenticated = customerId is not null || ctx.IsContact;
        var hits = await retriever.SearchAsync(new RetrievalQuery(text, authenticated ? KnowledgeAudience.AuthenticatedCustomer : KnowledgeAudience.Public,
            customerId, brandId, brandSlug, [ChunkSources.Article], Take: 4, MinScore: 0.18, Locale: locale, Feature: AiFeatures.PortalAssistant), ct);
        var sources = hits.Where(h => h.Visibility != KbVisibility.Internal)
            .Select((h, i) => new RetrievedSource(i + 1, h.SourceType, h.SourceId, h.Title, TextNormalization.Truncate(h.Text, 1500), h.Url, h.Score, h.Visibility.ToString()))
            .ToList();
        var result = await gateway.CompleteAsync(new AiCall
        {
            Feature = AiFeatures.PortalAssistant, PromptKey = PromptKeys.PortalAssistant, EntityType = conversationId is null ? "PortalQuestion" : "Conversation",
            EntityId = conversationId ?? ctx.ContactId,
            Values = new Dictionary<string, string> { ["locale"] = locale ?? "en" },
            Untrusted = new Dictionary<string, string> { ["question"] = text, ["sources"] = AiReplyService.RenderSources(sources) },
            LocalTask = new AssistantTask(text, sources, authenticated, locale), MaxTokens = 600, Temperature = 0.2
        }, raw =>
        {
            var parsed = AiReplyService.ParseReply(raw, sources, "answer");
            var json = AiJson.ParseObject(raw);
            var handoff = AiJson.Bool(json, "handoff") ?? false;
            return new AiOutput<(string, List<int>, double, bool, bool)>((parsed.Value.Body, parsed.Value.Citations, parsed.Value.Confidence, parsed.Value.Grounded, handoff),
                parsed.Confidence, parsed.Citations, new { answer = parsed.Value.Body, citations = parsed.Value.Citations, confidence = parsed.Value.Confidence, handoff });
        }, ct);
        var (answer, cited, confidence, grounded, modelHandoff) = result.Value;
        var citations = cited.Select(i => sources[i - 1]).Select(s => new Citation(s.Index, s.SourceType, s.SourceId, s.Title, s.Url,
            TextNormalization.Truncate(s.Text, 240))).ToList();
        var wantsHuman = HumanRequest().IsMatch(text);
        var handoff = modelHandoff || wantsHuman || !grounded || confidence < HandoffThreshold || result.InjectionFlags.Count > 0;
        if (handoff && !answer.Contains("agent", StringComparison.OrdinalIgnoreCase))
            answer += "\n\nWould you like me to connect you with a support agent?";
        return new AssistantReply(answer, citations, handoff, Math.Round(confidence, 3));
    }

    [GeneratedRegex(@"\b(human|real person|agent|representative|someone|somebody|operator|call me|speak to|talk to|escalate)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HumanRequest();
}
