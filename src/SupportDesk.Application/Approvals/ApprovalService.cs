using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Approvals;

public sealed record ApprovalDto(Guid Id, string Type, string EntityType, Guid EntityId, Guid? TicketId, long? TicketNumber, string? TicketSubject,
    Guid RequestedById, string RequestedByName, string Status, string? Reason, string? DecisionNote, string? DecidedByName, DateTime CreatedAt,
    DateTime? DecidedAt, DateTime? ExpiresAt, System.Text.Json.JsonElement Payload);

public sealed record DecideApprovalRequest(bool Approve, string? Note);

public sealed record CreateApprovalRequest(string Type, Guid TicketId, string Reason, Guid? ApproverUserId, Guid? ApproverRoleId, System.Text.Json.JsonElement? Payload);

/// <summary>Applies an approved request's payload (registered per approval type by each module).</summary>
public interface IApprovalAction
{
    string Type { get; }
    Task ApplyAsync(ApprovalRequest request, CancellationToken ct);
}

/// <summary>
/// Formal approval requests (department transfers, blueprint transitions, AI actions, custom requests).
/// Deciding requires approvals.decide (or being the named approver); the requester can never approve their own request.
/// </summary>
public sealed class ApprovalService(DeskDbContext db, RequestContext ctx, IEventPublisher events, TicketHistory history, IEnumerable<IApprovalAction> actions,
    TicketLookups lookups)
{
    public async Task<ApprovalRequest> CreateAsync(CreateApprovalRequest r, CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.TicketsEdit, Permissions.TicketsNote);
        var t = await db.Tickets.FirstOrDefaultAsync(x => x.Id == r.TicketId, ct) ?? throw new NotFoundException("Ticket", r.TicketId);
        var a = new ApprovalRequest
        {
            Type = string.IsNullOrWhiteSpace(r.Type) ? "custom" : r.Type, EntityType = "Ticket", EntityId = t.Id, TicketId = t.Id, RequestedById = ctx.UserId ?? Guid.Empty,
            ApproverUserId = r.ApproverUserId, ApproverRoleId = r.ApproverRoleId, Reason = r.Reason, PayloadJson = r.Payload?.GetRawText() ?? "{}",
            ExpiresAt = db.Clock.UtcNow.AddDays(7)
        };
        db.ApprovalRequests.Add(a);
        history.Record(t, TicketEventTypes.ApprovalRequested, $"Approval requested ({a.Type}): {r.Reason}", null, new { approvalId = a.Id });
        events.Publish(EventTypes.ApprovalRequested, "ApprovalRequest", a.Id, new EntityEventPayload(a.Id, a.Type));
        await db.SaveChangesAsync(ct);
        return a;
    }

    public async Task<List<ApprovalDto>> ListAsync(string? status, bool mine, CancellationToken ct = default)
    {
        var q = db.ApprovalRequests.AsNoTracking().AsQueryable();
        if (Enum.TryParse<ApprovalStatus>(status, true, out var st)) q = q.Where(a => a.Status == st);
        if (mine) q = q.Where(a => a.RequestedById == ctx.UserId);
        else if (!ctx.Has(Permissions.ApprovalsDecide)) q = q.Where(a => a.ApproverUserId == ctx.UserId || a.RequestedById == ctx.UserId);
        var rows = await q.OrderByDescending(a => a.CreatedAt).Take(200).ToListAsync(ct);
        return await MapAsync(rows, ct);
    }

    public async Task<ApprovalDto> DecideAsync(Guid id, DecideApprovalRequest r, CancellationToken ct = default)
    {
        var a = await db.ApprovalRequests.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Approval request", id);
        if (a.Status != ApprovalStatus.Pending) throw new ConflictException("already_decided", $"This request is already {a.Status.ToString().ToLowerInvariant()}.");
        var namedApprover = a.ApproverUserId == ctx.UserId ||
                            (a.ApproverRoleId is { } role && await db.UserRoles.AnyAsync(ur => ur.UserId == ctx.UserId && ur.RoleId == role, ct));
        if (!namedApprover) ctx.Require(Permissions.ApprovalsDecide);
        if (a.RequestedById == ctx.UserId) throw new ForbiddenException("You cannot decide your own request.");

        a.Status = r.Approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        a.DecisionNote = r.Note;
        a.DecidedById = ctx.UserId;
        a.DecidedAt = db.Clock.UtcNow;
        if (a.TicketId is { } tid && await db.Tickets.FirstOrDefaultAsync(t => t.Id == tid, ct) is { } ticket)
            history.Record(ticket, TicketEventTypes.ApprovalDecided, $"{a.Type} request {(r.Approve ? "approved" : "rejected")}{(r.Note is null ? "" : ": " + r.Note)}");
        if (r.Approve && actions.FirstOrDefault(x => x.Type == a.Type) is { } action) await action.ApplyAsync(a, ct);
        events.Publish(EventTypes.ApprovalDecided, "ApprovalRequest", a.Id, new EntityEventPayload(a.Id, a.Type));
        await db.SaveChangesAsync(ct);
        return (await MapAsync([a], ct))[0];
    }

    private async Task<List<ApprovalDto>> MapAsync(IReadOnlyList<ApprovalRequest> rows, CancellationToken ct)
    {
        await lookups.LoadAsync(ct);
        var ticketIds = rows.Where(r => r.TicketId.HasValue).Select(r => r.TicketId!.Value).Distinct().ToList();
        var tickets = await db.Tickets.AsNoTracking().Where(t => ticketIds.Contains(t.Id)).Select(t => new { t.Id, t.Number, t.Subject }).ToDictionaryAsync(t => t.Id, ct);
        return rows.Select(a =>
        {
            var t = a.TicketId is { } tid && tickets.TryGetValue(tid, out var x) ? x : null;
            return new ApprovalDto(a.Id, a.Type, a.EntityType, a.EntityId, a.TicketId, t?.Number, t?.Subject, a.RequestedById, lookups.User(a.RequestedById) ?? "",
                a.Status.ToString(), a.Reason, a.DecisionNote, lookups.User(a.DecidedById), a.CreatedAt, a.DecidedAt, a.ExpiresAt, Json.Element(a.PayloadJson));
        }).ToList();
    }
}

/// <summary>Applies an approved department transfer.</summary>
public sealed class TransferApprovalAction(DeskDbContext db, TicketService tickets, RequestContext ctx) : IApprovalAction
{
    public string Type => "transfer";

    public async Task ApplyAsync(ApprovalRequest request, CancellationToken ct)
    {
        var p = Json.Object(request.PayloadJson);
        Guid? G(string k) => p.TryGetValue(k, out var v) && Guid.TryParse(v.GetString(), out var g) ? g : null;
        var department = G("departmentId") ?? throw new DomainException("validation_failed", "The approved transfer has no department.");
        var t = await db.Tickets.Include(x => x.Tags).FirstAsync(x => x.Id == request.EntityId, ct);
        var reason = p.TryGetValue("reason", out var r) ? r.GetString() : null;
        await tickets.TransferCoreAsync(t, department, G("teamId"), G("assigneeId"), $"Approved by {ctx.ActorName}{(reason is null ? "" : $": {reason}")}", ct);
    }
}
