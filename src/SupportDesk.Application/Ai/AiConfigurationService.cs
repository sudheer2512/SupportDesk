using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Domain.Ai;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Ai;

/// <summary>Host-level AI options (appsettings "Ai" section).</summary>
public sealed class AiOptions
{
    /// <summary>Per-call timeout for remote model calls.</summary>
    public int TimeoutSeconds { get; set; } = 60;
    /// <summary>Allow http:// and private/loopback endpoints (local Ollama/vLLM servers). Keep false in multi-tenant production.</summary>
    public bool AllowPrivateEndpoints { get; set; }
    /// <summary>Agents only act on events younger than this (older events are history being replayed or seeded).</summary>
    public int AgentMaxEventAgeMinutes { get; set; } = 60;
}

public sealed record AiFeatureSettings(bool Enabled, IReadOnlyList<string> Roles, bool AutoApply, double MinConfidence)
{
    public static readonly AiFeatureSettings Default = new(true, [], false, 0.75);
}

/// <summary>An explicitly approved low-risk scenario in which the auto-responder may reply without a human.</summary>
public sealed record AutoReplyScenario(
    string Key,
    string Name,
    string? Description,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<Guid> CategoryIds,
    IReadOnlyList<Guid> ArticleIds,
    IReadOnlyList<string> Channels,
    double MinConfidence,
    bool Enabled);

/// <summary>Effective AI settings of the current tenant.</summary>
public sealed class AiSettings(AiConfiguration entity, IReadOnlyDictionary<string, AiFeatureSettings> features, IReadOnlyList<AutoReplyScenario> scenarios,
    AiProviderSettings chat, AiProviderSettings embedding)
{
    public AiConfiguration Entity { get; } = entity;
    public IReadOnlyDictionary<string, AiFeatureSettings> Features { get; } = features;
    public IReadOnlyList<AutoReplyScenario> Scenarios { get; } = scenarios;
    public AiProviderSettings Chat { get; } = chat;
    public AiProviderSettings Embedding { get; } = embedding;
    public bool IsEnabled => Entity.IsEnabled;
    public bool ChatIsLocal => AiProviderNames.IsLocal(Chat.Provider);
    public bool EmbeddingIsLocal => AiProviderNames.IsLocal(Embedding.Provider);
    public string EmbeddingModelKey => EmbeddingIsLocal ? AiProviderNames.LocalEmbeddingModel : $"{Embedding.Provider}:{Embedding.Model}";

    public AiFeatureSettings Feature(string feature) => Features.TryGetValue(feature, out var f) ? f : AiFeatureSettings.Default;
}

/// <summary>Loads (and lazily creates) the tenant's <see cref="AiConfiguration"/>; decrypts the API key only when a remote provider is used.</summary>
public sealed class AiConfigurationService(DeskDbContext db, ISecretProtector secrets, AiOptions options)
{
    private AiSettings? _settings;

    public async Task<AiConfiguration> GetOrCreateEntityAsync(CancellationToken ct)
    {
        var entity = await db.AiConfigurations.OrderBy(c => c.Id).FirstOrDefaultAsync(ct);
        if (entity is not null) return entity;
        entity = new AiConfiguration
        {
            FeaturesJson = Json.Serialize(AiFeatures.All.ToDictionary(f => f, _ => new { enabled = true, autoApply = false, minConfidence = 0.75 }))
        };
        db.AiConfigurations.Add(entity);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.Entry(entity).State = EntityState.Detached;
            entity = await db.AiConfigurations.OrderBy(c => c.Id).FirstAsync(ct);
        }
        return entity;
    }

    public async Task<AiSettings> GetAsync(CancellationToken ct)
    {
        if (_settings is not null) return _settings;
        var e = await db.AiConfigurations.AsNoTracking().OrderBy(c => c.Id).FirstOrDefaultAsync(ct) ?? await GetOrCreateEntityAsync(ct);
        _settings = Build(e);
        return _settings;
    }

    public void Invalidate() => _settings = null;

    private AiSettings Build(AiConfiguration e)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 5, 300));
        string? key = null;
        var provider = AiProviderNames.Normalize(e.Provider);
        if (provider.Length == 0) provider = AiProviderNames.Local;
        var embeddingProvider = AiProviderNames.Normalize(e.EmbeddingProvider);
        if (embeddingProvider.Length == 0 || !AiProviderNames.EmbeddingCapable.Contains(embeddingProvider)) embeddingProvider = AiProviderNames.Local;
        if ((!AiProviderNames.IsLocal(provider) || !AiProviderNames.IsLocal(embeddingProvider)) && !string.IsNullOrEmpty(e.ApiKeyProtected))
        {
            try { key = secrets.Unprotect(e.ApiKeyProtected); }
            catch (Exception ex) when (ex is CryptographicException or FormatException) { key = null; }
        }
        var chat = AiProviderNames.IsLocal(provider)
            ? new AiProviderSettings(AiProviderNames.Local, AiProviderNames.LocalChatModel, null, null, timeout)
            : new AiProviderSettings(provider, e.Model, e.Endpoint, key, timeout);
        var embedding = AiProviderNames.IsLocal(embeddingProvider)
            ? new AiProviderSettings(AiProviderNames.Local, AiProviderNames.LocalEmbeddingModel, null, null, timeout)
            : new AiProviderSettings(embeddingProvider, e.EmbeddingModel, e.Endpoint, key, timeout);
        return new AiSettings(e, ParseFeatures(e.FeaturesJson), ParseScenarios(e.AutoReplyScenariosJson), chat, embedding);
    }

    public static Dictionary<string, AiFeatureSettings> ParseFeatures(string? json)
    {
        var result = new Dictionary<string, AiFeatureSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, v) in Json.Object(json))
        {
            if (v.ValueKind != JsonValueKind.Object) continue;
            bool B(string name, bool fallback) => v.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetBoolean() : fallback;
            var roles = v.TryGetProperty("roles", out var r) && r.ValueKind == JsonValueKind.Array
                ? r.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => x.Length > 0).ToList()
                : [];
            var min = v.TryGetProperty("minConfidence", out var m) && m.ValueKind == JsonValueKind.Number ? Math.Clamp(m.GetDouble(), 0, 1) : 0.75;
            result[key] = new AiFeatureSettings(B("enabled", true), roles, B("autoApply", false), min);
        }
        return result;
    }

    public static List<AutoReplyScenario> ParseScenarios(string? json)
    {
        var list = new List<AutoReplyScenario>();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var s in doc.RootElement.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) continue;
                string? S(string n) => s.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                List<string> L(string n) => s.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.Array
                    ? p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).ToList() : [];
                List<Guid> G(string n) => L(n).Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
                var key = S("key");
                if (string.IsNullOrWhiteSpace(key)) continue;
                var min = s.TryGetProperty("minConfidence", out var m) && m.ValueKind == JsonValueKind.Number ? Math.Clamp(m.GetDouble(), 0, 1) : 0.8;
                var enabled = !s.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False;
                list.Add(new AutoReplyScenario(key.Trim(), S("name") ?? key, S("description"), L("keywords"), G("categoryIds"), G("articleIds"), L("channels"), min, enabled));
            }
        }
        catch (JsonException) { }
        return list;
    }

    public static string SerializeFeatures(IReadOnlyDictionary<string, AiFeatureSettings> features) =>
        Json.Serialize(features.ToDictionary(kv => kv.Key, kv => new
        {
            enabled = kv.Value.Enabled, roles = kv.Value.Roles, autoApply = kv.Value.AutoApply, minConfidence = Math.Round(kv.Value.MinConfidence, 3)
        }));

    public static string SerializeScenarios(IEnumerable<AutoReplyScenario> scenarios) =>
        Json.Serialize(scenarios.Select(s => new
        {
            key = s.Key, name = s.Name, description = s.Description, keywords = s.Keywords, categoryIds = s.CategoryIds, articleIds = s.ArticleIds,
            channels = s.Channels, minConfidence = s.MinConfidence, enabled = s.Enabled
        }));
}
