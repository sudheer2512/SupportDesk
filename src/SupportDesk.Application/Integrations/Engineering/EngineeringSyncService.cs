using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
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
/// Payload of <c>engineering.item_updated</c> (one event per linked ticket; aggregate = Ticket). The first fields match
/// <see cref="TicketEventPayload"/> so ticket-oriented consumers (webhooks, realtime, future automation triggers) can read it.
/// </summary>
public sealed record EngineeringItemEventPayload(
    Guid TicketId,
    long Number,
    IReadOnlyList<FieldChange>? Changes,
    Guid? ThreadId,
    string? Note,
    string Action,
    Guid? ItemId,
    Guid LinkId,
    Guid? IntegrationId,
    string Provider,
    string ExternalId,
    string DisplayId,
    string Title,
    string? Url,
    string? State,
    string? FixVersion,
    string? ReleaseName,
    bool IsResolved,
    bool IsReleased,
    bool IsOrphaned,
    bool BecameResolved,
    bool BecameReleased,
    bool BecameOrphaned)
{
    public const string Linked = "linked";
    public const string Updated = "updated";
    public const string Unlinked = "unlinked";
}

/// <summary>JSON kept on EngineeringItem/EngineeringLink: tracker details beyond the indexed columns.</summary>
public sealed record EngineeringSnapshot(
    IReadOnlyList<string> Tags,
    string? Severity,
    string? Priority,
    IReadOnlyList<EngineeringArtifactDto> Artifacts,
    string? ReleaseName = null,
    DateTime? ReleasedAt = null,
    bool IsReleased = false,
    bool IsOrphaned = false,
    string? OrphanReason = null,
    string? ClientRequestId = null);

/// <summary>
/// Brings linked work items up to date from their tracker (webhooks, polling, the local board, "refresh now") and fans
/// changes out to every linked ticket: link snapshot, ticket history and one <c>engineering.item_updated</c> outbox
/// event per ticket (notes, notifications and customer replies are side effects of those events). Deleted or moved
/// items are marked orphaned and keep their last known data. Nothing here calls SaveChanges unless stated.
/// </summary>
public sealed class EngineeringSyncService(
    DeskDbContext db,
    RequestContext ctx,
    EngineeringTrackerResolver resolver,
    TicketHistory history,
    IEventPublisher events,
    ILogger<EngineeringSyncService> logger)
{
    public const int BatchSize = 100;

    public static string DisplayId(EngineeringProvider provider, string externalId) =>
        provider == EngineeringProvider.Local ? LocalTracker.KeyPrefix + externalId : "#" + externalId;

    // ───────────────────────────── Items ─────────────────────────────

    /// <summary>Finds or creates the shared item for a tracker snapshot (used when linking; no events).</summary>
    public async Task<EngineeringItem> UpsertAsync(Integration tracker, TrackerWorkItem w, TrackerSettings settings, CancellationToken ct)
    {
        var item = await db.Set<EngineeringItem>().FirstOrDefaultAsync(i => i.IntegrationId == tracker.Id && i.ExternalId == w.ExternalId, ct)
                   ?? db.Set<EngineeringItem>().Local.FirstOrDefault(i => i.IntegrationId == tracker.Id && i.ExternalId == w.ExternalId);
        if (item is null)
        {
            item = new EngineeringItem { IntegrationId = tracker.Id, Provider = TrackerProviders.FromIntegrationType(tracker.Type), ExternalId = w.ExternalId };
            db.Set<EngineeringItem>().Add(item);
        }
        Copy(item, w, settings);
        item.IsOrphaned = false;
        item.OrphanReason = null;
        item.OrphanedAt = null;
        item.LastSyncedAt = db.Clock.UtcNow;
        item.LastSyncError = null;
        return item;
    }

    private void Copy(EngineeringItem item, TrackerWorkItem w, TrackerSettings settings)
    {
        var now = db.Clock.UtcNow;
        item.Title = TextNormalization.Truncate(w.Title, 500);
        item.Url = w.Url ?? item.Url;
        item.TrackerType = w.TrackerType;
        item.ItemType = w.ItemType;
        item.Project = w.Project ?? item.Project;
        item.State = w.State;
        item.AssignedTo = w.AssignedTo;
        item.AreaPath = w.AreaPath;
        item.IterationPath = w.IterationPath;
        item.FixVersion = w.FixVersion;
        item.ExternalChangedAt = w.ChangedAt ?? item.ExternalChangedAt;
        if (w.Released == true)
        {
            item.ReleaseName = w.ReleaseName ?? w.FixVersion ?? item.ReleaseName;
            item.ReleasedAt = w.ReleasedAt ?? item.ReleasedAt ?? now;
            item.DeployedAt = w.DeployedAt ?? item.DeployedAt;
        }
        else if (w.Released == false)
        {
            item.ReleaseName = null;
            item.ReleasedAt = null;
            item.DeployedAt = null;
        }
        var releasedByState = StateMatcher.Matches(settings.ReleasedStates, w.State, w.Tags);
        if (releasedByState && item.ReleasedAt is null)
        {
            item.ReleasedAt = now;
            item.ReleaseName ??= w.FixVersion;
        }
        item.IsReleased = item.ReleasedAt is not null && (releasedByState || w.Released == true || (w.Released is null && item.ReleaseName is not null));
        item.IsResolved = item.IsReleased || (w.Resolved ?? StateMatcher.Matches(settings.ResolvedStates, w.State, w.Tags));
        var old = Json.DeserializeOr<EngineeringSnapshot?>(item.SnapshotJson, null);
        item.SnapshotJson = Json.Serialize(new EngineeringSnapshot(w.Tags, w.Severity, w.Priority,
            w.Artifacts.Count > 0 || old is null ? w.Artifacts.Select(a => new EngineeringArtifactDto(a.Kind, a.Id, a.Title, a.Url, a.State)).ToList() : old.Artifacts,
            item.ReleaseName, item.ReleasedAt, item.IsReleased));
    }

    private sealed record ItemState(string Title, string? State, string? AssignedTo, string? FixVersion, string? ReleaseName, bool IsResolved, bool IsReleased,
        bool IsOrphaned, DateTime? DeployedAt);

    private static ItemState Capture(EngineeringItem i) => new(i.Title, i.State, i.AssignedTo, i.FixVersion, i.ReleaseName, i.IsResolved, i.IsReleased, i.IsOrphaned, i.DeployedAt);

    /// <summary>Applies a tracker lookup to an existing item and fans the change out. Returns true when something changed.</summary>
    public async Task<bool> ApplyAsync(EngineeringItem item, TrackerLookup lookup, TrackerSettings settings, string source, CancellationToken ct)
    {
        var before = Capture(item);
        var now = db.Clock.UtcNow;
        if (lookup.Status == TrackerLookupStatus.Found && lookup.Item is { } w)
        {
            Copy(item, w, settings);
            item.IsOrphaned = false;
            item.OrphanReason = null;
            item.OrphanedAt = null;
        }
        else if (!item.IsOrphaned)
        {
            MarkOrphaned(item, lookup.Status switch
            {
                TrackerLookupStatus.Deleted => "deleted",
                TrackerLookupStatus.Moved => "moved",
                _ => "not_found"
            });
        }
        item.LastSyncedAt = now;
        item.LastSyncError = null;
        return await PublishChangesAsync(item, before, source, ct);
    }

    private void MarkOrphaned(EngineeringItem item, string reason)
    {
        item.IsOrphaned = true;
        item.OrphanReason = reason;
        item.OrphanedAt = db.Clock.UtcNow;
        var snap = Json.DeserializeOr<EngineeringSnapshot?>(item.SnapshotJson, null) ?? new EngineeringSnapshot([], null, null, []);
        item.SnapshotJson = Json.Serialize(snap with { IsOrphaned = true, OrphanReason = reason });
    }

    /// <summary>Marks the item orphaned (deleted/moved in the tracker) keeping its last known data.</summary>
    public async Task<bool> OrphanAsync(EngineeringItem item, string reason, string source, CancellationToken ct)
    {
        if (item.IsOrphaned) return false;
        var before = Capture(item);
        MarkOrphaned(item, reason);
        item.LastSyncedAt = db.Clock.UtcNow;
        return await PublishChangesAsync(item, before, source, ct);
    }

    /// <summary>
    /// A release/deployment happened in the tracker (GitHub release, Azure DevOps deployment, local release): every item of
    /// the tracker whose fix version names that version becomes released. Returns the number of items updated.
    /// </summary>
    public async Task<int> ApplyReleaseAsync(Integration tracker, string version, DateTime at, bool deployed, string source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(version)) return 0;
        var candidates = await db.Set<EngineeringItem>().Where(i => i.IntegrationId == tracker.Id && !i.IsOrphaned && i.FixVersion != null).ToListAsync(ct);
        var updated = 0;
        foreach (var item in candidates.Where(i => StateMatcher.SameVersion(i.FixVersion, version)))
        {
            if (item.IsReleased && (!deployed || item.DeployedAt is not null)) continue;
            var before = Capture(item);
            item.ReleaseName ??= version;
            item.ReleasedAt ??= at;
            if (deployed) item.DeployedAt ??= at;
            item.IsReleased = true;
            item.IsResolved = true;
            var snap = Json.DeserializeOr<EngineeringSnapshot?>(item.SnapshotJson, null) ?? new EngineeringSnapshot([], null, null, []);
            item.SnapshotJson = Json.Serialize(snap with { ReleaseName = item.ReleaseName, ReleasedAt = item.ReleasedAt, IsReleased = true });
            if (await PublishChangesAsync(item, before, source, ct)) updated++;
        }
        return updated;
    }

    /// <summary>Adds artifacts (e.g. a merged pull request reported by a webhook) to the item's snapshot without state changes.</summary>
    public void AddArtifacts(EngineeringItem item, IEnumerable<EngineeringArtifactDto> artifacts)
    {
        var snap = Json.DeserializeOr<EngineeringSnapshot?>(item.SnapshotJson, null) ?? new EngineeringSnapshot([], null, null, []);
        var merged = snap.Artifacts.ToList();
        foreach (var a in artifacts)
            if (!merged.Any(x => x.Kind == a.Kind && x.Id == a.Id)) merged.Add(a);
        item.SnapshotJson = Json.Serialize(snap with { Artifacts = merged });
    }

    private async Task<bool> PublishChangesAsync(EngineeringItem item, ItemState before, string source, CancellationToken ct)
    {
        var after = Capture(item);
        var changes = new List<FieldChange>();
        void Diff(string field, string? from, string? to)
        {
            if (!string.Equals(from, to, StringComparison.Ordinal)) changes.Add(new FieldChange(field, from, to));
        }
        Diff("title", before.Title, after.Title);
        Diff("state", before.State, after.State);
        Diff("assignedTo", before.AssignedTo, after.AssignedTo);
        Diff("fixVersion", before.FixVersion, after.FixVersion);
        Diff("release", before.ReleaseName, after.ReleaseName);
        Diff("resolved", before.IsResolved ? "true" : "false", after.IsResolved ? "true" : "false");
        Diff("released", before.IsReleased ? "true" : "false", after.IsReleased ? "true" : "false");
        Diff("deployed", before.DeployedAt is null ? "false" : "true", after.DeployedAt is null ? "false" : "true");
        Diff("orphaned", before.IsOrphaned ? "true" : "false", after.IsOrphaned ? "true" : "false");

        var links = await db.EngineeringLinks.Where(l => l.IntegrationId == item.IntegrationId && l.ExternalId == item.ExternalId).ToListAsync(ct);
        foreach (var link in links) CopyToLink(link, item);
        if (changes.Count == 0) return false;

        var becameResolved = !before.IsResolved && after.IsResolved;
        var becameReleased = !before.IsReleased && after.IsReleased;
        var becameOrphaned = !before.IsOrphaned && after.IsOrphaned;
        var display = DisplayId(item.Provider, item.ExternalId);
        var summary = Summary(item, display, before, changes);
        var ticketIds = links.Select(l => l.TicketId).Distinct().ToList();
        var tickets = await db.Tickets.AsNoTracking().Where(t => ticketIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        foreach (var link in links)
        {
            if (!tickets.TryGetValue(link.TicketId, out var ticket)) continue; // deleted ticket: the link stays for history only
            history.Record(ticket, TicketEventTypes.EngineeringUpdated, summary, changes,
                new { itemId = item.Id, provider = TrackerProviders.Name(item.Provider), externalId = item.ExternalId, url = item.Url, source });
            events.Publish(EventTypes.EngineeringItemUpdated, "Ticket", ticket.Id, Payload(EngineeringItemEventPayload.Updated, ticket, link, item, changes,
                becameResolved, becameReleased, becameOrphaned, summary));
        }
        logger.LogInformation("Engineering item {Provider} {ExternalId} updated from {Source}: {Changes}", item.Provider, item.ExternalId, source,
            string.Join(", ", changes.Select(c => c.Field)));
        return true;
    }

    public static void CopyToLink(EngineeringLink link, EngineeringItem item)
    {
        link.Title = item.Title;
        link.Url = item.Url ?? link.Url;
        link.ItemType = item.ItemType;
        link.State = item.State;
        link.AssignedTo = item.AssignedTo;
        link.IterationPath = item.IterationPath;
        link.FixVersion = item.ReleaseName ?? item.FixVersion;
        link.IsResolved = item.IsResolved;
        link.LastSyncedAt = item.LastSyncedAt;
        var keep = Json.DeserializeOr<EngineeringSnapshot?>(link.SnapshotJson, null)?.ClientRequestId;
        var snap = Json.DeserializeOr<EngineeringSnapshot?>(item.SnapshotJson, null) ?? new EngineeringSnapshot([], null, null, []);
        link.SnapshotJson = Json.Serialize(snap with
        {
            ReleaseName = item.ReleaseName, ReleasedAt = item.ReleasedAt, IsReleased = item.IsReleased, IsOrphaned = item.IsOrphaned, OrphanReason = item.OrphanReason,
            ClientRequestId = keep
        });
    }

    private static string Summary(EngineeringItem item, string display, ItemState before, IReadOnlyList<FieldChange> changes)
    {
        var parts = new List<string>();
        if (item.IsOrphaned && !before.IsOrphaned)
            parts.Add(item.OrphanReason == "moved" ? "moved out of the tracker project (last known data kept)" : "deleted in the tracker (last known data kept)");
        if (changes.Any(c => c.Field == "state")) parts.Add($"state {before.State ?? "—"} → {item.State ?? "—"}");
        if (item.IsReleased && !before.IsReleased) parts.Add($"fix released{(item.ReleaseName is null ? "" : " in " + item.ReleaseName)}");
        else if (changes.Any(c => c.Field == "fixVersion") && item.FixVersion is not null) parts.Add($"fix version {item.FixVersion}");
        if (item.DeployedAt is not null && before.DeployedAt is null) parts.Add("deployed");
        if (changes.Any(c => c.Field == "assignedTo")) parts.Add($"assigned to {item.AssignedTo ?? "nobody"}");
        if (parts.Count == 0) parts.Add("details updated");
        return TextNormalization.Truncate($"Engineering {item.ItemType} {display}: {string.Join("; ", parts)}", 1000);
    }

    public static EngineeringItemEventPayload Payload(string action, Ticket ticket, EngineeringLink link, EngineeringItem? item, IReadOnlyList<FieldChange>? changes,
        bool becameResolved, bool becameReleased, bool becameOrphaned, string? note) =>
        new(ticket.Id, ticket.Number, changes, null, note, action, item?.Id, link.Id, link.IntegrationId, TrackerProviders.Name(link.Provider), link.ExternalId,
            DisplayId(link.Provider, link.ExternalId), link.Title, link.Url, item?.State ?? link.State, item?.FixVersion ?? link.FixVersion, item?.ReleaseName,
            item?.IsResolved ?? link.IsResolved, item?.IsReleased ?? false, item?.IsOrphaned ?? false, becameResolved, becameReleased, becameOrphaned);

    // ───────────────────────────── Polling / refresh ─────────────────────────────

    /// <summary>Re-reads the given items from their trackers (errors are recorded per item, never thrown). Saves.</summary>
    public async Task RefreshAsync(IReadOnlyList<EngineeringItem> items, CancellationToken ct)
    {
        foreach (var group in items.Where(i => !i.IsOrphaned).GroupBy(i => i.IntegrationId))
        {
            var tracker = await resolver.Trackers().AsNoTracking().FirstOrDefaultAsync(t => t.Id == group.Key, ct);
            if (tracker is not { IsActive: true }) continue;
            var settings = resolver.Settings(tracker);
            try
            {
                var adapter = resolver.Create(tracker);
                var lookups = await adapter.GetAsync(group.Select(i => i.ExternalId).ToList(), ct);
                foreach (var item in group)
                    if (lookups.FirstOrDefault(l => l.ExternalId == item.ExternalId) is { } lookup)
                        await ApplyAsync(item, lookup, settings, "refresh", ct);
            }
            catch (Exception ex) when (ex is TrackerException or DomainException or HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Engineering refresh failed for tracker {Tracker}", tracker.Id);
                foreach (var item in group) item.LastSyncError = TextNormalization.Truncate(ex.Message, 1000);
            }
        }
        await SaveWithRetryAsync(ct);
    }

    /// <summary>Polls one tracker for every linked, non-orphaned item (oldest sync first). Saves after every batch.</summary>
    public async Task<TrackerSyncResultDto> SyncTrackerAsync(Guid trackerId, CancellationToken ct)
    {
        var tracker = await resolver.GetAsync(trackerId, ct, track: true);
        var settings = resolver.Settings(tracker);
        var linked = db.EngineeringLinks.Where(l => l.IntegrationId == tracker.Id).Select(l => l.ExternalId);
        var items = await db.Set<EngineeringItem>().Where(i => i.IntegrationId == tracker.Id && !i.IsOrphaned && linked.Contains(i.ExternalId))
            .OrderBy(i => i.LastSyncedAt).Take(1000).ToListAsync(ct);
        int updated = 0, orphaned = 0, failed = 0;
        string? error = null;
        IEngineeringTracker adapter;
        try { adapter = resolver.Create(tracker); }
        catch (DomainException ex) { return await FailAsync(ex.Message); }

        foreach (var batch in items.Chunk(BatchSize))
        {
            try
            {
                var lookups = await adapter.GetAsync(batch.Select(i => i.ExternalId).ToList(), ct);
                foreach (var item in batch)
                {
                    if (lookups.FirstOrDefault(l => l.ExternalId == item.ExternalId) is not { } lookup) continue;
                    var wasOrphaned = item.IsOrphaned;
                    if (await ApplyAsync(item, lookup, settings, "poll", ct)) updated++;
                    if (!wasOrphaned && item.IsOrphaned) orphaned++;
                }
                await SaveWithRetryAsync(ct);
            }
            catch (Exception ex) when (ex is TrackerException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Engineering poll failed for tracker {Tracker}", tracker.Id);
                failed += batch.Length;
                error = ex.Message;
                foreach (var item in batch) item.LastSyncError = TextNormalization.Truncate(ex.Message, 1000);
                break;
            }
        }
        tracker.LastSyncAt = db.Clock.UtcNow;
        tracker.Status = error is null ? (tracker.IsActive ? IntegrationStatus.Connected : IntegrationStatus.Disabled) : IntegrationStatus.Error;
        tracker.LastError = error is null ? null : TextNormalization.Truncate(error, 1000);
        await SaveWithRetryAsync(ct);
        return new TrackerSyncResultDto(items.Count, updated, orphaned, failed, error);

        async Task<TrackerSyncResultDto> FailAsync(string message)
        {
            tracker.Status = IntegrationStatus.Error;
            tracker.LastError = TextNormalization.Truncate(message, 1000);
            tracker.LastSyncAt = db.Clock.UtcNow;
            await SaveWithRetryAsync(ct);
            return new TrackerSyncResultDto(items.Count, 0, 0, items.Count, message);
        }
    }

    /// <summary>The local board changed these work items: propagate to linked tickets (same unit of work as the board change).</summary>
    public async Task ApplyLocalAsync(Integration tracker, IReadOnlyList<LocalWorkItem> workItems, CancellationToken ct)
    {
        if (workItems.Count == 0) return;
        var settings = resolver.Settings(tracker);
        var ids = workItems.Select(w => w.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var items = await db.Set<EngineeringItem>().Where(i => i.IntegrationId == tracker.Id && ids.Contains(i.ExternalId)).ToListAsync(ct);
        if (items.Count == 0) return;
        var releaseIds = workItems.Where(w => w.ReleaseId != null).Select(w => w.ReleaseId!.Value).Distinct().ToList();
        var releases = await db.Set<LocalRelease>().AsNoTracking().Where(r => releaseIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        foreach (var local in db.Set<LocalRelease>().Local.Where(r => releaseIds.Contains(r.Id))) releases[local.Id] = local;
        var userIds = workItems.Where(w => w.AssigneeUserId != null).Select(w => w.AssigneeUserId!.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        foreach (var w in workItems)
        {
            var item = items.FirstOrDefault(i => i.ExternalId == w.Number.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (item is null) continue;
            var lookup = w.IsDeleted
                ? TrackerLookup.Missing(item.ExternalId, TrackerLookupStatus.Deleted)
                : TrackerLookup.Found(LocalTracker.Snapshot(w, w.ReleaseId is { } rid ? releases.GetValueOrDefault(rid) : null,
                    w.AssigneeUserId is { } a ? users.GetValueOrDefault(a) : null) with { ChangedAt = db.Clock.UtcNow });
            await ApplyAsync(item, lookup, settings, "board", ct);
        }
    }

    /// <summary>Saves; on a rowversion conflict (webhook and poll racing) reloads conflicting rows once and retries.</summary>
    public async Task SaveWithRetryAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            logger.LogInformation("Engineering sync concurrency conflict; keeping the newer data and retrying ({Count} rows)", ex.Entries.Count);
            foreach (var entry in ex.Entries) await entry.ReloadAsync(ct);
            await db.SaveChangesAsync(ct);
        }
    }

    internal RequestContext Context => ctx;
}

/// <summary>Polling fallback: every 5 minutes, syncs each active external tracker whose poll interval has elapsed.</summary>
public sealed class EngineeringSyncTask(TenantScopeRunner tenants, ILogger<EngineeringSyncTask> logger) : IRecurringTask
{
    public const string TaskName = "engineering-sync";
    public string Name => TaskName;
    public TimeSpan Interval => TimeSpan.FromMinutes(5);

    public Task RunAsync(CancellationToken ct) => tenants.ForEachActiveTenantAsync(Name, async (sp, tenant, token) =>
    {
        var db = sp.GetRequiredService<DeskDbContext>();
        var now = db.Clock.UtcNow;
        var trackers = await db.Integrations.AsNoTracking()
            .Where(i => i.IsActive && (i.Type == IntegrationTypes.AzureDevOps || i.Type == IntegrationTypes.GitHub)).ToListAsync(token);
        if (trackers.Count == 0) return;
        var resolver = sp.GetRequiredService<EngineeringTrackerResolver>();
        foreach (var t in trackers)
        {
            var interval = Math.Max(5, resolver.Settings(t).PollIntervalMinutes);
            if (t.LastSyncAt is { } last && last.AddMinutes(interval) > now.AddSeconds(30)) continue;
            try
            {
                using var scope = sp.CreateScope();
                scope.ServiceProvider.GetRequiredService<RequestContext>().SetSystem(tenant.Id, "Engineering sync", ActorType.Integration);
                await scope.ServiceProvider.GetRequiredService<EngineeringSyncService>().SyncTrackerAsync(t.Id, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Engineering sync failed for tracker {Tracker} of tenant {Tenant}", t.Id, tenant.Id);
            }
        }
    }, ct);
}
