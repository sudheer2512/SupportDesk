using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Domain;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Notifications;

/// <summary>
/// Turns domain events into agent notifications: assignments, mentions, customer replies, escalations, SLA warnings
/// and breaches (assignee + department supervisors, plus escalation-level recipients), approval requests and decisions.
/// The actor who caused an event is never notified about it.
/// </summary>
public sealed class NotificationRulesHandler(DeskDbContext db, NotificationService notifications) : IOutboxHandler
{
    public string Name => "notification-rules";
    public int Order => HandlerOrder.Notifications;

    private static readonly HashSet<string> Types =
    [
        EventTypes.TicketAssigned, EventTypes.TicketNoteAdded, EventTypes.TicketCustomerReplied, EventTypes.TicketEscalated, EventTypes.SlaWarning,
        EventTypes.SlaBreached, EventTypes.SlaEscalated, EventTypes.ApprovalRequested, EventTypes.ApprovalDecided
    ];

    public bool CanHandle(string eventType) => Types.Contains(eventType);

    public async Task HandleAsync(OutboxEvent ev, CancellationToken ct)
    {
        switch (ev.Type)
        {
            case EventTypes.TicketAssigned:
            {
                var p = Json.Deserialize<TicketEventPayload>(ev.PayloadJson)!;
                var t = await Ticket(p.TicketId, ct);
                if (t?.AssigneeId is not { } a || a == ev.ActorId) return;
                await notifications.NotifyAsync(new NotifyRequest([a], NotificationTypes.TicketAssigned, $"#{t.Number} assigned to you", t.Subject,
                    Link(t), Severity(t), false, "Ticket", t.Id, $"{ev.Id}"), ct);
                break;
            }
            case EventTypes.TicketNoteAdded:
            {
                var p = Json.Deserialize<TicketEventPayload>(ev.PayloadJson)!;
                var mentions = p.Changes?.FirstOrDefault(c => c.Field == "mentions")?.To?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(Guid.Parse).Where(id => id != ev.ActorId).ToList();
                if (mentions is not { Count: > 0 }) return;
                var t = await Ticket(p.TicketId, ct);
                if (t is null) return;
                var author = await db.Users.AsNoTracking().Where(u => u.Id == ev.ActorId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? "A colleague";
                await notifications.NotifyAsync(new NotifyRequest(mentions, NotificationTypes.TicketMentioned, $"{author} mentioned you on #{t.Number}", t.Subject,
                    Link(t), NotificationSeverity.Info, false, "Ticket", t.Id, $"{ev.Id}"), ct);
                break;
            }
            case EventTypes.TicketCustomerReplied:
            {
                var p = Json.Deserialize<TicketEventPayload>(ev.PayloadJson)!;
                var t = await Ticket(p.TicketId, ct);
                if (t is null) return;
                var recipients = await db.TicketWatchers.AsNoTracking().Where(w => w.TicketId == t.Id).Select(w => w.UserId).ToListAsync(ct);
                if (t.AssigneeId is { } a) recipients.Add(a);
                await notifications.NotifyAsync(new NotifyRequest(recipients.Distinct().ToList(), NotificationTypes.CustomerReplied, $"Customer replied on #{t.Number}",
                    t.Subject, Link(t), NotificationSeverity.Info, false, "Ticket", t.Id, $"{ev.Id}"), ct);
                break;
            }
            case EventTypes.TicketEscalated:
            {
                var p = Json.Deserialize<TicketEventPayload>(ev.PayloadJson)!;
                var t = await Ticket(p.TicketId, ct);
                if (t is null) return;
                var recipients = await SupervisorsAsync(t.DepartmentId, ct);
                recipients.AddRange(await db.TicketWatchers.AsNoTracking().Where(w => w.TicketId == t.Id && w.Kind == WatcherKind.Specialist).Select(w => w.UserId).ToListAsync(ct));
                if (t.AssigneeId is { } a) recipients.Add(a);
                await notifications.NotifyAsync(new NotifyRequest(recipients.Where(r => r != ev.ActorId).Distinct().ToList(), NotificationTypes.TicketEscalated,
                    $"#{t.Number} escalated (level {t.EscalationLevel})", p.Note ?? t.Subject, Link(t), NotificationSeverity.Warning, false, "Ticket", t.Id, $"{ev.Id}"), ct);
                break;
            }
            case EventTypes.SlaWarning or EventTypes.SlaBreached or EventTypes.SlaEscalated:
            {
                var p = Json.Deserialize<SlaEventPayload>(ev.PayloadJson)!;
                var t = await Ticket(p.TicketId, ct);
                if (t is null) return;
                var breach = ev.Type != EventTypes.SlaWarning;
                var recipients = new List<Guid>();
                if (t.AssigneeId is { } a) recipients.Add(a);
                if (breach) recipients.AddRange(await SupervisorsAsync(t.DepartmentId, ct));
                if (ev.Type == EventTypes.SlaEscalated) recipients.AddRange(await EscalationRecipientsAsync(t, p, ct));
                if (recipients.Count == 0) recipients.AddRange(await SupervisorsAsync(t.DepartmentId, ct));
                var milestone = p.Milestone switch { SlaMilestoneKind.FirstResponse => "first response", SlaMilestoneKind.NextResponse => "next response", _ => "resolution" };
                var title = ev.Type switch
                {
                    EventTypes.SlaWarning => $"#{t.Number}: {milestone} due {p.DueAt:HH:mm} UTC",
                    EventTypes.SlaBreached => $"#{t.Number}: {milestone} SLA breached",
                    _ => $"#{t.Number}: SLA escalation level {p.EscalationLevel}"
                };
                await notifications.NotifyAsync(new NotifyRequest(recipients.Distinct().ToList(), breach ? NotificationTypes.SlaBreached : NotificationTypes.SlaWarning,
                    title, t.Subject, Link(t), breach ? NotificationSeverity.Critical : NotificationSeverity.Warning, breach, "Ticket", t.Id, $"{ev.Id}"), ct);
                break;
            }
            case EventTypes.ApprovalRequested:
            {
                var p = Json.Deserialize<EntityEventPayload>(ev.PayloadJson)!;
                var approval = await db.ApprovalRequests.AsNoTracking().FirstOrDefaultAsync(a => a.Id == p.Id, ct);
                if (approval is null) return;
                var recipients = new List<Guid>();
                if (approval.ApproverUserId is { } u) recipients.Add(u);
                else if (approval.ApproverRoleId is { } roleId)
                    recipients.AddRange(await db.UserRoles.AsNoTracking().Where(r => r.RoleId == roleId).Select(r => r.UserId).ToListAsync(ct));
                else if (approval.TicketId is { } tid && await Ticket(tid, ct) is { } t) recipients.AddRange(await SupervisorsAsync(t.DepartmentId, ct));
                else recipients.AddRange(await UsersWithPermissionAsync(Permissions.ApprovalsDecide, ct));
                await notifications.NotifyAsync(new NotifyRequest(recipients.Where(r => r != approval.RequestedById).Distinct().ToList(), NotificationTypes.ApprovalRequested,
                    $"Approval requested: {approval.Type}", approval.Reason, approval.TicketId is { } ticketId ? $"/tickets/{ticketId}" : "/approvals",
                    NotificationSeverity.Warning, false, "ApprovalRequest", approval.Id, $"{ev.Id}"), ct);
                break;
            }
            case EventTypes.ApprovalDecided:
            {
                var p = Json.Deserialize<EntityEventPayload>(ev.PayloadJson)!;
                var approval = await db.ApprovalRequests.AsNoTracking().FirstOrDefaultAsync(a => a.Id == p.Id, ct);
                if (approval is null) return;
                await notifications.NotifyAsync(new NotifyRequest([approval.RequestedById], NotificationTypes.ApprovalDecided,
                    $"Your {approval.Type} request was {approval.Status.ToString().ToLowerInvariant()}", approval.DecisionNote,
                    approval.TicketId is { } ticketId ? $"/tickets/{ticketId}" : "/approvals", NotificationSeverity.Info, false, "ApprovalRequest", approval.Id, $"{ev.Id}"), ct);
                break;
            }
        }
    }

    private Task<Ticket?> Ticket(Guid id, CancellationToken ct) => db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);

    private async Task<List<Guid>> SupervisorsAsync(Guid departmentId, CancellationToken ct) =>
        await db.AgentDepartments.AsNoTracking().Where(d => d.DepartmentId == departmentId && d.IsSupervisor).Select(d => d.UserId).ToListAsync(ct);

    private async Task<List<Guid>> UsersWithPermissionAsync(string permission, CancellationToken ct) =>
        await db.UserRoles.AsNoTracking().Where(ur => db.RolePermissions.Any(p => p.RoleId == ur.RoleId && p.PermissionCode == permission))
            .Select(ur => ur.UserId).Distinct().ToListAsync(ct);

    private async Task<List<Guid>> EscalationRecipientsAsync(Ticket t, SlaEventPayload p, CancellationToken ct)
    {
        var instance = await db.SlaInstances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == p.InstanceId, ct);
        if (instance is null) return [];
        var snapshot = Sla.SlaSnapshot.Policy(instance.PolicySnapshotJson);
        var level = snapshot.Escalations.FirstOrDefault(e => e.Milestone == p.Milestone && e.Level == p.EscalationLevel);
        if (level is null) return [];
        var notify = Json.Object(level.NotifyJson);
        var result = new List<Guid>();
        if (notify.TryGetValue("userIds", out var users) && users.ValueKind == System.Text.Json.JsonValueKind.Array)
            result.AddRange(users.EnumerateArray().Select(u => Guid.TryParse(u.GetString(), out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty));
        if (notify.TryGetValue("roleIds", out var roles) && roles.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var roleIds = roles.EnumerateArray().Select(r => Guid.TryParse(r.GetString(), out var g) ? g : Guid.Empty).ToList();
            result.AddRange(await db.UserRoles.AsNoTracking().Where(r => roleIds.Contains(r.RoleId)).Select(r => r.UserId).ToListAsync(ct));
        }
        if (notify.TryGetValue("notifySupervisors", out var sup) && sup.ValueKind == System.Text.Json.JsonValueKind.True)
            result.AddRange(await SupervisorsAsync(t.DepartmentId, ct));
        if (notify.TryGetValue("notifyAssignee", out var asg) && asg.ValueKind == System.Text.Json.JsonValueKind.True && t.AssigneeId is { } a) result.Add(a);
        return result;
    }

    private static string Link(Ticket t) => $"/tickets/{t.Id}";

    private static NotificationSeverity Severity(Ticket t) => t.Priority >= TicketPriority.High ? NotificationSeverity.Warning : NotificationSeverity.Info;
}
