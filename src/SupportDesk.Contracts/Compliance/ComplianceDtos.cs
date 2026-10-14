using System.Text.Json;

namespace SupportDesk.Contracts.Compliance;

// ───── Audit viewer ─────

public sealed record AuditEntryDto(
    Guid Id,
    long Sequence,
    DateTime OccurredAt,
    string ActorType,
    Guid? ActorId,
    string? ActorName,
    string Action,
    string? EntityType,
    Guid? EntityId,
    string? Summary,
    string? IpAddress,
    bool Sealed);

/// <summary>One flattened difference between the before and after state recorded in an audit entry (kind: added | removed | changed).</summary>
public sealed record AuditDiffDto(string Path, string? Before, string? After, string Kind);

public sealed record AuditEntryDetailDto(
    AuditEntryDto Entry,
    JsonElement? Changes,
    IReadOnlyList<AuditDiffDto> Diff,
    string? UserAgent,
    string? CorrelationId,
    string Hash,
    string PrevHash);

public sealed record AuditFacetsDto(IReadOnlyList<string> Actions, IReadOnlyList<string> EntityTypes, IReadOnlyList<string> ActorTypes);

public sealed record AuditChainVerificationDto(
    bool Valid,
    long Checked,
    long? BrokenAtSequence,
    Guid? BrokenEntryId,
    string Message,
    DateTime VerifiedAt,
    long Unsealed,
    long? AnchoredAfterSequence);

// ───── Retention ─────

public sealed record RetentionDataTypeDto(string Key, string Label, string Description, IReadOnlyList<string> Actions, int MinimumDays);

public sealed record RetentionPolicyDto(
    Guid Id,
    string Name,
    string DataType,
    string Action,
    int AfterDays,
    bool IsActive,
    DateTime? LastRunAt,
    int LastRunAffected,
    DateTime CreatedAt);

public sealed record SaveRetentionPolicyRequest(string Name, string DataType, string Action, int AfterDays, bool IsActive = true);

public sealed record RetentionDryRunDto(
    Guid? PolicyId,
    string DataType,
    string Action,
    int AfterDays,
    DateTime Cutoff,
    long Eligible,
    long HeldBack,
    DateTime EvaluatedAt);

public sealed record RetentionRunResultDto(Guid PolicyId, long Affected, long HeldBack, DateTime RanAt);

// ───── Legal holds ─────

public sealed record LegalHoldDto(
    Guid Id,
    string Name,
    string Reason,
    Guid? CustomerId,
    string? CustomerName,
    Guid? ContactId,
    string? ContactName,
    IReadOnlyList<long> TicketNumbers,
    DateTime? FromDate,
    DateTime? ToDate,
    bool Active,
    DateTime CreatedAt,
    string? CreatedByName,
    DateTime? ReleasedAt,
    long CoveredTickets);

/// <summary>A hold covers tickets matching the customer, the contact or the listed ticket numbers, within the optional date range.</summary>
public sealed record SaveLegalHoldRequest(
    string Name,
    string Reason,
    Guid? CustomerId = null,
    Guid? ContactId = null,
    IReadOnlyList<long>? TicketNumbers = null,
    DateTime? FromDate = null,
    DateTime? ToDate = null);

// ───── Data subject requests ─────

public sealed record DataRequestDto(
    Guid Id,
    string Type,
    string Status,
    Guid ContactId,
    string ContactName,
    string? Notes,
    DateTime CreatedAt,
    string? RequestedByName,
    DateTime? ApprovedAt,
    string? ApprovedByName,
    DateTime? CompletedAt,
    string? RejectionReason,
    string? Error,
    bool CanDownload,
    DateTime? DownloadExpiresAt,
    DateTime? DownloadedAt,
    string? ResultFileName,
    long? ResultSizeBytes,
    bool BlockedByHold);

/// <summary>Type is Export or Erasure. Exports start immediately; erasures wait for approval.</summary>
public sealed record CreateDataRequestRequest(string Type, Guid ContactId, string? Notes = null);

public sealed record RejectDataRequestRequest(string Reason);

/// <summary>A one-time download link (valid once, until <see cref="ExpiresAt"/>).</summary>
public sealed record DownloadLinkDto(string Url, DateTime ExpiresAt);
