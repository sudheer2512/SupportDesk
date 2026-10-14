using System.Linq.Expressions;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Search;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Search;

/// <summary>A query split into stemmed terms (as indexed), the raw lower-case words (for highlighting) and an optional prefix.</summary>
public sealed record ParsedQuery(IReadOnlyList<string> Terms, IReadOnlyList<string> Words, string? Prefix)
{
    public bool IsEmpty => Terms.Count == 0 && Prefix is null;
}

public sealed record ScoredDocument(SearchDocument Document, double Score);

/// <summary>
/// Lexical ranking over the inverted index with BM25 (k1 = 1.2, b = 0.75) computed at query time from the stored
/// field-weighted term frequencies. The last word of the query also matches as a prefix (search-as-you-type);
/// prefix expansions score lower than exact terms. Very common terms are dropped when rarer terms are present so a
/// single query never scans a large share of the postings table.
/// </summary>
public sealed partial class SearchEngine(DeskDbContext db)
{
    private const double K1 = 1.2;
    private const double B = 0.75;
    private const double PrefixFactor = 0.6;
    private const int MaxPostingRows = 30_000;
    private const int CommonTermThreshold = 5_000;

    public static ParsedQuery Parse(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return new ParsedQuery([], [], null);
        var trimmed = query.Trim();
        var terms = TextNormalization.Tokenize(trimmed).Distinct().Take(12).ToList();
        var words = WordRegex().Matches(trimmed.ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length >= 2).Distinct().Take(12).ToList();
        string? prefix = null;
        if (!char.IsWhiteSpace(query[^1]) && words.Count > 0 && words[^1].Length >= 3) prefix = words[^1];
        return new ParsedQuery(terms, words, prefix);
    }

    /// <summary>
    /// Scores documents matching <paramref name="query"/> among <paramref name="documents"/> (already filtered by type,
    /// visibility and permissions) and returns the best <paramref name="take"/>.
    /// </summary>
    public async Task<List<ScoredDocument>> QueryAsync(ParsedQuery query, IQueryable<SearchDocument> documents, int take, CancellationToken ct = default)
    {
        if (query.IsEmpty || take <= 0) return [];
        var factors = query.Terms.ToDictionary(t => t, _ => 1.0, StringComparer.Ordinal);
        if (query.Prefix is { } prefix)
        {
            var expansions = await db.SearchPostings.AsNoTracking().Where(p => p.Term.StartsWith(prefix))
                .Select(p => p.Term).Distinct().OrderBy(t => t).Take(24).ToListAsync(ct);
            foreach (var e in expansions) factors.TryAdd(e, PrefixFactor);
        }
        if (factors.Count == 0) return [];
        var keys = factors.Keys.ToList();

        // BM25 collection statistics in one pass over the (permission-filtered) documents.
        var stats = await documents.GroupBy(_ => 1).Select(g => new { N = g.Count(), Avg = g.Average(d => (double)d.TermCount) }).SingleOrDefaultAsync(ct);
        if (stats is null || stats.N == 0) return [];
        var n = stats.N;
        var avgdl = Math.Max(1, stats.Avg);

        var postings = from p in db.SearchPostings.AsNoTracking()
                       join d in documents on p.DocumentId equals d.Id
                       where keys.Contains(p.Term)
                       select new { p.DocumentId, p.Term, p.Weight, d.TermCount };
        var dfs = await postings.GroupBy(p => p.Term).Select(g => new { Term = g.Key, Df = g.Count() }).ToListAsync(ct);
        if (dfs.Count == 0) return [];
        var df = dfs.ToDictionary(x => x.Term, x => x.Df, StringComparer.Ordinal);

        // Drop very common terms when the query has rarer ones (they barely change the ranking but cost the most).
        var keep = df.Where(kv => kv.Value <= CommonTermThreshold).Select(kv => kv.Key).ToList();
        if (keep.Count == 0) keep = df.OrderBy(kv => kv.Value).Take(1).Select(kv => kv.Key).ToList();
        var rows = await postings.Where(p => keep.Contains(p.Term)).OrderByDescending(p => p.Weight).Take(MaxPostingRows).ToListAsync(ct);

        var scores = new Dictionary<Guid, double>();
        foreach (var row in rows)
        {
            var dft = df[row.Term];
            var idf = Math.Log(1 + (n - dft + 0.5) / (dft + 0.5));
            var tf = row.Weight;
            var norm = tf * (K1 + 1) / (tf + K1 * (1 - B + B * row.TermCount / avgdl));
            scores[row.DocumentId] = scores.GetValueOrDefault(row.DocumentId) + idf * norm * factors[row.Term];
        }
        if (scores.Count == 0) return [];

        var top = scores.OrderByDescending(kv => kv.Value).Take(Math.Max(take * 2, take + 10)).ToList();
        var ids = top.Select(kv => kv.Key).ToList();
        var docs = await db.SearchDocuments.AsNoTracking().Where(d => ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);
        var phrase = string.Join(' ', query.Words);
        return top.Where(kv => docs.ContainsKey(kv.Key))
            .Select(kv =>
            {
                var d = docs[kv.Key];
                var score = kv.Value;
                // Small boost when the whole query appears in the title (exact intent).
                if (phrase.Length >= 3 && d.Title.Contains(phrase, StringComparison.OrdinalIgnoreCase)) score *= 1.25;
                return new ScoredDocument(d, Math.Round(score, 4));
            })
            .OrderByDescending(s => s.Score)
            .Take(take)
            .ToList();
    }

    /// <summary>
    /// A fragment of <paramref name="text"/> around the first match, HTML-escaped, with matched words wrapped in
    /// &lt;mark&gt;. Returns the start of the text when nothing matches.
    /// </summary>
    public static string? Highlight(string? text, ParsedQuery query, int length = 220)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var flat = WhitespaceRegex().Replace(text, " ").Trim();
        var needles = query.Words.Concat(query.Terms).Where(w => w.Length >= 2).Distinct().OrderByDescending(w => w.Length).ToList();
        var first = -1;
        foreach (var w in needles)
        {
            var i = flat.IndexOf(w, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && (first < 0 || i < first)) first = i;
        }
        var start = first <= 60 ? 0 : first - 60;
        // Start at a word boundary.
        if (start > 0)
        {
            var space = flat.IndexOf(' ', start);
            if (space > 0 && space < first) start = space + 1;
        }
        var end = Math.Min(flat.Length, start + length);
        if (end < flat.Length)
        {
            var space = flat.LastIndexOf(' ', end);
            if (space > start + length / 2) end = space;
        }
        var fragment = flat[start..end];
        var sb = new StringBuilder();
        if (start > 0) sb.Append("… ");
        sb.Append(WebUtility.HtmlEncode(fragment));
        if (end < flat.Length) sb.Append(" …");
        var html = sb.ToString();
        if (needles.Count == 0) return html;
        var pattern = "(?<![\\p{L}\\p{N}])(" + string.Join('|', needles.Select(Regex.Escape)) + ")[\\p{L}\\p{N}]*";
        return Regex.Replace(html, pattern, m => $"<mark>{m.Value}</mark>", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
    }

    /// <summary>Lower-cased, trimmed, whitespace-collapsed query (analytics grouping).</summary>
    public static string NormalizeQuery(string? query) =>
        WhitespaceRegex().Replace((query ?? "").Trim().ToLowerInvariant(), " ") is var s && s.Length > 300 ? s[..300] : s;

    public static Expression<Func<SearchDocument, bool>> OfTypes(IReadOnlyCollection<string> types) => d => types.Contains(d.EntityType);

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}_\-]*")] private static partial Regex WordRegex();
    [GeneratedRegex(@"\s+")] private static partial Regex WhitespaceRegex();
}
