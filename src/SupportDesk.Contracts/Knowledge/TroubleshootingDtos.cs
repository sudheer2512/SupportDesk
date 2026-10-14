namespace SupportDesk.Contracts.Knowledge;

// ───────────────────────────── Guided troubleshooting ─────────────────────────────

/// <summary>An answer of a question node (or "did not help" path of a solution/article node) leading to another node.</summary>
public sealed record FlowOptionDto(string Label, string Next);

/// <summary>
/// Decision-tree node. Type: question (needs options), solution (text answer), article (links a KB article),
/// escalate (offer a ticket). Solution/article nodes may have options ("Still not working" → next node).
/// </summary>
public sealed record FlowNodeDto(string Id, string Type, string? Title, string Text, Guid? ArticleId, IReadOnlyList<FlowOptionDto> Options);

public sealed record FlowDefinitionDto(string StartNodeId, IReadOnlyList<FlowNodeDto> Nodes);

public sealed record FlowValidationIssueDto(string? NodeId, string Message);

public sealed record FlowListItemDto(
    Guid Id,
    string Name,
    string? Description,
    string Status,
    string Visibility,
    Guid? ProductId,
    string? ProductName,
    Guid? CategoryId,
    string? CategoryName,
    int Version,
    int NodeCount,
    long Started,
    long Resolved,
    long Escalated,
    DateTime UpdatedAt);

public sealed record FlowDetailDto(
    Guid Id,
    string Name,
    string? Description,
    string Status,
    string Visibility,
    Guid? ProductId,
    string? ProductName,
    Guid? CategoryId,
    string? CategoryName,
    int Version,
    long Started,
    long Resolved,
    long Escalated,
    FlowDefinitionDto Definition,
    IReadOnlyList<FlowValidationIssueDto> Issues,
    DateTime UpdatedAt);

public sealed record SaveFlowRequest(
    string Name,
    string? Description,
    string Visibility,
    Guid? ProductId,
    Guid? CategoryId,
    FlowDefinitionDto Definition);

/// <summary>publish | unpublish (back to Draft) | archive</summary>
public sealed record FlowTransitionRequest(string Action);

/// <summary>Outcome: started | resolved | escalated. Path: node ids visited (for analytics).</summary>
public sealed record FlowUsageRequest(string Outcome, IReadOnlyList<string>? Path = null);
