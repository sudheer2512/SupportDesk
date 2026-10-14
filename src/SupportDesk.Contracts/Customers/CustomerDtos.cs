using System.Text.Json;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Products;

namespace SupportDesk.Contracts.Customers;

// ───────────────────────────── Customers (organisations) ─────────────────────────────

/// <summary>Query parameters for GET /api/v1/customers (all optional; Status/Tier/Region/Industry accept comma-separated values).</summary>
public sealed record CustomerQuery
{
    public string? Q { get; init; }
    public string? Status { get; init; }
    public string? Tier { get; init; }
    public string? Region { get; init; }
    public string? Industry { get; init; }
    public string? Category { get; init; }
    public Guid? GroupId { get; init; }
    public Guid? AccountManagerId { get; init; }
    public Guid? ParentId { get; init; }
    /// <summary>Only customers without a parent (hierarchy roots).</summary>
    public bool? TopLevelOnly { get; init; }
    /// <summary>Only customers the caller manages as account manager.</summary>
    public bool? Mine { get; init; }
    public bool? HasOpenTickets { get; init; }
    /// <summary>name (default) | code | created | updated</summary>
    public string? Sort { get; init; }
    public string? Dir { get; init; }
    public string? Cursor { get; init; }
    public int? Limit { get; init; }
    public bool? IncludeTotal { get; init; }
}

public sealed record CustomerListItemDto(
    Guid Id,
    string Name,
    string? Code,
    string Status,
    string Tier,
    string? Category,
    string? Industry,
    string? ServiceRegion,
    Guid? CustomerGroupId,
    string? GroupName,
    Guid? AccountManagerUserId,
    string? AccountManagerName,
    Guid? ParentCustomerId,
    string? ParentName,
    int ChildCount,
    int ContactCount,
    int OpenTickets,
    string? ErpCode,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>Channel preferences of a customer organisation (stored as CommunicationPrefsJson).</summary>
public sealed record CommunicationPreferencesDto(
    bool Email = true,
    bool Phone = true,
    bool Sms = false,
    bool WhatsApp = false,
    string? PreferredChannel = null,
    string? Notes = null);

public sealed record CustomerChildDto(Guid Id, string Name, string? Code, string Tier, string Status, int OpenTickets, int ChildCount);

/// <summary>Counts for the customer and all of its descendants (hierarchy roll-up).</summary>
public sealed record CustomerRollupDto(int Descendants, int Contacts, int Locations, int OpenTickets, int Installations, int ActiveContracts);

public sealed record CustomerPermissionsDto(bool Edit, bool Delete, bool ManageContacts, bool MergeContacts, bool ViewContracts, bool ManageContracts,
    bool ViewProducts, bool ManageProducts, bool CreateTickets);

public sealed record CustomerDetailDto(
    Guid Id,
    string Name,
    string? Code,
    string Status,
    string Tier,
    string? Category,
    string? Industry,
    string? ServiceRegion,
    Guid? CustomerGroupId,
    string? GroupName,
    Guid? AccountManagerUserId,
    string? AccountManagerName,
    Guid? ParentCustomerId,
    string? ParentName,
    IReadOnlyList<IdName> Ancestors,
    IReadOnlyList<CustomerChildDto> Children,
    CustomerRollupDto Rollup,
    string PreferredLanguage,
    string? TimeZone,
    string? Website,
    string? Phone,
    string? Email,
    IReadOnlyList<string> Domains,
    CommunicationPreferencesDto CommunicationPreferences,
    string? Notes,
    JsonElement CustomFields,
    Guid? ErpCustomerId,
    string? ErpCode,
    DateTime? ErpSyncedAt,
    bool IsDeleted,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string RowVersion,
    CustomerPermissionsDto Permissions);

/// <summary>Create (POST) or full update (PUT, requires RowVersion) of a customer.</summary>
public sealed record SaveCustomerRequest
{
    public string Name { get; init; } = "";
    public string? Code { get; init; }
    public string? Status { get; init; }
    public Guid? ParentCustomerId { get; init; }
    public Guid? CustomerGroupId { get; init; }
    public string? Category { get; init; }
    public string? Industry { get; init; }
    public string? Tier { get; init; }
    public Guid? AccountManagerUserId { get; init; }
    public string? ServiceRegion { get; init; }
    public string? PreferredLanguage { get; init; }
    public string? TimeZone { get; init; }
    public string? Website { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public IReadOnlyList<string>? Domains { get; init; }
    public CommunicationPreferencesDto? CommunicationPreferences { get; init; }
    public string? Notes { get; init; }
    public Dictionary<string, JsonElement>? CustomFields { get; init; }
    public Guid? ErpCustomerId { get; init; }
    public string? ErpCode { get; init; }
    /// <summary>Base64 rowversion loaded with the customer (required for updates).</summary>
    public string? RowVersion { get; init; }
}

/// <summary>Distinct values used by list filters.</summary>
public sealed record CustomerFacetsDto(
    IReadOnlyList<string> Tiers,
    IReadOnlyList<string> Regions,
    IReadOnlyList<string> Industries,
    IReadOnlyList<string> Categories,
    IReadOnlyList<IdName> Groups,
    IReadOnlyList<IdName> AccountManagers,
    IReadOnlyList<string> Statuses);

public sealed record CustomerGroupDto(Guid Id, string Name, string? Description, int CustomerCount);

public sealed record SaveCustomerGroupRequest(string Name, string? Description);

// ───────────────────────────── Locations & customer departments ─────────────────────────────

public sealed record LocationDto(
    Guid Id,
    Guid CustomerId,
    string Name,
    string? Code,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? State,
    string? Country,
    string? PostalCode,
    string? Phone,
    string? TimeZone,
    string? ServiceRegion,
    bool IsHeadOffice,
    bool IsActive,
    Guid? ErpLocationId,
    int ContactCount,
    int InstallationCount);

public sealed record SaveLocationRequest
{
    public string Name { get; init; } = "";
    public string? Code { get; init; }
    public string? AddressLine1 { get; init; }
    public string? AddressLine2 { get; init; }
    public string? City { get; init; }
    public string? State { get; init; }
    public string? Country { get; init; }
    public string? PostalCode { get; init; }
    public string? Phone { get; init; }
    public string? TimeZone { get; init; }
    public string? ServiceRegion { get; init; }
    public bool IsHeadOffice { get; init; }
    public bool IsActive { get; init; } = true;
}

public sealed record CustomerDepartmentDto(Guid Id, Guid CustomerId, string Name, int ContactCount);

public sealed record SaveCustomerDepartmentRequest(string Name);

// ───────────────────────────── Customer 360 ─────────────────────────────

public sealed record Customer360KpisDto(
    int OpenTickets,
    int OverdueTickets,
    int EscalatedTickets,
    int TicketsLast90Days,
    int ResolvedLast90Days,
    int SlaBreachesLast90Days,
    int SlaBreachesOpen,
    double? CsatAverage90Days,
    int CsatResponses90Days,
    double? AvgResolutionHours90Days,
    int ActiveContracts,
    int ContractsExpiringSoon,
    int Installations,
    int Contacts,
    int UpcomingActivities,
    DateTime? LastTicketAt);

public sealed record CsatPointDto(string Month, double? Average, int Responses, int Positive, int Negative);

public sealed record TicketVolumePointDto(string Month, int Created, int Resolved);

/// <summary>A ticket highlighted in Customer 360 (outstanding complaints and escalations).</summary>
public sealed record Customer360TicketDto(
    Guid Id,
    long Number,
    string DisplayNumber,
    string Subject,
    string Priority,
    string Status,
    string StatusCategory,
    bool IsEscalated,
    string SlaState,
    string Sentiment,
    string? AssigneeName,
    DateTime CreatedAt,
    IReadOnlyList<string> Reasons);

/// <summary>Excerpt of a recent conversation entry with the customer (any channel).</summary>
public sealed record ConversationExcerptDto(
    Guid TicketId,
    string DisplayNumber,
    string Subject,
    Guid ThreadId,
    string Kind,
    string Channel,
    string Direction,
    string? AuthorName,
    string Excerpt,
    DateTime CreatedAt);

public sealed record Customer360Dto(
    CustomerDetailDto Customer,
    Customer360KpisDto Kpis,
    IReadOnlyList<CountDto> OpenByPriority,
    IReadOnlyList<CsatPointDto> CsatTrend,
    IReadOnlyList<TicketVolumePointDto> TicketTrend,
    IReadOnlyList<Customer360TicketDto> OutstandingComplaints,
    IReadOnlyList<ConversationExcerptDto> RecentConversations,
    IReadOnlyList<ContractListItemDto> Contracts,
    IReadOnlyList<EntitlementDto> Entitlements,
    IReadOnlyList<InstallationListItemDto> Installations,
    IReadOnlyList<AssetDto> Assets,
    IReadOnlyList<LicenceDto> Licences,
    IReadOnlyList<ServiceActivityDto> UpcomingActivities,
    IReadOnlyList<ContactListItemDto> KeyContacts,
    IReadOnlyList<LocationDto> Locations,
    bool ErpConnected);

// ───────────────────────────── Service activities ─────────────────────────────

public sealed record ServiceActivityQuery
{
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public Guid? CustomerId { get; init; }
    public Guid? AssignedUserId { get; init; }
    public string? Status { get; init; }
    public string? Type { get; init; }
    public int? Limit { get; init; }
}

public sealed record ServiceActivityDto(
    Guid Id,
    Guid CustomerId,
    string CustomerName,
    Guid? LocationId,
    string? LocationName,
    Guid? TicketId,
    string? TicketNumber,
    Guid? InstallationId,
    string? InstallationName,
    Guid? ContractId,
    string? ContractNumber,
    string Type,
    string Title,
    string? Description,
    DateTime ScheduledStart,
    DateTime ScheduledEnd,
    Guid? AssignedUserId,
    string? AssignedUserName,
    string Status,
    string? Outcome,
    bool VisibleToCustomer,
    DateTime CreatedAt);

public sealed record SaveServiceActivityRequest
{
    public Guid CustomerId { get; init; }
    public Guid? LocationId { get; init; }
    public Guid? TicketId { get; init; }
    public Guid? InstallationId { get; init; }
    public Guid? ContractId { get; init; }
    public string Type { get; init; } = "OnsiteVisit";
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public DateTime ScheduledStart { get; init; }
    public DateTime ScheduledEnd { get; init; }
    public Guid? AssignedUserId { get; init; }
    public string? Status { get; init; }
    public string? Outcome { get; init; }
    public bool VisibleToCustomer { get; init; } = true;
}

// ───────────────────────────── Contacts ─────────────────────────────

public sealed record ContactQuery
{
    public string? Q { get; init; }
    public Guid? CustomerId { get; init; }
    public Guid? LocationId { get; init; }
    /// <summary>Active (default) | Inactive | Merged | all</summary>
    public string? Status { get; init; }
    public bool? IsVip { get; init; }
    public string? PortalAccess { get; init; }
    /// <summary>name (default) | created | updated</summary>
    public string? Sort { get; init; }
    public string? Dir { get; init; }
    public string? Cursor { get; init; }
    public int? Limit { get; init; }
    public bool? IncludeTotal { get; init; }
}

public sealed record ContactListItemDto(
    Guid Id,
    string FullName,
    string? Email,
    string? Phone,
    string? JobTitle,
    Guid? CustomerId,
    string? CustomerName,
    Guid? LocationId,
    string? LocationName,
    bool IsVip,
    bool IsPrimary,
    string Status,
    string PortalAccess,
    bool HasPortalPassword,
    int OpenTickets,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ContactIdentityDto(Guid Id, string Channel, string Identifier, bool IsVerified, DateTime? VerifiedAt, DateTime CreatedAt);

public sealed record ContactConsentDto(string Channel, string Purpose, string Status, string? Source, DateTime? RecordedAt);

public sealed record ContactStatsDto(int OpenTickets, int TotalTickets, DateTime? LastTicketAt, double? CsatAverage, int Conversations);

public sealed record ContactPermissionsDto(bool Edit, bool Merge, bool ManagePortal);

public sealed record ContactDetailDto(
    Guid Id,
    string FullName,
    string? FirstName,
    string? LastName,
    string? Email,
    string? Phone,
    string? Mobile,
    string? WhatsAppNumber,
    string? JobTitle,
    Guid? CustomerId,
    string? CustomerName,
    Guid? LocationId,
    string? LocationName,
    Guid? CustomerDepartmentId,
    string? CustomerDepartmentName,
    string PreferredLanguage,
    string? PreferredChannel,
    string? TimeZone,
    bool IsVip,
    bool IsPrimary,
    string Status,
    Guid? MergedIntoContactId,
    string PortalAccess,
    bool HasPortalPassword,
    DateTime? PortalLastLoginAt,
    bool PortalLocked,
    DateTime? EmailVerifiedAt,
    DateTime? PhoneVerifiedAt,
    JsonElement CustomFields,
    Guid? ErpContactId,
    IReadOnlyList<ContactIdentityDto> Identities,
    IReadOnlyList<ContactConsentDto> Consents,
    ContactStatsDto Stats,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string RowVersion,
    ContactPermissionsDto Permissions);

/// <summary>Create (POST) or full update (PUT, requires RowVersion) of a contact.</summary>
public sealed record SaveContactRequest
{
    public string? FullName { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? Mobile { get; init; }
    public string? WhatsAppNumber { get; init; }
    public string? JobTitle { get; init; }
    public Guid? CustomerId { get; init; }
    public Guid? LocationId { get; init; }
    public Guid? CustomerDepartmentId { get; init; }
    public string? PreferredLanguage { get; init; }
    public string? PreferredChannel { get; init; }
    public string? TimeZone { get; init; }
    public bool IsVip { get; init; }
    public bool IsPrimary { get; init; }
    public string? Status { get; init; }
    public string? PortalAccess { get; init; }
    public Dictionary<string, JsonElement>? CustomFields { get; init; }
    public string? RowVersion { get; init; }
}

public sealed record AddContactIdentityRequest(string Channel, string Identifier, bool Verified = false);

public sealed record SetConsentRequest(string Channel, string Purpose, string Status, string? Source);

public sealed record SetPortalAccessRequest(string Access);

/// <summary>Sends a portal invitation (first access) or a password-reset sign-in code to the contact's e-mail.</summary>
public sealed record PortalInviteRequest(string Mode = "invite", Guid? BrandId = null);

public sealed record PortalInviteResultDto(string Mode, string SentTo, string LoginUrl, DateTime ExpiresAt, string? DemoCode);

/// <summary>A possible duplicate of a contact, with a score in [0,1] and the reasons that produced it.</summary>
public sealed record DuplicateCandidateDto(ContactListItemDto Contact, double Score, string Confidence, IReadOnlyList<string> Reasons);

public sealed record DuplicatePairDto(ContactListItemDto First, ContactListItemDto Second, double Score, string Confidence, IReadOnlyList<string> Reasons);

/// <summary>
/// Merges <see cref="MergedId"/> into <see cref="SurvivorId"/>. The survivor keeps its own non-empty values and fills
/// gaps from the merged contact; <see cref="PreferMergedFields"/> lists fields whose merged value should win.
/// </summary>
public sealed record MergeContactsRequest(Guid SurvivorId, Guid MergedId, IReadOnlyList<string>? PreferMergedFields = null);

public sealed record ContactMergeResultDto(
    Guid SurvivorId,
    Guid MergedId,
    bool AlreadyMerged,
    int Tickets,
    int Threads,
    int Conversations,
    int Messages,
    int Identities,
    int Consents,
    int SatisfactionResponses,
    int OtherRecords,
    IReadOnlyList<string> FieldsCopied,
    Guid? MergeLogId);

public sealed record ContactMergeLogDto(Guid Id, Guid SurvivorContactId, Guid MergedContactId, string? MergedContactName, Guid? MergedById,
    string? MergedByName, DateTime MergedAt, JsonElement Snapshot);
