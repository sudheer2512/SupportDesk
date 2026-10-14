using SupportDesk.Contracts.Common;

namespace SupportDesk.Contracts.Knowledge;

/// <summary>
/// One global-search hit. Type: Ticket, Article, Contact, Customer, Topic. SnippetHtml is HTML-escaped text with the
/// matched terms wrapped in &lt;mark&gt;. Url is the desk route of the record.
/// </summary>
public sealed record SearchHitDto(
    string Type,
    Guid Id,
    string Title,
    string? Subtitle,
    string? SnippetHtml,
    double Score,
    string Url,
    string? Badge,
    DateTime UpdatedAt);

public sealed record GlobalSearchResultDto(string Query, IReadOnlyList<SearchHitDto> Hits, IReadOnlyList<CountDto> Counts, int TookMs);

public sealed record SearchIndexStatusDto(IReadOnlyList<CountDto> Documents, long Postings, DateTime? LastIndexedAt);
