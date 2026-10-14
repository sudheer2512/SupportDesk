using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Automation;
using SupportDesk.Application.Common;
using SupportDesk.Application.Customers;
using SupportDesk.Application.Integrations.Engineering;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Collaboration;
using SupportDesk.Contracts.Common;
using SupportDesk.Domain;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Integration;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Integrations.Collaboration;

/// <summary>Builds ticket messages (facts + absolute deep link) for Teams/Slack.</summary>
public sealed class CollaborationMessageFactory(DeskDbContext db, TenantSettingsService settings, IConfiguration configuration)
{
    public string PublicBaseUrl =>
        (configuration["Communication:PublicBaseUrl"] is { Length: > 0 } a ? a : configuration["App:PublicUrl"] is { Length: > 0 } b ? b : "http://localhost:5830").TrimEnd('/');

    public string TicketLink(Guid ticketId) => $"{PublicBaseUrl}/tickets/{ticketId}";

    public async Task<string> DisplayAsync(Ticket t, CancellationToken ct) => (await settings.TicketsAsync(ct)).NumberPrefix + t.Number;

    public async Task<CollaborationMessage> ForTicketAsync(Ticket t, string title, string? text, string tone, CancellationToken ct, params CollaborationFact[] extra)
    {
        var customer = t.CustomerId is { } cid ? await db.Customers.AsNoTracking().Where(c => c.Id == cid).Select(c => c.Name).FirstOrDefaultAsync(ct) : null;
        var department = await db.Departments.AsNoTracking().Where(d => d.Id == t.DepartmentId).Select(d => d.Name).FirstOrDefaultAsync(ct);
        var status = await db.TicketStatuses.AsNoTracking().Where(s => s.Id == t.StatusId).Select(s => s.Name).FirstOrDefaultAsync(ct);
        var assignee = t.AssigneeId is { } aid ? await db.Users.AsNoTracking().Where(u => u.Id == aid).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) : null;
        var facts = new List<CollaborationFact>
        {
            new("Ticket", $"{await DisplayAsync(t, ct)} · {t.Subject}"),
            new("Customer", customer ?? "—"),
            new("Priority", t.Priority.ToString()),
            new("Status", status ?? t.StatusCategory.ToString()),
            new("Department", department ?? "—"),
            new("Assignee", assignee ?? "Unassigned")
        };
        facts.AddRange(extra);
        return new CollaborationMessage(title, text ?? "", facts, TicketLink(t.Id), tone);
    }
}

/// <summary>Posts a message to one channel through the SSRF-guarded automation HTTP client and records the outcome.</summary>
public sealed class CollaborationSender(
    DeskDbContext db,
    OutboundHttpSender http,
    ISecretProtector secrets,
    WebhookTransportOptions options,
    ILogger<CollaborationSender> logger)
{
    public sealed record Outcome(bool Success, int? StatusCode, int DurationMs, string? Error, bool Transient);

    public async Task<Outcome> SendAsync(CollaborationChannel channel, CollaborationMessage message, string operation, CancellationToken ct)
    {
        string url;
        try { url = secrets.Unprotect(channel.WebhookUrlProtected); }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return Record(channel, new Outcome(false, null, 0, "The webhook URL cannot be decrypted; enter it again.", false), operation);
        }
        var system = channel.Provider == CollaborationProvider.Slack ? "Slack" : IntegrationTypes.MicrosoftTeams;
        if (await CollaborationUrlPolicy.CheckAsync(url, options.AllowPrivateNetworks, null, ct) is { } blocked)
        {
            logger.LogWarning("Collaboration channel {Channel} blocked: {Reason}", channel.Id, blocked);
            db.IntegrationLogs.Add(new IntegrationLog
            {
                IntegrationId = channel.Id, System = system, Direction = MessageDirection.Outbound, Operation = operation, Url = channel.UrlHint, Success = false,
                ResponseExcerpt = blocked, CreatedAt = db.Clock.UtcNow
            });
            return Record(channel, new Outcome(false, null, 0, blocked, false), operation);
        }
        var body = CollaborationPayloads.Build(channel.Provider, message);
        var result = await http.SendAsync(new OutboundHttpRequest("POST", url, new Dictionary<string, string>(), body,
            TimeSpan.FromSeconds(Math.Clamp(options.DefaultTimeoutSeconds, 5, 30)), system, operation), ct);
        // The log row belongs to this channel; never keep the secret webhook path in it.
        foreach (var entry in db.ChangeTracker.Entries<IntegrationLog>().Where(e => e.State == EntityState.Added && e.Entity.IntegrationId == null && e.Entity.System == system))
        {
            entry.Entity.IntegrationId = channel.Id;
            entry.Entity.Url = channel.UrlHint;
        }
        // Slack answers "ok" (text); Teams answers 200/202 — anything 2xx is a success.
        return Record(channel, new Outcome(result.Success, result.StatusCode, result.DurationMs, result.Error, result.Transient), operation);
    }

    private Outcome Record(CollaborationChannel channel, Outcome o, string operation)
    {
        channel.LastSentAt = db.Clock.UtcNow;
        channel.LastStatusCode = o.StatusCode;
        channel.LastError = o.Error is null ? null : TextNormalization.Truncate(o.Error, 1000);
        return o;
    }
}

/// <summary>
/// Microsoft Teams / Slack collaboration (admin.integrations): channels with event mappings, send test, delivery log;
/// agents can share a ticket to a channel (composer action).
/// </summary>
public sealed class CollaborationService(
    DeskDbContext db,
    RequestContext ctx,
    ISecretProtector secrets,
    IAuditWriter audit,
    CollaborationSender sender,
    CollaborationMessageFactory messages,
    WebhookTransportOptions options,
    TicketService tickets,
    TicketHistory history)
{
    public CollaborationCatalogDto Catalog()
    {
        ctx.Require(Permissions.AdminIntegrations);
        return new CollaborationCatalogDto(CollaborationEvents.All.Select(e => new CollaborationEventTypeDto(e.Key, e.Label, e.Description)).ToList(),
            [nameof(CollaborationProvider.Teams), nameof(CollaborationProvider.Slack)], options.AllowPrivateNetworks);
    }

    public async Task<List<CollaborationChannelDto>> ListAsync(CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var rows = await db.Set<CollaborationChannel>().AsNoTracking().OrderBy(c => c.Provider).ThenBy(c => c.Name).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    private static CollaborationChannelDto Map(CollaborationChannel c) =>
        new(c.Id, c.Provider.ToString(), c.Name, c.UrlHint, Json.StringList(c.EventsJson), Json.GuidList(c.DepartmentIdsJson), c.MinPriority.ToString(), c.IsActive,
            c.LastSentAt, c.LastStatusCode, c.LastError, Convert.ToBase64String(c.RowVersion));

    public async Task<CollaborationChannelDto> CreateAsync(SaveCollaborationChannelRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var c = new CollaborationChannel();
        await ApplyAsync(c, r, isNew: true, ct);
        db.Set<CollaborationChannel>().Add(c);
        await db.SaveChangesAsync(ct);
        return Map(c);
    }

    public async Task<CollaborationChannelDto> UpdateAsync(Guid id, SaveCollaborationChannelRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var c = await db.Set<CollaborationChannel>().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Collaboration channel", id);
        if (r.RowVersion is { Length: > 0 } rv && rv != Convert.ToBase64String(c.RowVersion))
            throw new ConflictException(ErrorCodes.ConcurrencyConflict, "The channel was changed by someone else. Reload and try again.");
        await ApplyAsync(c, r, isNew: false, ct);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException(ErrorCodes.ConcurrencyConflict, "The channel was changed by someone else. Reload and try again."); }
        return Map(c);
    }

    private async Task ApplyAsync(CollaborationChannel c, SaveCollaborationChannelRequest r, bool isNew, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        CollaborationProvider provider = c.Provider;
        if (isNew)
        {
            if (!Enum.TryParse(r.Provider, true, out provider) || !Enum.IsDefined(provider)) errors["provider"] = "Provider must be Teams or Slack.";
        }
        else if (!string.IsNullOrWhiteSpace(r.Provider) && !string.Equals(r.Provider, c.Provider.ToString(), StringComparison.OrdinalIgnoreCase))
            errors["provider"] = "The provider of a channel cannot change.";
        var name = Parse.Clean(r.Name, 200);
        if (name is null) errors["name"] = "Name the channel (e.g. #support-escalations).";
        string? url = null;
        if (isNew || r.WebhookUrl is not null)
        {
            url = r.WebhookUrl?.Trim();
            if (CollaborationUrlPolicy.Validate(url, options.AllowPrivateNetworks) is { } urlError) errors["webhookUrl"] = urlError;
            else if (provider == CollaborationProvider.Slack && url!.Length > 1000) errors["webhookUrl"] = "The URL is too long.";
        }
        var events = (r.Events ?? []).Select(e => e.Trim()).Where(e => e.Length > 0).Distinct().ToList();
        if (events.Count == 0) errors["events"] = "Choose at least one event.";
        else if (events.FirstOrDefault(e => !CollaborationEvents.IsKnown(e)) is { } unknown) errors["events"] = $"Unknown event '{unknown}'.";
        var departments = (r.DepartmentIds ?? []).Distinct().ToList();
        if (departments.Count > 0 && await db.Departments.CountAsync(d => departments.Contains(d.Id), ct) != departments.Count) errors["departmentIds"] = "Unknown department.";
        var minPriority = Parse.Enum(r.MinPriority, TicketPriority.Critical, "minPriority");
        if (errors.Count > 0) throw new ValidationException(errors);

        c.Provider = provider;
        c.Name = name!;
        if (url is not null)
        {
            c.WebhookUrlProtected = secrets.Protect(url);
            c.UrlHint = CollaborationUrlPolicy.Hint(url);
            c.LastError = null;
        }
        c.EventsJson = Json.Serialize(events);
        c.DepartmentIdsJson = Json.Serialize(departments);
        c.MinPriority = minPriority;
        c.IsActive = r.IsActive;
        audit.Write(AuditActions.IntegrationChanged, "CollaborationChannel", c.Id, $"{provider} channel {c.Name} {(isNew ? "added" : "updated")}",
            new { provider = provider.ToString(), c.Name, events, departments, minPriority = minPriority.ToString(), r.IsActive, webhookUrlChanged = url is not null });
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var c = await db.Set<CollaborationChannel>().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Collaboration channel", id);
        db.Set<CollaborationChannel>().Remove(c);
        audit.Write(AuditActions.IntegrationChanged, "CollaborationChannel", c.Id, $"{c.Provider} channel {c.Name} deleted");
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Sends a sample card right now and returns the provider's answer.</summary>
    public async Task<CollaborationSendResultDto> TestAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var c = await db.Set<CollaborationChannel>().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Collaboration channel", id);
        var message = new CollaborationMessage("SupportDesk test message",
            $"This channel receives: {string.Join(", ", Json.StringList(c.EventsJson).Select(e => CollaborationEvents.All.FirstOrDefault(x => x.Key == e).Label ?? e))}.",
            [new CollaborationFact("Sent by", ctx.ActorName ?? "Administrator"), new CollaborationFact("Channel", c.Name)], $"{messages.PublicBaseUrl}/admin/collaboration");
        var o = await sender.SendAsync(c, message, "collaboration.test", ct);
        await db.SaveChangesAsync(ct);
        return new CollaborationSendResultDto(o.Success, o.StatusCode, o.DurationMs, o.Error);
    }

    public async Task<List<CollaborationLogDto>> LogsAsync(Guid id, int limit, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        if (!await db.Set<CollaborationChannel>().AnyAsync(x => x.Id == id, ct)) throw new NotFoundException("Collaboration channel", id);
        return await db.IntegrationLogs.AsNoTracking().Where(l => l.IntegrationId == id).OrderByDescending(l => l.CreatedAt).Take(Math.Clamp(limit, 1, 200))
            .Select(l => new CollaborationLogDto(l.Id, l.Operation, l.StatusCode, l.Success, l.DurationMs, l.ResponseExcerpt, l.CreatedAt)).ToListAsync(ct);
    }

    // ───────────────────────────── Share a ticket (agents) ─────────────────────────────

    public async Task<List<CollaborationShareTargetDto>> ShareTargetsAsync(CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsNote);
        return await db.Set<CollaborationChannel>().AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name)
            .Select(c => new CollaborationShareTargetDto(c.Id, c.Provider.ToString(), c.Name)).ToListAsync(ct);
    }

    public async Task<CollaborationSendResultDto> ShareTicketAsync(Guid ticketId, ShareTicketToChannelRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsNote);
        var ticket = await tickets.LoadForUpdateAsync(ticketId, ct);
        var c = await db.Set<CollaborationChannel>().FirstOrDefaultAsync(x => x.Id == r.ChannelId && x.IsActive, ct) ?? throw new NotFoundException("Collaboration channel", r.ChannelId);
        var comment = Parse.Clean(r.Comment, 1000);
        var message = await messages.ForTicketAsync(ticket, $"{ctx.ActorName ?? "An agent"} shared {await messages.DisplayAsync(ticket, ct)}: {ticket.Subject}",
            comment ?? TextNormalization.Truncate(ticket.DescriptionText, 400), "info", ct);
        var o = await sender.SendAsync(c, message, "collaboration.share", ct);
        if (o.Success)
            history.Record(ticket, TicketEventTypes.FieldsChanged, $"Shared to {c.Provider} channel {c.Name}{(comment is null ? "" : ": " + comment)}", null,
                new { channelId = c.Id, provider = c.Provider.ToString() });
        await db.SaveChangesAsync(ct);
        return new CollaborationSendResultDto(o.Success, o.StatusCode, o.DurationMs, o.Error);
    }
}

public sealed record CollaborationSendJob(Guid ChannelId, string EventKey, CollaborationMessage Message);

/// <summary>
/// Maps outbox events to collaboration events and queues one durable job per subscribed channel (idempotent per event
/// and channel): new urgent ticket, SLA breach, escalation, mention, engineering escalation/resolution/release.
/// </summary>
public sealed class CollaborationOutboxHandler(DeskDbContext db, IJobQueue jobs, CollaborationMessageFactory messages) : IOutboxHandler
{
    public string Name => "collaboration-channels";
    public int Order => HandlerOrder.Integrations + 5;

    public bool CanHandle(string eventType) => eventType is EventTypes.TicketCreated or EventTypes.SlaBreached or EventTypes.TicketEscalated
        or EventTypes.TicketNoteAdded or EventTypes.EngineeringItemUpdated;

    public async Task HandleAsync(OutboxEvent ev, CancellationToken ct)
    {
        if (ev.TenantId is null) return;
        var channels = await db.Set<CollaborationChannel>().AsNoTracking().Where(c => c.IsActive).ToListAsync(ct);
        if (channels.Count == 0) return;
        var (key, ticketId) = Classify(ev);
        if (key is null || ticketId is null) return;
        var subscribed = channels.Where(c => Json.StringList(c.EventsJson).Contains(key)).ToList();
        if (subscribed.Count == 0) return;
        var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null || ticket.IsSpam) return;
        subscribed = subscribed.Where(c => Json.GuidList(c.DepartmentIdsJson) is var d && (d.Count == 0 || d.Contains(ticket.DepartmentId))).ToList();
        if (key == CollaborationEvents.UrgentTicket) subscribed = subscribed.Where(c => ticket.Priority >= c.MinPriority).ToList();
        if (subscribed.Count == 0) return;
        var message = await BuildAsync(key, ev, ticket, ct);
        if (message is null) return;
        foreach (var c in subscribed)
            jobs.Enqueue(CollaborationSendJobHandler.JobType, new CollaborationSendJob(c.Id, key, message), idempotencyKey: $"collab:{ev.Id:N}:{c.Id:N}", maxAttempts: 5);
        await db.SaveChangesAsync(ct);
    }

    private static (string? Key, Guid? TicketId) Classify(OutboxEvent ev)
    {
        switch (ev.Type)
        {
            case EventTypes.TicketCreated: return (CollaborationEvents.UrgentTicket, ev.AggregateId);
            case EventTypes.TicketEscalated: return (CollaborationEvents.Escalated, ev.AggregateId);
            case EventTypes.SlaBreached: return (CollaborationEvents.SlaBreached, Json.Deserialize<SlaEventPayload>(ev.PayloadJson)?.TicketId ?? ev.AggregateId);
            case EventTypes.TicketNoteAdded:
            {
                var p = Json.Deserialize<TicketEventPayload>(ev.PayloadJson);
                return p?.Changes?.Any(c => c.Field == "mentions" && !string.IsNullOrEmpty(c.To)) == true ? (CollaborationEvents.Mention, p.TicketId) : (null, null);
            }
            case EventTypes.EngineeringItemUpdated:
            {
                var p = Json.Deserialize<EngineeringItemEventPayload>(ev.PayloadJson);
                if (p is null) return (null, null);
                if (p.Action == EngineeringItemEventPayload.Linked) return (CollaborationEvents.EngineeringEscalated, p.TicketId);
                if (p.BecameReleased) return (CollaborationEvents.EngineeringReleased, p.TicketId);
                if (p.BecameResolved) return (CollaborationEvents.EngineeringResolved, p.TicketId);
                return (null, null);
            }
            default: return (null, null);
        }
    }

    private async Task<CollaborationMessage?> BuildAsync(string key, OutboxEvent ev, Ticket t, CancellationToken ct)
    {
        var shown = await messages.DisplayAsync(t, ct);
        switch (key)
        {
            case CollaborationEvents.UrgentTicket:
                return await messages.ForTicketAsync(t, $"New {t.Priority.ToString().ToLowerInvariant()} ticket {shown}: {t.Subject}",
                    TextNormalization.Truncate(t.DescriptionText, 400), t.Priority == TicketPriority.Critical ? "critical" : "warning", ct,
                    new CollaborationFact("Channel", t.Channel.ToString()));
            case CollaborationEvents.SlaBreached:
            {
                var p = Json.Deserialize<SlaEventPayload>(ev.PayloadJson);
                return await messages.ForTicketAsync(t, $"SLA breached on {shown}{(p is null ? "" : $" ({Milestone(p.Milestone)})")}", t.Subject, "critical", ct,
                    new CollaborationFact("Due", p is null ? "—" : $"{p.DueAt:yyyy-MM-dd HH:mm} UTC"));
            }
            case CollaborationEvents.Escalated:
            {
                var p = Json.Deserialize<TicketEventPayload>(ev.PayloadJson);
                return await messages.ForTicketAsync(t, $"{shown} escalated (level {t.EscalationLevel})", p?.Note ?? t.Subject, "warning", ct);
            }
            case CollaborationEvents.Mention:
            {
                var p = Json.Deserialize<TicketEventPayload>(ev.PayloadJson);
                var ids = p?.Changes?.FirstOrDefault(c => c.Field == "mentions")?.To?.Split(',').Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).ToList() ?? [];
                var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).Select(u => u.DisplayName).ToListAsync(ct);
                var thread = p?.ThreadId is { } tid ? await db.TicketThreads.AsNoTracking().Where(x => x.Id == tid).Select(x => new { x.AuthorName, x.BodyText }).FirstOrDefaultAsync(ct) : null;
                return await messages.ForTicketAsync(t, $"{thread?.AuthorName ?? "An agent"} mentioned {string.Join(", ", names)} on {shown}",
                    TextNormalization.Truncate(thread?.BodyText, 400), "info", ct);
            }
            default:
            {
                var p = Json.Deserialize<EngineeringItemEventPayload>(ev.PayloadJson)!;
                var title = key switch
                {
                    CollaborationEvents.EngineeringEscalated => $"{shown} escalated to engineering: {p.DisplayId}",
                    CollaborationEvents.EngineeringReleased => $"Fix released for {shown}{((p.ReleaseName ?? p.FixVersion) is { } v ? " in " + v : "")}",
                    _ => $"Engineering resolved {p.DisplayId} for {shown}"
                };
                return await messages.ForTicketAsync(t, title, p.Title, key == CollaborationEvents.EngineeringReleased ? "success" : "info", ct,
                    new CollaborationFact("Work item", $"{p.Provider} {p.DisplayId} · {p.State ?? "—"}"));
            }
        }
    }

    private static string Milestone(SlaMilestoneKind k) => k switch
    {
        SlaMilestoneKind.FirstResponse => "first response",
        SlaMilestoneKind.NextResponse => "next response",
        _ => "resolution"
    };
}

/// <summary>Delivers one queued collaboration message; transient failures (timeouts, 429, 5xx) are retried with the job backoff.</summary>
public sealed class CollaborationSendJobHandler(DeskDbContext db, CollaborationSender sender, ILogger<CollaborationSendJobHandler> logger) : JobHandler<CollaborationSendJob>
{
    public const string JobType = "collaboration.send";
    public override string Type => JobType;

    protected override async Task ExecuteAsync(CollaborationSendJob payload, BackgroundJob job, CancellationToken ct)
    {
        var channel = await db.Set<CollaborationChannel>().FirstOrDefaultAsync(c => c.Id == payload.ChannelId, ct);
        if (channel is not { IsActive: true }) return;
        var o = await sender.SendAsync(channel, payload.Message, $"collaboration.{payload.EventKey}", ct);
        await db.SaveChangesAsync(ct);
        if (!o.Success && o.Transient) throw new InvalidOperationException($"Collaboration delivery to {channel.Name} failed: {o.Error}");
        if (!o.Success) logger.LogWarning("Collaboration delivery to {Channel} failed permanently: {Error}", channel.Name, o.Error);
    }
}
