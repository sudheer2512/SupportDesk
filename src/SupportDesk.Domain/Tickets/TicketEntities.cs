namespace SupportDesk.Domain.Tickets;

public enum EntitlementStatus { Unknown, Entitled, NotEntitled, Overridden, Expired }

public enum SlaState { None, OnTrack, AtRisk, Breached, Paused, Met }

public enum Sentiment { Unknown, Positive, Neutral, Negative, Frustrated }

/// <summary>
/// The central support record. Status is a configurable <see cref="TicketStatus"/>; <see cref="StatusCategory"/> is the
/// denormalised lifecycle category used by SLA, metrics and queues. Dates are UTC.
/// </summary>
public class Ticket : AuditedEntity, ISoftDeletable, IConcurrencyVersioned
{
    /// <summary>Per-tenant sequential human-readable number (displayed with the tenant prefix, e.g. #1042).</summary>
    public long Number { get; set; }
    public string Subject { get; set; } = "";
    /// <summary>Sanitised HTML.</summary>
    public string? DescriptionHtml { get; set; }
    public string? DescriptionText { get; set; }
    public Channel Channel { get; set; }
    public Guid? BrandId { get; set; }
    public Guid? ChannelAccountId { get; set; }

    // Customer
    public Guid? CustomerId { get; set; }
    public Guid? ContactId { get; set; }
    public Guid? LocationId { get; set; }

    // Ownership
    public Guid DepartmentId { get; set; }
    public Guid? TeamId { get; set; }
    public Guid? AssigneeId { get; set; }

    // Product context
    public Guid? ProductId { get; set; }
    public Guid? ModuleId { get; set; }
    public Guid? VersionId { get; set; }
    public Guid? InstallationId { get; set; }
    public Guid? AssetId { get; set; }

    // Classification
    public Guid? CategoryId { get; set; }
    public Guid? SubcategoryId { get; set; }
    public Guid? IssueTypeId { get; set; }
    public TicketPriority Priority { get; set; } = TicketPriority.Medium;
    public TicketSeverity? Severity { get; set; }
    public Guid StatusId { get; set; }
    public StatusCategory StatusCategory { get; set; } = StatusCategory.Open;
    public string? Substatus { get; set; }
    public string Language { get; set; } = "en";

    // Lifecycle dates
    public DateTime? AssignedAt { get; set; }
    public DateTime? FirstResponseAt { get; set; }
    public DateTime? LastAgentResponseAt { get; set; }
    public DateTime? LastCustomerResponseAt { get; set; }
    public DateTime? FirstResponseDueAt { get; set; }
    public DateTime? NextResponseDueAt { get; set; }
    public DateTime? DueAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public DateTime? ReopenedAt { get; set; }
    public DateTime LastActivityAt { get; set; }

    // SLA
    public Guid? SlaPolicyId { get; set; }
    public SlaState SlaState { get; set; } = SlaState.None;

    // Entitlement (support eligibility)
    public EntitlementStatus EntitlementStatus { get; set; } = EntitlementStatus.Unknown;
    public Guid? ContractId { get; set; }
    public Guid? EntitlementId { get; set; }
    public string? EntitlementNote { get; set; }

    // Relations
    public Guid? ParentTicketId { get; set; }
    public Guid? MergedIntoTicketId { get; set; }

    // Resolution
    public string? ResolutionCode { get; set; }
    public string? RootCause { get; set; }
    public string? ResolutionHtml { get; set; }
    public bool AwaitingCustomerConfirmation { get; set; }
    public DateTime? ConfirmationRequestedAt { get; set; }

    // Satisfaction & AI signals
    public int? CsatScore { get; set; }
    public string? CsatComment { get; set; }
    public Sentiment Sentiment { get; set; } = Sentiment.Unknown;
    public double? SentimentScore { get; set; }
    public string? AiSummary { get; set; }
    public DateTime? AiSummaryAt { get; set; }

    // Effort & cost
    public int TimeSpentMinutes { get; set; }
    public int BillableMinutes { get; set; }
    public decimal SupportCost { get; set; }

    // Counters
    public int ReopenCount { get; set; }
    public int AgentReplyCount { get; set; }
    public int CustomerReplyCount { get; set; }
    public int AssignmentCount { get; set; }
    /// <summary>Highest thread sequence number (collision detection: replies since the agent last loaded the ticket).</summary>
    public int LastThreadSeq { get; set; }

    // Escalation
    public bool IsEscalated { get; set; }
    public int EscalationLevel { get; set; }

    // Blueprint workflow
    public Guid? WorkflowId { get; set; }
    public Guid? WorkflowVersionId { get; set; }
    public string? WorkflowStateKey { get; set; }

    /// <summary>Custom field values keyed by field key: {"erp_module":"Finance","go_live":"2026-11-01"}.</summary>
    public string CustomFieldsJson { get; set; } = "{}";

    public bool IsSpam { get; set; }
    public bool IsArchived { get; set; }
    public DateTime? ArchivedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public List<TicketTag> Tags { get; set; } = [];
}

/// <summary>Configurable ticket status. Each maps to a lifecycle category; transitions can be restricted.</summary>
public class TicketStatus : AuditedEntity, IConcurrencyVersioned
{
    public byte[] RowVersion { get; set; } = [];
    public string Name { get; set; } = "";
    public StatusCategory Category { get; set; }
    public string Color { get; set; } = "#64748b";
    public int SortOrder { get; set; }
    /// <summary>Default status for new tickets (exactly one per tenant).</summary>
    public bool IsDefault { get; set; }
    /// <summary>Time in this status does not count against SLA resolution targets.</summary>
    public bool PausesSla { get; set; }
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Label shown to customers in the portal (internal names stay private).</summary>
    public string? CustomerLabel { get; set; }
    /// <summary>Allowed next status ids; empty = any.</summary>
    public string AllowedNextJson { get; set; } = "[]";
    /// <summary>Allowed substatus values, e.g. ["Customer","Third party","Engineering"].</summary>
    public string SubstatusesJson { get; set; } = "[]";
}

public class TicketCategory : AuditedEntity
{
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = "";
    public Guid? DepartmentId { get; set; }
    public bool VisibleInPortal { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

public class IssueType : AuditedEntity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

/// <summary>Generic tenant pick-lists (resolution codes, root causes, close reasons, time activity types…).</summary>
public class LookupValue : AuditedEntity
{
    public string Type { get; set; } = "";
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public enum ThreadKind
{
    CustomerMessage, AgentReply, InternalNote, HandoverNote, Forward, ChatTranscript, CallLog, SystemMessage, AiDraft
}

public enum AuthorType { Customer, Agent, System, Bot, Ai }

/// <summary>One entry of the ticket conversation (public message or internal note). Seq is per-ticket and gap-free.</summary>
public class TicketThread : TenantEntity, IHasTimestamps
{
    public Guid TicketId { get; set; }
    public int Seq { get; set; }
    public ThreadKind Kind { get; set; }
    public Visibility Visibility { get; set; } = Visibility.Public;
    public Channel Channel { get; set; }
    public MessageDirection Direction { get; set; }
    public AuthorType AuthorType { get; set; }
    public Guid? AuthorUserId { get; set; }
    public Guid? AuthorContactId { get; set; }
    public string? AuthorName { get; set; }
    public string? FromAddress { get; set; }
    public string? ToJson { get; set; }
    public string? CcJson { get; set; }
    public string? BccJson { get; set; }
    public string? Subject { get; set; }
    public string? BodyHtml { get; set; }
    public string? BodyText { get; set; }
    /// <summary>Channel message behind this entry (inbound or outbound).</summary>
    public Guid? MessageId { get; set; }
    public Guid? ConversationId { get; set; }
    /// <summary>Channel-specific metadata (WhatsApp template, call duration, chat transcript ids…).</summary>
    public string? ChannelMetadataJson { get; set; }
    public string? DeliveryStatus { get; set; }
    public Sentiment Sentiment { get; set; } = Sentiment.Unknown;
    public bool IsRedacted { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? EditedAt { get; set; }
    /// <summary>Client-supplied idempotency key (mobile offline sync, retries).</summary>
    public string? ClientRequestId { get; set; }
}

/// <summary>Immutable activity history of a ticket (field changes, assignment, SLA, automation, AI…).</summary>
public class TicketEvent : TenantEntity, IAppendOnly
{
    public Guid TicketId { get; set; }
    public string Type { get; set; } = "";
    public ActorType ActorType { get; set; }
    public Guid? ActorId { get; set; }
    public string? ActorName { get; set; }
    public DateTime OccurredAt { get; set; }
    /// <summary>[{"field":"priority","from":"Medium","to":"High"}]</summary>
    public string? ChangesJson { get; set; }
    public string? DetailsJson { get; set; }
    public string? Summary { get; set; }
    public Guid? RuleId { get; set; }
    public string? CorrelationId { get; set; }
}

public class TicketAttachment : TenantEntity, IHasTimestamps
{
    public Guid TicketId { get; set; }
    public Guid? ThreadId { get; set; }
    public Guid FileId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public Visibility Visibility { get; set; } = Visibility.Public;
    public bool IsInline { get; set; }
    public string? ContentId { get; set; }
    public Guid? UploadedByUserId { get; set; }
    public Guid? UploadedByContactId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum TicketLinkType { Related, Duplicate, Blocks, CausedBy, SplitFrom, ClonedFrom }

public class TicketLink : TenantEntity
{
    public Guid SourceTicketId { get; set; }
    public Guid TargetTicketId { get; set; }
    public TicketLinkType LinkType { get; set; }
    public Guid? CreatedById { get; set; }
    public DateTime CreatedAt { get; set; }
}

public enum WatcherKind { Watcher, Collaborator, Specialist }

public class TicketWatcher : TenantEntity
{
    public Guid TicketId { get; set; }
    public Guid UserId { get; set; }
    public WatcherKind Kind { get; set; }
    public Guid? AddedById { get; set; }
    public DateTime AddedAt { get; set; }
    /// <summary>Temporary participation (specialists) ends automatically.</summary>
    public DateTime? ExpiresAt { get; set; }
}

public enum TaskItemStatus { Open, InProgress, Done, Cancelled }

/// <summary>Ticket task, subtask (ParentTaskId) or checklist item.</summary>
public class TicketTask : AuditedEntity
{
    public Guid TicketId { get; set; }
    public Guid? ParentTaskId { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public bool IsChecklistItem { get; set; }
    public Guid? AssigneeId { get; set; }
    public DateTime? DueAt { get; set; }
    public TaskItemStatus Status { get; set; } = TaskItemStatus.Open;
    public DateTime? CompletedAt { get; set; }
    public Guid? CompletedById { get; set; }
    public int SortOrder { get; set; }
}

public class Tag : AuditedEntity
{
    public string Name { get; set; } = "";
    public string? Color { get; set; }
}

public class TicketTag : TenantEntity
{
    public Guid TicketId { get; set; }
    public Guid TagId { get; set; }
    public Tag? Tag { get; set; }
}

public enum DraftKind { Reply, Note }

/// <summary>Auto-saved reply/note draft per agent per ticket.</summary>
public class TicketDraft : TenantEntity
{
    public Guid TicketId { get; set; }
    public Guid UserId { get; set; }
    public DraftKind Kind { get; set; }
    public string? BodyHtml { get; set; }
    public string? ToJson { get; set; }
    public string? CcJson { get; set; }
    public Guid? TemplateId { get; set; }
    /// <summary>Thread seq the agent had seen when drafting (collision detection).</summary>
    public int BasedOnSeq { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum TemplateVisibility { Personal, Team, Department, Global }

/// <summary>Reply template (canned response); ActionsJson turns it into a macro (set status, add tag…).</summary>
public class ReplyTemplate : AuditedEntity
{
    public string Name { get; set; } = "";
    public string? Category { get; set; }
    public string? Subject { get; set; }
    public string BodyHtml { get; set; } = "";
    public TemplateVisibility Visibility { get; set; } = TemplateVisibility.Global;
    public Guid? OwnerUserId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? TeamId { get; set; }
    public string ActionsJson { get; set; } = "[]";
    public int UsageCount { get; set; }
    public bool IsActive { get; set; } = true;
}

public enum FollowUpStatus { Pending, Fired, Dismissed, Cancelled }

public class TicketFollowUp : AuditedEntity
{
    public Guid TicketId { get; set; }
    public Guid UserId { get; set; }
    public DateTime RemindAt { get; set; }
    public string? Note { get; set; }
    public FollowUpStatus Status { get; set; } = FollowUpStatus.Pending;
    public DateTime? FiredAt { get; set; }
}

/// <summary>Reference to a record in another system (ERP invoice, order, CRM case…) that originated or relates to a ticket.</summary>
public class TicketExternalReference : TenantEntity
{
    public Guid TicketId { get; set; }
    /// <summary>WingsErp, WingsBooks, Crm, …</summary>
    public string System { get; set; } = "";
    /// <summary>SalesInvoice, SalesOrder, Receipt, Delivery, AmcContract, …</summary>
    public string RecordType { get; set; } = "";
    public Guid? RecordId { get; set; }
    public string? RecordNumber { get; set; }
    public string? Url { get; set; }
    public string? ContextJson { get; set; }
    public bool IsOrigin { get; set; }
    public DateTime CreatedAt { get; set; }
}

public enum EngineeringProvider { AzureDevOps, GitHub, Jira, Local }
public enum EngineeringItemType { Bug, Feature, Task, Changeset, PullRequest, Release, Deployment }

/// <summary>Link between a support ticket and an engineering work item / changeset / release. The snapshot keeps history if the item moves or closes.</summary>
public class EngineeringLink : AuditedEntity
{
    public Guid TicketId { get; set; }
    public EngineeringProvider Provider { get; set; }
    public Guid? IntegrationId { get; set; }
    public EngineeringItemType ItemType { get; set; }
    public string ExternalId { get; set; } = "";
    public string? Url { get; set; }
    public string Title { get; set; } = "";
    public string? State { get; set; }
    public string? AssignedTo { get; set; }
    public string? IterationPath { get; set; }
    public string? FixVersion { get; set; }
    public bool IsResolved { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public string? SnapshotJson { get; set; }
    public bool CustomerNotified { get; set; }
}

public enum ApprovalStatus { Pending, Approved, Rejected, Cancelled, Expired }

/// <summary>Formal approval request (transfers, escalations, blueprint transitions, AI actions, custom).</summary>
public class ApprovalRequest : AuditedEntity
{
    public string Type { get; set; } = "";
    public string EntityType { get; set; } = "Ticket";
    public Guid EntityId { get; set; }
    public Guid? TicketId { get; set; }
    public Guid RequestedById { get; set; }
    public Guid? ApproverUserId { get; set; }
    public Guid? ApproverRoleId { get; set; }
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public string? Reason { get; set; }
    public string? DecisionNote { get; set; }
    public Guid? DecidedById { get; set; }
    public DateTime? DecidedAt { get; set; }
    /// <summary>What to apply once approved, e.g. {"action":"transfer","departmentId":"…"}.</summary>
    public string PayloadJson { get; set; } = "{}";
    public DateTime? ExpiresAt { get; set; }
}

public enum ViewVisibility { Personal, Shared }

/// <summary>Saved ticket/report view: filters, columns and sorting.</summary>
public class SavedView : AuditedEntity
{
    /// <summary>tickets, customers, contacts, report:&lt;key&gt;</summary>
    public string Scope { get; set; } = "tickets";
    public string Name { get; set; } = "";
    public Guid? OwnerUserId { get; set; }
    public ViewVisibility Visibility { get; set; } = ViewVisibility.Personal;
    public string FilterJson { get; set; } = "{}";
    public string? ColumnsJson { get; set; }
    public string? SortJson { get; set; }
    public bool IsDefault { get; set; }
    public int SortOrder { get; set; }
}

public class SatisfactionResponse : TenantEntity
{
    public Guid TicketId { get; set; }
    public Guid? ContactId { get; set; }
    public Guid? AgentUserId { get; set; }
    public string TokenHash { get; set; } = "";
    public int? Rating { get; set; }
    public string? Comment { get; set; }
    public Channel Channel { get; set; }
    public DateTime SentAt { get; set; }
    public DateTime? RespondedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
