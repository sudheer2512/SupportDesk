using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Customers;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Engineering;
using SupportDesk.Domain;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Integration;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Infrastructure;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Integrations.Engineering;

/// <summary>
/// The built-in engineering board (engineering.view / engineering.manage): work items in New → Active → Resolved → Closed,
/// assignees among engineering agents (light agents included), fix versions and releases. Every change is propagated
/// to linked tickets in the same unit of work (state, fix version, release → notes and customer notifications).
/// </summary>
public sealed class EngineeringBoardService(
    DeskDbContext db,
    RequestContext ctx,
    EngineeringTrackerResolver resolver,
    EngineeringSyncService sync,
    EngineeringTicketService tickets,
    TenantCounterService counters,
    HtmlSanitization html,
    IAuditWriter audit)
{
    public static readonly string[] States = Enum.GetNames<LocalWorkItemState>();

    private async Task<Integration?> LocalTrackerAsync(Guid? trackerId, CancellationToken ct)
    {
        var q = resolver.Trackers().AsNoTracking().Where(i => i.Type == IntegrationTypes.LocalEngineering);
        if (trackerId is { } id) q = q.Where(i => i.Id == id);
        return await q.OrderByDescending(i => i.IsActive).ThenBy(i => i.CreatedAt).FirstOrDefaultAsync(ct);
    }

    private async Task<Integration> RequireTrackerAsync(Guid? trackerId, CancellationToken ct) =>
        await LocalTrackerAsync(trackerId, ct) ?? throw new DomainException("integration_unavailable", "The built-in engineering tracker is not set up. An administrator can enable it under Administration → Engineering.");

    // ───────────────────────────── Board ─────────────────────────────

    public async Task<EngineeringBoardDto> BoardAsync(Guid? trackerId, CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.EngineeringView, Permissions.EngineeringManage);
        var tracker = await LocalTrackerAsync(trackerId, ct);
        if (tracker is null) return new EngineeringBoardDto(null, null, States, [], [], [], ctx.Has(Permissions.EngineeringManage));
        var products = await db.Products.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Name).Select(p => new IdName(p.Id, p.Name)).ToListAsync(ct);
        return new EngineeringBoardDto(tracker.Id, tracker.Name, States, await ReleasesCoreAsync(tracker.Id, ct), await AssigneesAsync(ct), products,
            ctx.Has(Permissions.EngineeringManage) && tracker.IsActive);
    }

    /// <summary>Active agents (light agents included) holding engineering.manage through their roles.</summary>
    public async Task<List<EngineeringAssigneeDto>> AssigneesAsync(CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.EngineeringView, Permissions.EngineeringManage, Permissions.EngineeringLink);
        var roleIds = db.RolePermissions.Where(rp => rp.PermissionCode == Permissions.EngineeringManage).Select(rp => rp.RoleId);
        var userIds = db.UserRoles.Where(ur => roleIds.Contains(ur.RoleId)).Select(ur => ur.UserId);
        var users = await db.Users.AsNoTracking().Where(u => u.IsActive && u.Kind != UserKind.System && userIds.Contains(u.Id))
            .OrderBy(u => u.DisplayName).Select(u => new { u.Id, u.DisplayName, u.Kind }).Take(500).ToListAsync(ct);
        var ids = users.Select(u => u.Id).ToList();
        var agents = await db.Agents.AsNoTracking().Where(a => ids.Contains(a.UserId)).ToDictionaryAsync(a => a.UserId, ct);
        return users.Select(u => new EngineeringAssigneeDto(u.Id, u.DisplayName, agents.GetValueOrDefault(u.Id)?.Title,
            u.Kind == UserKind.LightAgent || agents.GetValueOrDefault(u.Id)?.IsLightAgent == true)).ToList();
    }

    /// <summary>Keyset-paged work items, most recently updated first (cursor = updatedAt + id).</summary>
    public async Task<PagedResult<EngineeringWorkItemDto>> ListAsync(Guid? trackerId, string? state, Guid? assigneeId, Guid? releaseId, string? q, bool includeClosed,
        string? cursor, int limit, CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.EngineeringView, Permissions.EngineeringManage);
        var tracker = await LocalTrackerAsync(trackerId, ct);
        if (tracker is null) return new PagedResult<EngineeringWorkItemDto>([], null, 0);
        limit = Math.Clamp(limit, 1, 200);
        var query = db.Set<LocalWorkItem>().AsNoTracking().Where(w => w.IntegrationId == tracker.Id);
        if (!string.IsNullOrWhiteSpace(state))
        {
            var st = Parse.Enum(state, LocalWorkItemState.New, "state");
            query = query.Where(w => w.State == st);
        }
        else if (!includeClosed) query = query.Where(w => w.State != LocalWorkItemState.Closed);
        if (assigneeId is { } a) query = query.Where(w => w.AssigneeUserId == a);
        if (releaseId is { } r) query = query.Where(w => w.ReleaseId == r);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            var number = LocalTracker.ParseLocalReference(term) is { } n ? int.Parse(n, CultureInfo.InvariantCulture) : -1;
            query = query.Where(w => w.Number == number || w.Title.Contains(term) || (w.FixVersion != null && w.FixVersion.Contains(term)));
        }
        var total = await query.CountAsync(ct);
        if (DbExtensions.DecodeCursor(cursor) is { } c)
        {
            var at = DateTime.Parse(c.V, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            query = query.Where(w => w.UpdatedAt < at || (w.UpdatedAt == at && w.Id.CompareTo(c.Id) < 0));
        }
        var rows = await query.OrderByDescending(w => w.UpdatedAt).ThenByDescending(w => w.Id).Take(limit + 1).ToListAsync(ct);
        var page = rows.Take(limit).ToList();
        var items = await MapAsync(page, withDescription: false, ct);
        var next = rows.Count > limit ? DbExtensions.EncodeCursor(page[^1].UpdatedAt.ToString("O", CultureInfo.InvariantCulture), page[^1].Id) : null;
        return new PagedResult<EngineeringWorkItemDto>(items, next, total);
    }

    public async Task<EngineeringWorkItemDetailDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.EngineeringView, Permissions.EngineeringManage);
        var w = await db.Set<LocalWorkItem>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Work item", id);
        var dto = (await MapAsync([w], withDescription: true, ct)).Single();
        var affected = await tickets.AffectedAsync(w.IntegrationId, w.Number.ToString(CultureInfo.InvariantCulture), null, ct);
        return new EngineeringWorkItemDetailDto(dto, affected);
    }

    private async Task<List<EngineeringWorkItemDto>> MapAsync(IReadOnlyList<LocalWorkItem> rows, bool withDescription, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var userIds = rows.Where(r => r.AssigneeUserId != null).Select(r => r.AssigneeUserId!.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var releaseIds = rows.Where(r => r.ReleaseId != null).Select(r => r.ReleaseId!.Value).Distinct().ToList();
        var releases = await db.Set<LocalRelease>().AsNoTracking().Where(r => releaseIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var productIds = rows.Where(r => r.ProductId != null).Select(r => r.ProductId!.Value).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var integrationIds = rows.Select(r => r.IntegrationId).Distinct().ToList();
        var externalIds = rows.Select(r => r.Number.ToString(CultureInfo.InvariantCulture)).ToList();
        var counts = await db.EngineeringLinks.AsNoTracking().Where(l => l.IntegrationId != null && integrationIds.Contains(l.IntegrationId.Value) && externalIds.Contains(l.ExternalId))
            .GroupBy(l => new { l.IntegrationId, l.ExternalId }).Select(g => new { g.Key.IntegrationId, g.Key.ExternalId, Count = g.Count() }).ToListAsync(ct);
        return rows.Select(w =>
        {
            var rel = w.ReleaseId is { } rid ? releases.GetValueOrDefault(rid) : null;
            var key = w.Number.ToString(CultureInfo.InvariantCulture);
            return new EngineeringWorkItemDto(w.Id, w.IntegrationId, w.Number, LocalTracker.KeyPrefix + key, w.Type.ToString(), w.Title, withDescription ? w.DescriptionHtml : null,
                w.State.ToString(), w.Severity?.ToString(), w.Priority.ToString(), w.AssigneeUserId, w.AssigneeUserId is { } a ? users.GetValueOrDefault(a) : null,
                w.AreaPath, w.IterationPath, Json.StringList(w.TagsJson), rel?.Name ?? w.FixVersion, w.ReleaseId, rel?.Name, rel?.ReleasedAt is not null, w.ProductId,
                w.ProductId is { } p ? products.GetValueOrDefault(p) : null,
                counts.FirstOrDefault(x => x.IntegrationId == w.IntegrationId && x.ExternalId == key)?.Count ?? 0, w.CustomerImpactCount, w.Rank, w.CreatedAt,
                w.UpdatedAt, w.ResolvedAt, w.ClosedAt, Convert.ToBase64String(w.RowVersion));
        }).ToList();
    }

    // ───────────────────────────── Work items ─────────────────────────────

    public async Task<EngineeringWorkItemDto> CreateAsync(SaveEngineeringWorkItemRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.EngineeringManage);
        var tracker = await RequireTrackerAsync(r.TrackerId, ct);
        if (!tracker.IsActive) throw new DomainException("integration_unavailable", "The built-in tracker is disabled.");
        var w = new LocalWorkItem { IntegrationId = tracker.Id, Number = (int)await counters.NextAsync(LocalTracker.CounterName, 1, ct) };
        await ApplyAsync(w, r, tracker.Id, ct);
        w.Rank = (await db.Set<LocalWorkItem>().Where(x => x.IntegrationId == tracker.Id && x.State == LocalWorkItemState.New).MaxAsync(x => (double?)x.Rank, ct) ?? 0) + 1;
        db.Set<LocalWorkItem>().Add(w);
        await db.SaveChangesAsync(ct);
        return (await MapAsync([w], withDescription: true, ct)).Single();
    }

    public async Task<EngineeringWorkItemDto> UpdateAsync(Guid id, SaveEngineeringWorkItemRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.EngineeringManage);
        var w = await LoadForUpdateAsync(id, r.RowVersion, ct);
        var tracker = await RequireTrackerAsync(w.IntegrationId, ct);
        await ApplyAsync(w, r, tracker.Id, ct);
        await sync.ApplyLocalAsync(tracker, [w], ct);
        await SaveAsync(ct);
        return (await MapAsync([w], withDescription: true, ct)).Single();
    }

    private async Task<LocalWorkItem> LoadForUpdateAsync(Guid id, string? rowVersion, CancellationToken ct)
    {
        var w = await db.Set<LocalWorkItem>().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Work item", id);
        if (rowVersion is { Length: > 0 })
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(rowVersion); }
            catch (FormatException) { throw new ValidationException(new Dictionary<string, string> { ["rowVersion"] = "Invalid rowVersion." }); }
            if (!bytes.SequenceEqual(w.RowVersion)) throw new ConflictException(ErrorCodes.ConcurrencyConflict, "The work item was changed by someone else. Reload and try again.");
            db.Entry(w).Property(x => x.RowVersion).OriginalValue = bytes;
        }
        return w;
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException(ErrorCodes.ConcurrencyConflict, "The work item was changed by someone else. Reload and try again."); }
    }

    private async Task ApplyAsync(LocalWorkItem w, SaveEngineeringWorkItemRequest r, Guid trackerId, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        var title = Parse.Clean(r.Title, 500);
        if (title is null) errors["title"] = "Enter a title.";
        var type = Parse.Enum(r.Type, w.Type, "type");
        if (type is not (EngineeringItemType.Bug or EngineeringItemType.Feature or EngineeringItemType.Task)) errors["type"] = "Choose Bug, Feature or Task.";
        if (r.AssigneeUserId is { } uid && !(await AssigneesAsync(ct)).Any(a => a.Id == uid)) errors["assigneeUserId"] = "Choose an engineer from the list.";
        LocalRelease? release = null;
        if (r.ReleaseId is { } rid)
        {
            release = await db.Set<LocalRelease>().FirstOrDefaultAsync(x => x.Id == rid && x.IntegrationId == trackerId, ct);
            if (release is null) errors["releaseId"] = "Unknown release.";
        }
        if (r.ProductId is { } pid && !await db.Products.AnyAsync(p => p.Id == pid, ct)) errors["productId"] = "Unknown product.";
        if (r.Tags is { Count: > 20 }) errors["tags"] = "Use at most 20 tags.";
        if (errors.Count > 0) throw new ValidationException(errors);
        w.Title = title!;
        w.Type = type;
        w.DescriptionHtml = string.IsNullOrWhiteSpace(r.DescriptionHtml) ? null : html.Sanitize(r.DescriptionHtml);
        w.Severity = string.IsNullOrWhiteSpace(r.Severity) ? null : Parse.Enum(r.Severity, TicketSeverity.Minor, "severity");
        w.Priority = Parse.Enum(r.Priority, w.Priority, "priority");
        w.AssigneeUserId = r.AssigneeUserId;
        w.AreaPath = Parse.Clean(r.AreaPath, 300);
        w.IterationPath = Parse.Clean(r.IterationPath, 300);
        w.TagsJson = Json.Serialize((r.Tags ?? []).Select(t => t.Trim()).Where(t => t.Length is > 0 and <= 100).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        w.ReleaseId = release?.Id;
        w.FixVersion = release?.Name ?? (r.ReleaseId is null ? null : w.FixVersion);
        w.ProductId = r.ProductId;
    }

    public async Task<EngineeringWorkItemDto> MoveAsync(Guid id, MoveEngineeringWorkItemRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.EngineeringManage);
        var w = await LoadForUpdateAsync(id, r.RowVersion, ct);
        var tracker = await RequireTrackerAsync(w.IntegrationId, ct);
        var state = Parse.Enum(r.State, w.State, "state");
        var column = await db.Set<LocalWorkItem>().AsNoTracking().Where(x => x.IntegrationId == w.IntegrationId && x.State == state && x.Id != w.Id)
            .OrderBy(x => x.Rank).Select(x => new { x.Id, x.Rank }).ToListAsync(ct);
        var index = r.BeforeId is { } before ? column.FindIndex(x => x.Id == before) : -1;
        w.Rank = index < 0 ? (column.Count == 0 ? 1 : column[^1].Rank + 1)
            : index == 0 ? column[0].Rank - 1 : (column[index - 1].Rank + column[index].Rank) / 2;
        SetState(w, state);
        await sync.ApplyLocalAsync(tracker, [w], ct);
        await SaveAsync(ct);
        return (await MapAsync([w], withDescription: false, ct)).Single();
    }

    private void SetState(LocalWorkItem w, LocalWorkItemState state)
    {
        if (w.State == state) return;
        var now = db.Clock.UtcNow;
        w.State = state;
        if (state is LocalWorkItemState.Resolved or LocalWorkItemState.Closed) w.ResolvedAt ??= now;
        else w.ResolvedAt = null;
        w.ClosedAt = state == LocalWorkItemState.Closed ? now : null;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.EngineeringManage);
        var w = await LoadForUpdateAsync(id, null, ct);
        var tracker = await RequireTrackerAsync(w.IntegrationId, ct);
        w.IsDeleted = true;
        w.DeletedAt = db.Clock.UtcNow;
        audit.Write("engineering.work_item_deleted", "LocalWorkItem", w.Id, $"Work item {LocalTracker.KeyPrefix}{w.Number} “{w.Title}” deleted", new { w.Number, w.Title });
        await sync.ApplyLocalAsync(tracker, [w], ct);
        await SaveAsync(ct);
    }

    // ───────────────────────────── Releases ─────────────────────────────

    public async Task<List<EngineeringReleaseDto>> ReleasesAsync(Guid? trackerId, CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.EngineeringView, Permissions.EngineeringManage);
        var tracker = await LocalTrackerAsync(trackerId, ct);
        return tracker is null ? [] : await ReleasesCoreAsync(tracker.Id, ct);
    }

    private async Task<List<EngineeringReleaseDto>> ReleasesCoreAsync(Guid trackerId, CancellationToken ct)
    {
        var rows = await db.Set<LocalRelease>().AsNoTracking().Where(r => r.IntegrationId == trackerId).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var counts = await db.Set<LocalWorkItem>().AsNoTracking().Where(w => w.ReleaseId != null && ids.Contains(w.ReleaseId.Value))
            .GroupBy(w => w.ReleaseId).Select(g => new { g.Key, Total = g.Count(), Open = g.Count(x => x.State == LocalWorkItemState.New || x.State == LocalWorkItemState.Active) })
            .ToListAsync(ct);
        var productIds = rows.Where(r => r.ProductId != null).Select(r => r.ProductId!.Value).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        return rows.OrderBy(r => r.ReleasedAt is null ? 0 : 1).ThenByDescending(r => r.ReleasedAt).ThenBy(r => r.PlannedDate).ThenBy(r => r.Name)
            .Select(r =>
            {
                var c = counts.FirstOrDefault(x => x.Key == r.Id);
                return new EngineeringReleaseDto(r.Id, r.IntegrationId, r.Name, r.Notes, r.ProductId, r.ProductId is { } p ? products.GetValueOrDefault(p) : null, r.PlannedDate,
                    r.ReleasedAt, r.DeployedAt, c?.Total ?? 0, c?.Open ?? 0, Convert.ToBase64String(r.RowVersion));
            }).ToList();
    }

    public async Task<EngineeringReleaseDto> SaveReleaseAsync(Guid? id, SaveEngineeringReleaseRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.EngineeringManage);
        LocalRelease release;
        Integration tracker;
        if (id is { } rid)
        {
            release = await db.Set<LocalRelease>().FirstOrDefaultAsync(x => x.Id == rid, ct) ?? throw new NotFoundException("Release", rid);
            tracker = await RequireTrackerAsync(release.IntegrationId, ct);
            if (r.RowVersion is { Length: > 0 } rv && rv != Convert.ToBase64String(release.RowVersion))
                throw new ConflictException(ErrorCodes.ConcurrencyConflict, "The release was changed by someone else. Reload and try again.");
        }
        else
        {
            tracker = await RequireTrackerAsync(r.TrackerId, ct);
            release = new LocalRelease { IntegrationId = tracker.Id };
            db.Set<LocalRelease>().Add(release);
        }
        var errors = new Dictionary<string, string>();
        var name = Parse.Clean(r.Name, 100);
        if (name is null) errors["name"] = "Enter the version name (e.g. 8.0.1).";
        else if (await db.Set<LocalRelease>().AnyAsync(x => x.IntegrationId == tracker.Id && x.Name == name && x.Id != release.Id, ct)) errors["name"] = "A release with this name exists.";
        if (r.ProductId is { } pid && !await db.Products.AnyAsync(p => p.Id == pid, ct)) errors["productId"] = "Unknown product.";
        if (errors.Count > 0) throw new ValidationException(errors);
        var renamed = release.Name != name;
        release.Name = name!;
        release.Notes = Parse.Clean(r.Notes, 4000);
        release.ProductId = r.ProductId;
        release.PlannedDate = r.PlannedDate;
        if (renamed && id is not null)
        {
            var items = await db.Set<LocalWorkItem>().Where(w => w.ReleaseId == release.Id).ToListAsync(ct);
            foreach (var w in items) w.FixVersion = release.Name;
            await sync.ApplyLocalAsync(tracker, items, ct);
        }
        await SaveAsync(ct);
        return (await ReleasesCoreAsync(tracker.Id, ct)).First(x => x.Id == release.Id);
    }

    /// <summary>Publishes the release: every assigned work item is released in this version (linked tickets get notes / customer notifications).</summary>
    public async Task<EngineeringReleaseDto> PublishReleaseAsync(Guid id, PublishEngineeringReleaseRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.EngineeringManage);
        var release = await db.Set<LocalRelease>().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Release", id);
        var tracker = await RequireTrackerAsync(release.IntegrationId, ct);
        if (r.RowVersion is { Length: > 0 } rv && rv != Convert.ToBase64String(release.RowVersion))
            throw new ConflictException(ErrorCodes.ConcurrencyConflict, "The release was changed by someone else. Reload and try again.");
        var now = db.Clock.UtcNow;
        release.ReleasedAt ??= now;
        if (r.Deployed) release.DeployedAt ??= now;
        var items = await db.Set<LocalWorkItem>().Where(w => w.ReleaseId == release.Id).ToListAsync(ct);
        foreach (var w in items)
        {
            w.FixVersion = release.Name;
            if (w.State is LocalWorkItemState.Resolved) SetState(w, LocalWorkItemState.Closed);
        }
        audit.Write("engineering.release_published", "LocalRelease", release.Id, $"Release {release.Name} published{(r.Deployed ? " and deployed" : "")}",
            new { release.Name, items = items.Count, r.Deployed });
        await sync.ApplyLocalAsync(tracker, items, ct);
        await SaveAsync(ct);
        return (await ReleasesCoreAsync(tracker.Id, ct)).First(x => x.Id == release.Id);
    }

    public async Task DeleteReleaseAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.EngineeringManage);
        var release = await db.Set<LocalRelease>().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Release", id);
        if (release.ReleasedAt is not null) throw new ConflictException("release_published", "Published releases cannot be deleted.");
        if (await db.Set<LocalWorkItem>().AnyAsync(w => w.ReleaseId == id, ct)) throw new ConflictException("release_in_use", "Move the release's work items to another release first.");
        db.Set<LocalRelease>().Remove(release);
        await SaveAsync(ct);
    }
}
