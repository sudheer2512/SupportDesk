using SupportDesk.Contracts.Common;

namespace SupportDesk.Contracts.Knowledge;

// ───────────────────────────── Categories ─────────────────────────────

public sealed record KbCategoryDto(
    Guid Id,
    Guid? ParentId,
    string Name,
    string Slug,
    string? Description,
    string? Icon,
    string Visibility,
    IReadOnlyList<Guid> BrandIds,
    int SortOrder,
    int ArticleCount);

public sealed record SaveKbCategoryRequest(
    Guid? ParentId,
    string Name,
    string? Slug,
    string? Description,
    string? Icon,
    string Visibility,
    IReadOnlyList<Guid>? BrandIds,
    int SortOrder);

// ───────────────────────────── Articles ─────────────────────────────

/// <summary>GET /kb/articles filters (all optional).</summary>
public sealed record ArticleQuery
{
    public string? Q { get; init; }
    /// <summary>Draft, InReview, Approved, Published, Archived, Expired</summary>
    public string? Status { get; init; }
    /// <summary>Public, Authenticated, Internal, Restricted</summary>
    public string? Visibility { get; init; }
    public Guid? CategoryId { get; init; }
    /// <summary>Articles having a translation in this locale (or this primary locale).</summary>
    public string? Locale { get; init; }
    public Guid? OwnerUserId { get; init; }
    public string? ArticleType { get; init; }
    public Guid? ProductId { get; init; }
    public string? Tag { get; init; }
    /// <summary>Only articles whose review date has passed.</summary>
    public bool? ReviewDue { get; init; }
    /// <summary>true = currently visible to readers; false = not live.</summary>
    public bool? Live { get; init; }
    /// <summary>updated (default) | title | views | helpful | reviewDue</summary>
    public string? Sort { get; init; }
    public int? Page { get; init; }
    public int? PageSize { get; init; }
}

public sealed record ArticleListItemDto(
    Guid Id,
    string Slug,
    string Title,
    string? Summary,
    string Status,
    string Visibility,
    string ArticleType,
    Guid? CategoryId,
    string? CategoryName,
    Guid? OwnerUserId,
    string? OwnerName,
    string PrimaryLocale,
    IReadOnlyList<string> TranslationLocales,
    IReadOnlyList<string> Tags,
    Guid? ProductId,
    string? ProductName,
    bool IsLive,
    bool HasPendingChanges,
    int LatestVersionNumber,
    int? PublishedVersionNumber,
    DateTime? PublishedAt,
    DateTime? ReviewDueAt,
    DateTime? ExpiresAt,
    long ViewCount,
    int HelpfulCount,
    int NotHelpfulCount,
    double? RatingAverage,
    int RatingCount,
    DateTime UpdatedAt);

public sealed record ArticleRefDto(Guid Id, string Title, string Slug, string Status, bool IsLive);

/// <summary>Which workflow actions the current agent may perform on the article right now.</summary>
public sealed record ArticleActionsDto(
    bool Edit,
    bool Submit,
    bool Approve,
    bool Reject,
    bool Publish,
    bool Archive,
    bool Reopen,
    bool Delete,
    bool ManageTranslations);

public sealed record ArticleAccessRuleDto(
    Guid Id,
    Guid? CustomerId,
    string? CustomerName,
    Guid? CustomerGroupId,
    string? CustomerGroupName,
    string? CustomerTier,
    Guid? ProductId,
    string? ProductName);

/// <summary>One rule of a Restricted article; all non-null fields of a rule must match, any matching rule grants access.</summary>
public sealed record ArticleAccessRuleInput(Guid? CustomerId, Guid? CustomerGroupId, string? CustomerTier, Guid? ProductId);

public sealed record ArticleAttachmentDto(Guid Id, Guid FileId, string FileName, string ContentType, long SizeBytes, bool IsImage, string Url, DateTime CreatedAt);

public sealed record ArticleTranslationSummaryDto(Guid Id, string Locale, string Title, string Status, int SourceVersionNumber, bool IsOutdated, DateTime UpdatedAt);

public sealed record ArticleDetailDto(
    Guid Id,
    string Slug,
    string Title,
    string? Summary,
    string BodyHtml,
    string? Keywords,
    string Status,
    string Visibility,
    string ArticleType,
    Guid? CategoryId,
    string? CategoryName,
    Guid? OwnerUserId,
    string? OwnerName,
    string PrimaryLocale,
    IReadOnlyList<string> Tags,
    IReadOnlyList<ArticleRefDto> RelatedArticles,
    IReadOnlyList<Guid> BrandIds,
    Guid? ProductId,
    string? ProductName,
    bool IsLive,
    bool HasPendingChanges,
    int CurrentVersionNumber,
    int? PublishedVersionNumber,
    DateTime? PublishedAt,
    DateTime? ReviewDueAt,
    DateTime? ExpiresAt,
    long ViewCount,
    int HelpfulCount,
    int NotHelpfulCount,
    double? RatingAverage,
    int RatingCount,
    Guid? SourceTicketId,
    string? SourceTicketNumber,
    IReadOnlyList<ArticleAccessRuleDto> AccessRules,
    IReadOnlyList<ArticleAttachmentDto> Attachments,
    IReadOnlyList<ArticleTranslationSummaryDto> Translations,
    ArticleActionsDto Actions,
    string? LastReviewNote,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string RowVersion);

/// <summary>
/// Create / update an article. Content changes (title, body, summary, keywords) create a new immutable version (Draft);
/// metadata (category, visibility, tags, related, brands, product, owner, dates, access rules) applies immediately.
/// </summary>
public sealed record SaveArticleRequest
{
    public string Title { get; init; } = "";
    public string BodyHtml { get; init; } = "";
    public string? Summary { get; init; }
    public string? Keywords { get; init; }
    public string? Slug { get; init; }
    public Guid? CategoryId { get; init; }
    /// <summary>FAQ, HowTo, Troubleshooting, Reference, ReleaseNote, ProductDoc</summary>
    public string ArticleType { get; init; } = "HowTo";
    /// <summary>Public, Authenticated, Internal, Restricted</summary>
    public string Visibility { get; init; } = "Public";
    public IReadOnlyList<string>? Tags { get; init; }
    public IReadOnlyList<Guid>? RelatedArticleIds { get; init; }
    public IReadOnlyList<Guid>? BrandIds { get; init; }
    public Guid? ProductId { get; init; }
    public Guid? OwnerUserId { get; init; }
    public DateTime? ReviewDueAt { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public string? PrimaryLocale { get; init; }
    public string? ChangeNote { get; init; }
    /// <summary>Replaces the access rules when not null (used for Restricted articles).</summary>
    public IReadOnlyList<ArticleAccessRuleInput>? AccessRules { get; init; }
    /// <summary>Required for updates (optimistic concurrency).</summary>
    public string? RowVersion { get; init; }
}

/// <summary>submit | approve | reject | publish | archive | reopen</summary>
public sealed record ArticleTransitionRequest(string Action, string? Note = null);

// ───────────────────────────── Versions ─────────────────────────────

public sealed record ArticleVersionDto(
    Guid Id,
    int VersionNumber,
    string Locale,
    string Title,
    string? Summary,
    string Status,
    string? ChangeNote,
    Guid? AuthorId,
    string? AuthorName,
    DateTime CreatedAt,
    Guid? ReviewerId,
    string? ReviewerName,
    DateTime? ReviewedAt,
    string? ReviewNote,
    bool IsCurrent,
    bool IsPublished);

public sealed record ArticleVersionContentDto(ArticleVersionDto Version, string BodyHtml, string? Keywords);

public sealed record FieldDiffDto(string? From, string? To);

/// <summary>Op: equal | insert | delete</summary>
public sealed record DiffLineDto(string Op, string Text);

public sealed record ArticleDiffDto(
    int FromVersion,
    int ToVersion,
    FieldDiffDto? Title,
    FieldDiffDto? Summary,
    FieldDiffDto? Keywords,
    IReadOnlyList<DiffLineDto> Body,
    int Added,
    int Removed);

public sealed record RestoreVersionRequest(string? ChangeNote);

// ───────────────────────────── Translations ─────────────────────────────

public sealed record ArticleTranslationDto(
    Guid Id,
    Guid ArticleId,
    string Locale,
    string Title,
    string? Summary,
    string BodyHtml,
    string Status,
    int SourceVersionNumber,
    bool IsOutdated,
    bool MachineTranslated,
    Guid? TranslatorUserId,
    string? TranslatorName,
    DateTime? PublishedAt,
    DateTime UpdatedAt);

public sealed record SaveTranslationRequest(string Title, string BodyHtml, string? Summary, bool MachineTranslated = false);

/// <summary>start (→ InTranslation) | submit (→ InReview) | publish (→ Published) | reopen (→ Draft)</summary>
public sealed record TranslationTransitionRequest(string Action);

// ───────────────────────────── Feedback ─────────────────────────────

public sealed record SubmitArticleFeedbackRequest(bool Helpful, int? Rating = null, string? Comment = null, string? Locale = null);

public sealed record ArticleFeedbackDto(
    Guid Id,
    Guid ArticleId,
    string ArticleTitle,
    bool Helpful,
    int? Rating,
    string? Comment,
    string Locale,
    string? AuthorName,
    string AuthorType,
    bool Reviewed,
    DateTime CreatedAt);

public sealed record ArticleFeedbackQuery
{
    public Guid? ArticleId { get; init; }
    public bool? Reviewed { get; init; }
    public bool? Helpful { get; init; }
    public bool? WithComments { get; init; }
    public int? Page { get; init; }
    public int? PageSize { get; init; }
}

public sealed record ReviewFeedbackRequest(IReadOnlyList<Guid> Ids);

// ───────────────────────────── Ticket → article ─────────────────────────────

public sealed record ArticleFromTicketRequest(string? Title = null, bool IncludeConversation = true, Guid? CategoryId = null);

// ───────────────────────────── Import / export ─────────────────────────────

public sealed record KbImportIssueDto(string Item, string Severity, string Message);

public sealed record KbImportResultDto(int Created, int Skipped, int CategoriesCreated, IReadOnlyList<KbImportIssueDto> Issues, IReadOnlyList<Guid> ArticleIds);

// ───────────────────────────── Options & analytics ─────────────────────────────

public sealed record KbOptionsDto(
    IReadOnlyList<string> ArticleTypes,
    IReadOnlyList<string> Visibilities,
    IReadOnlyList<string> Statuses,
    IReadOnlyList<string> Locales,
    IReadOnlyList<IdName> Brands,
    IReadOnlyList<IdName> Products,
    IReadOnlyList<IdName> CustomerGroups,
    IReadOnlyList<string> CustomerTiers,
    IReadOnlyList<IdName> Owners,
    IReadOnlyList<string> Tags);

public sealed record KbTotalsDto(
    int Articles,
    int Live,
    int Drafts,
    int InReview,
    int ReviewOverdue,
    int ExpiringSoon,
    long Views,
    int Helpful,
    int NotHelpful,
    double? HelpfulPercent,
    double? AverageRating,
    int PendingFeedback,
    int Searches,
    int SearchesWithoutResults);

public sealed record KbArticleStatDto(Guid Id, string Title, long Views, int Helpful, int NotHelpful, double? HelpfulPercent, double? RatingAverage);

public sealed record KbSearchTermDto(string Query, int Count, double AverageResults, DateTime LastSearchedAt);

public sealed record KbDailyPointDto(DateOnly Date, int Searches, int NoResults, int Helpful, int NotHelpful);

public sealed record FlowUsageStatDto(Guid Id, string Name, long Started, long Resolved, long Escalated);

public sealed record KbAnalyticsDto(
    int Days,
    KbTotalsDto Totals,
    IReadOnlyList<KbArticleStatDto> TopViewed,
    IReadOnlyList<KbArticleStatDto> LeastHelpful,
    IReadOnlyList<KbSearchTermDto> TopSearches,
    IReadOnlyList<KbSearchTermDto> NoResultSearches,
    IReadOnlyList<KbDailyPointDto> Daily,
    IReadOnlyList<FlowUsageStatDto> Flows);
