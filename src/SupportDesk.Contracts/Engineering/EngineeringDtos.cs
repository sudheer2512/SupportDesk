using SupportDesk.Contracts.Common;

namespace SupportDesk.Contracts.Engineering;

// ───────────────────────────── Tracker connections (admin.integrations) ─────────────────────────────

/// <summary>
/// A configured engineering tracker: Azure DevOps Services/Server (TFS), GitHub (github.com or Enterprise Server) or the
/// built-in Local tracker. Secrets are write-only (<see cref="HasToken"/> etc.); <see cref="WebhookSecret"/> is returned
/// only once, right after it is generated (create / rotate).
/// </summary>
public sealed record EngineeringTrackerDto(
    Guid Id,
    string Provider,
    string Name,
    bool IsActive,
    string Status,
    string? BaseUrl,
    string? Project,
    string? Owner,
    string? Repository,
    string AuthType,
    bool HasToken,
    bool HasAppPrivateKey,
    string? AppId,
    string? InstallationId,
    string DefaultItemType,
    IReadOnlyDictionary<string, string> TypeMap,
    TrackerFieldMapDto Fields,
    IReadOnlyList<string> ResolvedStates,
    IReadOnlyList<string> ReleasedStates,
    int PollIntervalMinutes,
    string? WebhookUrl,
    bool HasWebhookSecret,
    string? WebhookSecret,
    DateTime? LastSyncAt,
    string? LastError,
    int LinkedItemCount,
    string RowVersion);

/// <summary>Field mappings. Azure DevOps uses reference names (Microsoft.VSTS.TCM.ReproSteps…); GitHub uses labels and milestones.</summary>
public sealed record TrackerFieldMapDto(
    string? ReproStepsField,
    string? DescriptionField,
    string? SeverityField,
    string? PriorityField,
    string? FixVersionField,
    string? CustomerImpactField,
    string? DefaultAreaPath,
    string? DefaultIterationPath,
    IReadOnlyList<string> DefaultTags,
    string? SeverityLabelPrefix,
    string? PriorityLabelPrefix);

public sealed record SaveEngineeringTrackerRequest
{
    /// <summary>AzureDevOps | GitHub | Local (fixed after creation).</summary>
    public string Provider { get; init; } = "Local";
    public string? Name { get; init; }
    public bool IsActive { get; init; } = true;
    /// <summary>ADO: collection/organization URL (https://dev.azure.com/org, https://tfs.example.com/tfs/DefaultCollection). GitHub: API URL (https://api.github.com).</summary>
    public string? BaseUrl { get; init; }
    public string? Project { get; init; }
    public string? Owner { get; init; }
    public string? Repository { get; init; }
    /// <summary>ADO: Pat | OAuth. GitHub: Token | App.</summary>
    public string? AuthType { get; init; }
    /// <summary>PAT / OAuth or GitHub token. Write-only: null keeps the stored value, "" clears it.</summary>
    public string? Token { get; init; }
    /// <summary>GitHub App private key (PEM). Write-only like <see cref="Token"/>.</summary>
    public string? AppPrivateKey { get; init; }
    public string? AppId { get; init; }
    public string? InstallationId { get; init; }
    public string? DefaultItemType { get; init; }
    public Dictionary<string, string>? TypeMap { get; init; }
    public TrackerFieldMapDto? Fields { get; init; }
    public List<string>? ResolvedStates { get; init; }
    public List<string>? ReleasedStates { get; init; }
    public int PollIntervalMinutes { get; init; } = 15;
    public string? RowVersion { get; init; }
}

public sealed record TrackerProjectDto(string Id, string Name, string? Url);

public sealed record TrackerTestResultDto(bool Ok, string Message, int DurationMs, IReadOnlyList<TrackerProjectDto> Projects);

public sealed record TrackerSyncResultDto(int Checked, int Updated, int Orphaned, int Failed, string? Error);

/// <summary>One inbound tracker webhook delivery (idempotency log).</summary>
public sealed record EngineeringDeliveryDto(Guid Id, string Provider, string DeliveryId, string EventType, string? ExternalId, string Status, string? Detail,
    DateTime ReceivedAt);

/// <summary>Tenant-wide engineering behaviour (auto-notify rules, escalation substatus).</summary>
public sealed record EngineeringSettingsDto(
    bool AddNoteOnResolved,
    bool AddNoteOnReleased,
    bool NotifyAssignee,
    bool NotifyCustomerOnRelease,
    Guid? ReleaseReplyTemplateId,
    bool SetEngineeringSubstatus,
    Guid? EscalationStatusId,
    Guid? DefaultTrackerId,
    IReadOnlyList<IdName> ReplyTemplates,
    IReadOnlyList<IdName> EngineeringStatuses);

public sealed record SaveEngineeringSettingsRequest(
    bool AddNoteOnResolved,
    bool AddNoteOnReleased,
    bool NotifyAssignee,
    bool NotifyCustomerOnRelease,
    Guid? ReleaseReplyTemplateId,
    bool SetEngineeringSubstatus,
    Guid? EscalationStatusId,
    Guid? DefaultTrackerId);

// ───────────────────────────── Ticket panel ─────────────────────────────

public sealed record ActiveTrackerDto(Guid Id, string Provider, string Name, string? Project, IReadOnlyList<string> ItemTypes, string DefaultItemType,
    bool SupportsAssignee, bool IsDefault);

public sealed record EngineeringArtifactDto(string Kind, string Id, string? Title, string? Url, string? State);

/// <summary>A ticket's link to a work item with the item's last known state (kept even when the item is deleted or moved: orphaned).</summary>
public sealed record EngineeringLinkViewDto(
    Guid Id,
    Guid TicketId,
    Guid? ItemId,
    Guid? TrackerId,
    string TrackerName,
    string Provider,
    string ItemType,
    string ExternalId,
    string DisplayId,
    string? Url,
    string Title,
    string? State,
    string? AssignedTo,
    string? IterationPath,
    string? FixVersion,
    string? ReleaseName,
    DateTime? ReleasedAt,
    bool IsResolved,
    bool IsReleased,
    bool IsOrphaned,
    string? OrphanReason,
    DateTime? LastSyncedAt,
    string? SyncError,
    int TicketCount,
    int CustomerCount,
    bool CustomerNotified,
    DateTime LinkedAt,
    IReadOnlyList<EngineeringArtifactDto> Artifacts);

public sealed record TicketEngineeringDto(IReadOnlyList<EngineeringLinkViewDto> Links, IReadOnlyList<ActiveTrackerDto> Trackers, bool CanLink, bool CanManage);

/// <summary>Suggested work item content built from the ticket (summary, repro steps from the conversation, product context, impact).</summary>
public sealed record EscalationPrefillDto(
    string Title,
    string ReproStepsHtml,
    string? Severity,
    string Priority,
    string? ProductName,
    string? ModuleName,
    string? Version,
    int CustomerImpactCount,
    IReadOnlyList<string> Tags);

public sealed record EscalateToEngineeringRequest
{
    public Guid TrackerId { get; init; }
    public string? ItemType { get; init; }
    public string Title { get; init; } = "";
    public string? ReproStepsHtml { get; init; }
    public string? Severity { get; init; }
    public string? Priority { get; init; }
    public string? AreaPath { get; init; }
    public string? IterationPath { get; init; }
    public List<string>? Tags { get; init; }
    /// <summary>Local tracker only: engineer to assign.</summary>
    public Guid? AssigneeUserId { get; init; }
    /// <summary>Set the ticket substatus "Engineering" (per tenant settings).</summary>
    public bool SetSubstatus { get; init; } = true;
    /// <summary>Retry-safe: the same key never creates a second work item for the ticket.</summary>
    public string? ClientRequestId { get; init; }
}

public sealed record LinkWorkItemRequest
{
    public Guid TrackerId { get; init; }
    /// <summary>Work item id ("123", "#123", "ENG-12") or its URL.</summary>
    public string Reference { get; init; } = "";
    public bool SetSubstatus { get; init; } = true;
}

public sealed record EscalationResultDto(EngineeringLinkViewDto Link, bool SubstatusApplied, string? Message);

public sealed record EngineeringAffectedTicketDto(Guid Id, long Number, string DisplayNumber, string Subject, string Status, string? CustomerName, DateTime CreatedAt);

/// <summary>Tickets linked to the same work item. <see cref="Hidden"/> counts the ones outside the caller's ticket scope.</summary>
public sealed record EngineeringAffectedTicketsDto(int Total, int Hidden, IReadOnlyList<EngineeringAffectedTicketDto> Tickets);

// ───────────────────────────── Local tracker board ─────────────────────────────

public sealed record EngineeringWorkItemDto(
    Guid Id,
    Guid TrackerId,
    int Number,
    string Key,
    string Type,
    string Title,
    string? DescriptionHtml,
    string State,
    string? Severity,
    string Priority,
    Guid? AssigneeUserId,
    string? AssigneeName,
    string? AreaPath,
    string? IterationPath,
    IReadOnlyList<string> Tags,
    string? FixVersion,
    Guid? ReleaseId,
    string? ReleaseName,
    bool IsReleased,
    Guid? ProductId,
    string? ProductName,
    int LinkedTicketCount,
    int CustomerImpactCount,
    double Rank,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ResolvedAt,
    DateTime? ClosedAt,
    string RowVersion);

public sealed record EngineeringWorkItemDetailDto(EngineeringWorkItemDto Item, EngineeringAffectedTicketsDto Tickets);

public sealed record SaveEngineeringWorkItemRequest
{
    public Guid? TrackerId { get; init; }
    public string? Type { get; init; }
    public string Title { get; init; } = "";
    public string? DescriptionHtml { get; init; }
    public string? Severity { get; init; }
    public string? Priority { get; init; }
    public Guid? AssigneeUserId { get; init; }
    public string? AreaPath { get; init; }
    public string? IterationPath { get; init; }
    public List<string>? Tags { get; init; }
    public Guid? ReleaseId { get; init; }
    public Guid? ProductId { get; init; }
    public string? RowVersion { get; init; }
}

/// <summary>Kanban move: new state and optional neighbour (the card is placed before <see cref="BeforeId"/>, or last).</summary>
public sealed record MoveEngineeringWorkItemRequest(string State, Guid? BeforeId = null, string? RowVersion = null);

public sealed record EngineeringReleaseDto(Guid Id, Guid TrackerId, string Name, string? Notes, Guid? ProductId, string? ProductName, DateOnly? PlannedDate, DateTime? ReleasedAt,
    DateTime? DeployedAt, int ItemCount, int OpenItemCount, string RowVersion);

public sealed record SaveEngineeringReleaseRequest
{
    public Guid? TrackerId { get; init; }
    public string Name { get; init; } = "";
    public string? Notes { get; init; }
    public Guid? ProductId { get; init; }
    public DateOnly? PlannedDate { get; init; }
    public string? RowVersion { get; init; }
}

/// <summary>Publishes (releases) a version; <see cref="Deployed"/> also records the deployment.</summary>
public sealed record PublishEngineeringReleaseRequest(bool Deployed = false, string? RowVersion = null);

public sealed record EngineeringAssigneeDto(Guid Id, string Name, string? Title, bool IsLightAgent);

/// <summary>Board header: the local tracker, its states, releases and assignable engineers. Items come from the keyset-paged work item list.</summary>
public sealed record EngineeringBoardDto(
    Guid? TrackerId,
    string? TrackerName,
    IReadOnlyList<string> States,
    IReadOnlyList<EngineeringReleaseDto> Releases,
    IReadOnlyList<EngineeringAssigneeDto> Assignees,
    IReadOnlyList<IdName> Products,
    bool CanManage);
