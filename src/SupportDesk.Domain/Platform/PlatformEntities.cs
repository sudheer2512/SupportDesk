namespace SupportDesk.Domain.Platform;

// ───────────── Tenancy & subscriptions ─────────────

public enum TenantStatus { Trial, Active, Suspended, Cancelled, PendingDeletion }

public enum DatabaseMode { Shared, Dedicated }

/// <summary>A SaaS customer organisation. Not tenant-owned itself; lives in the catalog (shared) database.</summary>
public class Tenant : Entity, IHasTimestamps
{
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public TenantStatus Status { get; set; } = TenantStatus.Trial;
    public DatabaseMode DatabaseMode { get; set; } = DatabaseMode.Shared;
    /// <summary>Name of the connection string (configuration key) for a dedicated database. Never a raw connection string.</summary>
    public string? DedicatedConnectionName { get; set; }
    public string DefaultTimeZone { get; set; } = "UTC";
    public string DefaultLocale { get; set; } = "en";
    public string? Country { get; set; }
    public string? DataRegion { get; set; }
    public string? SuspensionReason { get; set; }
    public DateTime? SuspendedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    /// <summary>When a cancelled tenant's data is due for deletion (set by the platform operator on cancellation).</summary>
    public DateTime? DeletionScheduledAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Commercial plan with usage limits. Global (not tenant-owned). A null limit means unlimited.</summary>
public class Plan : Entity, IHasTimestamps
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public decimal MonthlyPricePerAgent { get; set; }
    public decimal AnnualPricePerAgent { get; set; }
    public string Currency { get; set; } = "USD";
    public bool IsPublic { get; set; } = true;
    public int TrialDays { get; set; }
    public int? MaxAgents { get; set; }
    /// <summary>Light agents (collaborators: view + internal notes). Null = unlimited.</summary>
    public int? MaxLightAgents { get; set; }
    public int? MaxPortalUsers { get; set; }
    public long? StorageMb { get; set; }
    public int? MaxChannels { get; set; }
    public long? AiTokensMonthly { get; set; }
    public int? MaxAttachmentMb { get; set; }
    public long? ApiCallsMonthly { get; set; }
    public int? MaxBrands { get; set; }
    /// <summary>Feature flags enabled by the plan, e.g. ["automation","blueprints","ai","sso","advanced_analytics"].</summary>
    public string FeaturesJson { get; set; } = "[]";
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum SubscriptionStatus { Trialing, Active, PastDue, Suspended, Cancelled }

public enum BillingCycle { Monthly, Annual }

public class Subscription : AuditedEntity, IConcurrencyVersioned
{
    public Guid PlanId { get; set; }
    public SubscriptionStatus Status { get; set; }
    public BillingCycle BillingCycle { get; set; } = BillingCycle.Monthly;
    public int Seats { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? TrialEndsAt { get; set; }
    public DateTime CurrentPeriodStart { get; set; }
    public DateTime CurrentPeriodEnd { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    public Guid? PendingPlanId { get; set; }
    /// <summary>Per-tenant overrides of plan limits, e.g. {"MaxAgents":80}.</summary>
    public string? LimitOverridesJson { get; set; }
    /// <summary>End of the grace period after a trial expired or an invoice went unpaid; the tenant is suspended afterwards.</summary>
    public DateTime? GraceEndsAt { get; set; }
    public DateTime? SuspendedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? BillingContactName { get; set; }
    public string? BillingContactEmail { get; set; }
    public string? BillingAddress { get; set; }
    public string? BillingTaxId { get; set; }
    /// <summary>Key of the <c>IPaymentProvider</c> that collects payments ("manual" = offline / bank transfer). Card data is never stored.</summary>
    public string PaymentProvider { get; set; } = "manual";
    public byte[] RowVersion { get; set; } = [];
}

public static class UsageMetrics
{
    public const string Agents = "agents";
    public const string LightAgents = "light_agents";
    public const string PortalUsers = "portal_users";
    public const string StorageBytes = "storage_bytes";
    public const string Channels = "channels";
    public const string AiTokens = "ai_tokens";
    public const string ApiCalls = "api_calls";
    public const string TicketsCreated = "tickets_created";
    public const string MessagesSent = "messages_sent";
    public const string Attachments = "attachments";
}

/// <summary>Usage counter for one metric in one monthly period. Updated atomically.</summary>
public class UsageRecord : TenantEntity
{
    public string Metric { get; set; } = "";
    public DateTime PeriodStart { get; set; }
    public long Quantity { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum InvoiceStatus { Draft, Open, Paid, Void }

/// <summary>Subscription = the regular period invoice (one per period); Proration = mid-period upgrade charge; Manual = operator-issued.</summary>
public enum InvoiceKind { Subscription, Proration, Manual }

public class BillingInvoice : AuditedEntity
{
    public Guid SubscriptionId { get; set; }
    public InvoiceKind Kind { get; set; } = InvoiceKind.Subscription;
    public string Number { get; set; } = "";
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    /// <summary>[{description, quantity, unitPrice, amount}]</summary>
    public string LinesJson { get; set; } = "[]";
    public decimal Subtotal { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public string Currency { get; set; } = "USD";
    public InvoiceStatus Status { get; set; }
    public DateTime IssuedAt { get; set; }
    public DateTime DueAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public string? PaymentReference { get; set; }
    public string? PaymentProvider { get; set; }
    public string? Notes { get; set; }
}

/// <summary>Platform operators run the SaaS (tenant administration). Not tenant-owned.</summary>
public class PlatformOperator : Entity, IHasTimestamps
{
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string? MfaSecretProtected { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// ───────────── Brands & portals ─────────────

public enum PortalAuthMode { Local, Sso, LocalAndSso }

/// <summary>A brand owns a customer portal, knowledge base, chat widget and outbound identity.</summary>
public class Brand : AuditedEntity, IConcurrencyVersioned
{
    public string Name { get; set; } = "";
    /// <summary>Globally unique portal slug (/portal/{slug}).</summary>
    public string Slug { get; set; } = "";
    /// <summary>Optional custom portal host name (support.example.com). Globally unique.</summary>
    public string? CustomDomain { get; set; }
    public string? LogoUrl { get; set; }
    public string? FaviconUrl { get; set; }
    /// <summary>Sanitised HTML appended to customer e-mails sent for this brand (signature / legal footer).</summary>
    public string? EmailFooterHtml { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public string PrimaryColor { get; set; } = "#2563eb";
    public string AccentColor { get; set; } = "#0ea5e9";
    public string? SupportEmail { get; set; }
    public string? SupportPhone { get; set; }
    public string PortalTitle { get; set; } = "Help Center";
    public string? PortalWelcome { get; set; }
    public string? FooterHtml { get; set; }
    public Guid? DefaultDepartmentId { get; set; }
    public string DefaultLocale { get; set; } = "en";
    public string SupportedLocalesJson { get; set; } = "[\"en\"]";
    public bool IsDefault { get; set; }
    public bool PortalEnabled { get; set; } = true;
    public bool KnowledgeBaseEnabled { get; set; } = true;
    public bool CommunityEnabled { get; set; } = true;
    public bool ChatEnabled { get; set; } = true;
    public bool AiAssistantEnabled { get; set; } = true;
    public bool AllowAnonymousTickets { get; set; }
    public bool AllowSelfRegistration { get; set; }
    public PortalAuthMode PortalAuthMode { get; set; } = PortalAuthMode.Local;
    public string? ChatWidgetSettingsJson { get; set; }
}

/// <summary>Per-tenant named counters (ticket numbers, invoice numbers). Incremented atomically with UPDATE … OUTPUT.</summary>
public class TenantCounter : TenantEntity
{
    public string Name { get; set; } = "";
    public long NextValue { get; set; } = 1;
}

/// <summary>Versioned key/value settings (security policy, numbering, closure confirmation, retention defaults…).</summary>
public class TenantSetting : AuditedEntity, IConcurrencyVersioned
{
    public string Key { get; set; } = "";
    public string ValueJson { get; set; } = "{}";
    public int Version { get; set; } = 1;
    public byte[] RowVersion { get; set; } = [];
}

// ───────────── Audit ─────────────

/// <summary>
/// Tamper-evident audit trail. Each row's Hash = SHA-256(PrevHash + canonical content), chained per tenant
/// (platform rows use the empty tenant). Append-only: the DbContext rejects updates and deletes.
/// </summary>
public class AuditLog : Entity, IAppendOnly
{
    public Guid? TenantId { get; set; }
    public long Sequence { get; set; }
    public DateTime OccurredAt { get; set; }
    public ActorType ActorType { get; set; }
    public Guid? ActorId { get; set; }
    public string? ActorName { get; set; }
    public string Action { get; set; } = "";
    public string? EntityType { get; set; }
    public Guid? EntityId { get; set; }
    public string? Summary { get; set; }
    public string? ChangesJson { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? CorrelationId { get; set; }
    public string PrevHash { get; set; } = "";
    public string Hash { get; set; } = "";
}

// ───────────── Reliable messaging & jobs ─────────────

public enum OutboxStatus { Pending, Processing, Processed, Failed, DeadLetter }

/// <summary>
/// Transactional outbox: written in the same transaction as the business change, dispatched afterwards to
/// in-process handlers (automation, SLA, notifications, realtime, webhooks, search indexing, metrics).
/// </summary>
public class OutboxEvent : Entity
{
    public Guid? TenantId { get; set; }
    public string Type { get; set; } = "";
    public string? AggregateType { get; set; }
    public Guid? AggregateId { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public DateTime OccurredAt { get; set; }
    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime? LockedUntil { get; set; }
    public string? LockedBy { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public string? Error { get; set; }
    public string? CorrelationId { get; set; }
    /// <summary>Id of the event whose handling produced this one (automation chains).</summary>
    public Guid? CausationId { get; set; }
    /// <summary>How many automation hops produced this event; used to stop trigger loops.</summary>
    public int ChainDepth { get; set; }
    public Guid? ActorId { get; set; }
    public ActorType ActorType { get; set; } = ActorType.System;
}

/// <summary>Per-handler completion record so a retried event never re-runs handlers that already succeeded.</summary>
public class OutboxDelivery : Entity
{
    public Guid EventId { get; set; }
    public string Handler { get; set; } = "";
    public bool Succeeded { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum JobStatus { Queued, Running, Succeeded, Failed, DeadLetter, Cancelled }

/// <summary>
/// Durable background job with a lease. Workers claim jobs by setting LockedUntil; a job whose lease expired
/// (worker crashed or restarted) is reclaimed by the next worker. Handlers must be idempotent.
/// </summary>
public class BackgroundJob : Entity
{
    public Guid? TenantId { get; set; }
    public string Type { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public int Priority { get; set; }
    public DateTime RunAt { get; set; }
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 6;
    public string? LockedBy { get; set; }
    public DateTime? LockedUntil { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
    /// <summary>Unique when set: enqueueing the same key twice is a no-op.</summary>
    public string? IdempotencyKey { get; set; }
    public string? CorrelationId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>State of a recurring task (SLA monitor, mailbox polling…). Lease prevents two hosts running the same tick.</summary>
public class ScheduledTaskState : Entity
{
    public string Name { get; set; } = "";
    public DateTime? LastRunAt { get; set; }
    public DateTime NextRunAt { get; set; }
    public string? LockedBy { get; set; }
    public DateTime? LockedUntil { get; set; }
    public string? LastError { get; set; }
    public long RunCount { get; set; }
}

// ───────────── Configuration lifecycle & compliance ─────────────

public enum ConfigSnapshotKind { Publish, Import, PreRollback, Manual, Rollback }

/// <summary>Full configuration bundle captured at publish/import time so settings can be exported, compared and rolled back.</summary>
public class ConfigurationSnapshot : AuditedEntity
{
    public int Version { get; set; }
    public string Label { get; set; } = "";
    public ConfigSnapshotKind Kind { get; set; }
    public string BundleJson { get; set; } = "{}";
    public string? SectionsJson { get; set; }
    public DateTime? AppliedAt { get; set; }
}

public enum RetentionAction { Archive, Anonymize, Delete }

public class RetentionPolicy : AuditedEntity
{
    public string Name { get; set; } = "";
    /// <summary>Ticket, Message, AuditLog, AiExecution, Attachment, IntegrationLog</summary>
    public string EntityType { get; set; } = "Ticket";
    public RetentionAction Action { get; set; }
    public int AfterDays { get; set; }
    public string? ConditionJson { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? LastRunAt { get; set; }
    public int LastRunAffected { get; set; }
}

/// <summary>Prevents retention jobs and erasure requests from touching the covered records.</summary>
public class LegalHold : AuditedEntity
{
    public string Name { get; set; } = "";
    public string Reason { get; set; } = "";
    public Guid? CustomerId { get; set; }
    public Guid? ContactId { get; set; }
    public string? TicketIdsJson { get; set; }
    /// <summary>Optional date range (ticket creation time) the hold covers; open-ended when null.</summary>
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public DateTime? ReleasedAt { get; set; }
    public Guid? ReleasedById { get; set; }
}

public enum DataRequestType { Export, Erasure }
public enum DataRequestStatus { Pending, Processing, Completed, Rejected, Failed }

public class DataRequest : AuditedEntity
{
    public DataRequestType Type { get; set; }
    public Guid ContactId { get; set; }
    public DataRequestStatus Status { get; set; }
    public string? Notes { get; set; }
    public string? ResultStorageKey { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? RejectionReason { get; set; }
    /// <summary>Erasure requests need an explicit approval before the job runs.</summary>
    public Guid? ApprovedById { get; set; }
    public DateTime? ApprovedAt { get; set; }
    /// <summary>Export download: SHA-256 of the one-time token, its expiry, and when it was used.</summary>
    public string? DownloadTokenHash { get; set; }
    public DateTime? DownloadExpiresAt { get; set; }
    public DateTime? DownloadedAt { get; set; }
    public string? ResultFileName { get; set; }
    public long? ResultSizeBytes { get; set; }
    public string? Error { get; set; }
}

/// <summary>Metadata for every stored file (attachments, exports, recordings). Content lives in IFileStorage under a tenant prefix.</summary>
public class StoredFile : TenantEntity, IHasTimestamps
{
    public string StorageKey { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string Purpose { get; set; } = "attachment";
    public ScanStatus ScanStatus { get; set; } = ScanStatus.Pending;
    public string? ScanDetail { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? UploadedByUserId { get; set; }
    public Guid? UploadedByContactId { get; set; }
}

public enum ScanStatus { Pending, Clean, Infected, Blocked }
