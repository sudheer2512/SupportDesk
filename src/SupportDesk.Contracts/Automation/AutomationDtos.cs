using System.Text.Json;

namespace SupportDesk.Contracts.Automation;

// ───────────────────────────── Designer catalog ─────────────────────────────

public sealed record TriggerInfoDto(string Key, string Label, string Kind, string Description);

public sealed record ConditionFieldDto(string Field, string Label, string Type);

public sealed record OperatorInfoDto(string Key, string Label, string ValueKind, IReadOnlyList<string> AppliesTo);

public sealed record ActionParamDto(string Name, string Label, string Type, bool Required, string? Description, IReadOnlyList<string>? Options);

public sealed record ActionTypeDto(string Type, string Label, string Category, string Description, IReadOnlyList<ActionParamDto> Params);

/// <summary>Everything the visual rule/blueprint designer needs: triggers, condition fields, operators, action types with parameter schemas.</summary>
public sealed record AutomationCatalogDto(
    IReadOnlyList<TriggerInfoDto> Triggers,
    IReadOnlyList<ConditionFieldDto> Fields,
    IReadOnlyList<OperatorInfoDto> Operators,
    IReadOnlyList<ActionTypeDto> Actions,
    IReadOnlyList<string> TemplatePlaceholders,
    IReadOnlyList<string> BlueprintRequiredFields);

// ───────────────────────────── Rules ─────────────────────────────

public sealed record RuleSummaryDto(
    Guid Id,
    string Name,
    string? Description,
    string Trigger,
    bool IsActive,
    int ExecutionOrder,
    bool StopProcessing,
    Guid? PublishedVersionId,
    int? PublishedVersionNumber,
    int LatestVersionNumber,
    bool HasUnpublishedDraft,
    int? ScheduleIntervalMinutes,
    DateTime? NextScheduledRunAt,
    string? WebhookUrl,
    long ExecutionCount,
    DateTime? LastExecutedAt,
    DateTime UpdatedAt,
    string RowVersion);

public sealed record RuleVersionSummaryDto(Guid Id, int VersionNumber, string State, string? ChangeNote, DateTime CreatedAt, string? CreatedByName,
    DateTime? PublishedAt, string? PublishedByName);

public sealed record RuleVersionDto(Guid Id, int VersionNumber, string State, JsonElement Definition, string? ChangeNote, DateTime CreatedAt,
    string? CreatedByName, DateTime? PublishedAt, string? PublishedByName);

public sealed record RuleDetailDto(
    RuleSummaryDto Rule,
    RuleVersionDto? Published,
    RuleVersionDto? Latest,
    IReadOnlyList<RuleVersionSummaryDto> Versions,
    IReadOnlyList<RuleConflictDto> Conflicts,
    /// <summary>Inbound webhook secret — returned only when it is created or rotated.</summary>
    string? WebhookSecret = null);

/// <summary>
/// Create/update a rule. A non-null <see cref="Definition"/> always creates a new draft version
/// ({"trigger":{"fields":[…]},"conditions":{logic,conditions,groups},"actions":[{type,params}]}). Publish=true publishes it.
/// </summary>
public sealed record SaveRuleRequest
{
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string Trigger { get; init; } = "ticket.created";
    public JsonElement? Definition { get; init; }
    public bool StopProcessing { get; init; }
    public int? ExecutionOrder { get; init; }
    public int? ScheduleIntervalMinutes { get; init; }
    public bool? IsActive { get; init; }
    public bool Publish { get; init; }
    public string? ChangeNote { get; init; }
    /// <summary>Optional optimistic-concurrency token from the last read (base64).</summary>
    public string? RowVersion { get; init; }
}

public sealed record PublishVersionRequest(string? ChangeNote);

public sealed record ReorderRulesRequest(IReadOnlyList<Guid> RuleIds);

public sealed record ValidationIssueDto(string Path, string Message);

public sealed record ValidationResultDto(bool Valid, IReadOnlyList<ValidationIssueDto> Errors, IReadOnlyList<ValidationIssueDto> Warnings);

public sealed record ValidateRuleRequest(string Trigger, JsonElement Definition);

// ───────────────────────────── Simulation ─────────────────────────────

/// <summary>Dry run: a given version (default: latest), or an unsaved definition, against one ticket or the last N tickets.</summary>
public sealed record SimulateRuleRequest
{
    public Guid? VersionId { get; init; }
    public JsonElement? Definition { get; init; }
    public string? Trigger { get; init; }
    public Guid? TicketId { get; init; }
    public int? LastTickets { get; init; }
}

public sealed record ConditionTraceDto(string Field, string Op, string? Expected, string? Actual, bool Result);

public sealed record SimulatedActionDto(int Index, string Type, string Description, bool WouldRun, string? Note);

public sealed record SimulationTicketResultDto(Guid TicketId, long Number, string Subject, bool Matched, IReadOnlyList<ConditionTraceDto> Trace,
    IReadOnlyList<SimulatedActionDto> Actions, string? Note);

public sealed record SimulationResultDto(Guid? RuleId, Guid? VersionId, int? VersionNumber, int Evaluated, int Matched,
    IReadOnlyList<SimulationTicketResultDto> Results);

// ───────────────────────────── Conflicts ─────────────────────────────

/// <summary>Kinds: field_conflict, assignment_conflict, tag_conflict, stop_processing.</summary>
public sealed record RuleConflictDto(string Kind, string Trigger, Guid RuleId, string RuleName, Guid OtherRuleId, string OtherRuleName, string? Field,
    string Explanation);

// ───────────────────────────── Executions ─────────────────────────────

public sealed record ActionResultDto(int Index, string Type, string Status, string? Message, string? Error, bool Transient, int Attempts);

public sealed record RuleExecutionDto(
    Guid Id,
    Guid RuleId,
    string? RuleName,
    Guid RuleVersionId,
    int? VersionNumber,
    Guid EventId,
    string Trigger,
    string EntityType,
    Guid EntityId,
    long? TicketNumber,
    string Status,
    IReadOnlyList<ConditionTraceDto> Trace,
    IReadOnlyList<ActionResultDto> Actions,
    string? Error,
    int ChainDepth,
    int Attempt,
    DateTime StartedAt,
    DateTime CompletedAt,
    string? CorrelationId);

public sealed record ExecutionQuery
{
    public Guid? RuleId { get; init; }
    public Guid? TicketId { get; init; }
    public string? Status { get; init; }
    public string? Trigger { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

// ───────────────────────────── Blueprints ─────────────────────────────

public sealed record BlueprintSummaryDto(
    Guid Id,
    string Name,
    string? Description,
    Guid? DepartmentId,
    string? DepartmentName,
    Guid? CategoryId,
    string? CategoryName,
    bool IsActive,
    Guid? PublishedVersionId,
    int? PublishedVersionNumber,
    int LatestVersionNumber,
    bool HasUnpublishedDraft,
    int ActiveTickets,
    DateTime UpdatedAt,
    string RowVersion);

public sealed record BlueprintDetailDto(BlueprintSummaryDto Blueprint, RuleVersionDto? Published, RuleVersionDto? Latest,
    IReadOnlyList<RuleVersionSummaryDto> Versions);

/// <summary>
/// Create/update a blueprint. Definition: {"states":[{key,name,statusId,substatus,x,y,isInitial,isFinal}],
/// "transitions":[{key,name,from,to,allowedRoleIds,requiredFields,validation,approval:{roleId|userId},actions,requireComment}]}.
/// </summary>
public sealed record SaveBlueprintRequest
{
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public Guid? DepartmentId { get; init; }
    public Guid? CategoryId { get; init; }
    public JsonElement? Definition { get; init; }
    public bool? IsActive { get; init; }
    public bool Publish { get; init; }
    public string? ChangeNote { get; init; }
    public string? RowVersion { get; init; }
}

public sealed record ValidateBlueprintRequest(JsonElement Definition);

public sealed record BlueprintStateDto(string Key, string Name, Guid? StatusId, string? StatusName, string? Substatus, bool IsInitial, bool IsFinal,
    double? X, double? Y);

public sealed record RequiredFieldDto(string Field, string Label, bool Missing);

public sealed record BlueprintTransitionDto(
    string Key,
    string Name,
    string To,
    string ToName,
    bool RequiresComment,
    bool RequiresApproval,
    IReadOnlyList<RequiredFieldDto> RequiredFields,
    IReadOnlyList<string> MissingFields,
    bool ValidationPassed,
    bool CanExecute,
    string? BlockedReason);

public sealed record PendingTransitionDto(Guid ApprovalId, string TransitionKey, string TransitionName, Guid RequestedById, string? RequestedByName,
    DateTime RequestedAt);

/// <summary>The ticket's blueprint position and the transitions available to the caller.</summary>
public sealed record TicketBlueprintDto(
    Guid TicketId,
    Guid WorkflowId,
    string WorkflowName,
    Guid? VersionId,
    int? VersionNumber,
    BlueprintStateDto CurrentState,
    IReadOnlyList<BlueprintTransitionDto> Transitions,
    PendingTransitionDto? PendingApproval,
    IReadOnlyList<BlueprintStateDto> States);

/// <summary>Executes a transition. <see cref="Fields"/> fills mandatory fields as part of the transition ("severity", "cf:key", …).</summary>
public sealed record ExecuteTransitionRequest
{
    public string? Comment { get; init; }
    public Dictionary<string, JsonElement>? Fields { get; init; }
}

/// <summary>Status: completed | pending_approval.</summary>
public sealed record TransitionResultDto(string Status, string StateKey, string StateName, Guid? ApprovalId, IReadOnlyList<ActionResultDto> ActionResults);

// ───────────────────────────── Outbound webhooks ─────────────────────────────

public sealed record WebhookDto(
    Guid Id,
    string Name,
    string Url,
    IReadOnlyList<string> Events,
    string Status,
    int ConsecutiveFailures,
    DateTime? LastDeliveryAt,
    int? LastStatusCode,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    /// <summary>Signing secret — returned only when created or rotated.</summary>
    string? Secret = null);

public sealed record SaveWebhookRequest
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public IReadOnlyList<string> Events { get; init; } = [];
    public bool IsActive { get; init; } = true;
    /// <summary>Optional signing secret (min 16 characters); generated when omitted on create.</summary>
    public string? Secret { get; init; }
}

public sealed record WebhookDeliveryDto(
    Guid Id,
    Guid WebhookId,
    Guid EventId,
    string EventType,
    string Status,
    int Attempts,
    DateTime NextAttemptAt,
    int? ResponseCode,
    string? ResponseExcerpt,
    int? DurationMs,
    DateTime CreatedAt,
    DateTime? DeliveredAt,
    JsonElement? Payload);

public sealed record WebhookEventTypesDto(IReadOnlyList<string> EventTypes);

// ───────────────────────────── Inbound webhook trigger ─────────────────────────────

public sealed record InboundWebhookResultDto(Guid RuleId, Guid TicketId, long TicketNumber, string Status, Guid? ExecutionId, string? Message);
