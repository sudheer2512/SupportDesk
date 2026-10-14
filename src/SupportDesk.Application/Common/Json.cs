using System.Text.Json;
using System.Text.Json.Serialization;

namespace SupportDesk.Application.Common;

/// <summary>JSON conventions for JSON columns, outbox payloads and integration bodies (camelCase, enums as strings).</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = Create();

    public static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        return JsonSerializer.Deserialize<T>(json, Options);
    }

    public static T DeserializeOr<T>(string? json, T fallback)
    {
        if (string.IsNullOrWhiteSpace(json)) return fallback;
        try { return JsonSerializer.Deserialize<T>(json, Options) ?? fallback; }
        catch (JsonException) { return fallback; }
    }

    public static JsonElement Element(string? json, string fallback = "{}")
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? fallback : json);
        return doc.RootElement.Clone();
    }

    public static JsonElement? ElementOrNull(string? json) => string.IsNullOrWhiteSpace(json) ? null : Element(json);

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static List<string> StringList(string? json) => DeserializeOr(json, new List<string>());

    public static List<Guid> GuidList(string? json) => DeserializeOr(json, new List<Guid>());

    public static Dictionary<string, JsonElement> Object(string? json) =>
        DeserializeOr(json, new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase));
}
