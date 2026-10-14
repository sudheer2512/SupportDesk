namespace SupportDesk.Domain.Automation;

public static class RuleTriggers
{
    public const string TicketCreated = "ticket.created";
    public const string TicketUpdated = "ticket.updated";
    public const string StatusChanged = "ticket.status_changed";
    public const string TicketAssigned = "ticket.assigned";
    public const string ReplyAdded = "ticket.reply_added";
    public const string CustomerReplied = "ticket.customer_replied";
    public const string NoteAdded = "ticket.note_added";
    public const string SlaWarning = "sla.warning";
    public const string SlaBreached = "sla.breached";
    public const string Scheduled = "scheduled";
    public const string CustomerActivity = "customer.activity";
    public const string Webhook = "webhook";

    public static readonly string[] All =
    [
        TicketCreated, TicketUpdated, StatusChanged, TicketAssigned, ReplyAdded, CustomerReplied, NoteAdded,
        SlaWarning, SlaBreached, Scheduled, CustomerActivity, Webhook
    ];
}

/// <summary>
/// Trigger → Conditions → Actions automation rule. The executable definition lives in versioned
/// <see cref="RuleVersion"/> rows; only the published version runs.
/// </summary>
public class AutomationRule : AuditedEntity, IConcurrencyVersioned
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Trigger { get; set; } = RuleTriggers.TicketCreated;
    public bool IsActive { get; set; } = true;
    public int ExecutionOrder { get; set; }
    /// <summary>When this rule matches, later rules for the same event are skipped.</summary>
    public bool StopProcessing { get; set; }
    public Guid? PublishedVersionId { get; set; }
    public int LatestVersionNumber { get; set; }
    /// <summary>For scheduled rules: interval in minutes between runs.</summary>
    public int? ScheduleIntervalMinutes { get; set; }
    public DateTime? NextScheduledRunAt { get; set; }
    /// <summary>For webhook-triggered rules: SHA-256 of the inbound webhook secret.</summary>
    public string? WebhookSecretHash { get; set; }
    public string? WebhookKey { get; set; }
    public long ExecutionCount { get; set; }
    public DateTime? LastExecutedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public enum VersionState { Draft, Published, Superseded }

/// <summary>
/// Immutable rule definition. DefinitionJson = {"trigger":{…},"conditions":{logic,conditions[],groups[]},"actions":[{type,params}]}.
/// </summary>
public class RuleVersion : TenantEntity
{
    public Guid RuleId { get; set; }
    public int VersionNumber { get; set; }
    public string DefinitionJson { get; set; } = "{}";
    public VersionState State { get; set; } = VersionState.Draft;
    public string? ChangeNote { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? CreatedById { get; set; }
    public DateTime? PublishedAt { get; set; }
    public Guid? PublishedById { get; set; }
}

public enum RuleExecutionStatus { NotMatched, Succeeded, PartiallyFailed, Failed, SkippedLoop, Simulated }

/// <summary>History of one rule evaluation. Unique (RuleVersionId, EventId) gives idempotency for retried events.</summary>
public class RuleExecution : TenantEntity
{
    public Guid RuleId { get; set; }
    public Guid RuleVersionId { get; set; }
    public Guid EventId { get; set; }
    public string Trigger { get; set; } = "";
    public string EntityType { get; set; } = "Ticket";
    public Guid EntityId { get; set; }
    public RuleExecutionStatus Status { get; set; }
    public string? ConditionTraceJson { get; set; }
    public string? ActionResultsJson { get; set; }
    public string? Error { get; set; }
    public int ChainDepth { get; set; }
    public int Attempt { get; set; } = 1;
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
    public string? CorrelationId { get; set; }
}

/// <summary>Blueprint-style state-transition workflow for tickets (per department and/or category).</summary>
public class Workflow : AuditedEntity, IConcurrencyVersioned
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? CategoryId { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid? PublishedVersionId { get; set; }
    public int LatestVersionNumber { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// DefinitionJson = {"states":[{"key","name","statusId","x","y","isInitial","isFinal"}],
/// "transitions":[{"key","name","from","to","allowedRoleIds","requiredFields","validation":{conditionGroup},
/// "approval":{"roleId"|"userId"},"actions":[…],"requireComment":bool}]}
/// </summary>
public class WorkflowVersion : TenantEntity
{
    public Guid WorkflowId { get; set; }
    public int VersionNumber { get; set; }
    public string DefinitionJson { get; set; } = "{}";
    public VersionState State { get; set; } = VersionState.Draft;
    public string? ChangeNote { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? CreatedById { get; set; }
    public DateTime? PublishedAt { get; set; }
    public Guid? PublishedById { get; set; }
}

public enum WebhookStatus { Active, Disabled, Failing }

/// <summary>Outbound webhook subscription (signed with HMAC-SHA256 of the payload).</summary>
public class Webhook : AuditedEntity
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string SecretProtected { get; set; } = "";
    /// <summary>Subscribed event types, e.g. ["ticket.created","ticket.status_changed"]; ["*"] = all.</summary>
    public string EventsJson { get; set; } = "[]";
    public WebhookStatus Status { get; set; } = WebhookStatus.Active;
    public int ConsecutiveFailures { get; set; }
    public DateTime? LastDeliveryAt { get; set; }
    public int? LastStatusCode { get; set; }
}

public class WebhookDelivery : TenantEntity
{
    public Guid WebhookId { get; set; }
    public Guid EventId { get; set; }
    public string EventType { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public DeliveryStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public int? ResponseCode { get; set; }
    public string? ResponseExcerpt { get; set; }
    public int? DurationMs { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
}
