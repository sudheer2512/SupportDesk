using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Realtime;

/// <summary>
/// Pushes committed ticket and SLA changes to connected agents: the ticket's own group (viewers), its department
/// group (queues) and the assignee. Internal notes are announced only to agent groups (never to customers).
/// Portal contacts receive public conversation updates for their own tickets.
/// </summary>
public sealed class RealtimeOutboxHandler(DeskDbContext db, IRealtimePublisher realtime) : IOutboxHandler
{
    public string Name => "realtime";
    public int Order => HandlerOrder.Realtime;

    public bool CanHandle(string eventType) => eventType.StartsWith("ticket.", StringComparison.Ordinal) || eventType.StartsWith("sla.", StringComparison.Ordinal);

    public async Task HandleAsync(OutboxEvent ev, CancellationToken ct)
    {
        if (ev.TenantId is not { } tenantId) return;
        if (ev.Type.StartsWith("sla.", StringComparison.Ordinal))
        {
            var sp = Json.Deserialize<SlaEventPayload>(ev.PayloadJson)!;
            var st = await db.Tickets.AsNoTracking().Where(t => t.Id == sp.TicketId).Select(t => new { t.DepartmentId, t.AssigneeId }).FirstOrDefaultAsync(ct);
            if (st is null) return;
            var payload = new { ticketId = sp.TicketId, number = sp.Number, milestone = sp.Milestone.ToString(), kind = ev.Type == EventTypes.SlaWarning ? "warning" : "breach", dueAt = sp.DueAt };
            await realtime.ToDepartmentsAsync(tenantId, [st.DepartmentId], "sla.alert", payload, ct);
            await realtime.ToTicketAsync(tenantId, sp.TicketId, "sla.alert", payload, ct);
            return;
        }

        var p = Json.Deserialize<TicketEventPayload>(ev.PayloadJson);
        if (p is null) return;
        var ticket = await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(t => t.Id == p.TicketId).Select(t => new { t.DepartmentId, t.AssigneeId, t.ContactId }).FirstOrDefaultAsync(ct);
        if (ticket is null) return;
        var actor = ev.ActorId is { } aid ? await db.Users.AsNoTracking().Where(u => u.Id == aid).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) : null;
        var changed = new { ticketId = p.TicketId, number = p.Number, type = ev.Type, changes = p.Changes ?? [], actorName = actor ?? (ev.ActorType == Domain.ActorType.Contact ? "Customer" : "System") };
        await realtime.ToTicketAsync(tenantId, p.TicketId, "ticket.changed", changed, ct);
        await realtime.ToDepartmentsAsync(tenantId, [ticket.DepartmentId], "ticket.changed", changed, ct);
        if (ticket.AssigneeId is { } assignee) await realtime.ToUsersAsync(tenantId, [assignee], "ticket.changed", changed, ct);

        if (p.ThreadId is { } threadId)
        {
            var thread = await db.TicketThreads.AsNoTracking().Where(t => t.Id == threadId).Select(t => new { t.Seq, t.Kind, t.Visibility }).FirstOrDefaultAsync(ct);
            if (thread is null) return;
            var payload = new { ticketId = p.TicketId, threadId, seq = thread.Seq, kind = thread.Kind.ToString(), visibility = thread.Visibility.ToString() };
            await realtime.ToTicketAsync(tenantId, p.TicketId, "ticket.thread", payload, ct);
            if (thread.Visibility == Domain.Visibility.Public && ticket.ContactId is { } contactId)
                await realtime.ToContactAsync(tenantId, contactId, "ticket.thread", payload, ct);
        }
        else if (ev.Type == EventTypes.TicketStatusChanged && ticket.ContactId is { } contactId)
        {
            await realtime.ToContactAsync(tenantId, contactId, "ticket.changed", new { ticketId = p.TicketId, number = p.Number }, ct);
        }
    }
}
