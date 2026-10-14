namespace SupportDesk.Domain.Knowledge;

/// <summary>
/// One customer-facing knowledge search (portal search box, assistant fallback). Feeds KB analytics: top searches and
/// searches without results (content gaps). Queries are normalised (lower-case, trimmed, collapsed whitespace).
/// </summary>
public class KnowledgeSearchLog : TenantEntity
{
    public string Query { get; set; } = "";
    public string NormalizedQuery { get; set; } = "";
    public int ResultCount { get; set; }
    /// <summary>portal | assistant | desk</summary>
    public string Source { get; set; } = "portal";
    public Guid? BrandId { get; set; }
    public Guid? ContactId { get; set; }
    public string Locale { get; set; } = "en";
    public DateTime CreatedAt { get; set; }
}
