using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Ai.Features;
using SupportDesk.Application.Common;
using SupportDesk.Application.Notifications;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Ai;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Ai;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Ai.Agents;

/// <summary>
/// AI action proposals: creation (by agents/features), listing, approve-and-apply, reject and revert. Every applied or
/// reverted action is audited (ai.action_applied / ai.action_reverted) and recorded in the ticket history (AiAction).
/// Revert uses BeforeStateJson and only restores fields that still hold the AI-applied value (human edits made since win).
/// </summary>
public sealed class AiProposalService(
    DeskDbContext db,
    RequestContext ctx,
    AiGateway gateway,
    AiClassificationService classifier,
    TicketService tickets,
    ConversationService conversations,
    TicketHistory history,
    TicketLookups lookups,
    IAuditWriter audit,
    IEventPublisher events,
    NotificationService notifications,
    HtmlSanitization html)
{
    public async Task<AiActionProposal> CreateAsync(Guid ticketId, string actionType, object payload, string? rationale, double confidence, AiRiskLevel risk,
        bool requiresApproval, Guid? agentId, Guid? executionId, CancellationToken ct)
    {
        var info = AiActionCatalog.Info(actionType);
        var p = new AiActionProposal
        {
            AiExecutionId = executionId, AgentId = agentId, TicketId = ticketId, ActionType = info.Type, PayloadJson = Json.Serialize(payload),
            Rationale = rationale is null ? null : TextNormalization.Truncate(rationale, 2000), Confidence = Math.Round(Math.Clamp(confidence, 0, 1), 4),
            Risk = (AiRiskLevel)Math.Max((int)risk, (int)info.Risk),
            // Consequential and customer-facing actions can never skip approval, whatever the caller asked for.
            RequiresApproval = requiresApproval || info.Consequential || info.CustomerFacing,
            CreatedAt = db.Clock.UtcNow
        };
        db.AiActionProposals.Add(p);
        events.Publish(EventTypes.AiProposalCreated, "AiActionProposal", p.Id, new EntityEventPayload(p.Id, p.ActionType));
        await db.SaveChangesAsync(ct);
        if (p.Risk == AiRiskLevel.High && await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ticketId, ct) is { } t)
        {
            var recipients = await db.AgentDepartments.AsNoTracking().Where(d => d.DepartmentId == t.DepartmentId && d.IsSupervisor).Select(d => d.UserId).ToListAsync(ct);
            if (t.AssigneeId is { } a) recipients.Add(a);
            await notifications.NotifyAsync(new NotifyRequest(recipients.Distinct().ToList(), NotificationTypes.AiProposal, $"#{t.Number}: AI flagged a request that needs a decision",
                p.Rationale ?? info.Label, $"/tickets/{t.Id}", NotificationSeverity.Warning, false, "AiActionProposal", p.Id, $"ai-proposal:{p.Id:N}"), ct);
        }
        return p;
    }

    public async Task<List<AiProposalDto>> ListAsync(Guid? ticketId, string? status, int limit, CancellationToken ct)
    {
        var q = db.AiActionProposals.AsNoTracking().AsQueryable();
        if (ticketId is { } tid)
        {
            ctx.Require(Permissions.TicketsView);
            var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == tid, ct) ?? throw new NotFoundException("Ticket", tid);
            await tickets.EnsureVisibleAsync(t, ct);
            q = q.Where(p => p.TicketId == tid);
        }
        else
        {
            ctx.RequireAny(Permissions.AiApprove, Permissions.AdminAi);
            var visible = db.Tickets.VisibleTo(ctx, db).Select(t => t.Id);
            q = q.Where(p => visible.Contains(p.TicketId));
        }
        if (Enum.TryParse<ProposalStatus>(status, true, out var st)) q = q.Where(p => p.Status == st);
        var rows = await q.OrderByDescending(p => p.CreatedAt).Take(Math.Clamp(limit, 1, 500)).ToListAsync(ct);
        return await MapAsync(rows, ct);
    }

    public async Task<AiProposalDto> ApproveAsync(Guid id, DecideProposalRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AiApprove);
        var p = await LoadAsync(id, ct);
        if (p.Status != ProposalStatus.Proposed) throw new ConflictException("already_decided", $"This proposal is already {p.Status.ToString().ToLowerInvariant()}.");
        p.DecidedById = ctx.UserId;
        p.DecidedAt = db.Clock.UtcNow;
        p.Status = ProposalStatus.Approved;
        await ApplyAsync(p, $"approved by {ctx.ActorName}{(string.IsNullOrWhiteSpace(r.Note) ? "" : ": " + r.Note)}", ct);
        return (await MapAsync([p], ct))[0];
    }

    /// <summary>Applies a low-risk proposal automatically (AutoLowRisk agents). Caller runs as an AI/system actor.</summary>
    public async Task<bool> AutoApplyAsync(AiActionProposal p, CancellationToken ct)
    {
        p.Status = ProposalStatus.Approved;
        p.DecidedAt = db.Clock.UtcNow;
        try
        {
            await ApplyAsync(p, "applied automatically (low-risk policy)", ct);
            return true;
        }
        catch (DomainException)
        {
            return false;
        }
    }

    private async Task ApplyAsync(AiActionProposal p, string how, CancellationToken ct)
    {
        var info = AiActionCatalog.Info(p.ActionType);
        try
        {
            var before = await ApplyCoreAsync(p, ct);
            p.BeforeStateJson = before;
            p.Status = ProposalStatus.Applied;
            p.AppliedAt = db.Clock.UtcNow;
            p.Error = null;
            var t = await db.Tickets.AsNoTracking().FirstAsync(x => x.Id == p.TicketId, ct);
            history.Record(t, TicketEventTypes.AiAction, $"AI action '{info.Label}' {how}", null, new { proposalId = p.Id, p.ActionType, p.AgentId });
            audit.Write(AuditActions.AiActionApplied, "Ticket", p.TicketId, $"AI action '{info.Label}' {how} on #{t.Number}",
                new { proposalId = p.Id, p.ActionType, payload = Json.Element(p.PayloadJson), p.Confidence, p.Risk });
            await db.SaveChangesAsync(ct);
            if (p.AiExecutionId is { } exec) await gateway.MarkOutcomeAsync(exec, AiOutcome.Applied, ct);
        }
        catch (DomainException ex)
        {
            db.ChangeTracker.Clear();
            await db.AiActionProposals.Where(x => x.Id == p.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, ProposalStatus.Failed)
                .SetProperty(x => x.Error, TextNormalization.Truncate(ex.Message, 1000)).SetProperty(x => x.DecidedById, p.DecidedById)
                .SetProperty(x => x.DecidedAt, p.DecidedAt), ct);
            p.Status = ProposalStatus.Failed;
            p.Error = ex.Message;
            throw new DomainException("proposal_failed", $"The action could not be applied: {ex.Message}", 409);
        }
    }

    /// <summary>Performs the action through the normal services. Returns the before-state JSON (null for irreversible actions).</summary>
    private async Task<string?> ApplyCoreAsync(AiActionProposal p, CancellationToken ct)
    {
        var payload = Json.Element(p.PayloadJson);
        switch (p.ActionType)
        {
            case AiActionCatalog.SetClassification:
            {
                var values = new Dictionary<string, string?>();
                if (payload.TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Object)
                    foreach (var prop in v.EnumerateObject().Where(x => ClassificationFields.All.Contains(x.Name) && x.Value.ValueKind == JsonValueKind.String))
                        values[prop.Name] = prop.Value.GetString();
                var (applied, skipped, before) = await classifier.ApplyValuesAsync(p.TicketId, values, "AI proposal applied", p.AiExecutionId, ct);
                if (applied.Count == 0) throw new DomainException("nothing_to_apply", skipped.Count > 0 ? "The suggested values are no longer valid." : "The ticket already has these values.");
                var after = ClassificationService_CurrentAfter(await db.Tickets.AsNoTracking().FirstAsync(t => t.Id == p.TicketId, ct));
                return Json.Serialize(new { before, after, applied });
            }
            case AiActionCatalog.SetPriority:
            {
                var priority = payload.TryGetProperty("priority", out var pr) ? pr.GetString() : null;
                if (!Enum.TryParse<TicketPriority>(priority, true, out var target) || !Enum.IsDefined(target)) throw new DomainException("validation_failed", "Invalid priority.");
                var t = await tickets.LoadForUpdateAsync(p.TicketId, ct);
                if (t.Priority == target) throw new DomainException("nothing_to_apply", $"The ticket is already {target} priority.");
                var before = t.Priority.ToString();
                await tickets.UpdateAsync(t.Id, new UpdateTicketRequest { RowVersion = Convert.ToBase64String(t.RowVersion), Priority = target.ToString() }, ct);
                return Json.Serialize(new { priority = before, after = target.ToString() });
            }
            case AiActionCatalog.AddTags:
            {
                var add = payload.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array
                    ? tg.EnumerateArray().Select(x => TicketService.NormalizeTag(x.GetString() ?? "")).Where(x => x.Length > 0).Distinct().ToList() : [];
                var current = await db.TicketTags.Where(x => x.TicketId == p.TicketId).Select(x => x.Tag!.Name).ToListAsync(ct);
                var added = add.Except(current).ToList();
                if (added.Count == 0) throw new DomainException("nothing_to_apply", "The ticket already has these tags.");
                await tickets.SetTagsAsync(p.TicketId, current.Concat(added).ToList(), ct);
                return Json.Serialize(new { tags = current, added });
            }
            case AiActionCatalog.SendReply or AiActionCatalog.RequestInfo:
            {
                var body = payload.TryGetProperty("bodyHtml", out var b) ? b.GetString() : null;
                if (string.IsNullOrWhiteSpace(body)) throw new DomainException("validation_failed", "The proposal has no reply text.");
                var thread = await conversations.ReplyAsync(p.TicketId, new ReplyRequest { BodyHtml = html.Sanitize(body), Force = true, ClientRequestId = $"ai-proposal:{p.Id:N}" }, ct);
                return Json.Serialize(new { threadId = thread.Id });
            }
            case AiActionCatalog.AddNote:
            {
                var body = payload.TryGetProperty("bodyHtml", out var b) ? b.GetString() : null;
                if (string.IsNullOrWhiteSpace(body)) throw new DomainException("validation_failed", "The proposal has no note text.");
                var thread = await conversations.AddNoteAsync(p.TicketId, new NoteRequest { BodyHtml = html.Sanitize(body), ClientRequestId = $"ai-proposal:{p.Id:N}" }, ct);
                return Json.Serialize(new { threadId = thread.Id });
            }
            case AiActionCatalog.ConsequentialReview:
            {
                var kind = payload.TryGetProperty("kind", out var k) ? k.GetString() : "request";
                var note = $"<p><strong>Decision recorded:</strong> {System.Net.WebUtility.HtmlEncode(ctx.ActorName ?? "A supervisor")} approved handling the customer's " +
                           $"{System.Net.WebUtility.HtmlEncode(kind?.Replace('_', ' ') ?? "request")} request. The AI took no action — follow the standard procedure.</p>";
                var thread = await conversations.AddNoteAsync(p.TicketId, new NoteRequest { BodyHtml = note, ClientRequestId = $"ai-proposal:{p.Id:N}" }, ct);
                return Json.Serialize(new { threadId = thread.Id });
            }
            default:
                throw new DomainException("validation_failed", $"Unknown AI action '{p.ActionType}'.");
        }
    }

    private static Dictionary<string, string?> ClassificationService_CurrentAfter(Ticket t)
    {
        var d = AiClassificationService.CurrentValues(t);
        d["categoryId"] = t.CategoryId?.ToString();
        d["subcategoryId"] = t.SubcategoryId?.ToString();
        return d;
    }

    public async Task<AiProposalDto> RejectAsync(Guid id, DecideProposalRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AiApprove);
        var p = await LoadAsync(id, ct);
        if (p.Status != ProposalStatus.Proposed) throw new ConflictException("already_decided", $"This proposal is already {p.Status.ToString().ToLowerInvariant()}.");
        p.Status = ProposalStatus.Rejected;
        p.DecidedById = ctx.UserId;
        p.DecidedAt = db.Clock.UtcNow;
        p.Error = string.IsNullOrWhiteSpace(r.Note) ? null : TextNormalization.Truncate(r.Note, 1000);
        var t = await db.Tickets.AsNoTracking().FirstAsync(x => x.Id == p.TicketId, ct);
        history.Record(t, TicketEventTypes.AiAction, $"AI proposal '{AiActionCatalog.Info(p.ActionType).Label}' rejected by {ctx.ActorName}", null, new { proposalId = p.Id });
        await db.SaveChangesAsync(ct);
        if (p.AiExecutionId is { } exec) await gateway.MarkOutcomeAsync(exec, AiOutcome.Rejected, ct);
        return (await MapAsync([p], ct))[0];
    }

    public async Task<AiProposalDto> RevertAsync(Guid id, DecideProposalRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AiApprove);
        var p = await LoadAsync(id, ct);
        var info = AiActionCatalog.Info(p.ActionType);
        if (p.Status != ProposalStatus.Applied) throw new ConflictException("not_applied", "Only applied actions can be reverted.");
        if (!info.Reversible || string.IsNullOrEmpty(p.BeforeStateJson))
            throw new ConflictException("not_reversible", $"'{info.Label}' cannot be undone automatically (messages already sent stay sent).");
        var state = Json.Element(p.BeforeStateJson);
        var restored = new List<string>();
        var skipped = new List<string>();
        switch (p.ActionType)
        {
            case AiActionCatalog.SetClassification:
            {
                var before = state.GetProperty("before");
                var after = state.GetProperty("after");
                string? S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var t = await tickets.LoadForUpdateAsync(p.TicketId, ct);
                var current = ClassificationService_CurrentAfter(t);
                var applied = state.TryGetProperty("applied", out var a) ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
                var req = new UpdateTicketRequest { RowVersion = Convert.ToBase64String(t.RowVersion) };
                var clear = new List<string>();
                var changed = false;
                if (applied.Contains(ClassificationFields.Category))
                {
                    if (current["categoryId"] == S(after, "categoryId") && current["subcategoryId"] == S(after, "subcategoryId"))
                    {
                        var cat = S(before, "categoryId");
                        var sub = S(before, "subcategoryId");
                        req = req with { CategoryId = cat is null ? null : Guid.Parse(cat), SubcategoryId = sub is null ? null : Guid.Parse(sub) };
                        if (cat is null) clear.Add("categoryId");
                        if (sub is null) clear.Add("subcategoryId");
                        restored.Add(ClassificationFields.Category);
                        changed = true;
                    }
                    else skipped.Add(ClassificationFields.Category);
                }
                if (applied.Contains(ClassificationFields.Product))
                {
                    if (current[ClassificationFields.Product] == S(after, ClassificationFields.Product))
                    {
                        var prod = S(before, ClassificationFields.Product);
                        req = req with { ProductId = prod is null ? null : Guid.Parse(prod) };
                        if (prod is null) clear.Add("productId");
                        restored.Add(ClassificationFields.Product);
                        changed = true;
                    }
                    else skipped.Add(ClassificationFields.Product);
                }
                if (changed) await tickets.UpdateAsync(t.Id, req with { Clear = clear.Count > 0 ? clear : null }, ct);
                if (applied.Contains(ClassificationFields.Department) && S(before, ClassificationFields.Department) is { } dept)
                {
                    var tracked = await tickets.LoadForUpdateAsync(t.Id, ct);
                    if (tracked.DepartmentId.ToString() == S(after, ClassificationFields.Department))
                    {
                        await tickets.TransferCoreAsync(tracked, Guid.Parse(dept), null, null, "AI action reverted", ct);
                        await tickets.SaveAsync(tracked, ct);
                        restored.Add(ClassificationFields.Department);
                    }
                    else skipped.Add(ClassificationFields.Department);
                }
                break;
            }
            case AiActionCatalog.SetPriority:
            {
                var t = await tickets.LoadForUpdateAsync(p.TicketId, ct);
                var before = state.GetProperty("priority").GetString();
                var after = state.GetProperty("after").GetString();
                if (t.Priority.ToString() == after)
                {
                    await tickets.UpdateAsync(t.Id, new UpdateTicketRequest { RowVersion = Convert.ToBase64String(t.RowVersion), Priority = before }, ct);
                    restored.Add("priority");
                }
                else skipped.Add("priority");
                break;
            }
            case AiActionCatalog.AddTags:
            {
                var added = state.GetProperty("added").EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet();
                var current = await db.TicketTags.Where(x => x.TicketId == p.TicketId).Select(x => x.Tag!.Name).ToListAsync(ct);
                await tickets.SetTagsAsync(p.TicketId, current.Where(x => !added.Contains(x)).ToList(), ct);
                restored.Add("tags");
                break;
            }
        }
        p.Status = ProposalStatus.Reverted;
        p.RevertedAt = db.Clock.UtcNow;
        var ticket = await db.Tickets.AsNoTracking().FirstAsync(x => x.Id == p.TicketId, ct);
        var summary = $"AI action '{info.Label}' reverted by {ctx.ActorName}" + (restored.Count > 0 ? $" (restored {string.Join(", ", restored)})" : "") +
                      (skipped.Count > 0 ? $"; kept later human changes to {string.Join(", ", skipped)}" : "");
        history.Record(ticket, TicketEventTypes.AiAction, summary, null, new { proposalId = p.Id, restored, skipped, r.Note });
        audit.Write(AuditActions.AiActionReverted, "Ticket", p.TicketId, $"{summary} on #{ticket.Number}", new { proposalId = p.Id, restored, skipped });
        await db.SaveChangesAsync(ct);
        if (p.AiExecutionId is { } exec) await gateway.MarkOutcomeAsync(exec, AiOutcome.Reverted, ct);
        return (await MapAsync([p], ct))[0];
    }

    private async Task<AiActionProposal> LoadAsync(Guid id, CancellationToken ct)
    {
        var p = await db.AiActionProposals.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("AI proposal", id);
        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == p.TicketId, ct) ?? throw new NotFoundException("AI proposal", id);
        await tickets.EnsureVisibleAsync(t, ct);
        return p;
    }

    public async Task<List<AiProposalDto>> MapAsync(IReadOnlyList<AiActionProposal> rows, CancellationToken ct)
    {
        await lookups.LoadAsync(ct);
        var ticketIds = rows.Select(r => r.TicketId).Distinct().ToList();
        var ticketInfo = await db.Tickets.AsNoTracking().Where(t => ticketIds.Contains(t.Id)).Select(t => new { t.Id, t.Number, t.Subject }).ToDictionaryAsync(t => t.Id, ct);
        var agentIds = rows.Where(r => r.AgentId.HasValue).Select(r => r.AgentId!.Value).Distinct().ToList();
        var agents = await db.AiAgents.AsNoTracking().Where(a => agentIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        return rows.Select(p =>
        {
            var info = AiActionCatalog.Info(p.ActionType);
            var t = ticketInfo.GetValueOrDefault(p.TicketId);
            return new AiProposalDto(p.Id, p.TicketId, t?.Number, t?.Subject, p.ActionType, info.Label, Json.Element(p.PayloadJson), p.Rationale, p.Confidence,
                p.Risk.ToString(), p.RequiresApproval, info.Reversible, p.Status.ToString(), p.AgentId, p.AgentId is { } a ? agents.GetValueOrDefault(a) : null,
                p.AiExecutionId, p.CreatedAt, lookups.User(p.DecidedById), p.DecidedAt, p.AppliedAt, p.RevertedAt, p.Error);
        }).ToList();
    }
}
