using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Customers;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Engineering;
using SupportDesk.Domain;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Integration;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Integrations.Engineering;

/// <summary>
/// Engineering escalation from a ticket (UC-9): prefill a work item from the ticket, create it in a tracker or link an
/// existing one, list links with live state, refresh, unlink and show the other tickets affected by the same item.
/// Escalation records a timeline entry, can set the "Engineering" substatus and publishes <c>engineering.item_updated</c>.
/// </summary>
public sealed class EngineeringTicketService(
    DeskDbContext db,
    RequestContext ctx,
    TicketService tickets,
    TicketHistory history,
    IEventPublisher events,
    EngineeringTrackerResolver resolver,
    EngineeringSyncService sync,
    TenantSettingsService settings,
    HtmlSanitization html,
    LimitGuard limits,
    IConfiguration configuration,
    ILogger<EngineeringTicketService> logger)
{
    public const string EngineeringSubstatus = "Engineering";
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    private string PublicBaseUrl =>
        (configuration["Communication:PublicBaseUrl"] is { Length: > 0 } a ? a : configuration["App:PublicUrl"] is { Length: > 0 } b ? b : "http://localhost:5830").TrimEnd('/');

    private async Task<Ticket> LoadVisibleAsync(Guid ticketId, CancellationToken ct)
    {
        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ticketId, ct) ?? throw new NotFoundException("Ticket", ticketId);
        await tickets.EnsureVisibleAsync(t, ct);
        return t;
    }

    // ───────────────────────────── Read ─────────────────────────────

    public async Task<List<ActiveTrackerDto>> ActiveTrackersAsync(CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.EngineeringLink, Permissions.EngineeringView, Permissions.TicketsView);
        var s = await settings.GetAsync<EngineeringSettings>(EngineeringSettings.Key, ct);
        var rows = await resolver.Trackers().AsNoTracking().Where(i => i.IsActive).OrderBy(i => i.Name).ToListAsync(ct);
        return rows.Select(i =>
        {
            var ts = resolver.Settings(i);
            var provider = TrackerProviders.FromIntegrationType(i.Type);
            return new ActiveTrackerDto(i.Id, TrackerProviders.Name(provider), i.Name,
                provider == EngineeringProvider.GitHub ? $"{ts.Owner}/{ts.Repository}" : ts.Project,
                [nameof(EngineeringItemType.Bug), nameof(EngineeringItemType.Feature), nameof(EngineeringItemType.Task)],
                Enum.TryParse<EngineeringItemType>(ts.DefaultItemType, true, out var dt) ? dt.ToString() : nameof(EngineeringItemType.Bug),
                provider == EngineeringProvider.Local, s.DefaultTrackerId == i.Id);
        }).OrderByDescending(t => t.IsDefault).ToList();
    }

    public async Task<TicketEngineeringDto> GetAsync(Guid ticketId, bool refresh, CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.EngineeringLink, Permissions.EngineeringView, Permissions.TicketsView);
        await LoadVisibleAsync(ticketId, ct);
        if (refresh)
        {
            var now = db.Clock.UtcNow;
            var keys = await db.EngineeringLinks.AsNoTracking().Where(l => l.TicketId == ticketId).Select(l => new { l.IntegrationId, l.ExternalId }).ToListAsync(ct);
            var integrationIds = keys.Where(k => k.IntegrationId != null).Select(k => k.IntegrationId!.Value).Distinct().ToList();
            var externalIds = keys.Select(k => k.ExternalId).Distinct().ToList();
            var stale = (await db.Set<EngineeringItem>().Where(i => integrationIds.Contains(i.IntegrationId) && externalIds.Contains(i.ExternalId)).ToListAsync(ct))
                .Where(i => keys.Any(k => k.IntegrationId == i.IntegrationId && k.ExternalId == i.ExternalId))
                .Where(i => !i.IsOrphaned && i.Provider != EngineeringProvider.Local && (i.LastSyncedAt is null || i.LastSyncedAt < now - StaleAfter)).ToList();
            if (stale.Count > 0)
            {
                try { await sync.RefreshAsync(stale, ct); }
                catch (DbUpdateException ex) { logger.LogWarning(ex, "Engineering refresh for ticket {Ticket} could not be saved", ticketId); db.ChangeTracker.Clear(); }
            }
        }
        var links = await ViewsAsync(ticketId, null, ct);
        return new TicketEngineeringDto(links, await ActiveTrackersAsync(ct), ctx.Has(Permissions.EngineeringLink), ctx.Has(Permissions.EngineeringManage));
    }

    private async Task<List<EngineeringLinkViewDto>> ViewsAsync(Guid ticketId, Guid? onlyLinkId, CancellationToken ct)
    {
        var q = db.EngineeringLinks.AsNoTracking().Where(l => l.TicketId == ticketId);
        if (onlyLinkId is { } lid) q = q.Where(l => l.Id == lid);
        var links = await q.OrderBy(l => l.CreatedAt).ToListAsync(ct);
        if (links.Count == 0) return [];
        var integrationIds = links.Where(l => l.IntegrationId != null).Select(l => l.IntegrationId!.Value).Distinct().ToList();
        var externalIds = links.Select(l => l.ExternalId).Distinct().ToList();
        var items = await db.Set<EngineeringItem>().AsNoTracking().Where(i => integrationIds.Contains(i.IntegrationId) && externalIds.Contains(i.ExternalId)).ToListAsync(ct);
        var trackers = await db.Integrations.AsNoTracking().Where(i => integrationIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Name, ct);
        var siblings = await db.EngineeringLinks.AsNoTracking().Where(l => l.IntegrationId != null && integrationIds.Contains(l.IntegrationId.Value) && externalIds.Contains(l.ExternalId))
            .Select(l => new { l.IntegrationId, l.ExternalId, l.TicketId }).ToListAsync(ct);
        var siblingTicketIds = siblings.Select(s => s.TicketId).Distinct().ToList();
        var customers = await db.Tickets.AsNoTracking().Where(t => siblingTicketIds.Contains(t.Id)).Select(t => new { t.Id, t.CustomerId }).ToDictionaryAsync(t => t.Id, t => t.CustomerId, ct);
        return links.Select(l =>
        {
            var item = items.FirstOrDefault(i => i.IntegrationId == l.IntegrationId && i.ExternalId == l.ExternalId);
            var same = siblings.Where(s => s.IntegrationId == l.IntegrationId && s.ExternalId == l.ExternalId && customers.ContainsKey(s.TicketId)).ToList();
            var snap = Json.DeserializeOr<EngineeringSnapshot?>(item?.SnapshotJson ?? l.SnapshotJson, null);
            return new EngineeringLinkViewDto(
                l.Id, l.TicketId, item?.Id, l.IntegrationId, l.IntegrationId is { } iid ? trackers.GetValueOrDefault(iid) ?? "Removed tracker" : "Tracker",
                TrackerProviders.Name(l.Provider), l.ItemType.ToString(), l.ExternalId, EngineeringSyncService.DisplayId(l.Provider, l.ExternalId),
                item?.Url ?? l.Url, item?.Title ?? l.Title, item?.State ?? l.State, item?.AssignedTo ?? l.AssignedTo, item?.IterationPath ?? l.IterationPath,
                item?.FixVersion ?? l.FixVersion, item?.ReleaseName ?? snap?.ReleaseName, item?.ReleasedAt ?? snap?.ReleasedAt, item?.IsResolved ?? l.IsResolved,
                item?.IsReleased ?? snap?.IsReleased ?? false, item?.IsOrphaned ?? snap?.IsOrphaned ?? false, item?.OrphanReason ?? snap?.OrphanReason,
                item?.LastSyncedAt ?? l.LastSyncedAt, item?.LastSyncError, Math.Max(1, same.Select(s => s.TicketId).Distinct().Count()),
                same.Select(s => customers[s.TicketId]).Where(c => c != null).Distinct().Count(), l.CustomerNotified, l.CreatedAt,
                snap?.Artifacts ?? []);
        }).ToList();
    }

    /// <summary>Tickets linked to the same work item as <paramref name="linkId"/> (scope-filtered; hidden ones are only counted).</summary>
    public async Task<AffectedTicketsDto> AffectedTicketsAsync(Guid ticketId, Guid linkId, CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.EngineeringLink, Permissions.EngineeringView, Permissions.TicketsView);
        await LoadVisibleAsync(ticketId, ct);
        var link = await db.EngineeringLinks.AsNoTracking().FirstOrDefaultAsync(l => l.Id == linkId && l.TicketId == ticketId, ct)
                   ?? throw new NotFoundException("Engineering link", linkId);
        return await AffectedAsync(link.IntegrationId, link.ExternalId, ticketId, ct);
    }

    internal async Task<AffectedTicketsDto> AffectedAsync(Guid? integrationId, string externalId, Guid? excludeTicketId, CancellationToken ct)
    {
        var ids = await db.EngineeringLinks.AsNoTracking().Where(l => l.IntegrationId == integrationId && l.ExternalId == externalId && l.TicketId != excludeTicketId)
            .Select(l => l.TicketId).Distinct().ToListAsync(ct);
        var existing = await db.Tickets.AsNoTracking().Where(t => ids.Contains(t.Id)).Select(t => t.Id).ToListAsync(ct);
        var visible = await db.Tickets.AsNoTracking().VisibleTo(ctx, db).Where(t => ids.Contains(t.Id))
            .Select(t => new { t.Id, t.Number, t.Subject, t.StatusId, t.CustomerId, t.CreatedAt }).ToListAsync(ct);
        var statusIds = visible.Select(v => v.StatusId).Distinct().ToList();
        var statuses = await db.TicketStatuses.AsNoTracking().Where(s => statusIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var customerIds = visible.Where(v => v.CustomerId != null).Select(v => v.CustomerId!.Value).Distinct().ToList();
        var customers = await db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var prefix = (await settings.TicketsAsync(ct)).NumberPrefix;
        var list = visible.OrderByDescending(v => v.CreatedAt).Select(v => new AffectedTicketDto(v.Id, v.Number, $"{prefix}{v.Number}", v.Subject,
            statuses.GetValueOrDefault(v.StatusId) ?? "", v.CustomerId is { } c ? customers.GetValueOrDefault(c) : null, v.CreatedAt)).ToList();
        return new AffectedTicketsDto(existing.Count, existing.Count - list.Count, list);
    }

    // ───────────────────────────── Prefill ─────────────────────────────

    public async Task<EscalationPrefillDto> PrefillAsync(Guid ticketId, CancellationToken ct = default)
    {
        ctx.Require(Permissions.EngineeringLink);
        var t = await LoadVisibleAsync(ticketId, ct);
        var product = t.ProductId is { } pid ? await db.Products.AsNoTracking().Where(p => p.Id == pid).Select(p => new { p.Name, p.Code }).FirstOrDefaultAsync(ct) : null;
        var module = t.ModuleId is { } mid ? await db.ProductModules.AsNoTracking().Where(m => m.Id == mid).Select(m => m.Name).FirstOrDefaultAsync(ct) : null;
        var installation = t.InstallationId is { } iid ? await db.Installations.AsNoTracking().FirstOrDefaultAsync(i => i.Id == iid, ct) : null;
        var versionId = t.VersionId ?? installation?.VersionId;
        var version = versionId is { } vid ? await db.ProductVersions.AsNoTracking().Where(v => v.Id == vid).Select(v => v.Version).FirstOrDefaultAsync(ct) : null;
        var customer = t.CustomerId is { } cid ? await db.Customers.AsNoTracking().Where(c => c.Id == cid).Select(c => c.Name).FirstOrDefaultAsync(ct) : null;
        var threads = await db.TicketThreads.AsNoTracking().Where(x => x.TicketId == ticketId && !x.IsRedacted &&
                (x.Kind == ThreadKind.CustomerMessage || x.Kind == ThreadKind.InternalNote || x.Kind == ThreadKind.AgentReply || x.Kind == ThreadKind.CallLog))
            .OrderBy(x => x.Seq).Take(40).Select(x => new { x.Kind, x.AuthorName, x.BodyText, x.CreatedAt }).ToListAsync(ct);
        var impact = await CustomerImpactAsync(t, ct);
        var prefix = (await settings.TicketsAsync(ct)).NumberPrefix;

        string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        sb.Append("<h3>Problem reported</h3>");
        sb.Append(string.IsNullOrWhiteSpace(t.DescriptionHtml) ? $"<p>{E(t.Subject)}</p>" : html.Sanitize(t.DescriptionHtml));
        var customerMessages = threads.Where(x => x.Kind is ThreadKind.CustomerMessage or ThreadKind.CallLog).Take(6).ToList();
        if (customerMessages.Count > 0)
        {
            sb.Append("<h3>Customer conversation</h3><ul>");
            foreach (var m in customerMessages) sb.Append($"<li><strong>{E(m.AuthorName ?? "Customer")}</strong> ({m.CreatedAt:yyyy-MM-dd HH:mm} UTC): {E(TextNormalization.Truncate(m.BodyText, 600))}</li>");
            sb.Append("</ul>");
        }
        var notes = threads.Where(x => x.Kind == ThreadKind.InternalNote).TakeLast(5).ToList();
        if (notes.Count > 0)
        {
            sb.Append("<h3>Support analysis</h3><ul>");
            foreach (var n in notes) sb.Append($"<li><strong>{E(n.AuthorName ?? "Agent")}</strong>: {E(TextNormalization.Truncate(n.BodyText, 600))}</li>");
            sb.Append("</ul>");
        }
        sb.Append("<h3>Environment</h3><ul>");
        sb.Append($"<li>Product: {E(product?.Name ?? "—")}</li>");
        if (module is not null) sb.Append($"<li>Module: {E(module)}</li>");
        sb.Append($"<li>Version: {E(version ?? "—")}</li>");
        if (installation is not null)
            sb.Append($"<li>Installation: {E(installation.Name)} ({installation.Environment}, {installation.DeploymentMode}{(installation.DatabaseVersion is null ? "" : ", " + E(installation.DatabaseVersion))}{(installation.OperatingSystem is null ? "" : ", " + E(installation.OperatingSystem))})</li>");
        sb.Append("</ul>");
        sb.Append($"<h3>Customer impact</h3><p>{impact} customer{(impact == 1 ? "" : "s")} affected{(customer is null ? "" : " (reported by " + E(customer) + ")")}. Support ticket {E(prefix + t.Number)}.</p>");

        var severity = t.Severity ?? t.Priority switch
        {
            TicketPriority.Critical => TicketSeverity.Critical,
            TicketPriority.High => TicketSeverity.Major,
            TicketPriority.Medium => TicketSeverity.Minor,
            _ => TicketSeverity.Cosmetic
        };
        var tags = new List<string>();
        if (product?.Code is { Length: > 0 } code) tags.Add(code);
        if (module is not null) tags.Add(module);
        return new EscalationPrefillDto(TextNormalization.Truncate(t.Subject, 255), sb.ToString(), severity.ToString(), t.Priority.ToString(), product?.Name, module, version,
            impact, tags);
    }

    /// <summary>Distinct customers of this ticket and the tickets linked to it (duplicates / related problems).</summary>
    private async Task<int> CustomerImpactAsync(Ticket t, CancellationToken ct)
    {
        var related = await db.TicketLinks.AsNoTracking().Where(l => l.SourceTicketId == t.Id || l.TargetTicketId == t.Id)
            .Select(l => l.SourceTicketId == t.Id ? l.TargetTicketId : l.SourceTicketId).ToListAsync(ct);
        related.Add(t.Id);
        var customers = await db.Tickets.AsNoTracking().Where(x => related.Contains(x.Id) && x.CustomerId != null).Select(x => x.CustomerId).Distinct().CountAsync(ct);
        return Math.Max(1, customers);
    }

    // ───────────────────────────── Escalate / link / unlink ─────────────────────────────

    public async Task<EscalationResultDto> EscalateAsync(Guid ticketId, EscalateToEngineeringRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.EngineeringLink);
        var ticket = await tickets.LoadForUpdateAsync(ticketId, ct);
        if (r.ClientRequestId is { Length: > 0 } key)
        {
            var marker = $"\"clientRequestId\":\"{key.Replace("\"", "")}\"";
            var existing = await db.EngineeringLinks.AsNoTracking().Where(l => l.TicketId == ticketId && l.SnapshotJson != null && l.SnapshotJson.Contains(marker))
                .Select(l => l.Id).FirstOrDefaultAsync(ct);
            if (existing != Guid.Empty) return new EscalationResultDto((await ViewsAsync(ticketId, existing, ct)).Single(), false, "Already escalated (retry).");
        }
        var errors = new Dictionary<string, string>();
        var title = Parse.Clean(r.Title, 255);
        if (title is null) errors["title"] = "Enter a title for the work item.";
        var type = Parse.Enum(r.ItemType, EngineeringItemType.Bug, "itemType");
        if (type is not (EngineeringItemType.Bug or EngineeringItemType.Feature or EngineeringItemType.Task)) errors["itemType"] = "Choose Bug, Feature or Task.";
        var severity = string.IsNullOrWhiteSpace(r.Severity) ? (TicketSeverity?)null : Parse.Enum(r.Severity, TicketSeverity.Minor, "severity");
        var priority = Parse.Enum(r.Priority, ticket.Priority, "priority");
        if (r.Tags is { Count: > 20 }) errors["tags"] = "Use at most 20 tags.";
        if (errors.Count > 0) throw new ValidationException(errors);

        var tracker = await resolver.GetActiveAsync(r.TrackerId, ct);
        var provider = TrackerProviders.FromIntegrationType(tracker.Type);
        if (provider != EngineeringProvider.Local) await limits.RequireFeatureAsync(PlanFeatures.EngineeringIntegration, ct);
        if (r.AssigneeUserId is not null && provider != EngineeringProvider.Local)
            throw new ValidationException(new Dictionary<string, string> { ["assigneeUserId"] = "Assign engineers in the external tracker." });
        var ts = resolver.Settings(tracker);
        var prefix = (await settings.TicketsAsync(ct)).NumberPrefix;
        var tags = (r.Tags ?? []).Concat(ts.Fields.DefaultTags).Select(x => x.Trim()).Where(x => x.Length is > 0 and <= 100)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
        var body = string.IsNullOrWhiteSpace(r.ReproStepsHtml) ? null : html.Sanitize(r.ReproStepsHtml);
        var draft = new WorkItemDraft(type, ts.TrackerTypeFor(type), title!, body, severity, priority,
            Parse.Clean(r.AreaPath, 300) ?? ts.Fields.DefaultAreaPath, Parse.Clean(r.IterationPath, 300) ?? ts.Fields.DefaultIterationPath, tags, r.AssigneeUserId,
            await CustomerImpactAsync(ticket, ct), ticket.ProductId, $"{prefix}{ticket.Number}", $"{PublicBaseUrl}/tickets/{ticket.Id}");

        TrackerWorkItem created;
        try { created = await resolver.Create(tracker).CreateAsync(draft, ct); }
        catch (TrackerException ex) { throw new DomainException("integration_error", $"{tracker.Name}: {ex.Message}", 502); }

        var item = await sync.UpsertAsync(tracker, created, ts, ct);
        var link = AddLink(ticket, tracker, item, r.ClientRequestId);
        var display = EngineeringSyncService.DisplayId(provider, created.ExternalId);
        history.Record(ticket, TicketEventTypes.EngineeringLinked, $"Escalated to engineering: {item.ItemType} {display} “{item.Title}” created in {tracker.Name}", null,
            new { trackerId = tracker.Id, provider = TrackerProviders.Name(provider), externalId = created.ExternalId, url = created.Url, created = true });
        var (applied, message) = r.SetSubstatus ? await ApplySubstatusAsync(ticket, ct) : (false, null);
        ticket.LastActivityAt = db.Clock.UtcNow;
        events.Publish(EventTypes.EngineeringItemUpdated, "Ticket", ticket.Id,
            EngineeringSyncService.Payload(EngineeringItemEventPayload.Linked, ticket, link, item, null, false, false, false, $"Escalated to engineering: {display}"));
        try
        {
            await tickets.SaveAsync(ticket, ct);
        }
        catch (Exception ex) when (ex is DbUpdateException or ConcurrencyConflictException && provider != EngineeringProvider.Local)
        {
            logger.LogError(ex, "Work item {ExternalId} was created in {Tracker} but the ticket link could not be saved", created.ExternalId, tracker.Name);
            throw new DomainException("integration_partial", $"{display} was created in {tracker.Name} but could not be linked; link it by id.", 409);
        }
        return new EscalationResultDto((await ViewsAsync(ticketId, link.Id, ct)).Single(), applied, message);
    }

    public async Task<EscalationResultDto> LinkAsync(Guid ticketId, LinkWorkItemRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.EngineeringLink);
        var ticket = await tickets.LoadForUpdateAsync(ticketId, ct);
        var tracker = await resolver.GetActiveAsync(r.TrackerId, ct);
        var provider = TrackerProviders.FromIntegrationType(tracker.Type);
        if (provider != EngineeringProvider.Local) await limits.RequireFeatureAsync(PlanFeatures.EngineeringIntegration, ct);
        var adapter = resolver.Create(tracker);
        var externalId = string.IsNullOrWhiteSpace(r.Reference) ? null : adapter.ParseReference(r.Reference);
        if (externalId is null)
            throw new ValidationException(new Dictionary<string, string> { ["reference"] = $"Enter a {tracker.Name} work item id or URL." });
        if (await db.EngineeringLinks.AnyAsync(l => l.TicketId == ticketId && l.IntegrationId == tracker.Id && l.ExternalId == externalId, ct))
            throw new ConflictException("already_linked", "This work item is already linked to the ticket.");

        IReadOnlyList<TrackerLookup> found;
        try { found = await adapter.GetAsync([externalId], ct); }
        catch (TrackerException ex) { throw new DomainException("integration_error", $"{tracker.Name}: {ex.Message}", 502); }
        var lookup = found.FirstOrDefault();
        if (lookup is not { Status: TrackerLookupStatus.Found, Item: { } w })
            throw new ValidationException(new Dictionary<string, string> { ["reference"] = lookup?.Detail ?? $"{adapter.DisplayId(externalId)} was not found in {tracker.Name}." });

        var ts = resolver.Settings(tracker);
        var item = await sync.UpsertAsync(tracker, w, ts, ct);
        var link = AddLink(ticket, tracker, item, null);
        var display = EngineeringSyncService.DisplayId(provider, externalId);
        history.Record(ticket, TicketEventTypes.EngineeringLinked, $"Linked engineering {item.ItemType} {display} “{item.Title}” ({tracker.Name})", null,
            new { trackerId = tracker.Id, provider = TrackerProviders.Name(provider), externalId, url = item.Url, created = false });
        var (applied, message) = r.SetSubstatus ? await ApplySubstatusAsync(ticket, ct) : (false, null);
        ticket.LastActivityAt = db.Clock.UtcNow;
        events.Publish(EventTypes.EngineeringItemUpdated, "Ticket", ticket.Id,
            EngineeringSyncService.Payload(EngineeringItemEventPayload.Linked, ticket, link, item, null, false, false, false, $"Linked to {display}"));
        await tickets.SaveAsync(ticket, ct);
        return new EscalationResultDto((await ViewsAsync(ticketId, link.Id, ct)).Single(), applied, message);
    }

    private EngineeringLink AddLink(Ticket ticket, Integration tracker, EngineeringItem item, string? clientRequestId)
    {
        var link = new EngineeringLink
        {
            TicketId = ticket.Id, Provider = item.Provider, IntegrationId = tracker.Id, ItemType = item.ItemType, ExternalId = item.ExternalId,
            Url = item.Url, Title = item.Title
        };
        EngineeringSyncService.CopyToLink(link, item);
        if (clientRequestId is { Length: > 0 })
        {
            var snap = Json.DeserializeOr<EngineeringSnapshot?>(link.SnapshotJson, null) ?? new EngineeringSnapshot([], null, null, []);
            link.SnapshotJson = Json.Serialize(snap with { ClientRequestId = clientRequestId.Replace("\"", "") });
        }
        db.EngineeringLinks.Add(link);
        return link;
    }

    /// <summary>
    /// Sets substatus "Engineering": on the current status when it offers that substatus, otherwise on the configured
    /// escalation status. Returns whether it was applied and why not (never fails the escalation).
    /// </summary>
    private async Task<(bool Applied, string? Message)> ApplySubstatusAsync(Ticket t, CancellationToken ct)
    {
        var s = await settings.GetAsync<EngineeringSettings>(EngineeringSettings.Key, ct);
        if (!s.SetEngineeringSubstatus) return (false, null);
        if (t.StatusCategory.IsResolvedOrClosed()) return (false, "Resolved tickets keep their status.");
        if (!ctx.Has(Permissions.TicketsEdit)) return (false, "You cannot change the ticket status.");
        var statuses = await tickets.StatusesAsync(ct);
        string? Sub(TicketStatus st) => Json.StringList(st.SubstatusesJson).FirstOrDefault(x => string.Equals(x, EngineeringSubstatus, StringComparison.OrdinalIgnoreCase));
        var current = statuses.FirstOrDefault(x => x.Id == t.StatusId);
        var target = current is not null && Sub(current) is not null ? current
            : s.EscalationStatusId is { } sid ? statuses.FirstOrDefault(x => x.Id == sid) : null;
        if (target is null) return (false, "No status offers the \"Engineering\" substatus; configure one under Administration → Engineering.");
        var sub = Sub(target);
        if (target.Id == t.StatusId && t.Substatus == sub) return (true, null);
        try
        {
            await tickets.ApplyStatusAsync(t, target, sub, "Escalated to engineering", bypassRules: false, ct);
            return (true, null);
        }
        catch (DomainException ex)
        {
            return (false, $"Status not changed: {ex.Message}");
        }
    }

    public async Task UnlinkAsync(Guid ticketId, Guid linkId, CancellationToken ct = default)
    {
        ctx.Require(Permissions.EngineeringLink);
        var ticket = await tickets.LoadForUpdateAsync(ticketId, ct);
        var link = await db.EngineeringLinks.FirstOrDefaultAsync(l => l.Id == linkId && l.TicketId == ticketId, ct) ?? throw new NotFoundException("Engineering link", linkId);
        var item = await db.Set<EngineeringItem>().FirstOrDefaultAsync(i => i.IntegrationId == link.IntegrationId && i.ExternalId == link.ExternalId, ct);
        db.EngineeringLinks.Remove(link);
        var display = EngineeringSyncService.DisplayId(link.Provider, link.ExternalId);
        history.Record(ticket, TicketEventTypes.EngineeringUpdated, $"Unlinked engineering {link.ItemType} {display} “{link.Title}”",
            [new FieldChange("engineeringLink", display, null)],
            new { provider = TrackerProviders.Name(link.Provider), link.ExternalId, link.Url, link.State, link.FixVersion, unlinked = true });
        if (item is not null && !await db.EngineeringLinks.AnyAsync(l => l.Id != link.Id && l.IntegrationId == link.IntegrationId && l.ExternalId == link.ExternalId, ct))
            db.Set<EngineeringItem>().Remove(item);
        ticket.LastActivityAt = db.Clock.UtcNow;
        events.Publish(EventTypes.EngineeringItemUpdated, "Ticket", ticket.Id,
            EngineeringSyncService.Payload(EngineeringItemEventPayload.Unlinked, ticket, link, null, null, false, false, false, $"Unlinked {display}"));
        await tickets.SaveAsync(ticket, ct);
    }

    /// <summary>Re-reads one linked item from its tracker now.</summary>
    public async Task<EngineeringLinkViewDto> RefreshAsync(Guid ticketId, Guid linkId, CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.EngineeringLink, Permissions.EngineeringView, Permissions.TicketsView);
        await LoadVisibleAsync(ticketId, ct);
        var link = await db.EngineeringLinks.AsNoTracking().FirstOrDefaultAsync(l => l.Id == linkId && l.TicketId == ticketId, ct) ?? throw new NotFoundException("Engineering link", linkId);
        var item = await db.Set<EngineeringItem>().FirstOrDefaultAsync(i => i.IntegrationId == link.IntegrationId && i.ExternalId == link.ExternalId, ct);
        if (item is not null && !item.IsOrphaned) await sync.RefreshAsync([item], ct);
        return (await ViewsAsync(ticketId, linkId, ct)).Single();
    }
}
