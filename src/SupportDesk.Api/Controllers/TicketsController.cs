using Microsoft.AspNetCore.Mvc;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Tickets;

namespace SupportDesk.Api.Controllers;

/// <summary>Ticket lifecycle, conversation, collaboration and workbench context.</summary>
[Route("api/v1/tickets")]
public sealed class TicketsController(
    TicketService tickets,
    TicketQueryService queries,
    ConversationService conversation,
    TicketCollaborationService collaboration) : DeskControllerBase
{
    // ───── Read ─────

    [HttpGet]
    public Task<PagedResult<TicketListItemDto>> List([FromQuery] TicketQuery query, CancellationToken ct) => queries.ListAsync(query, ct);

    [HttpGet("queue-counts")]
    public Task<Dictionary<string, int>> QueueCounts(CancellationToken ct) => queries.QueueCountsAsync(ct);

    [HttpGet("{id:guid}")]
    public Task<TicketDetailDto> Get(Guid id, CancellationToken ct) => queries.GetDetailAsync(id, ct);

    [HttpGet("by-number/{number:long}")]
    public Task<TicketDetailDto> GetByNumber(long number, CancellationToken ct) => queries.GetByNumberAsync(number, ct);

    [HttpGet("{id:guid}/context")]
    public Task<TicketContextDto> Context(Guid id, CancellationToken ct) => queries.ContextAsync(id, ct);

    [HttpGet("{id:guid}/timeline")]
    public async Task<List<TimelineItemDto>> Timeline(Guid id, [FromQuery] bool includeEvents = true, CancellationToken ct = default)
    {
        await queries.GetDetailAsync(id, ct); // access check
        return await conversation.TimelineAsync(id, includeEvents, internalView: true, ct);
    }

    // ───── Lifecycle ─────

    [HttpPost]
    public async Task<ActionResult<TicketDetailDto>> Create(CreateTicketRequest request, CancellationToken ct)
    {
        var t = await tickets.CreateAsync(request, TicketCreationSource.Agent, ct);
        return CreatedAtAction(nameof(Get), new { id = t.Id }, await queries.GetDetailAsync(t.Id, ct));
    }

    [HttpPatch("{id:guid}")]
    public async Task<TicketDetailDto> Update(Guid id, UpdateTicketRequest request, CancellationToken ct)
    {
        await tickets.UpdateAsync(id, request, ct);
        return await queries.GetDetailAsync(id, ct);
    }

    [HttpPost("{id:guid}/assign")]
    public async Task<TicketDetailDto> Assign(Guid id, AssignTicketRequest request, CancellationToken ct)
    {
        await tickets.AssignAsync(id, request, ct);
        return await queries.GetDetailAsync(id, ct);
    }

    [HttpPost("{id:guid}/transfer")]
    public async Task<TicketDetailDto> Transfer(Guid id, TransferTicketRequest request, CancellationToken ct)
    {
        await tickets.TransferAsync(id, request, ct);
        return await queries.GetDetailAsync(id, ct);
    }

    [HttpPost("{id:guid}/status")]
    public async Task<TicketDetailDto> Status(Guid id, ChangeStatusRequest request, CancellationToken ct)
    {
        await tickets.ChangeStatusAsync(id, request, ct);
        return await queries.GetDetailAsync(id, ct);
    }

    [HttpPost("{id:guid}/escalate")]
    public async Task<TicketDetailDto> Escalate(Guid id, EscalateTicketRequest request, CancellationToken ct)
    {
        await tickets.EscalateAsync(id, request, ct);
        return await queries.GetDetailAsync(id, ct);
    }

    [HttpPost("{id:guid}/resolve")]
    public async Task<TicketDetailDto> Resolve(Guid id, ResolveTicketRequest request, CancellationToken ct)
    {
        await tickets.ResolveAsync(id, request, ct);
        return await queries.GetDetailAsync(id, ct);
    }

    [HttpPost("{id:guid}/reopen")]
    public async Task<TicketDetailDto> Reopen(Guid id, ReopenTicketRequest request, CancellationToken ct)
    {
        await tickets.ReopenAsync(id, request, ct);
        return await queries.GetDetailAsync(id, ct);
    }

    [HttpPost("{id:guid}/close")]
    public async Task<TicketDetailDto> Close(Guid id, CancellationToken ct)
    {
        await tickets.CloseAsync(id, ct);
        return await queries.GetDetailAsync(id, ct);
    }

    [HttpPost("{id:guid}/merge")]
    public async Task<TicketDetailDto> Merge(Guid id, MergeTicketsRequest request, CancellationToken ct)
    {
        await tickets.MergeAsync(id, request, ct);
        return await queries.GetDetailAsync(id, ct);
    }

    [HttpPost("{id:guid}/split")]
    public async Task<TicketDetailDto> Split(Guid id, SplitTicketRequest request, CancellationToken ct)
    {
        var created = await tickets.SplitAsync(id, request, ct);
        return await queries.GetDetailAsync(created.Id, ct);
    }

    [HttpPost("{id:guid}/clone")]
    public async Task<TicketDetailDto> Clone(Guid id, CancellationToken ct)
    {
        var created = await tickets.CloneAsync(id, ct);
        return await queries.GetDetailAsync(created.Id, ct);
    }

    [HttpPost("{id:guid}/links")]
    public async Task<TicketDetailDto> Link(Guid id, LinkTicketRequest request, CancellationToken ct)
    {
        await tickets.LinkAsync(id, request, ct);
        return await queries.GetDetailAsync(id, ct);
    }

    [HttpDelete("{id:guid}/links/{linkId:guid}")]
    public async Task<IActionResult> Unlink(Guid id, Guid linkId, CancellationToken ct)
    {
        await tickets.UnlinkAsync(id, linkId, ct);
        return NoContent();
    }

    [HttpPut("{id:guid}/tags")]
    public async Task<TicketDetailDto> Tags(Guid id, SetTagsRequest request, CancellationToken ct)
    {
        await tickets.SetTagsAsync(id, request.Tags, ct);
        return await queries.GetDetailAsync(id, ct);
    }

    [HttpPost("{id:guid}/lock")]
    public Task<TicketLockInfo> Lock(Guid id, CancellationToken ct) => tickets.LockAsync(id, ct);

    [HttpPost("{id:guid}/unlock")]
    public async Task<IActionResult> Unlock(Guid id, CancellationToken ct)
    {
        await tickets.UnlockAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/override-entitlement")]
    public async Task<TicketDetailDto> OverrideEntitlement(Guid id, OverrideEntitlementRequest request, CancellationToken ct)
    {
        await tickets.OverrideEntitlementAsync(id, request, ct);
        return await queries.GetDetailAsync(id, ct);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] bool spam = false, CancellationToken ct = default)
    {
        await tickets.DeleteAsync(id, spam, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/restore")]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct)
    {
        await tickets.RestoreAsync(id, ct);
        return NoContent();
    }

    [HttpPost("bulk")]
    public Task<BulkResultDto> Bulk(BulkTicketRequest request, CancellationToken ct) => tickets.BulkAsync(request, ct);

    // ───── Conversation ─────

    [HttpPost("{id:guid}/replies")]
    public async Task<ActionResult<ThreadDto>> Reply(Guid id, ReplyRequest request, CancellationToken ct)
    {
        var thread = await conversation.ReplyAsync(id, request, ct);
        return (await conversation.MapThreadsAsync([thread], internalView: true, ct))[0];
    }

    [HttpPost("{id:guid}/notes")]
    public async Task<ActionResult<ThreadDto>> Note(Guid id, NoteRequest request, CancellationToken ct)
    {
        var thread = await conversation.AddNoteAsync(id, request, ct);
        return (await conversation.MapThreadsAsync([thread], internalView: true, ct))[0];
    }

    [HttpGet("{id:guid}/draft")]
    public async Task<ActionResult<DraftDto>> GetDraft(Guid id, [FromQuery] string kind = "reply", CancellationToken ct = default) =>
        await conversation.GetDraftAsync(id, kind, ct) is { } d ? d : NoContent();

    [HttpPut("{id:guid}/draft")]
    public Task<DraftDto> SaveDraft(Guid id, SaveDraftRequest request, CancellationToken ct) => conversation.SaveDraftAsync(id, request, ct);

    [HttpDelete("{id:guid}/draft")]
    public async Task<IActionResult> DeleteDraft(Guid id, [FromQuery] string kind = "reply", CancellationToken ct = default)
    {
        await conversation.DeleteDraftAsync(id, kind, ct);
        return NoContent();
    }

    // ───── Collaboration ─────

    [HttpGet("{id:guid}/attachments")]
    public Task<List<AttachmentDto>> Attachments(Guid id, CancellationToken ct) => collaboration.ListAttachmentsAsync(id, ct);

    [HttpPost("{id:guid}/watchers")]
    public Task<IReadOnlyList<WatcherDto>> AddWatcher(Guid id, AddWatcherRequest request, CancellationToken ct) => collaboration.AddWatcherAsync(id, request, ct);

    [HttpDelete("{id:guid}/watchers/{userId:guid}")]
    public async Task<IActionResult> RemoveWatcher(Guid id, Guid userId, CancellationToken ct)
    {
        await collaboration.RemoveWatcherAsync(id, userId, ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/tasks")]
    public Task<List<TaskDto>> Tasks(Guid id, CancellationToken ct) => collaboration.ListTasksAsync(id, ct);

    [HttpPost("{id:guid}/tasks")]
    public Task<TaskDto> CreateTask(Guid id, SaveTaskRequest request, CancellationToken ct) => collaboration.SaveTaskAsync(id, null, request, ct);

    [HttpGet("{id:guid}/time-entries")]
    public Task<List<TimeEntryDto>> TimeEntries(Guid id, CancellationToken ct) => collaboration.ListTimeAsync(id, ct);

    [HttpPost("{id:guid}/time-entries")]
    public Task<TimeEntryDto> LogTime(Guid id, LogTimeRequest request, CancellationToken ct) => collaboration.LogTimeAsync(id, request, ct);

    [HttpGet("{id:guid}/follow-ups")]
    public Task<List<FollowUpDto>> FollowUps(Guid id, CancellationToken ct) => collaboration.ListFollowUpsAsync(id, ct);

    [HttpPost("{id:guid}/follow-ups")]
    public Task<FollowUpDto> ScheduleFollowUp(Guid id, CreateFollowUpRequest request, CancellationToken ct) => collaboration.ScheduleFollowUpAsync(id, request, ct);

    [HttpPost("{id:guid}/render-template/{templateId:guid}")]
    public async Task<object> RenderTemplate(Guid id, Guid templateId, CancellationToken ct) =>
        new { bodyHtml = await collaboration.RenderTemplateAsync(templateId, id, ct) };
}

/// <summary>Task, time-entry and follow-up items addressed by their own id.</summary>
[Route("api/v1")]
public sealed class TicketItemsController(TicketCollaborationService collaboration) : DeskControllerBase
{
    [HttpPatch("tasks/{taskId:guid}")]
    public async Task<TaskDto> UpdateTask(Guid taskId, SaveTaskRequest request, CancellationToken ct)
    {
        var existing = await collaboration.FindTaskTicketAsync(taskId, ct) ?? throw new Domain.NotFoundException("Task", taskId);
        return await collaboration.SaveTaskAsync(existing.TicketId, taskId, request, ct);
    }

    [HttpDelete("tasks/{taskId:guid}")]
    public async Task<IActionResult> DeleteTask(Guid taskId, CancellationToken ct)
    {
        await collaboration.DeleteTaskAsync(taskId, ct);
        return NoContent();
    }

    [HttpDelete("time-entries/{entryId:guid}")]
    public async Task<IActionResult> DeleteTime(Guid entryId, CancellationToken ct)
    {
        await collaboration.DeleteTimeAsync(entryId, ct);
        return NoContent();
    }

    [HttpDelete("follow-ups/{followUpId:guid}")]
    public async Task<IActionResult> CancelFollowUp(Guid followUpId, CancellationToken ct)
    {
        await collaboration.CancelFollowUpAsync(followUpId, ct);
        return NoContent();
    }
}
