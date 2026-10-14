namespace SupportDesk.Contracts.Knowledge;

// ───────────────────────────── Community ─────────────────────────────

public sealed record TopicListItemDto(
    Guid Id,
    string Title,
    string Excerpt,
    string AuthorName,
    bool AuthorIsAgent,
    string Status,
    bool IsQuestion,
    bool IsPinned,
    bool IsAnswered,
    int VoteScore,
    int PostCount,
    long ViewCount,
    IReadOnlyList<string> Tags,
    DateTime CreatedAt,
    DateTime LastActivityAt,
    Guid? ConvertedTicketId);

public sealed record PostDto(
    Guid Id,
    string BodyHtml,
    string AuthorName,
    bool IsAgentResponse,
    bool IsMine,
    bool IsAcceptedAnswer,
    bool IsHidden,
    int VoteScore,
    int MyVote,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record TopicActionsDto(bool Reply, bool Vote, bool Accept, bool Moderate, bool Edit, bool Convert);

public sealed record TopicDetailDto(
    Guid Id,
    string Title,
    string BodyHtml,
    string AuthorName,
    bool AuthorIsAgent,
    bool IsMine,
    string Status,
    bool IsQuestion,
    bool IsPinned,
    Guid? AcceptedPostId,
    int VoteScore,
    int MyVote,
    int PostCount,
    long ViewCount,
    IReadOnlyList<string> Tags,
    DateTime CreatedAt,
    DateTime LastActivityAt,
    IReadOnlyList<PostDto> Posts,
    TopicActionsDto Actions,
    Guid? ConvertedTicketId,
    string? ConvertedTicketNumber);

public sealed record TopicQuery
{
    public string? Q { get; init; }
    public string? Tag { get; init; }
    /// <summary>Open, Answered, Closed, Hidden (agents only), unanswered</summary>
    public string? Status { get; init; }
    /// <summary>activity (default) | newest | votes | unanswered</summary>
    public string? Sort { get; init; }
    public int? Page { get; init; }
    public int? PageSize { get; init; }
}

public sealed record CreateTopicRequest(string Title, string BodyHtml, bool IsQuestion = true, IReadOnlyList<string>? Tags = null);

public sealed record UpdateTopicRequest(string Title, string BodyHtml, IReadOnlyList<string>? Tags);

public sealed record CreatePostRequest(string BodyHtml);

public sealed record UpdatePostRequest(string BodyHtml);

/// <summary>Value: 1 (up), -1 (down) or 0 (remove my vote).</summary>
public sealed record VoteRequest(int Value);

public sealed record VoteResultDto(int VoteScore, int MyVote);

/// <summary>Topics: hide | unhide | close | reopen | pin | unpin. Posts: hide | unhide.</summary>
public sealed record ModerateRequest(string Action, string? Reason = null);

public sealed record ConvertTopicResultDto(Guid TicketId, long Number, string DisplayNumber);
