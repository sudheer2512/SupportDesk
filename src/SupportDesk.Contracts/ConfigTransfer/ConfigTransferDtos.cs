using System.Text.Json;

namespace SupportDesk.Contracts.ConfigTransfer;

/// <summary>An exportable configuration section (statuses, categories, SLA policies, …) and its item count.</summary>
public sealed record ConfigSectionInfoDto(string Key, string Label, int Count);

public sealed record ConfigFieldChangeDto(string Field, string? Current, string? Incoming);

/// <summary>
/// One item of an import diff. Status: new | changed | unchanged. Resolution: create | overwrite | skip | rename
/// (new items default to create, changed items to overwrite, unchanged items to skip).
/// </summary>
public sealed record ConfigDiffItemDto(
    string Key,
    string Section,
    string NaturalKey,
    string Name,
    string Status,
    string Resolution,
    string? NewName,
    IReadOnlyList<ConfigFieldChangeDto> Changes);

public sealed record ConfigDiffSectionDto(string Key, string Label, IReadOnlyList<ConfigDiffItemDto> Items);

public sealed record ConfigDiffDto(
    IReadOnlyList<ConfigDiffSectionDto> Sections,
    int New,
    int Changed,
    int Unchanged,
    IReadOnlyList<string> Warnings);

public sealed record ConfigDraftDto(
    Guid Id,
    string Label,
    string? SourceTenant,
    int FormatVersion,
    string Status,
    DateTime CreatedAt,
    string? CreatedByName,
    DateTime? PublishedAt,
    Guid? PublishedSnapshotId,
    ConfigDiffDto? Diff);

/// <summary>Import a previously exported bundle (the JSON document produced by the export endpoint) into a draft.</summary>
public sealed record ImportConfigRequest(JsonElement Bundle, string? Label = null);

public sealed record ConfigResolutionDto(string Key, string Action, string? NewName = null);

public sealed record SaveResolutionsRequest(IReadOnlyList<ConfigResolutionDto> Resolutions);

public sealed record ConfigSnapshotDto(
    Guid Id,
    int Version,
    string Label,
    string Kind,
    DateTime CreatedAt,
    string? CreatedByName,
    DateTime? AppliedAt,
    IReadOnlyList<ConfigSectionInfoDto> Sections);

public sealed record ConfigApplyResultDto(int Created, int Updated, int Skipped, int Deactivated, IReadOnlyList<string> Warnings);

public sealed record ConfigPublishResultDto(Guid? DraftId, ConfigSnapshotDto Snapshot, ConfigApplyResultDto Result);

public sealed record CreateSnapshotRequest(string? Label);

public sealed record RollbackRequest(string? Label);
