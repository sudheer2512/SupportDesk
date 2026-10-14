using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Service;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Tickets;

/// <summary>
/// Watchers/collaborators, tasks/subtasks/checklists, time tracking (billable cost, prepaid-hour consumption),
/// follow-up reminders, saved views, reply templates and attachment access.
/// </summary>
public sealed class TicketCollaborationService(
    DeskDbContext db,
    RequestContext ctx,
    TicketService tickets,
    TicketHistory history,
    IEventPublisher events,
    HtmlSanitization html,
    TicketLookups lookups,
    TicketQueryService queries,
    FileService files)
{
    // ───── Watchers ─────

    public async Task<IReadOnlyList<WatcherDto>> AddWatcherAsync(Guid ticketId, AddWatcherRequest r, CancellationToken ct = default)
    {
        var t = await tickets.LoadForUpdateAsync(ticketId, ct);
        if (r.UserId != ctx.UserId) ctx.Require(Permissions.TicketsEdit);
        if (!await db.Users.AnyAsync(u => u.Id == r.UserId && u.IsActive, ct)) throw new NotFoundException("User", r.UserId);
        var kind = TicketService.ParseEnum(r.Kind, WatcherKind.Watcher, "kind");
        if (!await db.TicketWatchers.AnyAsync(w => w.TicketId == ticketId && w.UserId == r.UserId, ct))
        {
            db.TicketWatchers.Add(new TicketWatcher { TicketId = ticketId, UserId = r.UserId, Kind = kind, AddedById = ctx.UserId, AddedAt = db.Clock.UtcNow, ExpiresAt = r.ExpiresAt });
            await lookups.LoadAsync(ct);
            history.Record(t, TicketEventTypes.WatcherAdded, $"{lookups.User(r.UserId)} added as {kind.ToString().ToLowerInvariant()}");
            events.Publish(EventTypes.TicketUpdated, "Ticket", t.Id, new TicketEventPayload(t.Id, t.Number, [new FieldChange("watchers", null, r.UserId.ToString())]));
            await db.SaveChangesAsync(ct);
        }
        return (await queries.GetDetailAsync(ticketId, ct)).Watchers;
    }

    public async Task RemoveWatcherAsync(Guid ticketId, Guid userId, CancellationToken ct = default)
    {
        var t = await tickets.LoadForUpdateAsync(ticketId, ct);
        if (userId != ctx.UserId) ctx.Require(Permissions.TicketsEdit);
        var w = await db.TicketWatchers.FirstOrDefaultAsync(x => x.TicketId == ticketId && x.UserId == userId, ct);
        if (w is null) return;
        db.TicketWatchers.Remove(w);
        await lookups.LoadAsync(ct);
        history.Record(t, TicketEventTypes.WatcherRemoved, $"{lookups.User(userId)} removed from watchers");
        await db.SaveChangesAsync(ct);
    }

    // ───── Tasks, subtasks, checklists ─────

    public async Task<List<TaskDto>> ListTasksAsync(Guid ticketId, CancellationToken ct = default)
    {
        await tickets.LoadForUpdateAsync(ticketId, ct);
        await lookups.LoadAsync(ct);
        var all = await db.TicketTasks.AsNoTracking().Where(x => x.TicketId == ticketId).OrderBy(x => x.SortOrder).ThenBy(x => x.CreatedAt).ToListAsync(ct);
        TaskDto Map(TicketTask x) => new(x.Id, x.TicketId, x.ParentTaskId, x.Title, x.Description, x.IsChecklistItem, x.AssigneeId, lookups.User(x.AssigneeId),
            x.DueAt, x.Status.ToString(), x.CompletedAt, x.SortOrder, all.Where(c => c.ParentTaskId == x.Id).Select(Map).ToList());
        return all.Where(x => x.ParentTaskId == null).Select(Map).ToList();
    }

    public async Task<TaskDto> SaveTaskAsync(Guid ticketId, Guid? taskId, SaveTaskRequest r, CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.TicketsEdit, Permissions.TicketsNote);
        var t = await tickets.LoadForUpdateAsync(ticketId, ct);
        if (string.IsNullOrWhiteSpace(r.Title)) throw new ValidationException(new Dictionary<string, string> { ["title"] = "A title is required." });
        TicketTask task;
        if (taskId is { } id)
        {
            task = await db.TicketTasks.FirstOrDefaultAsync(x => x.Id == id && x.TicketId == ticketId, ct) ?? throw new NotFoundException("Task", id);
        }
        else
        {
            task = new TicketTask { TicketId = ticketId, SortOrder = await db.TicketTasks.CountAsync(x => x.TicketId == ticketId, ct) };
            db.TicketTasks.Add(task);
        }
        if (r.ParentTaskId is { } parent && parent != task.Id && !await db.TicketTasks.AnyAsync(x => x.Id == parent && x.TicketId == ticketId, ct))
            throw new ValidationException(new Dictionary<string, string> { ["parentTaskId"] = "Unknown parent task." });
        task.Title = r.Title.Trim();
        task.Description = r.Description;
        task.IsChecklistItem = r.IsChecklistItem;
        task.ParentTaskId = r.ParentTaskId;
        task.AssigneeId = r.AssigneeId;
        task.DueAt = r.DueAt;
        if (r.SortOrder is { } so) task.SortOrder = so;
        if (r.Status is { } st)
        {
            var status = TicketService.ParseEnum(st, task.Status, "status");
            if (status == TaskItemStatus.Done && task.Status != TaskItemStatus.Done)
            {
                task.CompletedAt = db.Clock.UtcNow;
                task.CompletedById = ctx.UserId;
            }
            else if (status != TaskItemStatus.Done) task.CompletedAt = null;
            task.Status = status;
        }
        t.LastActivityAt = db.Clock.UtcNow;
        history.Record(t, TicketEventTypes.TaskChanged, $"{(taskId is null ? "Added" : "Updated")} {(task.IsChecklistItem ? "checklist item" : "task")} \"{task.Title}\" ({task.Status})");
        await db.SaveChangesAsync(ct);
        return (await ListTasksAsync(ticketId, ct)).SelectMany(x => x.Subtasks.Prepend(x)).First(x => x.Id == task.Id);
    }

    public async Task DeleteTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsEdit);
        var task = await db.TicketTasks.FirstOrDefaultAsync(x => x.Id == taskId, ct) ?? throw new NotFoundException("Task", taskId);
        var t = await tickets.LoadForUpdateAsync(task.TicketId, ct);
        await db.TicketTasks.Where(x => x.ParentTaskId == taskId).ExecuteDeleteAsync(ct);
        db.TicketTasks.Remove(task);
        history.Record(t, TicketEventTypes.TaskChanged, $"Removed task \"{task.Title}\"");
        await db.SaveChangesAsync(ct);
    }

    public async Task<TaskDto?> FindTaskTicketAsync(Guid taskId, CancellationToken ct) =>
        await db.TicketTasks.AsNoTracking().Where(x => x.Id == taskId).Select(x => new TaskDto(x.Id, x.TicketId, x.ParentTaskId, x.Title, x.Description,
            x.IsChecklistItem, x.AssigneeId, null, x.DueAt, x.Status.ToString(), x.CompletedAt, x.SortOrder, new List<TaskDto>())).FirstOrDefaultAsync(ct);

    // ───── Time tracking ─────

    public async Task<List<TimeEntryDto>> ListTimeAsync(Guid ticketId, CancellationToken ct = default)
    {
        await tickets.LoadForUpdateAsync(ticketId, ct);
        await lookups.LoadAsync(ct);
        var costs = ctx.Has(Permissions.TicketsViewCosts);
        var rows = await db.TimeEntries.AsNoTracking().Where(x => x.TicketId == ticketId).OrderByDescending(x => x.StartedAt).ToListAsync(ct);
        var contractIds = rows.Where(r => r.ContractId.HasValue).Select(r => r.ContractId!.Value).Distinct().ToList();
        var contracts = await db.Contracts.AsNoTracking().Where(c => contractIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Number, ct);
        return rows.Select(x => new TimeEntryDto(x.Id, x.UserId, lookups.User(x.UserId) ?? "", x.StartedAt, x.Minutes, x.IsBillable, x.ActivityType, x.Notes,
            costs ? x.CostAmount : null, costs ? x.BillableAmount : null, x.ContractId is { } cid && contracts.TryGetValue(cid, out var n) ? n : null)).ToList();
    }

    public async Task<TimeEntryDto> LogTimeAsync(Guid ticketId, LogTimeRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsTime);
        if (r.Minutes is <= 0 or > 24 * 60) throw new ValidationException(new Dictionary<string, string> { ["minutes"] = "Enter between 1 and 1440 minutes." });
        var t = await tickets.LoadForUpdateAsync(ticketId, ct);
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.UserId == ctx.UserId, ct);
        var contract = t.ContractId is null ? null : await db.Contracts.FirstOrDefaultAsync(c => c.Id == t.ContractId, ct);
        var hourlyCost = agent?.HourlyCost ?? 0m;
        var billRate = contract?.HourlyRate ?? 0m;
        var hours = r.Minutes / 60m;
        var entry = new TimeEntry
        {
            TicketId = ticketId, UserId = ctx.UserId ?? Guid.Empty, StartedAt = r.StartedAt ?? db.Clock.UtcNow.AddMinutes(-r.Minutes), Minutes = r.Minutes,
            IsBillable = r.IsBillable, ActivityType = r.ActivityType, Notes = r.Notes, HourlyRate = billRate, CostAmount = Math.Round(hourlyCost * hours, 2),
            BillableAmount = r.IsBillable ? Math.Round(billRate * hours, 2) : 0m, ContractId = contract?.Id, EntitlementId = t.EntitlementId
        };
        db.TimeEntries.Add(entry);
        t.TimeSpentMinutes += r.Minutes;
        if (r.IsBillable) t.BillableMinutes += r.Minutes;
        t.SupportCost += entry.CostAmount;
        t.LastActivityAt = db.Clock.UtcNow;
        if (contract is not null && r.IsBillable && contract.Type == ContractType.PrepaidHours) contract.ConsumedHours += Math.Round(hours, 2);
        if (t.EntitlementId is { } eid && r.IsBillable)
            await db.Entitlements.Where(e => e.Id == eid && e.HoursAllowed != null).ExecuteUpdateAsync(u => u.SetProperty(e => e.HoursUsed, e => e.HoursUsed + Math.Round(hours, 2)), ct);
        history.Record(t, TicketEventTypes.TimeLogged, $"Logged {r.Minutes} min{(r.IsBillable ? " (billable)" : "")}{(r.ActivityType is null ? "" : $" — {r.ActivityType}")}");
        await db.SaveChangesAsync(ct);
        return (await ListTimeAsync(ticketId, ct)).First(x => x.Id == entry.Id);
    }

    public async Task DeleteTimeAsync(Guid entryId, CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsTime);
        var e = await db.TimeEntries.FirstOrDefaultAsync(x => x.Id == entryId, ct) ?? throw new NotFoundException("Time entry", entryId);
        if (e.UserId != ctx.UserId) ctx.Require(Permissions.SupervisorMonitor);
        var t = await tickets.LoadForUpdateAsync(e.TicketId, ct);
        t.TimeSpentMinutes = Math.Max(0, t.TimeSpentMinutes - e.Minutes);
        if (e.IsBillable) t.BillableMinutes = Math.Max(0, t.BillableMinutes - e.Minutes);
        t.SupportCost = Math.Max(0, t.SupportCost - e.CostAmount);
        if (e.ContractId is { } cid && e.IsBillable)
        {
            var c = await db.Contracts.FirstOrDefaultAsync(x => x.Id == cid, ct);
            if (c is { Type: ContractType.PrepaidHours }) c.ConsumedHours = Math.Max(0, c.ConsumedHours - Math.Round(e.Minutes / 60m, 2));
        }
        db.TimeEntries.Remove(e);
        history.Record(t, TicketEventTypes.TimeLogged, $"Removed a {e.Minutes}-minute time entry");
        await db.SaveChangesAsync(ct);
    }

    // ───── Follow-ups ─────

    public async Task<FollowUpDto> ScheduleFollowUpAsync(Guid ticketId, CreateFollowUpRequest r, CancellationToken ct = default)
    {
        var t = await tickets.LoadForUpdateAsync(ticketId, ct);
        if (r.RemindAt <= db.Clock.UtcNow) throw new ValidationException(new Dictionary<string, string> { ["remindAt"] = "Choose a time in the future." });
        var userId = r.UserId ?? ctx.UserId ?? throw new ForbiddenException();
        if (userId != ctx.UserId) ctx.Require(Permissions.TicketsAssign);
        var f = new TicketFollowUp { TicketId = ticketId, UserId = userId, RemindAt = r.RemindAt, Note = r.Note };
        db.TicketFollowUps.Add(f);
        await lookups.LoadAsync(ct);
        history.Record(t, TicketEventTypes.FollowUpScheduled, $"Follow-up for {lookups.User(userId)} at {r.RemindAt:yyyy-MM-dd HH:mm} UTC");
        await db.SaveChangesAsync(ct);
        return new FollowUpDto(f.Id, f.UserId, lookups.User(f.UserId) ?? "", f.RemindAt, f.Note, f.Status.ToString());
    }

    public async Task<List<FollowUpDto>> ListFollowUpsAsync(Guid ticketId, CancellationToken ct = default)
    {
        await tickets.LoadForUpdateAsync(ticketId, ct);
        await lookups.LoadAsync(ct);
        return (await db.TicketFollowUps.AsNoTracking().Where(f => f.TicketId == ticketId).OrderBy(f => f.RemindAt).ToListAsync(ct))
            .Select(f => new FollowUpDto(f.Id, f.UserId, lookups.User(f.UserId) ?? "", f.RemindAt, f.Note, f.Status.ToString())).ToList();
    }

    public async Task CancelFollowUpAsync(Guid followUpId, CancellationToken ct = default)
    {
        var f = await db.TicketFollowUps.FirstOrDefaultAsync(x => x.Id == followUpId, ct) ?? throw new NotFoundException("Follow-up", followUpId);
        if (f.UserId != ctx.UserId) ctx.Require(Permissions.TicketsAssign);
        f.Status = FollowUpStatus.Cancelled;
        await db.SaveChangesAsync(ct);
    }

    // ───── Saved views ─────

    public async Task<List<SavedViewDto>> ListViewsAsync(string scope, bool withCounts, CancellationToken ct = default)
    {
        var views = await db.SavedViews.AsNoTracking().Where(v => v.Scope == scope && (v.Visibility == ViewVisibility.Shared || v.OwnerUserId == ctx.UserId))
            .OrderBy(v => v.SortOrder).ThenBy(v => v.Name).ToListAsync(ct);
        var result = new List<SavedViewDto>();
        foreach (var v in views)
        {
            long? count = null;
            if (withCounts && scope == "tickets")
            {
                try { count = await queries.ApplyViewFilter(queries.Scoped(), v.FilterJson).LongCountAsync(ct); }
                catch (DomainException) { count = null; }
            }
            result.Add(new SavedViewDto(v.Id, v.Scope, v.Name, v.Visibility.ToString(), v.OwnerUserId == ctx.UserId, Json.Element(v.FilterJson),
                Json.ElementOrNull(v.ColumnsJson), Json.ElementOrNull(v.SortJson), v.IsDefault, v.SortOrder, count));
        }
        return result;
    }

    public async Task<SavedViewDto> SaveViewAsync(Guid? id, SaveViewRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsView);
        var visibility = TicketService.ParseEnum(r.Visibility, ViewVisibility.Personal, "visibility");
        if (visibility == ViewVisibility.Shared) ctx.Require(Permissions.SupervisorMonitor);
        var filterJson = r.Filter.GetRawText();
        if (r.Scope == "tickets") _ = TicketFilterTranslator.Translate(ConditionGroup.Parse(filterJson), db.Clock.UtcNow); // validate now, not at query time
        SavedView v;
        if (id is { } vid)
        {
            v = await db.SavedViews.FirstOrDefaultAsync(x => x.Id == vid, ct) ?? throw new NotFoundException("View", vid);
            if (v.OwnerUserId != ctx.UserId && !ctx.Has(Permissions.SupervisorMonitor)) throw new ForbiddenException("Only the owner can change this view.");
        }
        else
        {
            v = new SavedView { OwnerUserId = ctx.UserId };
            db.SavedViews.Add(v);
        }
        v.Scope = r.Scope;
        v.Name = r.Name.Trim();
        v.Visibility = visibility;
        v.FilterJson = filterJson;
        v.ColumnsJson = r.Columns?.GetRawText();
        v.SortJson = r.Sort?.GetRawText();
        v.IsDefault = r.IsDefault;
        v.SortOrder = r.SortOrder;
        // One default per owner among personal views, and one shared default per scope. The desk opens the caller's
        // personal default first, then the shared default (when the URL names no queue/view).
        if (r.IsDefault)
        {
            var shared = visibility == ViewVisibility.Shared;
            var owner = v.OwnerUserId;
            var keep = v.Id;
            await db.SavedViews.Where(x => x.Id != keep && x.Scope == r.Scope && x.IsDefault &&
                                           (shared ? x.Visibility == ViewVisibility.Shared : x.Visibility == ViewVisibility.Personal && x.OwnerUserId == owner))
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.IsDefault, false), ct);
        }
        await db.SaveChangesAsync(ct);
        return (await ListViewsAsync(r.Scope, false, ct)).First(x => x.Id == v.Id);
    }

    public async Task DeleteViewAsync(Guid id, CancellationToken ct = default)
    {
        var v = await db.SavedViews.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("View", id);
        if (v.OwnerUserId != ctx.UserId && !ctx.Has(Permissions.SupervisorMonitor)) throw new ForbiddenException("Only the owner can delete this view.");
        db.SavedViews.Remove(v);
        await db.SaveChangesAsync(ct);
    }

    // ───── Reply templates (canned responses / macros) ─────

    public async Task<List<ReplyTemplate>> ListTemplatesAsync(CancellationToken ct = default)
    {
        var depts = ctx.DepartmentIds.ToList();
        var teams = ctx.TeamIds.ToList();
        return await db.ReplyTemplates.AsNoTracking().Where(t => t.IsActive && (t.Visibility == TemplateVisibility.Global ||
                (t.Visibility == TemplateVisibility.Personal && t.OwnerUserId == ctx.UserId) ||
                (t.Visibility == TemplateVisibility.Department && t.DepartmentId != null && depts.Contains(t.DepartmentId.Value)) ||
                (t.Visibility == TemplateVisibility.Team && t.TeamId != null && teams.Contains(t.TeamId.Value))))
            .OrderByDescending(t => t.UsageCount).ThenBy(t => t.Name).ToListAsync(ct);
    }

    public async Task<ReplyTemplate> SaveTemplateAsync(Guid? id, string name, string? category, string? subject, string bodyHtml, string visibility,
        Guid? departmentId, Guid? teamId, string? actionsJson, CancellationToken ct = default)
    {
        var vis = TicketService.ParseEnum(visibility, TemplateVisibility.Personal, "visibility");
        if (vis != TemplateVisibility.Personal) ctx.RequireAny(Permissions.SupervisorMonitor, Permissions.AdminTicketConfig);
        ReplyTemplate t;
        if (id is { } tid)
        {
            t = await db.ReplyTemplates.FirstOrDefaultAsync(x => x.Id == tid, ct) ?? throw new NotFoundException("Template", tid);
            if (t.OwnerUserId != ctx.UserId) ctx.RequireAny(Permissions.SupervisorMonitor, Permissions.AdminTicketConfig);
        }
        else
        {
            t = new ReplyTemplate { OwnerUserId = ctx.UserId };
            db.ReplyTemplates.Add(t);
        }
        t.Name = name.Trim();
        t.Category = category;
        t.Subject = subject;
        t.BodyHtml = html.Sanitize(bodyHtml);
        t.Visibility = vis;
        t.DepartmentId = departmentId;
        t.TeamId = teamId;
        t.ActionsJson = string.IsNullOrWhiteSpace(actionsJson) ? "[]" : actionsJson;
        await db.SaveChangesAsync(ct);
        return t;
    }

    public async Task DeleteTemplateAsync(Guid id, CancellationToken ct = default)
    {
        var t = await db.ReplyTemplates.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Template", id);
        if (t.OwnerUserId != ctx.UserId) ctx.RequireAny(Permissions.SupervisorMonitor, Permissions.AdminTicketConfig);
        t.IsActive = false;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Renders template placeholders ({{contact.name}}, {{ticket.number}}, {{agent.name}}, {{ticket.subject}}, {{customer.name}}).</summary>
    public async Task<string> RenderTemplateAsync(Guid templateId, Guid ticketId, CancellationToken ct = default)
    {
        var template = await db.ReplyTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == templateId, ct) ?? throw new NotFoundException("Template", templateId);
        var detail = await queries.GetDetailAsync(ticketId, ct);
        string E(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        return template.BodyHtml
            .Replace("{{contact.name}}", E(detail.ContactName?.Split(' ')[0] ?? "there"))
            .Replace("{{contact.fullName}}", E(detail.ContactName))
            .Replace("{{ticket.number}}", E(detail.DisplayNumber))
            .Replace("{{ticket.subject}}", E(detail.Subject))
            .Replace("{{customer.name}}", E(detail.CustomerName))
            .Replace("{{agent.name}}", E(ctx.ActorName));
    }

    // ───── Attachments ─────

    /// <summary>Opens an attachment after checking the caller may see its ticket (agents: scope; contacts: public + portal scope via the portal service).</summary>
    public async Task<(TicketAttachment Attachment, Stream Content)> OpenAttachmentAsync(Guid attachmentId, CancellationToken ct = default)
    {
        var a = await db.TicketAttachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == attachmentId, ct) ?? throw new NotFoundException("Attachment", attachmentId);
        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == a.TicketId, ct) ?? throw new NotFoundException("Attachment", attachmentId);
        await tickets.EnsureVisibleAsync(t, ct);
        var (_, stream) = await files.OpenAsync(a.FileId, ct);
        return (a, stream);
    }

    public async Task<List<AttachmentDto>> ListAttachmentsAsync(Guid ticketId, CancellationToken ct = default)
    {
        await tickets.LoadForUpdateAsync(ticketId, ct);
        return await db.TicketAttachments.AsNoTracking().Where(a => a.TicketId == ticketId).OrderBy(a => a.CreatedAt)
            .Join(db.StoredFiles.AsNoTracking(), a => a.FileId, f => f.Id, (a, f) => new AttachmentDto(a.Id, a.FileId, a.FileName, a.ContentType, a.SizeBytes,
                a.Visibility.ToString(), a.IsInline, f.ScanStatus.ToString(), a.CreatedAt, "/api/v1/attachments/" + a.Id + "/download"))
            .ToListAsync(ct);
    }
}
