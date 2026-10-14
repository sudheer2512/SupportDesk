namespace SupportDesk.Contracts.Collaboration;

/// <summary>A Microsoft Teams or Slack incoming-webhook destination. The webhook URL is write-only (<see cref="UrlHint"/> identifies it).</summary>
public sealed record CollaborationChannelDto(
    Guid Id,
    string Provider,
    string Name,
    string UrlHint,
    IReadOnlyList<string> Events,
    IReadOnlyList<Guid> DepartmentIds,
    string MinPriority,
    bool IsActive,
    DateTime? LastSentAt,
    int? LastStatusCode,
    string? LastError,
    string RowVersion);

public sealed record SaveCollaborationChannelRequest
{
    /// <summary>Teams | Slack.</summary>
    public string Provider { get; init; } = "Teams";
    public string Name { get; init; } = "";
    /// <summary>Incoming-webhook URL. Required on create; null keeps the stored URL on update.</summary>
    public string? WebhookUrl { get; init; }
    public List<string> Events { get; init; } = [];
    public List<Guid>? DepartmentIds { get; init; }
    /// <summary>Minimum priority for "new urgent ticket" (Low, Medium, High, Critical).</summary>
    public string? MinPriority { get; init; }
    public bool IsActive { get; init; } = true;
    public string? RowVersion { get; init; }
}

public sealed record CollaborationEventTypeDto(string Key, string Label, string Description);

public sealed record CollaborationCatalogDto(IReadOnlyList<CollaborationEventTypeDto> Events, IReadOnlyList<string> Providers, bool AllowPrivateNetworks);

public sealed record CollaborationSendResultDto(bool Success, int? StatusCode, int DurationMs, string? Error);

public sealed record CollaborationLogDto(Guid Id, string Operation, int? StatusCode, bool Success, int DurationMs, string? ResponseExcerpt, DateTime CreatedAt);

/// <summary>A channel agents can share a ticket to (no secrets).</summary>
public sealed record ShareTargetDto(Guid Id, string Provider, string Name);

public sealed record ShareTicketRequest(Guid ChannelId, string? Comment);
