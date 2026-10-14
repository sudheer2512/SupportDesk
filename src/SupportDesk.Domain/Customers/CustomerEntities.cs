namespace SupportDesk.Domain.Customers;

/// <summary>Merged: folded into another customer by an organisation merge (hidden, read-only; see CustomerMergeLog).</summary>
public enum CustomerStatus { Active, Inactive, Prospect, OnHold, Merged }

/// <summary>A customer organisation (account). May have a parent (hierarchy), branches, contacts, products and contracts.</summary>
public class Customer : AuditedEntity, ISoftDeletable, IConcurrencyVersioned
{
    public string Name { get; set; } = "";
    public string? Code { get; set; }
    public Guid? ParentCustomerId { get; set; }
    public Guid? CustomerGroupId { get; set; }
    public string? Category { get; set; }
    public string? Industry { get; set; }
    /// <summary>Support tier (Platinum, Gold, Silver, Bronze or tenant-defined). Drives SLA policy selection.</summary>
    public string Tier { get; set; } = "Standard";
    public Guid? AccountManagerUserId { get; set; }
    public string? ServiceRegion { get; set; }
    public string PreferredLanguage { get; set; } = "en";
    public string? TimeZone { get; set; }
    public string? Website { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    /// <summary>E-mail domains that identify this customer's contacts, e.g. ["abcretail.com"].</summary>
    public string DomainsJson { get; set; } = "[]";
    public CustomerStatus Status { get; set; } = CustomerStatus.Active;
    /// <summary>{"email":true,"sms":false,"whatsapp":true,"preferredChannel":"Email"}</summary>
    public string? CommunicationPrefsJson { get; set; }
    public string? Notes { get; set; }
    public string CustomFieldsJson { get; set; } = "{}";
    /// <summary>Identifier of the customer master in the ERP (Wings ERP customer GUID).</summary>
    public Guid? ErpCustomerId { get; set; }
    public string? ErpCode { get; set; }
    public DateTime? ErpSyncedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public class CustomerGroup : AuditedEntity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
}

/// <summary>A branch / site of a customer.</summary>
public class CustomerLocation : AuditedEntity
{
    public Guid CustomerId { get; set; }
    public string Name { get; set; } = "";
    public string? Code { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Country { get; set; }
    public string? PostalCode { get; set; }
    public string? Phone { get; set; }
    public string? TimeZone { get; set; }
    public string? ServiceRegion { get; set; }
    public bool IsHeadOffice { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid? ErpLocationId { get; set; }
}

public class CustomerDepartment : AuditedEntity
{
    public Guid CustomerId { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>What a portal user may see within their organisation.</summary>
public enum PortalAccessScope { None = 0, Own = 1, Location = 2, Organisation = 3 }

public enum ContactStatus { Active, Inactive, Merged, Erased }

public class Contact : AuditedEntity, ISoftDeletable, IConcurrencyVersioned
{
    public Guid? CustomerId { get; set; }
    public Guid? LocationId { get; set; }
    public Guid? CustomerDepartmentId { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string FullName { get; set; } = "";
    public string? Email { get; set; }
    public string? EmailNormalized { get; set; }
    public string? Phone { get; set; }
    /// <summary>Digits-only E.164 form for matching (e.g. 919845012345).</summary>
    public string? PhoneNormalized { get; set; }
    public string? Mobile { get; set; }
    public string? WhatsAppNumber { get; set; }
    public string? JobTitle { get; set; }
    public string PreferredLanguage { get; set; } = "en";
    public Channel? PreferredChannel { get; set; }
    public string? TimeZone { get; set; }
    public bool IsVip { get; set; }
    public bool IsPrimary { get; set; }
    public PortalAccessScope PortalAccess { get; set; } = PortalAccessScope.Own;
    public string? PortalPasswordHash { get; set; }
    public DateTime? PortalLastLoginAt { get; set; }
    public int PortalFailedLogins { get; set; }
    public DateTime? PortalLockedUntil { get; set; }
    public DateTime? EmailVerifiedAt { get; set; }
    public DateTime? PhoneVerifiedAt { get; set; }
    public ContactStatus Status { get; set; } = ContactStatus.Active;
    public Guid? MergedIntoContactId { get; set; }
    public string CustomFieldsJson { get; set; } = "{}";
    public Guid? ErpContactId { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public DateTime? ErasedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>A channel identity (e-mail address, phone, WhatsApp id, Messenger PSID…) used for automatic contact matching.</summary>
public class ContactIdentity : TenantEntity
{
    public Guid ContactId { get; set; }
    public Channel Channel { get; set; }
    /// <summary>Normalised identifier (lower-case e-mail, digits-only phone, provider user id).</summary>
    public string Identifier { get; set; } = "";
    public bool IsVerified { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public enum ConsentPurpose { Transactional, Marketing, Surveys }
public enum ConsentStatus { Unknown, OptedIn, OptedOut }

public class ContactChannelConsent : TenantEntity
{
    public Guid ContactId { get; set; }
    public Channel Channel { get; set; }
    public ConsentPurpose Purpose { get; set; }
    public ConsentStatus Status { get; set; }
    public string? Source { get; set; }
    public DateTime RecordedAt { get; set; }
}

public class ContactMergeLog : TenantEntity
{
    public Guid SurvivorContactId { get; set; }
    public Guid MergedContactId { get; set; }
    public Guid? MergedById { get; set; }
    public DateTime MergedAt { get; set; }
    /// <summary>Snapshot of the merged contact and moved record counts (for audit).</summary>
    public string SnapshotJson { get; set; } = "{}";
}

public enum ServiceActivityType { OnsiteVisit, RemoteSession, Training, PreventiveMaintenance, Upgrade, Call, Meeting }
public enum ServiceActivityStatus { Scheduled, InProgress, Completed, Cancelled, Missed }

/// <summary>Service visits and scheduled activities (maintenance schedules, trainings…), shown in Customer 360 and the portal.</summary>
public class ServiceActivity : AuditedEntity
{
    public Guid CustomerId { get; set; }
    public Guid? LocationId { get; set; }
    public Guid? TicketId { get; set; }
    public Guid? InstallationId { get; set; }
    public Guid? ContractId { get; set; }
    public ServiceActivityType Type { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public DateTime ScheduledStart { get; set; }
    public DateTime ScheduledEnd { get; set; }
    public Guid? AssignedUserId { get; set; }
    public ServiceActivityStatus Status { get; set; } = ServiceActivityStatus.Scheduled;
    public string? Outcome { get; set; }
    public bool VisibleToCustomer { get; set; } = true;
}
