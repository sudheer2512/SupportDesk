using System.Text.Json;
using SupportDesk.Contracts.Common;

namespace SupportDesk.Contracts.Tickets;

// Enum-valued fields are strings on the wire:
//   channel:   Email, Portal, WebForm, Chat, WhatsApp, Phone, Sms, Facebook, Instagram, MobileApp, Api, WalkIn, Erp, Internal
//   priority:  Low, Medium, High, Critical       severity: Cosmetic, Minor, Major, Critical
//   statusCategory: Open, Assigned, InProgress, Waiting, Resolved, Closed
//   slaState:  None, OnTrack, AtRisk, Breached, Paused, Met
//   entitlementStatus: Unknown, Entitled, NotEntitled, Overridden, Expired
//   sentiment: Unknown, Positive, Neutral, Negative, Frustrated

public sealed record TicketListItemDto(
    Guid Id,
    long Number,
    string DisplayNumber,
    string Subject,
    string Channel,
    string Priority,
    string? Severity,
    Guid StatusId,
    string Status,
    string StatusCategory,
    string StatusColor,
    Guid DepartmentId,
    string DepartmentName,
    Guid? TeamId,
    string? TeamName,
    Guid? AssigneeId,
    string? AssigneeName,
    Guid? CustomerId,
    string? CustomerName,
    string? CustomerTier,
    Guid? ContactId,
    string? ContactName,
    string? ProductName,
    string? CategoryName,
    string SlaState,
    DateTime? DueAt,
    DateTime? FirstResponseDueAt,
    DateTime CreatedAt,
    DateTime LastActivityAt,
    DateTime? LastCustomerResponseAt,
    bool IsEscalated,
    string Sentiment,
    int AgentReplyCount,
    int CustomerReplyCount,
    IReadOnlyList<string> Tags,
    bool HasUnreadCustomerReply);

public sealed record TicketDetailDto(
    Guid Id,
    long Number,
    string DisplayNumber,
    string Subject,
    string? DescriptionHtml,
    string Channel,
    Guid? BrandId,
    string? BrandName,
    Guid? CustomerId,
    string? CustomerName,
    string? CustomerTier,
    Guid? ContactId,
    string? ContactName,
    string? ContactEmail,
    string? ContactPhone,
    bool ContactIsVip,
    Guid? LocationId,
    string? LocationName,
    Guid DepartmentId,
    string DepartmentName,
    Guid? TeamId,
    string? TeamName,
    Guid? AssigneeId,
    string? AssigneeName,
    Guid? ProductId,
    string? ProductName,
    Guid? ModuleId,
    string? ModuleName,
    Guid? VersionId,
    string? VersionName,
    Guid? InstallationId,
    string? InstallationName,
    Guid? AssetId,
    string? AssetName,
    Guid? CategoryId,
    string? CategoryName,
    Guid? SubcategoryId,
    string? SubcategoryName,
    Guid? IssueTypeId,
    string? IssueTypeName,
    string Priority,
    string? Severity,
    Guid StatusId,
    string Status,
    string StatusCategory,
    string StatusColor,
    string? Substatus,
    string Language,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? AssignedAt,
    DateTime? FirstResponseAt,
    DateTime? FirstResponseDueAt,
    DateTime? NextResponseDueAt,
    DateTime? DueAt,
    DateTime? ResolvedAt,
    DateTime? ClosedAt,
    DateTime LastActivityAt,
    Guid? SlaPolicyId,
    string? SlaPolicyName,
    string SlaState,
    string EntitlementStatus,
    Guid? ContractId,
    string? ContractName,
    string? EntitlementNote,
    Guid? ParentTicketId,
    long? ParentTicketNumber,
    Guid? MergedIntoTicketId,
    string? ResolutionCode,
    string? RootCause,
    string? ResolutionHtml,
    bool AwaitingCustomerConfirmation,
    int? CsatScore,
    string? CsatComment,
    string Sentiment,
    string? AiSummary,
    int TimeSpentMinutes,
    int BillableMinutes,
    decimal? SupportCost,
    int ReopenCount,
    bool IsEscalated,
    int EscalationLevel,
    int LastThreadSeq,
    Guid? WorkflowId,
    string? WorkflowStateKey,
    JsonElement CustomFields,
    IReadOnlyList<string> Tags,
    IReadOnlyList<WatcherDto> Watchers,
    IReadOnlyList<TicketLinkDto> Links,
    IReadOnlyList<ChildTicketDto> Children,
    IReadOnlyList<ExternalReferenceDto> ExternalReferences,
    TicketLockDto? Lock,
    string RowVersion,
    TicketPermissionsDto Can);

/// <summary>What the current user may do on this ticket (computed server-side from permissions and scope).</summary>
public sealed record TicketPermissionsDto(bool Edit, bool Reply, bool Note, bool Assign, bool Transfer, bool Escalate, bool Resolve,
    bool Reopen, bool Merge, bool Split, bool Delete, bool LogTime, bool ViewCosts, bool OverrideEntitlement);

public sealed record WatcherDto(Guid UserId, string Name, string Kind, DateTime AddedAt, DateTime? ExpiresAt);

public sealed record TicketLinkDto(Guid Id, string LinkType, string Direction, Guid TicketId, long Number, string Subject, string Status);

public sealed record ChildTicketDto(Guid Id, long Number, string Subject, string Status, string StatusCategory, string? AssigneeName);

public sealed record ExternalReferenceDto(Guid Id, string System, string RecordType, Guid? RecordId, string? RecordNumber, string? Url,
    bool IsOrigin, JsonElement? Context);

public sealed record TicketLockDto(Guid UserId, string UserName, DateTime Until);

public sealed record CreateTicketRequest
{
    public string Subject { get; init; } = "";
    public string? DescriptionHtml { get; init; }
    public string Channel { get; init; } = "Internal";
    public Guid? BrandId { get; init; }
    public Guid? CustomerId { get; init; }
    public Guid? ContactId { get; init; }
    /// <summary>Creates or matches a contact by e-mail/phone when ContactId is not given.</summary>
    public NewContactDto? NewContact { get; init; }
    public Guid? LocationId { get; init; }
    public Guid? DepartmentId { get; init; }
    public Guid? TeamId { get; init; }
    public Guid? AssigneeId { get; init; }
    public Guid? ProductId { get; init; }
    public Guid? ModuleId { get; init; }
    public Guid? VersionId { get; init; }
    public Guid? InstallationId { get; init; }
    public Guid? AssetId { get; init; }
    public Guid? CategoryId { get; init; }
    public Guid? SubcategoryId { get; init; }
    public Guid? IssueTypeId { get; init; }
    public string? Priority { get; init; }
    public string? Severity { get; init; }
    public Guid? StatusId { get; init; }
    public Guid? ParentTicketId { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public Dictionary<string, JsonElement>? CustomFields { get; init; }
    public IReadOnlyList<Guid>? AttachmentFileIds { get; init; }
    public ExternalReferenceInput? Origin { get; init; }
    /// <summary>Skip automatic routing (assignment rules).</summary>
    public bool SkipRouting { get; init; }
    /// <summary>Idempotency key (mobile/offline sync, API retries): the same key returns the same ticket.</summary>
    public string? ClientRequestId { get; init; }
}

public sealed record NewContactDto(string? FullName, string? Email, string? Phone, Guid? CustomerId);

public sealed record ExternalReferenceInput(string System, string RecordType, Guid? RecordId, string? RecordNumber, string? Url,
    JsonElement? Context);

/// <summary>Partial update: only non-null properties change. RowVersion (base64) is required for optimistic concurrency.</summary>
public sealed record UpdateTicketRequest
{
    public string RowVersion { get; init; } = "";
    public string? Subject { get; init; }
    public string? DescriptionHtml { get; init; }
    public Guid? CustomerId { get; init; }
    public Guid? ContactId { get; init; }
    public Guid? LocationId { get; init; }
    public Guid? ProductId { get; init; }
    public Guid? ModuleId { get; init; }
    public Guid? VersionId { get; init; }
    public Guid? InstallationId { get; init; }
    public Guid? AssetId { get; init; }
    public Guid? CategoryId { get; init; }
    public Guid? SubcategoryId { get; init; }
    public Guid? IssueTypeId { get; init; }
    public string? Priority { get; init; }
    public string? Severity { get; init; }
    public string? Language { get; init; }
    public Dictionary<string, JsonElement>? CustomFields { get; init; }
    /// <summary>Explicitly clear these fields (e.g. ["productId","installationId"]).</summary>
    public IReadOnlyList<string>? Clear { get; init; }
}

public sealed record AssignTicketRequest(Guid? AssigneeId, Guid? TeamId, string? Note);

public sealed record TransferTicketRequest(Guid DepartmentId, Guid? TeamId, Guid? AssigneeId, string? Reason);

public sealed record ChangeStatusRequest(Guid StatusId, string? Substatus, string? Comment, string? RowVersion);

public sealed record EscalateTicketRequest(string Reason, Guid? EscalateToUserId, bool RaisePriority);

public sealed record ResolveTicketRequest(string? ResolutionCode, string? RootCause, string? ResolutionHtml, bool NotifyCustomer = true,
    bool RequestConfirmation = false);

public sealed record ReopenTicketRequest(string Reason);

public sealed record MergeTicketsRequest(IReadOnlyList<Guid> SourceTicketIds, string? Note);

public sealed record SplitTicketRequest(IReadOnlyList<Guid> ThreadIds, string Subject, bool LinkAsChild = true);

public sealed record LinkTicketRequest(Guid TargetTicketId, string LinkType);

public sealed record OverrideEntitlementRequest(string Reason, Guid? ContractId);

public sealed record SetTagsRequest(IReadOnlyList<string> Tags);

public sealed record BulkTicketRequest(IReadOnlyList<Guid> TicketIds, string Action, JsonElement? Parameters);

/// <summary>Bulk actions: assign {assigneeId,teamId}, status {statusId}, priority {priority}, tags_add {tags}, tags_remove {tags},
/// transfer {departmentId}, merge {targetId}, delete, spam, close, watch.</summary>
public static class BulkActions
{
    public const string Assign = "assign";
    public const string Status = "status";
    public const string Priority = "priority";
    public const string TagsAdd = "tags_add";
    public const string TagsRemove = "tags_remove";
    public const string Transfer = "transfer";
    public const string Delete = "delete";
    public const string Spam = "spam";
    public const string Close = "close";
    public const string Watch = "watch";
}

// ───── Conversation ─────

public sealed record ThreadDto(
    Guid Id,
    int Seq,
    string Kind,
    string Visibility,
    string Channel,
    string Direction,
    string AuthorType,
    Guid? AuthorUserId,
    Guid? AuthorContactId,
    string? AuthorName,
    string? FromAddress,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    string? Subject,
    string? BodyHtml,
    string? BodyText,
    string? DeliveryStatus,
    string Sentiment,
    JsonElement? ChannelMetadata,
    IReadOnlyList<AttachmentDto> Attachments,
    DateTime CreatedAt,
    DateTime? EditedAt);

public sealed record TicketEventDto(Guid Id, string Type, string ActorType, Guid? ActorId, string? ActorName, DateTime OccurredAt,
    string? Summary, IReadOnlyList<FieldChangeDto> Changes, JsonElement? Details);

/// <summary>One chronological timeline item: either a conversation entry or an activity event.</summary>
public sealed record TimelineItemDto(string ItemType, DateTime At, ThreadDto? Thread, TicketEventDto? Event);

public sealed record ReplyRequest
{
    public string BodyHtml { get; init; } = "";
    public IReadOnlyList<string>? To { get; init; }
    public IReadOnlyList<string>? Cc { get; init; }
    public IReadOnlyList<string>? Bcc { get; init; }
    public IReadOnlyList<Guid>? AttachmentFileIds { get; init; }
    /// <summary>Change status together with the reply (e.g. Waiting / Resolved).</summary>
    public Guid? StatusId { get; init; }
    /// <summary>Last thread seq the agent had seen. If customers/agents posted since, the API answers 409 reply_collision unless Force.</summary>
    public int? BasedOnSeq { get; init; }
    public bool Force { get; init; }
    /// <summary>Send through this channel (defaults to the ticket's channel).</summary>
    public string? Channel { get; init; }
    public Guid? TemplateId { get; init; }
    public string? ClientRequestId { get; init; }
}

public sealed record NoteRequest
{
    public string BodyHtml { get; init; } = "";
    /// <summary>note or handover</summary>
    public string Kind { get; init; } = "note";
    public IReadOnlyList<Guid>? MentionUserIds { get; init; }
    public IReadOnlyList<Guid>? AttachmentFileIds { get; init; }
    public string? ClientRequestId { get; init; }
}

public sealed record ReplyCollisionDto(int CurrentSeq, IReadOnlyList<ThreadDto> NewEntries);

public sealed record DraftDto(string Kind, string? BodyHtml, IReadOnlyList<string> To, IReadOnlyList<string> Cc, Guid? TemplateId,
    int BasedOnSeq, DateTime UpdatedAt);

public sealed record SaveDraftRequest(string Kind, string? BodyHtml, IReadOnlyList<string>? To, IReadOnlyList<string>? Cc, Guid? TemplateId,
    int BasedOnSeq);

// ───── Collaboration items ─────

public sealed record AddWatcherRequest(Guid UserId, string Kind = "Watcher", DateTime? ExpiresAt = null);

public sealed record TaskDto(Guid Id, Guid TicketId, Guid? ParentTaskId, string Title, string? Description, bool IsChecklistItem,
    Guid? AssigneeId, string? AssigneeName, DateTime? DueAt, string Status, DateTime? CompletedAt, int SortOrder,
    IReadOnlyList<TaskDto> Subtasks);

public sealed record SaveTaskRequest(string Title, string? Description, bool IsChecklistItem, Guid? ParentTaskId, Guid? AssigneeId,
    DateTime? DueAt, string? Status, int? SortOrder);

public sealed record TimeEntryDto(Guid Id, Guid UserId, string UserName, DateTime StartedAt, int Minutes, bool IsBillable,
    string? ActivityType, string? Notes, decimal? CostAmount, decimal? BillableAmount, string? ContractNumber);

public sealed record LogTimeRequest(int Minutes, DateTime? StartedAt, bool IsBillable, string? ActivityType, string? Notes);

public sealed record FollowUpDto(Guid Id, Guid UserId, string UserName, DateTime RemindAt, string? Note, string Status);

public sealed record CreateFollowUpRequest(DateTime RemindAt, string? Note, Guid? UserId);

// ───── Views ─────

public sealed record SavedViewDto(Guid Id, string Scope, string Name, string Visibility, bool IsMine, JsonElement Filter,
    JsonElement? Columns, JsonElement? Sort, bool IsDefault, int SortOrder, long? Count);

public sealed record SaveViewRequest(string Scope, string Name, string Visibility, JsonElement Filter, JsonElement? Columns,
    JsonElement? Sort, bool IsDefault, int SortOrder);

/// <summary>Query parameters for GET /api/v1/tickets (all optional; lists accept comma-separated values).</summary>
public sealed record TicketQuery
{
    public Guid? ViewId { get; init; }
    public string? Q { get; init; }
    public string? StatusId { get; init; }
    public string? StatusCategory { get; init; }
    public string? Priority { get; init; }
    public string? AssigneeId { get; init; }
    public bool? Unassigned { get; init; }
    public bool? Mine { get; init; }
    public bool? Watching { get; init; }
    public string? DepartmentId { get; init; }
    public string? TeamId { get; init; }
    public Guid? CustomerId { get; init; }
    public Guid? ContactId { get; init; }
    public Guid? ProductId { get; init; }
    public string? Channel { get; init; }
    public string? Tag { get; init; }
    public string? SlaState { get; init; }
    public bool? Overdue { get; init; }
    public bool? Escalated { get; init; }
    public DateTime? CreatedFrom { get; init; }
    public DateTime? CreatedTo { get; init; }
    public string? Sort { get; init; }
    public string? Dir { get; init; }
    public string? Cursor { get; init; }
    public int? Limit { get; init; }
    public bool? IncludeTotal { get; init; }
}

/// <summary>Right-hand context panel of the agent workbench.</summary>
public sealed record TicketContextDto(
    CustomerSummaryDto? Customer,
    ContactSummaryDto? Contact,
    IReadOnlyList<TicketListItemDto> PreviousTickets,
    SlaStatusDto? Sla,
    IReadOnlyList<ContractSummaryDto> Contracts,
    IReadOnlyList<InstallationSummaryDto> Installations,
    IReadOnlyList<ExternalReferenceDto> RelatedTransactions,
    IReadOnlyList<ArticleSuggestionDto> SuggestedArticles,
    IReadOnlyList<AiRecommendationDto> AiRecommendations,
    IReadOnlyList<EngineeringLinkDto> EngineeringLinks);

public sealed record CustomerSummaryDto(Guid Id, string Name, string? Code, string Tier, string? Industry, string? AccountManagerName,
    string? ServiceRegion, int OpenTickets, int TicketsLast90Days, double? CsatAverage, int SlaBreachesLast90Days, string Status);

public sealed record ContactSummaryDto(Guid Id, string FullName, string? Email, string? Phone, string? JobTitle, bool IsVip,
    string PreferredLanguage, string? PreferredChannel, string? LocationName, int OpenTickets);

public sealed record SlaStatusDto(Guid InstanceId, string PolicyName, string Status, IReadOnlyList<SlaMilestoneDto> Milestones);

public sealed record SlaMilestoneDto(Guid Id, string Kind, int Cycle, string Status, int TargetMinutes, bool BusinessHours,
    DateTime StartedAt, DateTime DueAt, DateTime? CompletedAt, DateTime? BreachedAt, double ElapsedMinutes, double RemainingMinutes,
    double PercentElapsed, int EscalationLevel);

public sealed record ContractSummaryDto(Guid Id, string Number, string Name, string Type, string Status, DateOnly StartDate,
    DateOnly EndDate, decimal? PrepaidHours, decimal ConsumedHours, bool IsBillable, bool CoversTicket);

public sealed record InstallationSummaryDto(Guid Id, string Name, string ProductName, string? Version, string Environment,
    string DeploymentMode, string? OperatingSystem, string? DatabaseVersion, string Status);

public sealed record ArticleSuggestionDto(Guid Id, string Title, string? Summary, string Visibility, double Score, string Url);

public sealed record AiRecommendationDto(string Kind, string Title, string Detail, double? Confidence, Guid? ExecutionId,
    JsonElement? Payload);

public sealed record EngineeringLinkDto(Guid Id, string Provider, string ItemType, string ExternalId, string? Url, string Title,
    string? State, string? AssignedTo, string? FixVersion, bool IsResolved, DateTime? LastSyncedAt);
