using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Domain.Knowledge;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Ai.Retrieval;

/// <summary>Splits plain text into overlapping chunks on paragraph/sentence boundaries.</summary>
public static partial class TextChunker
{
    public static IReadOnlyList<string> Chunk(string? text, int maxChars = 900, int overlapChars = 160)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var sentences = new List<string>();
        foreach (var paragraph in ParagraphRegex().Split(text.Replace("\r\n", "\n")))
        {
            var p = WhitespaceRegex().Replace(paragraph, " ").Trim();
            if (p.Length == 0) continue;
            if (p.Length <= maxChars) { sentences.Add(p); continue; }
            foreach (var s in SentenceRegex().Split(p))
            {
                var t = s.Trim();
                if (t.Length == 0) continue;
                // Pathologically long "sentences" (tables, logs) are hard-wrapped.
                for (var i = 0; i < t.Length; i += maxChars) sentences.Add(t.Substring(i, Math.Min(maxChars, t.Length - i)));
            }
        }
        var chunks = new List<string>();
        var current = new StringBuilder();
        var tail = new List<string>();
        foreach (var s in sentences)
        {
            if (current.Length > 0 && current.Length + s.Length + 1 > maxChars)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
                // Carry the last sentences (up to overlapChars) into the next chunk for context continuity.
                var carry = new List<string>();
                var len = 0;
                for (var i = tail.Count - 1; i >= 0 && len + tail[i].Length <= overlapChars; i--)
                {
                    carry.Insert(0, tail[i]);
                    len += tail[i].Length + 1;
                }
                foreach (var c in carry) current.Append(c).Append(' ');
                tail = carry;
            }
            current.Append(s).Append(current.Length > 0 && !s.EndsWith('.') ? "\n" : " ");
            tail.Add(s);
        }
        if (current.ToString().Trim().Length > 0) chunks.Add(current.ToString().Trim());
        return chunks;
    }

    [GeneratedRegex(@"\n\s*\n|\n(?=\s*(?:\d+[.)]|[-*•])\s)")] private static partial Regex ParagraphRegex();
    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z0-9])")] private static partial Regex SentenceRegex();
    [GeneratedRegex(@"[ \t]+")] private static partial Regex WhitespaceRegex();
}

public sealed record IndexedChunk(Guid Id, string SourceType, Guid SourceId, KbVisibility Visibility, string Locale, float[] Vector,
    IReadOnlyDictionary<int, int> Terms, int Length);

/// <summary>In-memory vector + term index of one tenant's chunks for one embedding model.</summary>
public sealed class TenantVectorIndex(string version, IReadOnlyList<IndexedChunk> chunks)
{
    public string Version { get; } = version;
    public IReadOnlyList<IndexedChunk> Chunks { get; } = chunks;
    public IReadOnlyDictionary<int, int> DocumentFrequency { get; } = chunks.SelectMany(c => c.Terms.Keys).GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
    public double AverageLength { get; } = chunks.Count == 0 ? 1 : Math.Max(1, chunks.Average(c => c.Length));

    /// <summary>BM25 score of a chunk for the query terms.</summary>
    public double Bm25(IndexedChunk chunk, IReadOnlyCollection<int> queryTerms, double k1 = 1.2, double b = 0.75)
    {
        double score = 0;
        var n = Chunks.Count;
        foreach (var term in queryTerms)
        {
            if (!chunk.Terms.TryGetValue(term, out var tf)) continue;
            var df = DocumentFrequency.TryGetValue(term, out var d) ? d : 0;
            var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
            score += idf * tf * (k1 + 1) / (tf + k1 * (1 - b + b * chunk.Length / AverageLength));
        }
        return score;
    }

    public static int TermId(string token)
    {
        unchecked
        {
            var hash = (int)2166136261;
            foreach (var c in token) hash = (hash ^ c) * 16777619;
            return hash;
        }
    }

    public static Dictionary<int, int> Terms(string text, out int length)
    {
        var terms = new Dictionary<int, int>();
        length = 0;
        foreach (var t in TextNormalization.Tokenize(text))
        {
            var id = TermId(t);
            terms[id] = terms.GetValueOrDefault(id) + 1;
            length++;
        }
        return terms;
    }
}

/// <summary>
/// Process-wide cache of tenant vector indexes. A cheap version probe (count + latest update for the embedding model)
/// decides when to reload, so writes on any instance are picked up on the next search.
/// </summary>
public sealed class ChunkVectorCache
{
    private readonly ConcurrentDictionary<(Guid Tenant, string Model), TenantVectorIndex> _indexes = new();
    private readonly ConcurrentDictionary<(Guid Tenant, string Model), SemaphoreSlim> _locks = new();

    public async Task<TenantVectorIndex> GetAsync(DeskDbContext db, Guid tenantId, string model, CancellationToken ct)
    {
        var probe = await db.KnowledgeChunks.AsNoTracking().Where(c => c.EmbeddingModel == model)
            .GroupBy(_ => 1).Select(g => new { Count = g.Count(), Max = g.Max(c => c.UpdatedAt) }).SingleOrDefaultAsync(ct); // one group at most
        var version = probe is null ? "0" : $"{probe.Count}:{probe.Max.Ticks}";
        var key = (tenantId, model);
        if (_indexes.TryGetValue(key, out var cached) && cached.Version == version) return cached;
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_indexes.TryGetValue(key, out cached) && cached.Version == version) return cached;
            var rows = await db.KnowledgeChunks.AsNoTracking().Where(c => c.EmbeddingModel == model)
                .Select(c => new { c.Id, c.SourceType, c.SourceId, c.Visibility, c.Locale, c.Embedding, c.Title, c.Text }).ToListAsync(ct);
            var chunks = rows.Select(r =>
            {
                var terms = TenantVectorIndex.Terms(r.Title + " " + r.Text, out var length);
                return new IndexedChunk(r.Id, r.SourceType, r.SourceId, r.Visibility, r.Locale, VectorMath.FromBytes(r.Embedding), terms, length);
            }).ToList();
            var index = new TenantVectorIndex(version, chunks);
            _indexes[key] = index;
            return index;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Invalidate(Guid tenantId)
    {
        foreach (var key in _indexes.Keys.Where(k => k.Tenant == tenantId).ToList()) _indexes.TryRemove(key, out _);
    }
}
