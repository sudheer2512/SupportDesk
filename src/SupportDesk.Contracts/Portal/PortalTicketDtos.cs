using System.Text.Json;
using SupportDesk.Contracts.Common;

namespace SupportDesk.Contracts.Portal;

// ───────────────────────────── Portal tickets ─────────────────────────────
// Customer-facing projections only: no internal notes, events, internal attachments, internal status names,
// assignees, SLA data or other customers' records.

public sealed record PortalTicketQuery
{
    /// <summary>own | location | organisation (capped at the contact's portal access).</summary>
    public string? Scope { get; init; }
    /// <summary>open | closed | awaiting | all (default open+recent).</summary>
    public string? Status { get; init; }
    public string? Q { get; init; }
    public string? Cursor { get; init; }
    public int? Limit { get; init; }
}

public sealed record PortalTicketListItemDto(
    Guid Id,
    long Number,
    string DisplayNumber,
    string Subject,
    string Status,
    string StatusCategory,
    bool IsOpen,
    string Priority,
    DateTime CreatedAt,
    DateTime LastActivityAt,
    Guid? ContactId,
    string? ContactName,
    bool IsMine,
    string? LocationName,
    string? ProductName,
    bool AwaitingConfirmation,
    bool AwaitingYourReply);

public sealed record PortalTicketScopeDto(string Max, IReadOnlyList<string> Available);

public sealed record PortalFieldValueDto(string Key, string Label, string? Value);

public sealed record PortalTicketActionsDto(bool Reply, bool Reopen, bool Confirm, bool Rate);

public sealed record PortalTicketDetailDto(
    Guid Id,
    long Number,
    string DisplayNumber,
    string Subject,
    string? DescriptionHtml,
    string Status,
    string StatusCategory,
    bool IsOpen,
    string Priority,
    string Channel,
    DateTime CreatedAt,
    DateTime LastActivityAt,
    DateTime? ResolvedAt,
    DateTime? ClosedAt,
    Guid? ContactId,
    string? ContactName,
    bool IsMine,
    string? CustomerName,
    string? LocationName,
    string? DepartmentName,
    string? CategoryName,
    string? ProductName,
    string? InstallationName,
    IReadOnlyList<PortalFieldValueDto> Fields,
    IReadOnlyList<PortalAttachmentDto> Attachments,
    bool AwaitingConfirmation,
    int? CsatScore,
    string? CsatComment,
    PortalTicketActionsDto Actions,
    DateTime? ReopenUntil);

/// <summary>A public conversation entry. Kind: customer | agent | system.</summary>
public sealed record PortalMessageDto(
    Guid Id,
    int Seq,
    string Kind,
    string? AuthorName,
    bool IsMine,
    string BodyHtml,
    DateTime CreatedAt,
    IReadOnlyList<PortalAttachmentDto> Attachments);

public sealed record PortalOptionDto(Guid Id, string Name, string? Description);

public sealed record PortalCategoryOptionDto(Guid Id, Guid? ParentId, string Name, Guid? DepartmentId);

public sealed record PortalInstallationOptionDto(Guid Id, string Name, Guid ProductId, string ProductName, string? Version, string? LocationName, string Environment);

public sealed record PortalFieldDefDto(
    string Key,
    string Label,
    string FieldType,
    bool Required,
    bool Editable,
    string? HelpText,
    IReadOnlyList<OptionDto> Options,
    IReadOnlyList<Guid> DepartmentIds,
    string? DefaultValue,
    decimal? Min,
    decimal? Max,
    int? MaxLength);

/// <summary>Layout section (portal form layout). Field keys: standard names (departmentId, categoryId, productId, installationId, priority) or "cf:key".</summary>
public sealed record PortalFormSectionDto(string Title, int Columns, IReadOnlyList<string> Fields);

public sealed record PortalTicketFormDto(
    IReadOnlyList<PortalOptionDto> Departments,
    IReadOnlyList<PortalCategoryOptionDto> Categories,
    IReadOnlyList<PortalOptionDto> Products,
    IReadOnlyList<PortalInstallationOptionDto> Installations,
    IReadOnlyList<PortalFieldDefDto> Fields,
    IReadOnlyList<PortalFormSectionDto> Sections,
    IReadOnlyList<string> Priorities,
    int MaxAttachmentMb);

public sealed record PortalCreateTicketRequest
{
    public string Subject { get; init; } = "";
    public string? DescriptionHtml { get; init; }
    public Guid? DepartmentId { get; init; }
    public Guid? CategoryId { get; init; }
    public Guid? SubcategoryId { get; init; }
    public Guid? ProductId { get; init; }
    public Guid? InstallationId { get; init; }
    /// <summary>Low, Medium or High.</summary>
    public string? Priority { get; init; }
    public Dictionary<string, JsonElement>? CustomFields { get; init; }
    public IReadOnlyList<Guid>? AttachmentFileIds { get; init; }
    /// <summary>Idempotency key (double submit / retries).</summary>
    public string? ClientRequestId { get; init; }
    /// <summary>Guided troubleshooting flow the customer escalated from (recorded on the ticket).</summary>
    public Guid? SourceFlowId { get; init; }
}

public sealed record PortalTicketCreatedDto(Guid Id, long Number, string DisplayNumber);

public sealed record PortalReplyRequest(string BodyHtml, IReadOnlyList<Guid>? AttachmentFileIds = null, string? ClientRequestId = null);

public sealed record PortalReopenRequest(string Reason);

/// <summary>Resolved = true closes the ticket; false reopens it with the comment as a new message.</summary>
public sealed record PortalConfirmRequest(bool Resolved, string? Comment = null);

public sealed record PortalSatisfactionRequest(int Rating, string? Comment = null);

// ───────────────────────────── Portal account ─────────────────────────────

public sealed record PortalProfileDto(
    Guid Id,
    string FullName,
    string? Email,
    string? Phone,
    string? Mobile,
    string? JobTitle,
    string PreferredLanguage,
    string? PreferredChannel,
    string? TimeZone,
    string? CustomerName,
    string? LocationName,
    string AccessScope,
    bool CanViewContracts,
    DateTime? LastLoginAt);

public sealed record UpdatePortalProfileRequest(
    string FullName,
    string? Phone,
    string? Mobile,
    string? JobTitle,
    string PreferredLanguage,
    string? PreferredChannel,
    string? TimeZone);

/// <summary>Channel: Email, WhatsApp, Sms. Purpose: Transactional, Marketing, Surveys.</summary>
public sealed record PortalConsentDto(string Channel, string Purpose, bool OptedIn);

public sealed record PortalPreferencesDto(string? PreferredChannel, string PreferredLanguage, IReadOnlyList<PortalConsentDto> Consents);

public sealed record UpdatePortalPreferencesRequest(string? PreferredChannel, string? PreferredLanguage, IReadOnlyList<PortalConsentDto> Consents);

public sealed record ChangePortalPasswordRequest(string CurrentPassword, string NewPassword);

public sealed record PortalEntitlementDto(
    Guid Id,
    string Name,
    string Type,
    string? ProductName,
    string? InstallationName,
    int? IncidentsAllowed,
    int IncidentsUsed,
    decimal? HoursAllowed,
    decimal HoursUsed,
    DateOnly StartDate,
    DateOnly EndDate,
    bool IsActive);

public sealed record PortalContractDto(
    Guid Id,
    string Number,
    string Name,
    string Type,
    string Status,
    DateOnly StartDate,
    DateOnly EndDate,
    int DaysRemaining,
    decimal? PrepaidHours,
    decimal ConsumedHours,
    double? ConsumedPercent,
    bool AutoRenew,
    DateOnly? RenewalDate,
    IReadOnlyList<string> CoveredChannels,
    bool Onsite,
    IReadOnlyList<PortalEntitlementDto> Entitlements);

public sealed record PortalLicenceDto(Guid Id, string ProductName, string? Edition, int Seats, string KeyMasked, DateOnly IssuedOn, DateOnly? ExpiresOn,
    string Status, string? InstallationName);

public sealed record PortalInstallationDto(Guid Id, string Name, string? Code, string ProductName, string? Version, string Environment, string DeploymentMode,
    string? LocationName, string Status, DateOnly? GoLiveOn, int? UserCount);

public sealed record PortalServiceActivityDto(Guid Id, string Type, string Title, string? Description, DateTime ScheduledStart, DateTime ScheduledEnd,
    string Status, string? LocationName, string? InstallationName, string? Outcome);

public sealed record PortalAccountOverviewDto(
    string CustomerName,
    string? Tier,
    IReadOnlyList<PortalContractDto> Contracts,
    IReadOnlyList<PortalLicenceDto> Licences,
    IReadOnlyList<PortalInstallationDto> Installations,
    IReadOnlyList<PortalServiceActivityDto> Activities);

public sealed record PortalPersonDto(string Name, string? Email, string? Phone, string? Title);

public sealed record PortalDepartmentContactDto(string Name, string? Description, string? Email);

public sealed record PortalSupportInfoDto(string? SupportEmail, string? SupportPhone, PortalPersonDto? AccountManager,
    IReadOnlyList<PortalDepartmentContactDto> Departments);
