using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Ai.Features;
using SupportDesk.Application.Ai.Retrieval;
using SupportDesk.Application.Ai.Safety;
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

namespace SupportDesk.Application.Ai.Agents;

public static class AgentTriggers
{
    public const string Created = "created";
    public const string CustomerReplied = "customer_replied";
    public const string Manual = "manual";
}

/// <summary>
/// Runs the tenant's enabled AI agents for a ticket: Triage (classification, priority, tags), Info collector (missing
/// version/environment/steps/error), Knowledge resolver (grounded resolution reply) and Auto-responder (fully automated
/// reply only for explicitly approved low-risk scenarios above the confidence threshold on allowed channels). Everything
/// else becomes an <see cref="AiActionProposal"/>; consequential requests are flagged for a human decision.
/// </summary>
public sealed class AiAgentRunner(
    DeskDbContext db,
    RequestContext ctx,
    AiGateway gateway,
    AiTicketLoader loader,
    AiClassificationService classifier,
    AiReplyService replies,
    AiProposalService proposals,
    TicketLookups lookups,
    TicketService tickets,
    IServiceScopeFactory scopes,
    ILogger<AiAgentRunner> logger)
{
    /// <summary>Agent-initiated run ("Run AI agents" in the workbench). Produces proposals; never sends automatic replies.</summary>
    public async Task<AgentRunResultDto> RunManualAsync(Guid ticketId, CancellationToken ct)
    {
        ctx.Require(Permissions.AiUse);
        ctx.Require(Permissions.TicketsEdit);
        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ticketId, ct) ?? throw new NotFoundException("Ticket", ticketId);
        await tickets.EnsureVisibleAsync(t, ct);
        await gateway.EnsureAvailableAsync(AiFeatures.Agents, interactive: true, ct, requiresAgentsPlan: true);
        return await RunForTicketAsync(ticketId, AgentTriggers.Manual, ActorType.User, ct);
    }

    private sealed class RunState
    {
        public int AgentsRun;
        public int Proposals;
        public int Applied;
        public bool AutoReplied;
        public List<string> Notes { get; } = [];
    }

    public async Task<AgentRunResultDto> RunForTicketAsync(Guid ticketId, string trigger, ActorType? eventActor, CancellationToken ct)
    {
        var state = new RunState();
        if (!await gateway.IsAvailableAsync(AiFeatures.Agents, interactive: false, ct, requiresAgentsPlan: true))
            return Result(ticketId, state, "AI agents are turned off or not included in the plan.");
        var agents = await db.AiAgents.AsNoTracking().Where(a => a.IsEnabled).OrderBy(a => a.Kind).ThenBy(a => a.Name).ToListAsync(ct);
        if (agents.Count == 0) return Result(ticketId, state, "No AI agents are enabled.");
        var ticket = await db.Tickets.AsNoTracking().Include(t => t.Tags).FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null || !ticket.StatusCategory.IsOpen() || ticket.MergedIntoTicketId is not null || ticket.IsSpam)
            return Result(ticketId, state, "The ticket is not open.");
        var snapshot = await loader.SnapshotAsync(ticket, internalView: false, ct);
        var customerText = AiTicketLoader.CustomerText(snapshot);
        var injection = PromptInjectionDetector.Detect(customerText);
        var consequential = ConsequentialRequestDetector.Detect(AiTicketLoader.LatestCustomerText(snapshot) + "\n" + snapshot.Subject);
        var settings = await gateway.SettingsAsync(ct);

        foreach (var agent in agents)
        {
            var config = AgentConfig.Parse(agent.ConfigJson);
            if (config.Departments.Count > 0 && !config.Departments.Contains(ticket.DepartmentId)) continue;
            if (config.Channels.Count > 0 && !config.Channels.Contains(ticket.Channel.ToString(), StringComparer.OrdinalIgnoreCase)) continue;
            var applicable = agent.Kind switch
            {
                AiAgentKind.Triage => trigger is AgentTriggers.Created or AgentTriggers.Manual,
                AiAgentKind.InfoCollector => trigger is AgentTriggers.Created or AgentTriggers.Manual,
                _ => true
            };
            if (!applicable) continue;
            var minConfidence = config.MinConfidence ?? settings.Feature(AiFeatures.Agents).MinConfidence;
            try
            {
                switch (agent.Kind)
                {
                    case AiAgentKind.Triage: await TriageAsync(agent, config, minConfidence, ticket, snapshot, injection.Count > 0, state, ct); break;
                    case AiAgentKind.InfoCollector: await CollectInfoAsync(agent, config, ticket, snapshot, state, ct); break;
                    case AiAgentKind.KnowledgeResolver: await ResolveAsync(agent, config, ticket, snapshot, state, ct); break;
                    case AiAgentKind.AutoResponder:
                        await AutoRespondAsync(agent, config, minConfidence, ticket, snapshot, customerText, trigger, eventActor, injection, consequential, settings, state, ct);
                        break;
                }
                state.AgentsRun++;
                await db.AiAgents.Where(a => a.Id == agent.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.RunCount, a => a.RunCount + 1), ct);
            }
            catch (DomainException ex)
            {
                // A failing agent (provider down, budget, validation) must not stop the others or the event pipeline.
                db.ChangeTracker.Clear();
                state.Notes.Add($"{agent.Name}: {ex.Message}");
                logger.LogInformation("AI agent {Agent} skipped ticket {Ticket}: {Message}", agent.Name, ticketId, ex.Message);
            }
        }

        if (consequential is { } c && agents.Any(a => a.Kind is AiAgentKind.Triage or AiAgentKind.KnowledgeResolver or AiAgentKind.AutoResponder) &&
            !await PendingExistsAsync(ticketId, AiActionCatalog.ConsequentialReview, null, ct))
        {
            await proposals.CreateAsync(ticketId, AiActionCatalog.ConsequentialReview, new { kind = c.Kind, excerpt = c.Excerpt },
                $"The customer appears to ask for a {c.Kind.Replace('_', ' ')} decision (\"{TextNormalization.Truncate(c.Excerpt, 160)}\"). AI agents never commit to this; a person must decide.",
                0.9, AiRiskLevel.High, true, null, null, ct);
            state.Proposals++;
        }
        return Result(ticketId, state, null);
    }

    private static AgentRunResultDto Result(Guid ticketId, RunState s, string? note)
    {
        if (note is not null) s.Notes.Add(note);
        return new AgentRunResultDto(ticketId, s.AgentsRun, s.Proposals, s.Applied, s.AutoReplied, s.Notes);
    }

    private Task<bool> PendingExistsAsync(Guid ticketId, string actionType, Guid? agentId, CancellationToken ct) =>
        db.AiActionProposals.AnyAsync(p => p.TicketId == ticketId && p.ActionType == actionType && p.Status == ProposalStatus.Proposed &&
                                           (agentId == null || p.AgentId == agentId), ct);

    // ───────────────────────────── Triage ─────────────────────────────

    private async Task TriageAsync(AiAgent agent, AgentConfig config, double minConfidence, Ticket ticket, TicketSnapshot snapshot, bool injectionFlagged,
        RunState state, CancellationToken ct)
    {
        var result = await classifier.ClassifyAsync(ticket, snapshot, interactive: false, AiFeatures.Agents, agent.Id, ct);
        var current = AiClassificationService.CurrentValues(ticket);
        var flagged = injectionFlagged || result.InjectionFlags.Count > 0;

        // Classification: propose changes; in AutoLowRisk mode, fill EMPTY fields only (never override a human's choice).
        var values = new Dictionary<string, string?>();
        var confidences = new List<double>();
        var fillsOnlyEmpty = true;
        foreach (var field in new[] { ClassificationFields.Category, ClassificationFields.Product, ClassificationFields.Department })
        {
            if (!result.Fields.TryGetValue(field, out var p) || p.Value is null || p.Confidence < 0.5) continue;
            if (string.Equals(p.Value, current[field], StringComparison.OrdinalIgnoreCase)) continue;
            if (field == ClassificationFields.Department && !config.Allows("set_department")) continue;
            if (field != ClassificationFields.Department && !config.Allows("set_category")) continue;
            values[field] = p.Value;
            confidences.Add(p.Confidence);
            if (current[field] is not null) fillsOnlyEmpty = false;
        }
        if (values.Count > 0 && !await PendingExistsAsync(ticket.Id, AiActionCatalog.SetClassification, agent.Id, ct))
        {
            var risk = values.ContainsKey(ClassificationFields.Department) || !fillsOnlyEmpty ? AiRiskLevel.Medium : AiRiskLevel.Low;
            var confidence = confidences.Min();
            var info = AiActionCatalog.Info(AiActionCatalog.SetClassification);
            var approval = AiActionCatalog.RequiresApproval(info, risk, agent.Mode, confidence, minConfidence, flagged);
            await lookups.LoadAsync(ct);
            var labels = values.Select(kv => $"{kv.Key}: {Label(kv.Key, kv.Value)}");
            var p = await proposals.CreateAsync(ticket.Id, AiActionCatalog.SetClassification, new { values, confidence = confidences },
                $"{agent.Name} suggests {string.Join(", ", labels)} (confidence {confidence:P0}). {result.Explanation}", confidence, risk, approval, agent.Id,
                result.ExecutionId, ct);
            state.Proposals++;
            if (!approval && await proposals.AutoApplyAsync(p, ct)) state.Applied++;
        }

        if (result.Fields.TryGetValue(ClassificationFields.Priority, out var pr) && pr.Value is not null && pr.Confidence >= 0.55 &&
            !string.Equals(pr.Value, ticket.Priority.ToString(), StringComparison.OrdinalIgnoreCase) && config.Allows("set_priority") &&
            !await PendingExistsAsync(ticket.Id, AiActionCatalog.SetPriority, agent.Id, ct))
        {
            await proposals.CreateAsync(ticket.Id, AiActionCatalog.SetPriority, new { priority = pr.Value },
                $"{agent.Name} suggests priority {pr.Value} instead of {ticket.Priority} (confidence {pr.Confidence:P0}).", pr.Confidence, AiRiskLevel.Medium, true,
                agent.Id, result.ExecutionId, ct);
            state.Proposals++;
        }

        var newTags = result.Tags.Select(TicketService.NormalizeTag).Except(ticket.Tags.Select(t => t.Tag?.Name ?? "")).ToList();
        if (newTags.Count > 0 && config.Allows("add_tag") && !await PendingExistsAsync(ticket.Id, AiActionCatalog.AddTags, agent.Id, ct))
        {
            var info = AiActionCatalog.Info(AiActionCatalog.AddTags);
            var approval = AiActionCatalog.RequiresApproval(info, AiRiskLevel.Low, agent.Mode, result.Confidence, minConfidence, flagged);
            var p = await proposals.CreateAsync(ticket.Id, AiActionCatalog.AddTags, new { tags = newTags }, $"{agent.Name} suggests tags: {string.Join(", ", newTags)}.",
                result.Confidence, AiRiskLevel.Low, approval, agent.Id, result.ExecutionId, ct);
            state.Proposals++;
            if (!approval && await proposals.AutoApplyAsync(p, ct)) state.Applied++;
        }
    }

    private string? Label(string field, string? value) => value is null ? null : field switch
    {
        ClassificationFields.Category => Guid.TryParse(value, out var c) ? lookups.Category(c) : value,
        ClassificationFields.Product => Guid.TryParse(value, out var p) ? lookups.Product(p) : value,
        ClassificationFields.Department => Guid.TryParse(value, out var d) ? lookups.Department(d) : value,
        _ => value
    };

    // ───────────────────────────── Info collector ─────────────────────────────

    private async Task CollectInfoAsync(AiAgent agent, AgentConfig config, Ticket ticket, TicketSnapshot snapshot, RunState state, CancellationToken ct)
    {
        if (!config.Allows("request_info") || await PendingExistsAsync(ticket.Id, AiActionCatalog.RequestInfo, agent.Id, ct)) return;
        var required = (config.RequiredInfo.Count > 0 ? config.RequiredInfo : InfoItems.Default).Where(i => InfoItems.All.Contains(i)).ToList();
        var result = await gateway.CompleteAsync(new AiCall
        {
            Feature = AiFeatures.Agents, PromptKey = PromptKeys.InfoCollector, EntityType = "Ticket", EntityId = ticket.Id, AgentId = agent.Id, RequiresAgentsPlan = true,
            Values = new Dictionary<string, string> { ["required"] = string.Join(", ", required) },
            Untrusted = new Dictionary<string, string> { ["ticket"] = AiTicketLoader.RenderMetadata(snapshot) + "\n" + AiTicketLoader.RenderConversation(snapshot, false, 6000) },
            LocalTask = new InfoCollectorTask(snapshot, required), MaxTokens = 500
        }, raw =>
        {
            var json = AiJson.ParseObject(raw);
            var missing = AiJson.StrList(json, "missing", 6, 40).Select(m => m.ToLowerInvariant()).Where(required.Contains).Distinct().ToList();
            var request = AiJson.Str(json, "request")?.Trim() ?? "";
            if (missing.Count > 0 && request.Length == 0) throw new AiOutputException("Missing 'request'.");
            return new AiOutput<(List<string>, string)>((missing, TextNormalization.Truncate(request, 3000)), missing.Count > 0 ? 0.8 : 0.5,
                Logged: new { agent = agent.Kind.ToString(), missing, request });
        }, ct);
        var (missingItems, text) = result.Value;
        if (missingItems.Count == 0) return;
        await proposals.CreateAsync(ticket.Id, AiActionCatalog.RequestInfo, new { bodyHtml = TextNormalization.TextToHtml(text), missing = missingItems },
            $"{agent.Name}: the ticket is missing {string.Join(", ", missingItems.Select(m => m.Replace('_', ' ')))}.", 0.8, AiRiskLevel.Low, true, agent.Id,
            result.ExecutionId, ct);
        state.Proposals++;
    }

    // ───────────────────────────── Knowledge resolver ─────────────────────────────

    private async Task ResolveAsync(AiAgent agent, AgentConfig config, Ticket ticket, TicketSnapshot snapshot, RunState state, CancellationToken ct)
    {
        if (!config.Allows("suggest_article") && !config.Allows("send_reply")) return;
        if (await PendingExistsAsync(ticket.Id, AiActionCatalog.SendReply, agent.Id, ct)) return;
        var reply = await replies.ComposeAsync(ticket, snapshot, customerFacingOnly: false, AiFeatures.Agents, interactive: false, agent.Id, agent.Instructions, ct);
        if (!reply.Grounded) return;
        await proposals.CreateAsync(ticket.Id, AiActionCatalog.SendReply, new { bodyHtml = reply.BodyHtml, citations = reply.Citations },
            $"{agent.Name} drafted a resolution grounded in {string.Join(", ", reply.Citations.Select(c => $"[{c.Index}] {c.Title}"))}.", reply.Confidence,
            AiRiskLevel.Medium, true, agent.Id, reply.ExecutionId, ct);
        state.Proposals++;
    }

    // ───────────────────────────── Auto-responder ─────────────────────────────

    private async Task AutoRespondAsync(AiAgent agent, AgentConfig config, double minConfidence, Ticket ticket, TicketSnapshot snapshot, string customerText,
        string trigger, ActorType? eventActor, IReadOnlyList<string> injection, (string Kind, string Excerpt)? consequential, AiSettings settings, RunState state,
        CancellationToken ct)
    {
        var approved = settings.Scenarios.Where(s => s.Enabled && (config.Scenarios.Count == 0 || config.Scenarios.Contains(s.Key, StringComparer.OrdinalIgnoreCase))).ToList();
        if (approved.Count == 0) return;
        var (scenario, match) = await MatchScenarioAsync(approved, ticket, customerText, ct);
        if (scenario is null) return; // outside every approved scenario: the auto-responder stays silent

        var blockers = new List<string>();
        if (agent.Mode != AiAgentMode.AutoLowRisk) blockers.Add("the agent is in suggest-only mode");
        if (trigger == AgentTriggers.Manual) blockers.Add("manual runs never send automatically");
        if (eventActor == ActorType.User) blockers.Add("the ticket was logged by an agent");
        if (scenario.Channels.Count > 0 && !scenario.Channels.Contains(ticket.Channel.ToString(), StringComparer.OrdinalIgnoreCase))
            blockers.Add($"channel {ticket.Channel} is not approved for this scenario");
        if (ticket.Priority >= TicketPriority.Critical) blockers.Add("critical priority");
        if (ticket.Sentiment is Sentiment.Frustrated) blockers.Add("the customer appears frustrated");
        if (consequential is not null) blockers.Add($"the request involves a {consequential.Value.Kind.Replace('_', ' ')} decision");
        if (injection.Count > 0) blockers.Add("possible prompt-injection content");
        var aiReplies = await db.TicketThreads.CountAsync(x => x.TicketId == ticket.Id && x.AuthorType == AuthorType.Ai && x.Kind == ThreadKind.AgentReply, ct);
        if (aiReplies >= config.MaxRepliesPerTicket) blockers.Add("the automatic reply limit for this ticket was reached");
        if (!config.Allows("auto_reply")) blockers.Add("auto_reply is not an allowed action for this agent");

        var reply = await replies.ComposeAsync(ticket, snapshot, customerFacingOnly: true, AiFeatures.Agents, interactive: false, agent.Id, agent.Instructions, ct);
        if (!reply.Grounded) blockers.Add("no approved customer-visible knowledge supports an answer");
        if (scenario.ArticleIds.Count > 0 && !reply.Citations.Any(c => scenario.ArticleIds.Contains(c.SourceId)))
            blockers.Add("the answer is not based on the scenario's approved articles");
        if (reply.InjectionFlags.Count > 0) blockers.Add("possible prompt-injection content in sources");
        var confidence = Math.Round(Math.Min(reply.Confidence, match), 4);
        var threshold = Math.Max(scenario.MinConfidence, minConfidence);
        if (confidence < threshold) blockers.Add($"confidence {confidence:P0} is below the {threshold:P0} threshold");

        if (blockers.Count > 0)
        {
            if (aiReplies < config.MaxRepliesPerTicket && reply.Grounded && !await PendingExistsAsync(ticket.Id, AiActionCatalog.SendReply, agent.Id, ct))
            {
                await proposals.CreateAsync(ticket.Id, AiActionCatalog.SendReply, new { bodyHtml = reply.BodyHtml, citations = reply.Citations, scenario = scenario.Key },
                    $"{agent.Name} matched scenario '{scenario.Name}' but did not reply automatically: {string.Join("; ", blockers)}.", confidence, AiRiskLevel.Medium, true,
                    agent.Id, reply.ExecutionId, ct);
                state.Proposals++;
            }
            state.Notes.Add($"{agent.Name}: not sent automatically — {string.Join("; ", blockers)}");
            return;
        }

        // Send as the AI actor in its own scope so history and the conversation show who replied.
        var tenantId = ctx.TenantId;
        Guid threadId;
        using (var scope = scopes.CreateScope())
        {
            var sctx = scope.ServiceProvider.GetRequiredService<RequestContext>();
            sctx.SetSystem(tenantId, agent.Name, ActorType.Ai);
            sctx.ChainDepth = ctx.ChainDepth;
            sctx.CausationEventId = ctx.CausationEventId;
            sctx.CorrelationId = ctx.CorrelationId;
            var conversation = scope.ServiceProvider.GetRequiredService<ConversationService>();
            var body = reply.BodyHtml + "<p><em>This is an automated answer from our help center. Reply to this message if you still need help and an agent will follow up.</em></p>";
            var thread = await conversation.ReplyAsync(ticket.Id, new ReplyRequest
            {
                BodyHtml = body, Force = true, ClientRequestId = $"ai-auto:{ticket.Id:N}:{ticket.LastThreadSeq}"
            }, ct);
            threadId = thread.Id;
            var sdb = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
            await sdb.TicketThreads.Where(x => x.Id == thread.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.AuthorType, AuthorType.Ai)
                .SetProperty(x => x.AuthorName, agent.Name), ct);
            var t = await sdb.Tickets.AsNoTracking().FirstAsync(x => x.Id == ticket.Id, ct);
            scope.ServiceProvider.GetRequiredService<TicketHistory>().Record(t, TicketEventTypes.AiAction,
                $"{agent.Name} replied automatically (approved scenario '{scenario.Name}', confidence {confidence:P0})", null,
                new { scenario = scenario.Key, threadId = thread.Id, executionId = reply.ExecutionId, citations = reply.Citations.Select(c => c.SourceId) });
            scope.ServiceProvider.GetRequiredService<IAuditWriter>().Write(AuditActions.AiActionApplied, "Ticket", ticket.Id,
                $"AI auto-reply sent on #{t.Number} (scenario '{scenario.Name}')", new { agent = agent.Id, scenario = scenario.Key, confidence });
            await sdb.SaveChangesAsync(ct);
        }
        // Keep a decided record of the automatic action for the proposals log.
        db.AiActionProposals.Add(new AiActionProposal
        {
            AiExecutionId = reply.ExecutionId, AgentId = agent.Id, TicketId = ticket.Id, ActionType = AiActionCatalog.SendReply,
            PayloadJson = Json.Serialize(new { bodyHtml = reply.BodyHtml, citations = reply.Citations, scenario = scenario.Key, threadId }),
            Rationale = $"Automatic reply for approved scenario '{scenario.Name}' (confidence {confidence:P0}).", Confidence = confidence, Risk = AiRiskLevel.Low,
            RequiresApproval = false, Status = ProposalStatus.Applied, DecidedAt = db.Clock.UtcNow, AppliedAt = db.Clock.UtcNow, CreatedAt = db.Clock.UtcNow
        });
        await db.SaveChangesAsync(ct);
        await gateway.MarkOutcomeAsync(reply.ExecutionId, AiOutcome.Applied, ct);
        state.AutoReplied = true;
        state.Applied++;
    }

    /// <summary>Best approved scenario for the ticket: keyword phrases, category membership and semantic similarity.</summary>
    private async Task<(AutoReplyScenario? Scenario, double Score)> MatchScenarioAsync(IReadOnlyList<AutoReplyScenario> scenarios, Ticket ticket, string text,
        CancellationToken ct)
    {
        var tokens = TextNormalization.Tokenize(text).ToHashSet();
        var texts = new List<string> { text };
        texts.AddRange(scenarios.Select(s => $"{s.Name}. {s.Description} {string.Join(". ", s.Keywords)}"));
        var vectors = (await gateway.EmbedAsync(texts, AiFeatures.Agents, ct)).Vectors;
        AutoReplyScenario? best = null;
        var bestScore = 0.0;
        for (var i = 0; i < scenarios.Count; i++)
        {
            var s = scenarios[i];
            var hits = s.Keywords.Count(k => TextNormalization.Tokenize(k).ToList() is { Count: > 0 } kt && kt.All(tokens.Contains));
            var keywordScore = hits == 0 ? 0 : 0.6 + 0.4 * Math.Min(1, hits / 2.0);
            var semantic = Math.Max(0, VectorMath.Dot(vectors[0], vectors[i + 1]));
            var score = Math.Max(keywordScore, semantic * 1.1);
            if (s.CategoryIds.Count > 0 && (ticket.CategoryId is { } c && s.CategoryIds.Contains(c) || ticket.SubcategoryId is { } sc && s.CategoryIds.Contains(sc)))
                score += 0.25;
            score = Math.Min(1, score);
            if (score > bestScore) (best, bestScore) = (s, score);
        }
        return bestScore >= 0.6 ? (best, bestScore) : (null, bestScore);
    }
}
