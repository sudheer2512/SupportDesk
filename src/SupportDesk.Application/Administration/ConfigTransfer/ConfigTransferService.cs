using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.ConfigTransfer;
using SupportDesk.Domain;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Administration.ConfigTransfer;

/// <summary>
/// Configuration transfer (admin.config_transfer): export the tenant configuration as versioned JSON with ids remapped
/// to natural keys; import into a draft with a diff preview and per-item conflict resolution (skip / overwrite / rename);
/// publish (applies the draft and records a <see cref="ConfigurationSnapshot"/>); capture snapshots manually; roll back to
/// a snapshot (which captures the pre-rollback state and records the result as a new snapshot). All audited.
/// </summary>
public sealed class ConfigTransferService(DeskDbContext db, RequestContext ctx, IAuditWriter audit, ConfigTransferEngine engine)
{
    public async Task<List<ConfigSectionInfoDto>> SectionsAsync(CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminConfigTransfer);
        var live = await engine.LoadAsync(ct);
        return ConfigSections.All.Select(s => new ConfigSectionInfoDto(s.Key, s.Label, live.Sections[s.Key].Count)).ToList();
    }

    public async Task<(byte[] Content, string FileName)> ExportAsync(IReadOnlyCollection<string>? sections, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminConfigTransfer);
        var unknown = (sections ?? []).Where(s => ConfigSections.Find(s) is null).ToList();
        if (unknown.Count > 0) throw new ValidationException(new Dictionary<string, string> { ["sections"] = $"Unknown section(s): {string.Join(", ", unknown)}." });
        var live = await engine.LoadAsync(ct);
        var bundle = await engine.ExportAsync(live, ctx.TenantSlug, db.Clock.UtcNow, sections, ct);
        var summary = ConfigTransferEngine.Summarize(bundle);
        audit.Write(AuditActions.DataExported, "Configuration", null, $"Configuration exported ({summary.Sum(s => s.Count)} items in {summary.Count} sections)",
            new { sections = summary.Select(s => new { s.Key, s.Count }) });
        await db.SaveChangesAsync(ct);
        var json = bundle.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return (Encoding.UTF8.GetBytes(json), $"supportdesk-config-{ctx.TenantSlug ?? "tenant"}-{db.Clock.UtcNow:yyyyMMdd-HHmm}.json");
    }

    // ───── Drafts ─────

    public async Task<ConfigDraftDto> ImportAsync(ImportConfigRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminConfigTransfer);
        var node = JsonNode.Parse(r.Bundle.GetRawText());
        var (items, _, source, version) = ConfigTransferEngine.Parse(node);
        if (items.Count == 0) throw new DomainException("invalid_bundle", "The configuration file contains no items.", 400);
        var draft = new ConfigurationDraft
        {
            Label = string.IsNullOrWhiteSpace(r.Label) ? $"Import from {source ?? "file"} ({db.Clock.UtcNow:yyyy-MM-dd HH:mm})" : r.Label.Trim()[..Math.Min(200, r.Label.Trim().Length)],
            SourceTenant = source is { Length: > 64 } ? source[..64] : source, FormatVersion = version, BundleJson = node!.ToJsonString()
        };
        db.Set<ConfigurationDraft>().Add(draft);
        audit.Write(AuditActions.ConfigImported, "ConfigurationDraft", draft.Id, $"Configuration file imported into draft '{draft.Label}' ({items.Count} items)",
            new { draft.SourceTenant, items = items.Count, version });
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(draft, withDiff: true, ct);
    }

    public async Task<List<ConfigDraftDto>> DraftsAsync(CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminConfigTransfer);
        var drafts = await db.Set<ConfigurationDraft>().AsNoTracking().OrderByDescending(d => d.CreatedAt).Take(50).ToListAsync(ct);
        var result = new List<ConfigDraftDto>();
        foreach (var d in drafts) result.Add(await ToDtoAsync(d, withDiff: false, ct));
        return result;
    }

    public async Task<ConfigDraftDto> DraftAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminConfigTransfer);
        return await ToDtoAsync(await LoadDraftAsync(id, ct), withDiff: true, ct);
    }

    public async Task<ConfigDraftDto> SaveResolutionsAsync(Guid id, SaveResolutionsRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminConfigTransfer);
        var draft = await LoadDraftAsync(id, ct);
        if (draft.Status != ConfigDraftStatus.Draft) throw new ConflictException("draft_closed", "This draft was already published or discarded.");
        var (items, _, _, _) = ConfigTransferEngine.Parse(JsonNode.Parse(draft.BundleJson));
        var keys = items.Select(i => $"{i.Section}/{i.Key}").ToHashSet(StringComparer.Ordinal);
        var errors = new Dictionary<string, string>();
        var stored = Json.DeserializeOr(draft.ResolutionsJson, new Dictionary<string, ConfigResolutionDto>());
        foreach (var res in r.Resolutions ?? [])
        {
            if (!keys.Contains(res.Key)) { errors[res.Key] = "Unknown item."; continue; }
            var action = res.Action?.Trim().ToLowerInvariant();
            if (action is not ("create" or "overwrite" or "skip" or "rename")) { errors[res.Key] = "Use create, overwrite, skip or rename."; continue; }
            if (action == "rename" && (string.IsNullOrWhiteSpace(res.NewName) || res.NewName.Trim().Length > 200)) { errors[res.Key] = "Enter the new name (at most 200 characters)."; continue; }
            if (action == "rename" && ConfigSections.Find(res.Key.Split('/')[0]) is { AllowRename: false }) { errors[res.Key] = "Items of this section cannot be renamed."; continue; }
            stored[res.Key] = new ConfigResolutionDto(res.Key, action, action == "rename" ? res.NewName!.Trim() : null);
        }
        if (errors.Count > 0) throw new ValidationException(errors);
        draft.ResolutionsJson = Json.Serialize(stored);
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(draft, withDiff: true, ct);
    }

    public async Task DiscardAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminConfigTransfer);
        var draft = await LoadDraftAsync(id, ct);
        if (draft.Status != ConfigDraftStatus.Draft) throw new ConflictException("draft_closed", "This draft was already published or discarded.");
        draft.Status = ConfigDraftStatus.Discarded;
        audit.Write(AuditActions.ConfigImported, "ConfigurationDraft", draft.Id, $"Configuration draft '{draft.Label}' discarded", null);
        await db.SaveChangesAsync(ct);
    }

    public async Task<ConfigPublishResultDto> PublishAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminConfigTransfer);
        var draft = await LoadDraftAsync(id, ct);
        if (draft.Status != ConfigDraftStatus.Draft) throw new ConflictException("draft_closed", "This draft was already published or discarded.");
        var resolutions = Json.DeserializeOr(draft.ResolutionsJson, new Dictionary<string, ConfigResolutionDto>());
        ConfigApplyResultDto result = null!;
        ConfigurationSnapshot snapshot = null!;
        await db.InTransactionAsync(async () =>
        {
            if (!await db.ConfigurationSnapshots.AnyAsync(ct))
                await CaptureAsync("Baseline before the first import", ConfigSnapshotKind.Manual, null, ct);
            var context = new ConfigApplyContext(db.Clock.UtcNow, ctx.UserId, $"Imported: {draft.Label}");
            result = await engine.ApplyAsync(JsonNode.Parse(draft.BundleJson), resolutions, deactivateMissing: false, context, ct);
            await db.SaveChangesAsync(ct);
            snapshot = await CaptureAsync($"Published import: {draft.Label}", ConfigSnapshotKind.Import, db.Clock.UtcNow, ct);
            draft.Status = ConfigDraftStatus.Published;
            draft.PublishedAt = db.Clock.UtcNow;
            draft.PublishedSnapshotId = snapshot.Id;
            draft.ResultJson = Json.Serialize(result);
            audit.Write(AuditActions.ConfigPublished, "ConfigurationSnapshot", snapshot.Id,
                $"Configuration draft '{draft.Label}' published as v{snapshot.Version}: {result.Created} created, {result.Updated} updated, {result.Skipped} skipped",
                new { draftId = draft.Id, result.Created, result.Updated, result.Skipped, warnings = result.Warnings.Take(20) });
            await db.SaveChangesAsync(ct);
        }, ct);
        return new ConfigPublishResultDto(draft.Id, SnapshotDto(snapshot, null), result);
    }

    // ───── Snapshots ─────

    public async Task<List<ConfigSnapshotDto>> SnapshotsAsync(CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminConfigTransfer);
        var rows = await db.ConfigurationSnapshots.AsNoTracking().OrderByDescending(s => s.Version)
            .Select(s => new { s.Id, s.Version, s.Label, s.Kind, s.CreatedAt, s.CreatedById, s.AppliedAt, s.SectionsJson }).Take(200).ToListAsync(ct);
        var userIds = rows.Where(r => r.CreatedById != null).Select(r => r.CreatedById!.Value).Distinct().ToList();
        var users = await db.Users.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return rows.Select(r => new ConfigSnapshotDto(r.Id, r.Version, r.Label, r.Kind.ToString(), r.CreatedAt,
            r.CreatedById is { } u ? users.GetValueOrDefault(u) : null, r.AppliedAt,
            Json.DeserializeOr(r.SectionsJson, new List<ConfigSectionInfoDto>()))).ToList();
    }

    public async Task<ConfigSnapshotDto> CreateSnapshotAsync(CreateSnapshotRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminConfigTransfer);
        var label = string.IsNullOrWhiteSpace(r.Label) ? $"Manual snapshot {db.Clock.UtcNow:yyyy-MM-dd HH:mm}" : r.Label.Trim()[..Math.Min(200, r.Label.Trim().Length)];
        ConfigurationSnapshot snapshot = null!;
        await db.InTransactionAsync(async () =>
        {
            snapshot = await CaptureAsync(label, ConfigSnapshotKind.Manual, null, ct);
            audit.Write(AuditActions.ConfigPublished, "ConfigurationSnapshot", snapshot.Id, $"Configuration snapshot v{snapshot.Version} captured: {label}", null);
            await db.SaveChangesAsync(ct);
        }, ct);
        return SnapshotDto(snapshot, ctx.ActorName);
    }

    public async Task<(byte[] Content, string FileName)> DownloadSnapshotAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminConfigTransfer);
        var s = await db.ConfigurationSnapshots.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Snapshot", id);
        var pretty = JsonNode.Parse(s.BundleJson)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return (Encoding.UTF8.GetBytes(pretty), $"supportdesk-config-{ctx.TenantSlug ?? "tenant"}-v{s.Version}.json");
    }

    /// <summary>Restores a snapshot: the current state is captured first, the snapshot is applied (items missing from it are deactivated) and the result becomes a new snapshot.</summary>
    public async Task<ConfigPublishResultDto> RollbackAsync(Guid id, RollbackRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminConfigTransfer);
        var target = await db.ConfigurationSnapshots.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Snapshot", id);
        ConfigApplyResultDto result = null!;
        ConfigurationSnapshot snapshot = null!;
        await db.InTransactionAsync(async () =>
        {
            await CaptureAsync($"Before rollback to v{target.Version}", ConfigSnapshotKind.PreRollback, null, ct);
            var context = new ConfigApplyContext(db.Clock.UtcNow, ctx.UserId, $"Rollback to v{target.Version}");
            result = await engine.ApplyAsync(JsonNode.Parse(target.BundleJson), new Dictionary<string, ConfigResolutionDto>(), deactivateMissing: true, context, ct);
            await db.SaveChangesAsync(ct);
            var label = string.IsNullOrWhiteSpace(r.Label) ? $"Rollback to v{target.Version} ({target.Label})" : r.Label.Trim();
            snapshot = await CaptureAsync(label[..Math.Min(200, label.Length)], ConfigSnapshotKind.Rollback, db.Clock.UtcNow, ct);
            audit.Write(AuditActions.ConfigRolledBack, "ConfigurationSnapshot", snapshot.Id,
                $"Configuration rolled back to v{target.Version}: {result.Created} restored, {result.Updated} reverted, {result.Deactivated} deactivated (now v{snapshot.Version})",
                new { toVersion = target.Version, newVersion = snapshot.Version, result.Created, result.Updated, result.Deactivated });
            await db.SaveChangesAsync(ct);
        }, ct);
        return new ConfigPublishResultDto(null, SnapshotDto(snapshot, ctx.ActorName), result);
    }

    private async Task<ConfigurationSnapshot> CaptureAsync(string label, ConfigSnapshotKind kind, DateTime? appliedAt, CancellationToken ct)
    {
        var live = await engine.LoadAsync(ct);
        var bundle = await engine.ExportAsync(live, ctx.TenantSlug, db.Clock.UtcNow, null, ct);
        var version = (await db.ConfigurationSnapshots.MaxAsync(s => (int?)s.Version, ct) ?? 0) + 1;
        var snapshot = new ConfigurationSnapshot
        {
            Version = version, Label = label, Kind = kind, BundleJson = bundle.ToJsonString(), SectionsJson = Json.Serialize(ConfigTransferEngine.Summarize(bundle)),
            AppliedAt = appliedAt
        };
        db.ConfigurationSnapshots.Add(snapshot);
        await db.SaveChangesAsync(ct);
        return snapshot;
    }

    private static ConfigSnapshotDto SnapshotDto(ConfigurationSnapshot s, string? by) =>
        new(s.Id, s.Version, s.Label, s.Kind.ToString(), s.CreatedAt, by, s.AppliedAt, Json.DeserializeOr(s.SectionsJson, new List<ConfigSectionInfoDto>()));

    private async Task<ConfigurationDraft> LoadDraftAsync(Guid id, CancellationToken ct) =>
        await db.Set<ConfigurationDraft>().FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Configuration draft", id);

    private async Task<ConfigDraftDto> ToDtoAsync(ConfigurationDraft d, bool withDiff, CancellationToken ct)
    {
        var by = d.CreatedById is { } u ? await db.Users.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).Where(x => x.Id == u).Select(x => x.DisplayName).FirstOrDefaultAsync(ct) : null;
        ConfigDiffDto? diff = null;
        if (withDiff && d.Status == ConfigDraftStatus.Draft)
            diff = await engine.DiffAsync(JsonNode.Parse(d.BundleJson), Json.DeserializeOr(d.ResolutionsJson, new Dictionary<string, ConfigResolutionDto>()), ct);
        return new ConfigDraftDto(d.Id, d.Label, d.SourceTenant, d.FormatVersion, d.Status.ToString(), d.CreatedAt, by, d.PublishedAt, d.PublishedSnapshotId, diff);
    }
}
