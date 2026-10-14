namespace SupportDesk.Contracts.Common;

/// <summary>Keyset-paged list. Pass <see cref="NextCursor"/> back as ?cursor= to get the next page; null means no more rows.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, string? NextCursor, int? Total = null);

/// <summary>Offset-paged list (small admin lists).</summary>
public sealed record PageResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public sealed record IdName(Guid Id, string Name);

public sealed record IdNameCode(Guid Id, string Name, string? Code);

public sealed record FieldChangeDto(string Field, string? From, string? To);

public sealed record OptionDto(string Value, string Label);

/// <summary>Problem-details error codes returned in the "code" extension of 4xx responses.</summary>
public static class ErrorCodes
{
    public const string Validation = "validation_failed";
    public const string NotFound = "not_found";
    public const string Forbidden = "forbidden";
    public const string Conflict = "conflict";
    public const string ConcurrencyConflict = "concurrency_conflict";
    public const string ReplyCollision = "reply_collision";
    public const string TicketLocked = "ticket_locked";
    public const string LimitExceeded = "limit_exceeded";
    public const string TenantSuspended = "tenant_suspended";
    public const string ApprovalRequired = "approval_required";
    public const string RateLimited = "rate_limited";
    public const string InvalidTransition = "invalid_transition";
    public const string MissingFields = "missing_required_fields";
    public const string Unauthorized = "unauthorized";
}

public sealed record BulkResultDto(int Requested, int Succeeded, IReadOnlyList<BulkFailureDto> Failures);

public sealed record BulkFailureDto(Guid Id, string Code, string Message);

public sealed record CountDto(string Key, string Label, long Count);

public sealed record AttachmentDto(Guid Id, Guid FileId, string FileName, string ContentType, long SizeBytes, string Visibility,
    bool IsInline, string? ScanStatus, DateTime CreatedAt, string DownloadUrl);

public sealed record UploadedFileDto(Guid FileId, string FileName, string ContentType, long SizeBytes, string ScanStatus);
