using System.Text.Json;

namespace SupportDesk.Contracts.Administration;

// ───────────── Statuses ─────────────

/// <summary>A configurable ticket status (category = Open, Assigned, InProgress, Waiting, Resolved, Closed). Empty AllowedNext = any.</summary>
public sealed record AdminStatusDto(
    Guid Id,
    string Name,
    string Category,
    string Color,
    int SortOrder,
    bool IsDefault,
    bool PausesSla,
    bool IsSystem,
    bool IsActive,
    string? CustomerLabel,
    IReadOnlyList<Guid> AllowedNext,
    IReadOnlyList<string> Substatuses,
    int TicketCount,
    string RowVersion);

public sealed record SaveStatusRequest(
    string Name,
    string Category,
    string Color,
    bool IsDefault,
    bool PausesSla,
    bool IsActive,
    string? CustomerLabel,
    IReadOnlyList<Guid>? AllowedNext,
    IReadOnlyList<string>? Substatuses,
    string? RowVersion = null);

/// <summary>New order of a list (statuses, categories, issue types, fields): every id, first to last.</summary>
public sealed record ReorderRequest(IReadOnlyList<Guid> Ids);

// ───────────── Categories & issue types ─────────────

public sealed record AdminCategoryDto(Guid Id, string Name, Guid? ParentId, Guid? DepartmentId, bool VisibleInPortal, bool IsActive, int SortOrder, int TicketCount);

public sealed record SaveCategoryRequest(string Name, Guid? ParentId, Guid? DepartmentId, bool VisibleInPortal, bool IsActive);

public sealed record AdminIssueTypeDto(Guid Id, string Name, string? Description, bool IsActive, int SortOrder, int TicketCount);

public sealed record SaveIssueTypeRequest(string Name, string? Description, bool IsActive);

// ───────────── Priorities & severities display ─────────────

/// <summary>Display label and colour of a fixed priority/severity value (Low…Critical / Cosmetic…Critical).</summary>
public sealed record DisplayOptionDto(string Value, string Label, string Color);

public sealed record TicketDisplayDto(IReadOnlyList<DisplayOptionDto> Priorities, IReadOnlyList<DisplayOptionDto> Severities);

// ───────────── Numbering & lifecycle ─────────────

/// <summary>Ticket numbering (prefix editable, next number read-only) and lifecycle rules (reopen, auto-close, merge/split).</summary>
public sealed record TicketLifecycleDto(
    string NumberPrefix,
    long NextNumber,
    int ReopenWindowDays,
    bool AllowCustomerReopen,
    int AutoCloseResolvedAfterDays,
    bool RequireCustomerConfirmation,
    bool RequireResolutionCode,
    int SoftLockMinutes,
    bool AllowMergeAcrossCustomers,
    bool AllowMergeAcrossDepartments,
    int MaxMergeSources,
    bool AllowSplit,
    bool CsatEnabled,
    int CsatExpiryDays,
    bool AutoAcknowledge,
    int Version);

public sealed record SaveTicketLifecycleRequest(
    string NumberPrefix,
    int ReopenWindowDays,
    bool AllowCustomerReopen,
    int AutoCloseResolvedAfterDays,
    bool RequireCustomerConfirmation,
    bool RequireResolutionCode,
    int SoftLockMinutes,
    bool AllowMergeAcrossCustomers,
    bool AllowMergeAcrossDepartments,
    int MaxMergeSources,
    bool AllowSplit,
    bool CsatEnabled,
    int CsatExpiryDays,
    bool AutoAcknowledge);

// ───────────── Custom fields ─────────────

/// <summary>
/// A custom field (Ticket, Contact or Customer). Options: picklists use [{"value","label"}]. RequiredForStatusIds: ticket
/// statuses that cannot be entered while the field is empty.
/// </summary>
public sealed record AdminCustomFieldDto(
    Guid Id,
    string Entity,
    string Key,
    string Label,
    string FieldType,
    JsonElement Options,
    bool IsRequired,
    string? DefaultValue,
    string? HelpText,
    string? ValidationRegex,
    decimal? MinValue,
    decimal? MaxValue,
    int? MaxLength,
    bool VisibleInPortal,
    bool EditableInPortal,
    IReadOnlyList<Guid> DepartmentIds,
    IReadOnlyList<Guid> RequiredForStatusIds,
    int SortOrder,
    bool IsActive,
    int UsedInLayouts,
    string RowVersion);

public sealed record SaveCustomFieldRequest(
    string Entity,
    string Key,
    string Label,
    string FieldType,
    JsonElement? Options,
    bool IsRequired,
    string? DefaultValue,
    string? HelpText,
    string? ValidationRegex,
    decimal? MinValue,
    decimal? MaxValue,
    int? MaxLength,
    bool VisibleInPortal,
    bool EditableInPortal,
    IReadOnlyList<Guid>? DepartmentIds,
    IReadOnlyList<Guid>? RequiredForStatusIds,
    bool IsActive,
    string? RowVersion = null);

// ───────────── Form layouts ─────────────

/// <summary>
/// Form layout (form builder). Sections: [{"title":"Product","columns":2,"fields":[{"key":"productId","required":true},{"key":"cf:erp_module"}]}].
/// Kind: AgentForm or PortalForm. Scope: optional department, brand (portal) and channel.
/// </summary>
public sealed record AdminLayoutDto(
    Guid Id,
    string Entity,
    string Name,
    string Kind,
    Guid? DepartmentId,
    Guid? BrandId,
    string? Channel,
    JsonElement Sections,
    bool IsDefault,
    bool IsActive,
    DateTime UpdatedAt,
    string RowVersion);

public sealed record SaveLayoutRequest(
    string Entity,
    string Name,
    string Kind,
    Guid? DepartmentId,
    Guid? BrandId,
    string? Channel,
    JsonElement Sections,
    bool IsDefault,
    bool IsActive,
    string? RowVersion = null);

/// <summary>A field the form builder can place: standard ticket fields and custom fields ("cf:key").</summary>
public sealed record LayoutFieldOptionDto(string Key, string Label, string Type, bool Custom, bool AgentForm, bool PortalForm, bool AlwaysRequired);
