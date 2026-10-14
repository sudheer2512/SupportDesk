using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Tickets;

public sealed class ReplyCollisionException(ReplyCollisionDto details)
    : DomainException(ErrorCodes.ReplyCollision, "New messages were added to this ticket while you were writing. Review them, then send again.", 409)
{
    public ReplyCollisionDto Details { get; } = details;
}

/// <summary>Inbound customer message details (portal, e-mail, WhatsApp, chat…).</summary>
public sealed record CustomerMessageInput(
    string? BodyHtml,
    string? BodyText,
    Channel Channel,
    Guid? ContactId,
    string? AuthorName,
    string? FromAddress = null,
    IReadOnlyList<string>? To = null,
    IReadOnlyList<string>? Cc = null,
    string? Subject = null,
    Guid? MessageId = null,
    Guid? ConversationId = null,
    string? ChannelMetadataJson = null,
    IReadOnlyList<Guid>? AttachmentFileIds = null,
    DateTime? OccurredAt = null,
    string? ClientRequestId = null);

/// <summary>
/// Ticket conversation: public replies (with reply-collision detection), internal/handover notes with mentions,
/// inbound customer messages (with reopen and SLA next-response handling), drafts and the unified timeline.
/// Thread sequence numbers are allocated under the ticket's rowversion, so concurrent writers never collide.
/// </summary>
public sealed class ConversationService(
    DeskDbContext db,
    RequestContext ctx,
    TicketService tickets,
    IEventPublisher events,
    HtmlSanitization html,
    TenantSettingsService settings,
    ISlaEngine sla)
{
    private const int MaxSeqRetries = 15;

    /// <summary>Randomised back-off between sequence-allocation retries so concurrent writers spread out.</summary>
    private static Task JitterAsync(int attempt, CancellationToken ct) => Task.Delay(Random.Shared.Next(5, 20 * Math.Min(attempt, 5)), ct);

    public async Task<TicketThread> ReplyAsync(Guid ticketId, ReplyRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsReply);
        if (string.IsNullOrWhiteSpace(TextNormalization.HtmlToText(r.BodyHtml)) && r.AttachmentFileIds is not { Count: > 0 })
            throw new ValidationException(new Dictionary<string, string> { ["bodyHtml"] = "Write a reply first." });
        if (r.ClientRequestId is { Length: > 0 } key && await db.TicketThreads.FirstOrDefaultAsync(x => x.ClientRequestId == key, ct) is { } dup)
            return dup;

        for (var attempt = 1; ; attempt++)
        {
            var t = await tickets.LoadForUpdateAsync(ticketId, ct);
            if (t.MergedIntoTicketId is not null) throw new DomainException(ErrorCodes.Validation, "This ticket was merged; reply on the surviving ticket.");
            if (r.BasedOnSeq is { } seen && !r.Force && t.LastThreadSeq > seen)
            {
                var newer = await db.TicketThreads.AsNoTracking()
                    .Where(x => x.TicketId == ticketId && x.Seq > seen && x.Visibility == Visibility.Public && x.AuthorUserId != ctx.UserId)
                    .OrderBy(x => x.Seq).ToListAsync(ct);
                if (newer.Count > 0)
                    throw new ReplyCollisionException(new ReplyCollisionDto(t.LastThreadSeq, await MapThreadsAsync(newer, internalView: true, ct)));
            }

            var now = db.Clock.UtcNow;
            var body = html.Sanitize(r.BodyHtml);
            var channel = r.Channel is { } c && Enum.TryParse<Channel>(c, true, out var parsed) ? parsed : t.Channel;
            var contact = t.ContactId is null ? null : await db.Contacts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == t.ContactId, ct);
            var to = r.To is { Count: > 0 } ? r.To : contact?.Email is { } e ? [e] : [];
            var thread = new TicketThread
            {
                TicketId = t.Id, Seq = t.LastThreadSeq + 1, Kind = ThreadKind.AgentReply, Visibility = Visibility.Public, Channel = channel,
                Direction = MessageDirection.Outbound, AuthorType = ctx.IsSystem ? AuthorType.System : AuthorType.Agent, AuthorUserId = ctx.UserId,
                AuthorName = ctx.ActorName, ToJson = Json.Serialize(to), CcJson = r.Cc is { Count: > 0 } ? Json.Serialize(r.Cc) : null,
                BccJson = r.Bcc is { Count: > 0 } ? Json.Serialize(r.Bcc) : null, Subject = $"Re: {t.Subject}", BodyHtml = body,
                BodyText = TextNormalization.HtmlToText(body), DeliveryStatus = channel is Channel.Portal or Channel.Internal or Channel.WalkIn ? "Delivered" : "Queued",
                CreatedAt = now, ClientRequestId = r.ClientRequestId
            };
            db.TicketThreads.Add(thread);
            t.LastThreadSeq = thread.Seq;
            t.AgentReplyCount++;
            t.LastAgentResponseAt = now;
            t.LastActivityAt = now;
            var first = t.FirstResponseAt is null;
            t.FirstResponseAt ??= now;
            await AttachAsync(t, thread.Id, r.AttachmentFileIds, Visibility.Public, ct);

            if (r.StatusId is { } statusId && statusId != t.StatusId)
            {
                var status = await db.TicketStatuses.AsNoTracking().FirstOrDefaultAsync(s => s.Id == statusId && s.IsActive, ct)
                             ?? throw new ValidationException(new Dictionary<string, string> { ["statusId"] = "Unknown status." });
                if (status.Category.IsResolvedOrClosed()) ctx.Require(Permissions.TicketsResolve);
                await tickets.ApplyStatusAsync(t, status, null, "Changed with reply", bypassRules: false, ct);
            }
            else if (t.StatusCategory is StatusCategory.Open or StatusCategory.Assigned && t.AssigneeId is not null)
            {
                await tickets.ApplyStatusAsync(t, await tickets.StatusForCategoryAsync(StatusCategory.InProgress, ct), null, null, bypassRules: true, ct);
            }

            await sla.OnAgentPublicReplyAsync(t, now, ct);
            if (r.TemplateId is { } tid) await db.ReplyTemplates.Where(x => x.Id == tid).ExecuteUpdateAsync(u => u.SetProperty(x => x.UsageCount, x => x.UsageCount + 1), ct);
            await db.TicketDrafts.Where(d => d.TicketId == t.Id && d.UserId == ctx.UserId && d.Kind == DraftKind.Reply).ExecuteDeleteAsync(ct);
            events.Publish(EventTypes.TicketReplyAdded, "Ticket", t.Id, new TicketEventPayload(t.Id, t.Number, first ? [new FieldChange("firstResponse", null, "true")] : null, thread.Id));
            try
            {
                await db.SaveChangesAsync(ct);
                return thread;
            }
            catch (DbUpdateException) when (attempt < MaxSeqRetries)
            {
                // Another writer took the next sequence number (or changed the ticket) — reload and retry.
                db.ChangeTracker.Clear();
                await JitterAsync(attempt, ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new ConcurrencyConflictException(ticketId);
            }
        }
    }

    public async Task<TicketThread> AddNoteAsync(Guid ticketId, NoteRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsNote);
        if (string.IsNullOrWhiteSpace(TextNormalization.HtmlToText(r.BodyHtml)) && r.AttachmentFileIds is not { Count: > 0 })
            throw new ValidationException(new Dictionary<string, string> { ["bodyHtml"] = "Write a note first." });
        if (r.ClientRequestId is { Length: > 0 } key && await db.TicketThreads.FirstOrDefaultAsync(x => x.ClientRequestId == key, ct) is { } dup)
            return dup;
        var kind = string.Equals(r.Kind, "handover", StringComparison.OrdinalIgnoreCase) ? ThreadKind.HandoverNote : ThreadKind.InternalNote;

        for (var attempt = 1; ; attempt++)
        {
            var t = await tickets.LoadForUpdateAsync(ticketId, ct);
            var now = db.Clock.UtcNow;
            var body = html.Sanitize(r.BodyHtml);
            var mentions = r.MentionUserIds?.Distinct().ToList() ?? [];
            if (mentions.Count > 0)
                mentions = await db.Users.Where(u => mentions.Contains(u.Id) && u.IsActive).Select(u => u.Id).ToListAsync(ct);
            var thread = new TicketThread
            {
                TicketId = t.Id, Seq = t.LastThreadSeq + 1, Kind = kind, Visibility = Visibility.Internal, Channel = Channel.Internal,
                Direction = MessageDirection.Internal, AuthorType = ctx.IsSystem ? AuthorType.System : AuthorType.Agent, AuthorUserId = ctx.UserId,
                AuthorName = ctx.ActorName, BodyHtml = body, BodyText = TextNormalization.HtmlToText(body), CreatedAt = now,
                ChannelMetadataJson = mentions.Count > 0 ? Json.Serialize(new { mentions }) : null, ClientRequestId = r.ClientRequestId
            };
            db.TicketThreads.Add(thread);
            t.LastThreadSeq = thread.Seq;
            t.LastActivityAt = now;
            await AttachAsync(t, thread.Id, r.AttachmentFileIds, Visibility.Internal, ct);
            // Mentioned collaborators get access to the ticket (temporary participation) so they can respond.
            foreach (var m in mentions)
                if (!await db.TicketWatchers.AnyAsync(w => w.TicketId == t.Id && w.UserId == m, ct) && db.TicketWatchers.Local.All(w => !(w.TicketId == t.Id && w.UserId == m)))
                    db.TicketWatchers.Add(new TicketWatcher { TicketId = t.Id, UserId = m, Kind = WatcherKind.Collaborator, AddedById = ctx.UserId, AddedAt = now });
            await db.TicketDrafts.Where(d => d.TicketId == t.Id && d.UserId == ctx.UserId && d.Kind == DraftKind.Note).ExecuteDeleteAsync(ct);
            events.Publish(EventTypes.TicketNoteAdded, "Ticket", t.Id, new TicketEventPayload(t.Id, t.Number,
                mentions.Count > 0 ? [new FieldChange("mentions", null, string.Join(",", mentions))] : null, thread.Id, kind.ToString()));
            try
            {
                await db.SaveChangesAsync(ct);
                return thread;
            }
            catch (DbUpdateException) when (attempt < MaxSeqRetries)
            {
                db.ChangeTracker.Clear();
                await JitterAsync(attempt, ct);
            }
        }
    }

    /// <summary>
    /// Appends an inbound customer message. Resolved tickets inside the reopen window are reopened; waiting tickets
    /// move back to work; a next-response SLA cycle starts. Returns null when the ticket is closed beyond the window
    /// (callers then create a follow-up ticket).
    /// </summary>
    public async Task<TicketThread?> AddCustomerMessageAsync(Guid ticketId, CustomerMessageInput m, CancellationToken ct = default)
    {
        if (m.ClientRequestId is { Length: > 0 } key && await db.TicketThreads.FirstOrDefaultAsync(x => x.ClientRequestId == key, ct) is { } dup)
            return dup;
        for (var attempt = 1; ; attempt++)
        {
            var t = await db.Tickets.Include(x => x.Tags).FirstOrDefaultAsync(x => x.Id == ticketId, ct) ?? throw new NotFoundException("Ticket", ticketId);
            if (t.MergedIntoTicketId is { } survivor) return await AddCustomerMessageAsync(survivor, m, ct);
            var s = await settings.TicketsAsync(ct);
            var now = m.OccurredAt ?? db.Clock.UtcNow;
            if (t.StatusCategory == StatusCategory.Closed && (t.ClosedAt ?? now) < now.AddDays(-s.ReopenWindowDays)) return null;

            var body = m.BodyHtml is not null ? html.Sanitize(m.BodyHtml) : TextNormalization.TextToHtml(m.BodyText);
            var thread = new TicketThread
            {
                TicketId = t.Id, Seq = t.LastThreadSeq + 1, Kind = ThreadKind.CustomerMessage, Visibility = Visibility.Public, Channel = m.Channel,
                Direction = MessageDirection.Inbound, AuthorType = AuthorType.Customer, AuthorContactId = m.ContactId, AuthorName = m.AuthorName,
                FromAddress = m.FromAddress, ToJson = m.To is null ? null : Json.Serialize(m.To), CcJson = m.Cc is null ? null : Json.Serialize(m.Cc),
                Subject = m.Subject, BodyHtml = body, BodyText = m.BodyText ?? TextNormalization.HtmlToText(body), MessageId = m.MessageId,
                ConversationId = m.ConversationId, ChannelMetadataJson = m.ChannelMetadataJson, CreatedAt = now, ClientRequestId = m.ClientRequestId
            };
            db.TicketThreads.Add(thread);
            t.LastThreadSeq = thread.Seq;
            t.CustomerReplyCount++;
            t.LastCustomerResponseAt = now;
            t.LastActivityAt = now;
            await AttachAsync(t, thread.Id, m.AttachmentFileIds, Visibility.Public, ct, trusted: true);

            if (t.StatusCategory.IsResolvedOrClosed())
            {
                if (t.AwaitingCustomerConfirmation) t.AwaitingCustomerConfirmation = false;
                await tickets.ReopenCoreAsync(t, "Customer replied", ct);
            }
            else if (t.StatusCategory == StatusCategory.Waiting)
            {
                var target = await tickets.StatusForCategoryAsync(t.AssigneeId is null ? StatusCategory.Open : StatusCategory.InProgress, ct);
                await tickets.ApplyStatusAsync(t, target, null, "Customer replied", bypassRules: true, ct);
            }
            await sla.OnCustomerReplyAsync(t, now, ct);
            events.Publish(EventTypes.TicketCustomerReplied, "Ticket", t.Id, new TicketEventPayload(t.Id, t.Number, null, thread.Id));
            try
            {
                await db.SaveChangesAsync(ct);
                return thread;
            }
            catch (DbUpdateException) when (attempt < MaxSeqRetries)
            {
                db.ChangeTracker.Clear();
                await JitterAsync(attempt, ct);
            }
        }
    }

    private async Task AttachAsync(Ticket t, Guid threadId, IReadOnlyList<Guid>? fileIds, Visibility visibility, CancellationToken ct, bool trusted = false)
    {
        if (fileIds is not { Count: > 0 }) return;
        var files = await db.StoredFiles.Where(f => fileIds.Contains(f.Id)).ToListAsync(ct);
        if (files.Count != fileIds.Distinct().Count()) throw new ValidationException(new Dictionary<string, string> { ["attachments"] = "Unknown file." });
        foreach (var f in files)
        {
            if (!trusted && !ctx.IsSystem && f.UploadedByUserId != ctx.UserId && f.UploadedByContactId != ctx.ContactId)
                throw new ForbiddenException("You can only attach files you uploaded.");
            db.TicketAttachments.Add(new TicketAttachment
            {
                TicketId = t.Id, ThreadId = threadId, FileId = f.Id, FileName = f.FileName, ContentType = f.ContentType, SizeBytes = f.SizeBytes,
                Visibility = visibility, UploadedByUserId = f.UploadedByUserId, UploadedByContactId = f.UploadedByContactId
            });
        }
    }

    // ───────────────────────────── Drafts ─────────────────────────────

    public async Task<DraftDto?> GetDraftAsync(Guid ticketId, string kind, CancellationToken ct = default)
    {
        await tickets.LoadForUpdateAsync(ticketId, ct);
        var k = TicketService.ParseEnum(kind, DraftKind.Reply, "kind");
        var d = await db.TicketDrafts.AsNoTracking().FirstOrDefaultAsync(x => x.TicketId == ticketId && x.UserId == ctx.UserId && x.Kind == k, ct);
        return d is null ? null : new DraftDto(d.Kind.ToString(), d.BodyHtml, Json.StringList(d.ToJson), Json.StringList(d.CcJson), d.TemplateId, d.BasedOnSeq, d.UpdatedAt);
    }

    public async Task<DraftDto> SaveDraftAsync(Guid ticketId, SaveDraftRequest r, CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.TicketsReply, Permissions.TicketsNote);
        await tickets.LoadForUpdateAsync(ticketId, ct);
        var k = TicketService.ParseEnum(r.Kind, DraftKind.Reply, "kind");
        var d = await db.TicketDrafts.FirstOrDefaultAsync(x => x.TicketId == ticketId && x.UserId == ctx.UserId && x.Kind == k, ct);
        if (d is null)
        {
            d = new TicketDraft { TicketId = ticketId, UserId = ctx.UserId!.Value, Kind = k };
            db.TicketDrafts.Add(d);
        }
        d.BodyHtml = r.BodyHtml is null ? null : html.Sanitize(r.BodyHtml);
        d.ToJson = r.To is null ? null : Json.Serialize(r.To);
        d.CcJson = r.Cc is null ? null : Json.Serialize(r.Cc);
        d.TemplateId = r.TemplateId;
        d.BasedOnSeq = r.BasedOnSeq;
        d.UpdatedAt = db.Clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return new DraftDto(d.Kind.ToString(), d.BodyHtml, Json.StringList(d.ToJson), Json.StringList(d.CcJson), d.TemplateId, d.BasedOnSeq, d.UpdatedAt);
    }

    public async Task DeleteDraftAsync(Guid ticketId, string kind, CancellationToken ct = default)
    {
        var k = TicketService.ParseEnum(kind, DraftKind.Reply, "kind");
        await db.TicketDrafts.Where(x => x.TicketId == ticketId && x.UserId == ctx.UserId && x.Kind == k).ExecuteDeleteAsync(ct);
    }

    // ───────────────────────────── Timeline ─────────────────────────────

    /// <summary>Chronological conversation + activity. Customers (portal) only ever receive public entries and no events.</summary>
    public async Task<List<TimelineItemDto>> TimelineAsync(Guid ticketId, bool includeEvents, bool internalView, CancellationToken ct = default)
    {
        var threads = await db.TicketThreads.AsNoTracking().Where(x => x.TicketId == ticketId && (internalView || x.Visibility == Visibility.Public))
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Seq).ToListAsync(ct);
        var items = (await MapThreadsAsync(threads, internalView, ct)).Select(th => new TimelineItemDto("thread", th.CreatedAt, th, null)).ToList();
        if (includeEvents && internalView)
        {
            var evs = await db.TicketEvents.AsNoTracking().Where(e => e.TicketId == ticketId).OrderBy(e => e.OccurredAt).ToListAsync(ct);
            items.AddRange(evs.Select(e => new TimelineItemDto("event", e.OccurredAt, null,
                new TicketEventDto(e.Id, e.Type, e.ActorType.ToString(), e.ActorId, e.ActorName, e.OccurredAt, e.Summary, TicketLookups.Changes(e.ChangesJson),
                    Json.ElementOrNull(e.DetailsJson)))));
        }
        return items.OrderBy(i => i.At).ThenBy(i => i.ItemType == "thread" ? 0 : 1).ToList();
    }

    public async Task<List<ThreadDto>> MapThreadsAsync(IReadOnlyList<TicketThread> threads, bool internalView, CancellationToken ct)
    {
        var ids = threads.Select(t => t.Id).ToList();
        var attachments = (await db.TicketAttachments.AsNoTracking().Where(a => a.ThreadId != null && ids.Contains(a.ThreadId.Value) && (internalView || a.Visibility == Visibility.Public))
                .Join(db.StoredFiles.AsNoTracking(), a => a.FileId, f => f.Id, (a, f) => new { a, f.ScanStatus }).ToListAsync(ct))
            .GroupBy(x => x.a.ThreadId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AttachmentDto>)g.Select(x => new AttachmentDto(x.a.Id, x.a.FileId, x.a.FileName, x.a.ContentType, x.a.SizeBytes,
                x.a.Visibility.ToString(), x.a.IsInline, x.ScanStatus.ToString(), x.a.CreatedAt, $"/api/v1/attachments/{x.a.Id}/download")).ToList());
        return threads.Select(t => new ThreadDto(t.Id, t.Seq, t.Kind.ToString(), t.Visibility.ToString(), t.Channel.ToString(), t.Direction.ToString(),
            t.AuthorType.ToString(), internalView ? t.AuthorUserId : null, t.AuthorContactId, t.AuthorName, t.FromAddress, Json.StringList(t.ToJson), Json.StringList(t.CcJson),
            t.Subject, t.BodyHtml, t.BodyText, t.DeliveryStatus, t.Sentiment.ToString(), internalView ? Json.ElementOrNull(t.ChannelMetadataJson) : null,
            attachments.TryGetValue(t.Id, out var a) ? a : [], t.CreatedAt, t.EditedAt)).ToList();
    }
}
