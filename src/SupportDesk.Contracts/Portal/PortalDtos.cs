namespace SupportDesk.Contracts.Portal;

/// <summary>Public branding/feature configuration of a customer portal (GET /api/v1/portal/{brand}/config).</summary>
public sealed record PortalConfigDto(
    string BrandSlug,
    string Name,
    string? LogoUrl,
    string PrimaryColor,
    string AccentColor,
    string PortalTitle,
    string? PortalWelcome,
    string? FooterHtml,
    string? SupportEmail,
    string? SupportPhone,
    IReadOnlyList<string> Locales,
    PortalFeaturesDto Features,
    string AuthMode);

public sealed record PortalFeaturesDto(bool Kb, bool Community, bool Chat, bool AiAssistant, bool AnonymousTickets, bool SelfRegistration);

// ───────────────────────────── Knowledge base (portal) ─────────────────────────────

public sealed record PortalCategoryRefDto(Guid Id, string Name, string Slug);

public sealed record PortalCategoryDto(
    Guid Id,
    Guid? ParentId,
    string Name,
    string Slug,
    string? Description,
    string? Icon,
    int ArticleCount,
    IReadOnlyList<PortalCategoryDto> Children);

public sealed record PortalArticleSummaryDto(
    Guid Id,
    string Slug,
    string Title,
    string? Summary,
    string ArticleType,
    Guid? CategoryId,
    string? CategoryName,
    string? CategorySlug,
    DateTime? PublishedAt,
    DateTime UpdatedAt,
    long ViewCount,
    double? HelpfulPercent,
    IReadOnlyList<string> Tags,
    string? SnippetHtml);

public sealed record PortalAttachmentDto(Guid Id, string FileName, string ContentType, long SizeBytes, string Url, DateTime CreatedAt);

public sealed record PortalArticleDto(
    Guid Id,
    string Slug,
    string Title,
    string? Summary,
    string BodyHtml,
    string ArticleType,
    string Locale,
    string PrimaryLocale,
    IReadOnlyList<string> AvailableLocales,
    bool TranslationOutdated,
    bool TranslationFallback,
    Guid? CategoryId,
    string? CategoryName,
    string? CategorySlug,
    IReadOnlyList<PortalCategoryRefDto> Breadcrumb,
    IReadOnlyList<string> Tags,
    string? ProductName,
    DateTime? PublishedAt,
    DateTime UpdatedAt,
    long ViewCount,
    int HelpfulCount,
    int NotHelpfulCount,
    double? RatingAverage,
    IReadOnlyList<PortalArticleSummaryDto> Related,
    IReadOnlyList<PortalAttachmentDto> Attachments);

public sealed record PortalCategoryPageDto(
    PortalCategoryDto Category,
    IReadOnlyList<PortalCategoryRefDto> Breadcrumb,
    IReadOnlyList<PortalArticleSummaryDto> Articles);

public sealed record PortalSearchResultDto(
    string Query,
    IReadOnlyList<PortalArticleSummaryDto> Articles,
    IReadOnlyList<SupportDesk.Contracts.Knowledge.TopicListItemDto> Topics,
    int Total);

public sealed record PortalFlowSummaryDto(Guid Id, string Name, string? Description, string? ProductName);

public sealed record PortalFlowDto(
    Guid Id,
    string Name,
    string? Description,
    string? ProductName,
    SupportDesk.Contracts.Knowledge.FlowDefinitionDto Definition,
    IReadOnlyList<PortalArticleSummaryDto> Articles);

public sealed record PortalTicketStatsDto(int Open, int AwaitingYou, int AwaitingConfirmation);

public sealed record PortalHomeDto(
    IReadOnlyList<PortalCategoryDto> Categories,
    IReadOnlyList<PortalArticleSummaryDto> Popular,
    IReadOnlyList<PortalArticleSummaryDto> Recent,
    IReadOnlyList<PortalArticleSummaryDto> Announcements,
    IReadOnlyList<PortalFlowSummaryDto> Flows,
    IReadOnlyList<SupportDesk.Contracts.Knowledge.TopicListItemDto> RecentTopics,
    PortalTicketStatsDto? MyTickets);

// ───────────────────────────── Assistant ─────────────────────────────

public sealed record PortalAssistantRequest(string Message, string? Locale = null);

public sealed record PortalCitationDto(int Index, string SourceType, Guid SourceId, string Title, string? Url, string? Snippet);

/// <summary>Source: "ai" (assistant answer) or "kb" (knowledge-base fallback).</summary>
public sealed record PortalAssistantReplyDto(string Text, IReadOnlyList<PortalCitationDto> Citations, bool SuggestTicket, double Confidence, string Source);

// ───────────────────────────── Anonymous submissions ─────────────────────────────

/// <summary>Anonymous ticket / web form. "Website" is a honeypot (must stay empty); FormStartedAt (epoch ms) is a time trap.</summary>
public sealed record PortalAnonymousTicketRequest
{
    public string Name { get; init; } = "";
    public string Email { get; init; } = "";
    public string? Phone { get; init; }
    public string Subject { get; init; } = "";
    public string? DescriptionHtml { get; init; }
    public string? Description { get; init; }
    public Guid? DepartmentId { get; init; }
    public Guid? CategoryId { get; init; }
    public Guid? ProductId { get; init; }
    public Dictionary<string, System.Text.Json.JsonElement>? CustomFields { get; init; }
    public string? Website { get; init; }
    public long? FormStartedAt { get; init; }
}

public sealed record PortalSubmissionResultDto(bool Accepted, string? DisplayNumber, string Message);
