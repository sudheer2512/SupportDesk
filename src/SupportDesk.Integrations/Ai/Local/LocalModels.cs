using System.Collections.Concurrent;
using System.Text;
using SupportDesk.Domain.Rules;

namespace SupportDesk.Integrations.Ai.Local;

/// <summary>
/// Deterministic feature-hashing text embedder (384 dimensions): stemmed unigrams (weight 1) and bigrams (weight 0.7),
/// sub-linear term frequency, signed hashing (FNV-1a over UTF-8) and L2 normalisation. Same text → same vector on every
/// machine and process, so stored vectors stay valid across restarts.
/// </summary>
public static class FeatureHashingEmbedder
{
    public const int Dimensions = 384;
    public const double BigramWeight = 0.7;

    public static float[] Embed(string? text)
    {
        var vector = new float[Dimensions];
        var tokens = TextNormalization.Tokenize(text).ToList();
        if (tokens.Count == 0) return vector;
        var features = new Dictionary<string, (double Weight, int Count)>(StringComparer.Ordinal);
        void Add(string feature, double weight)
        {
            var current = features.GetValueOrDefault(feature);
            features[feature] = (weight, current.Count + 1);
        }
        foreach (var t in tokens) Add(t, 1.0);
        for (var i = 0; i < tokens.Count - 1; i++) Add(tokens[i] + " " + tokens[i + 1], BigramWeight);
        foreach (var (feature, (weight, count)) in features)
        {
            var hash = Fnv1a(feature);
            var index = (int)(hash % Dimensions);
            var sign = ((hash * 0x9E3779B1u) >> 31) == 0 ? 1.0 : -1.0;
            vector[index] += (float)(sign * weight * (1 + Math.Log(count)));
        }
        double norm = 0;
        foreach (var v in vector) norm += (double)v * v;
        if (norm <= 0) return vector;
        var inv = (float)(1 / Math.Sqrt(norm));
        for (var i = 0; i < vector.Length; i++) vector[i] *= inv;
        return vector;
    }

    public static uint Fnv1a(string s)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            hash ^= b;
            hash *= 16777619u;
        }
        return hash;
    }
}

/// <summary>
/// Multinomial naive Bayes with Laplace smoothing over stemmed unigrams and bigrams. Posteriors are computed from the
/// average per-feature log-likelihood (scaled by up to six features) so confidences stay calibrated instead of the
/// near-certain values plain naive Bayes produces on long texts.
/// </summary>
public sealed class NaiveBayesClassifier
{
    private readonly Dictionary<string, int> _docs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, int>> _features = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _totals = new(StringComparer.Ordinal);
    private readonly HashSet<string> _vocabulary = new(StringComparer.Ordinal);
    private const double Alpha = 1.0;

    public int Documents { get; private set; }
    public IReadOnlyCollection<string> Labels => _docs.Keys;
    public int Support(string label) => _docs.GetValueOrDefault(label);

    public static List<string> Features(string? text)
    {
        var tokens = TextNormalization.Tokenize(text).ToList();
        var features = new List<string>(tokens.Count * 2);
        features.AddRange(tokens);
        for (var i = 0; i < tokens.Count - 1; i++) features.Add(tokens[i] + "_" + tokens[i + 1]);
        return features;
    }

    public void Train(string text, string label)
    {
        var features = Features(text);
        if (features.Count == 0) return;
        Documents++;
        _docs[label] = _docs.GetValueOrDefault(label) + 1;
        if (!_features.TryGetValue(label, out var counts)) _features[label] = counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var f in features)
        {
            counts[f] = counts.GetValueOrDefault(f) + 1;
            _vocabulary.Add(f);
        }
        _totals[label] = _totals.GetValueOrDefault(label) + features.Count;
    }

    /// <summary>Labels with posterior probabilities (descending).</summary>
    public IReadOnlyList<(string Label, double Probability)> Predict(string? text)
    {
        if (Documents == 0) return [];
        var features = Features(text).Where(_vocabulary.Contains).ToList();
        var v = _vocabulary.Count;
        var scale = Math.Min(features.Count, 6);
        var scores = new List<(string Label, double Score)>();
        foreach (var (label, docCount) in _docs)
        {
            var prior = Math.Log((double)docCount / Documents);
            var counts = _features[label];
            var total = _totals[label];
            double loglik = 0;
            foreach (var f in features) loglik += Math.Log((counts.GetValueOrDefault(f) + Alpha) / (total + Alpha * v));
            var meanLoglik = features.Count == 0 ? 0 : loglik / features.Count;
            // Uniform reference so that "no evidence" leaves the prior in charge.
            var reference = Math.Log(1.0 / Math.Max(2, v));
            scores.Add((label, prior + scale * (meanLoglik - reference)));
        }
        var max = scores.Max(s => s.Score);
        var exp = scores.Select(s => (s.Label, Value: Math.Exp(s.Score - max))).ToList();
        var sum = exp.Sum(e => e.Value);
        return exp.Select(e => (e.Label, e.Value / sum)).OrderByDescending(e => e.Item2).ToList();
    }
}

/// <summary>Trained models per tenant corpus and field (corpus keys change whenever the tenant's labelled tickets change).</summary>
internal static class LocalModelCache
{
    private static readonly ConcurrentDictionary<string, NaiveBayesClassifier> Models = new();

    public static NaiveBayesClassifier GetOrTrain(string key, Func<NaiveBayesClassifier> train)
    {
        if (Models.TryGetValue(key, out var model)) return model;
        if (Models.Count > 256) Models.Clear();
        return Models.GetOrAdd(key, _ => train());
    }
}
