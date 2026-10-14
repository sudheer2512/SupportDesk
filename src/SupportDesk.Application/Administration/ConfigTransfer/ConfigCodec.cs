using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SupportDesk.Domain;

namespace SupportDesk.Application.Administration.ConfigTransfer;

/// <summary>Id → "kind/key" map of the tenant whose configuration is being exported (or compared).</summary>
public sealed class ConfigRefs
{
    private readonly Dictionary<Guid, string> _byId = new();
    public void Add(Guid id, string kind, string key) => _byId[id] = $"{kind}/{key}";
    public bool TryGet(Guid id, out string reference) => _byId.TryGetValue(id, out reference!);
    public IReadOnlyDictionary<Guid, string> All => _byId;
}

/// <summary>State while applying a bundle: "kind/key" → target id resolution, warnings, actor and time.</summary>
public sealed class ConfigApplyContext(DateTime now, Guid? userId, string changeNote)
{
    public Dictionary<string, Guid> Resolver { get; } = new(StringComparer.Ordinal);
    public List<string> Warnings { get; } = [];
    public DateTime Now { get; } = now;
    public Guid? UserId { get; } = userId;
    public string ChangeNote { get; } = changeNote;

    public Guid? Resolve(string reference, string context)
    {
        if (Resolver.TryGetValue(reference, out var id)) return id;
        var w = $"{context}: reference to '{reference}' was not found in this workspace and was cleared.";
        if (!Warnings.Contains(w) && Warnings.Count < 200) Warnings.Add(w);
        return null;
    }
}

/// <summary>
/// Converts configuration entities to portable JSON and back. Scalar public properties are copied; GUIDs that point at
/// known items become <c>"@ref:kind/key"</c>; string properties ending in "Json" are embedded as JSON with the same GUID
/// replacement applied recursively, so references inside rule definitions, conditions and layouts are remapped too.
/// </summary>
public static class ConfigCodec
{
    public const string RefPrefix = "@ref:";

    private static readonly HashSet<string> BaseExclude = new(StringComparer.Ordinal)
    {
        "Id", "TenantId", "CreatedAt", "UpdatedAt", "CreatedById", "UpdatedById", "RowVersion", "IsDeleted", "DeletedAt"
    };

    public static IEnumerable<PropertyInfo> Properties(Type type, IReadOnlySet<string>? exclude) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && IsScalar(p.PropertyType) && !BaseExclude.Contains(p.Name) && (exclude is null || !exclude.Contains(p.Name)))
            .OrderBy(p => p.Name, StringComparer.Ordinal);

    private static bool IsScalar(Type t)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        return u.IsEnum || u == typeof(string) || u == typeof(bool) || u == typeof(int) || u == typeof(long) || u == typeof(decimal) || u == typeof(double)
               || u == typeof(DateTime) || u == typeof(DateOnly) || u == typeof(Guid);
    }

    public static JsonObject ExportObject(object entity, ConfigRefs refs, IReadOnlySet<string>? exclude)
    {
        var o = new JsonObject();
        foreach (var p in Properties(entity.GetType(), exclude))
        {
            var value = p.GetValue(entity);
            o[p.Name] = value switch
            {
                null => null,
                Guid g => refs.TryGet(g, out var r) ? RefPrefix + r : g.ToString(),
                string s when p.Name.EndsWith("Json", StringComparison.Ordinal) => ExportJson(s, refs) ?? JsonValue.Create(s),
                string s => JsonValue.Create(s),
                Enum e => JsonValue.Create(e.ToString()),
                DateTime d => JsonValue.Create(d.ToString("O", CultureInfo.InvariantCulture)),
                DateOnly d => JsonValue.Create(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                decimal m => JsonValue.Create(m),
                double x => JsonValue.Create(x),
                _ => JsonValue.Create(value.ToString())
            };
        }
        return o;
    }

    /// <summary>Parses a JSON column and replaces GUID strings of known items with references. Null when not valid JSON.</summary>
    public static JsonNode? ExportJson(string json, ConfigRefs refs)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "null" : json); }
        catch (JsonException) { return null; }
        return Map(node, s => Guid.TryParse(s, out var g) && refs.TryGet(g, out var r) ? RefPrefix + r : s, dropUnresolved: false);
    }

    /// <summary>Resolves references inside embedded JSON to target ids; unresolved references are removed (arrays) or nulled.</summary>
    public static string ImportJson(JsonNode? node, ConfigApplyContext ctx, string context = "definition")
    {
        var mapped = Map(node?.DeepClone(), s =>
        {
            if (!s.StartsWith(RefPrefix, StringComparison.Ordinal)) return s;
            return ctx.Resolve(s[RefPrefix.Length..], context)?.ToString();
        }, dropUnresolved: true);
        return mapped?.ToJsonString() ?? "null";
    }

    private static JsonNode? Map(JsonNode? node, Func<string, string?> map, bool dropUnresolved)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject o:
            {
                var result = new JsonObject();
                foreach (var (k, v) in o) result[k] = Map(v?.DeepClone(), map, dropUnresolved);
                return result;
            }
            case JsonArray a:
            {
                var result = new JsonArray();
                foreach (var item in a)
                {
                    var mapped = Map(item?.DeepClone(), map, dropUnresolved);
                    if (dropUnresolved && item is JsonValue iv && iv.TryGetValue<string>(out var s) && s.StartsWith(RefPrefix, StringComparison.Ordinal) && mapped is null)
                        continue;
                    result.Add(mapped);
                }
                return result;
            }
            case JsonValue v when v.TryGetValue<string>(out var str):
                var m = map(str);
                return m is null ? null : JsonValue.Create(m);
            default:
                return node.DeepClone();
        }
    }

    /// <summary>
    /// Copies values from portable JSON into an entity. With <paramref name="refsOnly"/> false, plain scalars are set
    /// and references/JSON are skipped (phase 1); with true, only references and JSON columns are resolved (phase 2).
    /// When null, everything is applied in one pass (child rows).
    /// </summary>
    public static void ApplyObject(object entity, JsonObject data, ConfigApplyContext ctx, IReadOnlySet<string>? exclude, bool? refsOnly)
    {
        foreach (var p in Properties(entity.GetType(), exclude))
        {
            if (!data.TryGetPropertyValue(p.Name, out var node)) continue;
            var type = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            var isRef = type == typeof(Guid) || (p.PropertyType == typeof(string) && p.Name.EndsWith("Json", StringComparison.Ordinal));
            if (refsOnly is true && !isRef) continue;
            if (refsOnly is false && isRef && refsOnly is not null) continue;
            var context = $"{entity.GetType().Name}.{p.Name}";
            try
            {
                if (type == typeof(Guid))
                {
                    Guid? id = null;
                    if (node is JsonValue v && v.TryGetValue<string>(out var s))
                        id = s.StartsWith(RefPrefix, StringComparison.Ordinal) ? ctx.Resolve(s[RefPrefix.Length..], context) : Guid.TryParse(s, out var g) ? g : null;
                    if (id is null && p.PropertyType == typeof(Guid)) continue; // required id that cannot be resolved: keep current
                    p.SetValue(entity, id);
                }
                else if (p.PropertyType == typeof(string) && p.Name.EndsWith("Json", StringComparison.Ordinal))
                {
                    p.SetValue(entity, node is JsonValue sv && sv.TryGetValue<string>(out var raw) && !LooksLikeJson(raw) ? raw : ImportJson(node, ctx, context));
                }
                else if (node is null)
                {
                    if (!p.PropertyType.IsValueType || Nullable.GetUnderlyingType(p.PropertyType) is not null) p.SetValue(entity, null);
                }
                else if (type.IsEnum)
                {
                    if (Enum.TryParse(type, node.GetValue<string>(), true, out var e)) p.SetValue(entity, e);
                }
                else if (type == typeof(DateOnly))
                {
                    p.SetValue(entity, DateOnly.Parse(node.GetValue<string>(), CultureInfo.InvariantCulture));
                }
                else if (type == typeof(DateTime))
                {
                    p.SetValue(entity, DateTime.Parse(node.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
                }
                else
                {
                    p.SetValue(entity, node.Deserialize(p.PropertyType));
                }
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or JsonException or ArgumentException)
            {
                ctx.Warnings.Add($"{context}: value could not be applied ({ex.Message}).");
            }
        }
    }

    private static bool LooksLikeJson(string s) => s.TrimStart() is { Length: > 0 } t && (t[0] is '{' or '[' || t == "null");

    /// <summary>Field-level comparison of two exported items (current vs incoming).</summary>
    public static List<(string Field, string? Current, string? Incoming)> Compare(JsonObject current, JsonObject incoming)
    {
        var changes = new List<(string, string?, string?)>();
        foreach (var key in current.Select(k => k.Key).Union(incoming.Select(k => k.Key)).Distinct().OrderBy(k => k, StringComparer.Ordinal))
        {
            current.TryGetPropertyValue(key, out var a);
            incoming.TryGetPropertyValue(key, out var b);
            if (JsonNode.DeepEquals(a, b)) continue;
            changes.Add((key, Short(a), Short(b)));
        }
        return changes;
    }

    public static string? Short(JsonNode? node)
    {
        if (node is null) return null;
        var s = node is JsonValue v && v.TryGetValue<string>(out var str) ? str : node.ToJsonString();
        return s.Length > 300 ? s[..300] + "…" : s;
    }
}
