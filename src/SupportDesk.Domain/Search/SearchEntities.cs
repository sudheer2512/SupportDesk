namespace SupportDesk.Domain.Search;

/// <summary>
/// Tenant-scoped full-text search document (tickets, articles, contacts, customers, community topics).
/// Lexical search uses <see cref="SearchPosting"/> (an inverted index with BM25-style weights) so it works on
/// any SQL Server edition, including LocalDB which has no Full-Text Search.
/// </summary>
public class SearchDocument : TenantEntity
{
    /// <summary>Ticket, Article, Contact, Customer, Topic</summary>
    public string EntityType { get; set; } = "";
    public Guid EntityId { get; set; }
    public string Title { get; set; } = "";
    public string? Snippet { get; set; }
    /// <summary>Visibility tag used for filtering: internal, public, authenticated, restricted.</summary>
    public string Visibility { get; set; } = "internal";
    public string Locale { get; set; } = "en";
    /// <summary>Optional scoping ids (department for tickets, brand for articles) for permission filtering.</summary>
    public Guid? ScopeId { get; set; }
    public Guid? CustomerId { get; set; }
    public int TermCount { get; set; }
    public string ContentHash { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}

public class SearchPosting : TenantEntity
{
    public Guid DocumentId { get; set; }
    public string Term { get; set; } = "";
    /// <summary>Field-weighted term frequency (title terms count more than body terms).</summary>
    public float Weight { get; set; }
}
