using SupportDesk.Domain.Tickets;

namespace SupportDesk.Domain.Integration;

/// <summary>Kanban states of the built-in (local) engineering tracker.</summary>
public enum LocalWorkItemState { New, Active, Resolved, Closed }

/// <summary>
/// A work item of the built-in engineering tracker (tenants without Azure DevOps or GitHub). The local tracker is the
/// system of record for these rows; tickets reference them through <see cref="EngineeringItem"/> / <see cref="EngineeringLink"/>
/// exactly like external work items. Soft-deleted items disappear from the tracker and orphan their ticket links.
/// </summary>
public class LocalWorkItem : AuditedEntity, IConcurrencyVersioned, ISoftDeletable
{
    /// <summary>The LocalEngineering <see cref="Integration"/> the item belongs to.</summary>
    public Guid IntegrationId { get; set; }
    /// <summary>Per-tenant sequential number (shown as ENG-42).</summary>
    public int Number { get; set; }
    public EngineeringItemType Type { get; set; } = EngineeringItemType.Bug;
    public string Title { get; set; } = "";
    /// <summary>Sanitised HTML: description / repro steps.</summary>
    public string? DescriptionHtml { get; set; }
    public LocalWorkItemState State { get; set; } = LocalWorkItemState.New;
    public TicketSeverity? Severity { get; set; }
    public TicketPriority Priority { get; set; } = TicketPriority.Medium;
    public Guid? AssigneeUserId { get; set; }
    public string? AreaPath { get; set; }
    public string? IterationPath { get; set; }
    public string TagsJson { get; set; } = "[]";
    /// <summary>Version the fix ships in (copied from the release when one is assigned).</summary>
    public string? FixVersion { get; set; }
    public Guid? ReleaseId { get; set; }
    public Guid? ProductId { get; set; }
    /// <summary>Distinct customers affected by the linked tickets when the item was created (impact at escalation).</summary>
    public int CustomerImpactCount { get; set; }
    /// <summary>Order inside its kanban column (lower first).</summary>
    public double Rank { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>A release of the built-in tracker. Publishing it marks every assigned work item as released in that version.</summary>
public class LocalRelease : AuditedEntity, IConcurrencyVersioned
{
    public Guid IntegrationId { get; set; }
    /// <summary>Version name, e.g. "8.0.1".</summary>
    public string Name { get; set; } = "";
    public string? Notes { get; set; }
    public Guid? ProductId { get; set; }
    /// <summary>Optional link to the product catalogue release (Products module).</summary>
    public Guid? ProductReleaseId { get; set; }
    public DateOnly? PlannedDate { get; set; }
    /// <summary>Null while planned; set when the release is published.</summary>
    public DateTime? ReleasedAt { get; set; }
    /// <summary>Set when the release is reported as deployed (e.g. to the cloud / hosted customers).</summary>
    public DateTime? DeployedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// Last known state of one work item in one tracker, shared by every ticket linked to it: drives sync (polled once per
/// item, not per link), "other tickets affected" counts and orphan handling. When the item is deleted or moved in the
/// tracker it is marked orphaned and keeps the last known data.
/// </summary>
public class EngineeringItem : AuditedEntity, IConcurrencyVersioned
{
    public Guid IntegrationId { get; set; }
    public EngineeringProvider Provider { get; set; }
    public string ExternalId { get; set; } = "";
    public EngineeringItemType ItemType { get; set; } = EngineeringItemType.Bug;
    /// <summary>Type name in the tracker ("Bug", "User Story", "issue").</summary>
    public string? TrackerType { get; set; }
    public string Title { get; set; } = "";
    public string? Url { get; set; }
    /// <summary>Project (Azure DevOps) or owner/repo (GitHub) the item lives in.</summary>
    public string? Project { get; set; }
    public string? State { get; set; }
    public string? AssignedTo { get; set; }
    public string? AreaPath { get; set; }
    public string? IterationPath { get; set; }
    public string? FixVersion { get; set; }
    public string? ReleaseName { get; set; }
    public DateTime? ReleasedAt { get; set; }
    public DateTime? DeployedAt { get; set; }
    public bool IsResolved { get; set; }
    public bool IsReleased { get; set; }
    public bool IsOrphaned { get; set; }
    /// <summary>deleted | moved | not_found</summary>
    public string? OrphanReason { get; set; }
    public DateTime? OrphanedAt { get; set; }
    public DateTime? ExternalChangedAt { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public string? LastSyncError { get; set; }
    /// <summary>Tags/labels, severity, priority, linked PRs/changesets/builds/releases (JSON).</summary>
    public string? SnapshotJson { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public enum EngineeringDeliveryStatus { Processed, Ignored, Failed }

/// <summary>An inbound tracker webhook delivery (Azure DevOps service hook / GitHub webhook), unique per integration + delivery id (idempotency).</summary>
public class EngineeringWebhookDelivery : TenantEntity
{
    public Guid IntegrationId { get; set; }
    public EngineeringProvider Provider { get; set; }
    public string DeliveryId { get; set; } = "";
    public string EventType { get; set; } = "";
    public string? ExternalId { get; set; }
    public EngineeringDeliveryStatus Status { get; set; }
    public string? Detail { get; set; }
    public DateTime ReceivedAt { get; set; }
}

public enum CollaborationProvider { Teams, Slack }

/// <summary>
/// A Microsoft Teams or Slack incoming-webhook destination with the events posted to it (new urgent ticket, SLA breach,
/// escalation, mention, engineering updates). The webhook URL is a secret (AES-GCM encrypted).
/// </summary>
public class CollaborationChannel : AuditedEntity, IConcurrencyVersioned
{
    public CollaborationProvider Provider { get; set; }
    public string Name { get; set; } = "";
    public string WebhookUrlProtected { get; set; } = "";
    /// <summary>Non-secret display hint, e.g. "hooks.slack.com/…/x7Qa".</summary>
    public string UrlHint { get; set; } = "";
    public string EventsJson { get; set; } = "[]";
    /// <summary>Only tickets of these departments (empty = all).</summary>
    public string DepartmentIdsJson { get; set; } = "[]";
    /// <summary>Minimum priority for "new urgent ticket".</summary>
    public TicketPriority MinPriority { get; set; } = TicketPriority.Critical;
    public bool IsActive { get; set; } = true;
    public DateTime? LastSentAt { get; set; }
    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
