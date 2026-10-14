namespace SupportDesk.Domain.Knowledge;

/// <summary>Who may read a knowledge item.</summary>
public enum KbVisibility { Public, Authenticated, Internal, Restricted }

public enum ArticleStatus { Draft, InReview, Approved, Published, Archived, Expired }

public class KbCategory : AuditedEntity
{
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public KbVisibility Visibility { get; set; } = KbVisibility.Public;
    /// <summary>Brands (portals) this category appears in; empty = all.</summary>
    public string BrandIdsJson { get; set; } = "[]";
    public int SortOrder { get; set; }
}

/// <summary>
/// Knowledge article. Content lives in immutable <see cref="ArticleVersion"/> rows (primary locale) and
/// <see cref="ArticleTranslation"/> rows (other locales). PublishedVersionId points at what readers see.
/// </summary>
public class Article : AuditedEntity, ISoftDeletable, IConcurrencyVersioned
{
    public Guid? CategoryId { get; set; }
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Summary { get; set; }
    public ArticleStatus Status { get; set; } = ArticleStatus.Draft;
    public KbVisibility Visibility { get; set; } = KbVisibility.Public;
    /// <summary>FAQ, HowTo, Troubleshooting, Reference, ReleaseNote, ProductDoc</summary>
    public string ArticleType { get; set; } = "HowTo";
    public Guid? OwnerUserId { get; set; }
    public Guid? CurrentVersionId { get; set; }
    public Guid? PublishedVersionId { get; set; }
    public int LatestVersionNumber { get; set; }
    public string PrimaryLocale { get; set; } = "en";
    public string TagsJson { get; set; } = "[]";
    public string RelatedArticleIdsJson { get; set; } = "[]";
    public string BrandIdsJson { get; set; } = "[]";
    public Guid? ProductId { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime? ReviewDueAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public long ViewCount { get; set; }
    public int HelpfulCount { get; set; }
    public int NotHelpfulCount { get; set; }
    public double? RatingAverage { get; set; }
    public int RatingCount { get; set; }
    /// <summary>Ticket this article was generated from (ticket-to-article conversion).</summary>
    public Guid? SourceTicketId { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public class ArticleVersion : TenantEntity
{
    public Guid ArticleId { get; set; }
    public int VersionNumber { get; set; }
    public string Locale { get; set; } = "en";
    public string Title { get; set; } = "";
    public string BodyHtml { get; set; } = "";
    public string? Summary { get; set; }
    public string? Keywords { get; set; }
    public ArticleStatus Status { get; set; }
    public string? ChangeNote { get; set; }
    public Guid? AuthorId { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? ReviewerId { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }
}

public enum TranslationStatus { Draft, InTranslation, InReview, Published, Outdated }

public class ArticleTranslation : AuditedEntity
{
    public Guid ArticleId { get; set; }
    public string Locale { get; set; } = "";
    public string Title { get; set; } = "";
    public string BodyHtml { get; set; } = "";
    public string? Summary { get; set; }
    public TranslationStatus Status { get; set; } = TranslationStatus.Draft;
    public Guid? TranslatorUserId { get; set; }
    /// <summary>Primary-locale version this translation was made from; older than the published version ⇒ Outdated.</summary>
    public int SourceVersionNumber { get; set; }
    public bool MachineTranslated { get; set; }
    public DateTime? PublishedAt { get; set; }
}

public class ArticleAttachment : TenantEntity
{
    public Guid ArticleId { get; set; }
    public Guid FileId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Customer-specific visibility for Restricted articles: any matching row grants access.</summary>
public class ArticleAccessRule : TenantEntity
{
    public Guid ArticleId { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid? CustomerGroupId { get; set; }
    public string? CustomerTier { get; set; }
    public Guid? ProductId { get; set; }
}

public class ArticleFeedback : TenantEntity
{
    public Guid ArticleId { get; set; }
    public bool Helpful { get; set; }
    public int? Rating { get; set; }
    public string? Comment { get; set; }
    public string Locale { get; set; } = "en";
    public Guid? ContactId { get; set; }
    public Guid? UserId { get; set; }
    public bool Reviewed { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Chunk of an article (or other source) with its embedding vector for semantic search / RAG.</summary>
public class KnowledgeChunk : TenantEntity
{
    /// <summary>Article, Ticket, Topic, ProductDoc</summary>
    public string SourceType { get; set; } = "Article";
    public Guid SourceId { get; set; }
    public string Locale { get; set; } = "en";
    public int ChunkIndex { get; set; }
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public KbVisibility Visibility { get; set; }
    /// <summary>float32 little-endian vector.</summary>
    public byte[] Embedding { get; set; } = [];
    public string EmbeddingModel { get; set; } = "";
    public int Dimensions { get; set; }
    public string ContentHash { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}

public enum TopicStatus { Open, Answered, Closed, Hidden }

/// <summary>Community forum question/discussion.</summary>
public class CommunityTopic : AuditedEntity
{
    public Guid? BrandId { get; set; }
    public Guid? CategoryId { get; set; }
    public string Title { get; set; } = "";
    public string BodyHtml { get; set; } = "";
    public Guid? AuthorContactId { get; set; }
    public Guid? AuthorUserId { get; set; }
    public string AuthorName { get; set; } = "";
    public TopicStatus Status { get; set; } = TopicStatus.Open;
    public bool IsQuestion { get; set; } = true;
    public bool IsPinned { get; set; }
    public Guid? AcceptedPostId { get; set; }
    public int VoteScore { get; set; }
    public int PostCount { get; set; }
    public long ViewCount { get; set; }
    public string TagsJson { get; set; } = "[]";
    public DateTime LastActivityAt { get; set; }
    public Guid? ConvertedTicketId { get; set; }
}

public class CommunityPost : AuditedEntity
{
    public Guid TopicId { get; set; }
    public string BodyHtml { get; set; } = "";
    public Guid? AuthorContactId { get; set; }
    public Guid? AuthorUserId { get; set; }
    public string AuthorName { get; set; } = "";
    public bool IsAgentResponse { get; set; }
    public bool IsAcceptedAnswer { get; set; }
    public bool IsHidden { get; set; }
    public int VoteScore { get; set; }
}

public class CommunityVote : TenantEntity
{
    public Guid? TopicId { get; set; }
    public Guid? PostId { get; set; }
    /// <summary>contact:{id} or user:{id} — one vote per voter per item.</summary>
    public string VoterKey { get; set; } = "";
    public int Value { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Guided troubleshooting decision tree. DefinitionJson = {"startNodeId","nodes":[{"id","type":"question|solution|article|escalate",
/// "text","articleId","options":[{"label","next"}]}]}
/// </summary>
public class TroubleshootingFlow : AuditedEntity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public ArticleStatus Status { get; set; } = ArticleStatus.Draft;
    public KbVisibility Visibility { get; set; } = KbVisibility.Public;
    public Guid? ProductId { get; set; }
    public Guid? CategoryId { get; set; }
    public string DefinitionJson { get; set; } = "{}";
    public int Version { get; set; } = 1;
    public long StartedCount { get; set; }
    public long ResolvedCount { get; set; }
    public long EscalatedCount { get; set; }
}
