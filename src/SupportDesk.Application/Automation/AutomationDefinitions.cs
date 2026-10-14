using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SupportDesk.Domain;
using SupportDesk.Domain.Rules;

namespace SupportDesk.Application.Automation;

/// <summary>JSON conventions for rule and blueprint definitions (camelCase, case-insensitive, nulls omitted).</summary>
public static class AutomationJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Parses a definition; malformed JSON becomes a 400 validation error on "definition".</summary>
    public static T Parse<T>(string? json, string field = "definition") where T : new()
    {
        if (string.IsNullOrWhiteSpace(json)) return new T();
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options) ?? new T();
        }
        catch (JsonException ex)
        {
            throw new Tickets.ValidationException(new Dictionary<string, string> { [field] = $"The definition is not valid: {ex.Message}" });
        }
    }
}

/// <summary>
/// Executable rule definition stored in <see cref="Domain.Automation.RuleVersion.DefinitionJson"/>:
/// {"trigger":{"fields":["priority"]},"conditions":{logic,conditions,groups},"actions":[{"type":"set_priority","params":{"priority":"High"}}]}.
/// </summary>
public sealed class RuleDefinition
{
    [JsonPropertyName("trigger")] public TriggerOptions? Trigger { get; set; }
    [JsonPropertyName("conditions")] public ConditionGroup Conditions { get; set; } = new();
    [JsonPropertyName("actions")] public List<RuleAction> Actions { get; set; } = [];

    public static RuleDefinition Parse(string? json) => AutomationJson.Parse<RuleDefinition>(json);
    public string ToJson() => AutomationJson.Serialize(this);
}

/// <summary>Trigger refinements. For ticket.updated: fire only when one of <see cref="Fields"/> changed.</summary>
public sealed class TriggerOptions
{
    [JsonPropertyName("fields")] public List<string>? Fields { get; set; }
}

public sealed class RuleAction
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("params")] public JsonElement? Params { get; set; }

    [JsonIgnore] public ActionParams P => new(Params);
}

/// <summary>Typed, lenient access to an action's params object.</summary>
public readonly struct ActionParams(JsonElement? element)
{
    public bool IsObject => element is { ValueKind: JsonValueKind.Object };

    public bool Has(string name) => TryGet(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    public bool TryGet(string name, out JsonElement value)
    {
        value = default;
        if (element is not { ValueKind: JsonValueKind.Object } obj) return false;
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        return false;
    }

    public string? Str(string name) => TryGet(name, out var v) ? v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString(),
        _ => v.GetRawText()
    } : null;

    public Guid? Guid(string name) => System.Guid.TryParse(Str(name), out var g) && g != System.Guid.Empty ? g : null;

    public bool Bool(string name, bool fallback = false)
    {
        if (!TryGet(name, out var v)) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : fallback,
            _ => fallback
        };
    }

    public int? Int(string name)
    {
        if (!TryGet(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return (int)Math.Round(d);
        return int.TryParse(v.ValueKind == JsonValueKind.String ? v.GetString() : null, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null;
    }

    public double? Double(string name)
    {
        if (!TryGet(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        return double.TryParse(v.ValueKind == JsonValueKind.String ? v.GetString() : null, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : null;
    }

    public List<string> StrList(string name)
    {
        if (!TryGet(name, out var v)) return [];
        if (v.ValueKind == JsonValueKind.Array)
            return v.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString()).Where(s => s.Length > 0).ToList();
        if (v.ValueKind == JsonValueKind.String)
            return (v.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return [];
    }

    public List<Guid> GuidList(string name) => StrList(name).Select(s => System.Guid.TryParse(s, out var g) ? g : System.Guid.Empty).Where(g => g != System.Guid.Empty).ToList();

    public Dictionary<string, string> StrMap(string name)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!TryGet(name, out var v) || v.ValueKind != JsonValueKind.Object) return map;
        foreach (var p in v.EnumerateObject()) map[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString();
        return map;
    }
}

/// <summary>Action type names (stable API identifiers used in definitions).</summary>
public static class ActionTypes
{
    public const string Assign = "assign";
    public const string SetPriority = "set_priority";
    public const string SetSeverity = "set_severity";
    public const string SetStatus = "set_status";
    public const string SetSubstatus = "set_substatus";
    public const string SetCategory = "set_category";
    public const string SetIssueType = "set_issue_type";
    public const string TransferDepartment = "transfer_department";
    public const string SetCustomField = "set_custom_field";
    public const string AddTags = "add_tags";
    public const string RemoveTags = "remove_tags";
    public const string AddNote = "add_note";
    public const string AddWatcher = "add_watcher";
    public const string NotifyAgents = "notify_agents";
    public const string SendMessage = "send_message";
    public const string ReplyWithTemplate = "reply_with_template";
    public const string CreateTask = "create_task";
    public const string CreateChildTicket = "create_child_ticket";
    public const string InvokeWebhook = "invoke_webhook";
    public const string RequestApproval = "request_approval";
    public const string Escalate = "escalate";
    public const string AiClassify = "ai_classify";
    public const string AiSummarize = "ai_summarize";
    public const string UpdateErpRecord = "update_erp_record";
}

/// <summary>
/// Blueprint (state-transition) definition stored in <see cref="Domain.Automation.WorkflowVersion.DefinitionJson"/>.
/// </summary>
public sealed class BlueprintDefinition
{
    [JsonPropertyName("states")] public List<BlueprintState> States { get; set; } = [];
    [JsonPropertyName("transitions")] public List<BlueprintTransition> Transitions { get; set; } = [];

    public static BlueprintDefinition Parse(string? json) => AutomationJson.Parse<BlueprintDefinition>(json);
    public string ToJson() => AutomationJson.Serialize(this);

    public BlueprintState? State(string? key) => key is null ? null : States.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
    public BlueprintState? Initial => States.FirstOrDefault(s => s.IsInitial);

    public IEnumerable<BlueprintTransition> From(string stateKey) =>
        Transitions.Where(t => t.From.Any(f => f == "*" || string.Equals(f, stateKey, StringComparison.OrdinalIgnoreCase)));
}

public sealed class BlueprintState
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("statusId")] public Guid? StatusId { get; set; }
    [JsonPropertyName("substatus")] public string? Substatus { get; set; }
    [JsonPropertyName("x")] public double? X { get; set; }
    [JsonPropertyName("y")] public double? Y { get; set; }
    [JsonPropertyName("isInitial")] public bool IsInitial { get; set; }
    [JsonPropertyName("isFinal")] public bool IsFinal { get; set; }
}

public sealed class BlueprintTransition
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>Source state key(s): a string, an array, or "*" for any non-final state.</summary>
    [JsonPropertyName("from"), JsonConverter(typeof(StringOrArrayConverter))] public List<string> From { get; set; } = [];
    [JsonPropertyName("to")] public string To { get; set; } = "";
    [JsonPropertyName("allowedRoleIds")] public List<Guid> AllowedRoleIds { get; set; } = [];
    [JsonPropertyName("requiredFields")] public List<string> RequiredFields { get; set; } = [];
    [JsonPropertyName("validation")] public ConditionGroup? Validation { get; set; }
    [JsonPropertyName("validationMessage")] public string? ValidationMessage { get; set; }
    [JsonPropertyName("approval")] public BlueprintApproval? Approval { get; set; }
    [JsonPropertyName("actions")] public List<RuleAction> Actions { get; set; } = [];
    [JsonPropertyName("requireComment")] public bool RequireComment { get; set; }
}

public sealed class BlueprintApproval
{
    [JsonPropertyName("roleId")] public Guid? RoleId { get; set; }
    [JsonPropertyName("userId")] public Guid? UserId { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
}

/// <summary>Accepts "a" or ["a","b"] for list-of-string properties.</summary>
public sealed class StringOrArrayConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null: return [];
            case JsonTokenType.String: return [reader.GetString() ?? ""];
            case JsonTokenType.StartArray:
            {
                var list = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.String) list.Add(reader.GetString() ?? "");
                    else throw new JsonException("Expected a string.");
                }
                return list;
            }
            default: throw new JsonException("Expected a string or an array of strings.");
        }
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        if (value.Count == 1)
        {
            writer.WriteStringValue(value[0]);
            return;
        }
        writer.WriteStartArray();
        foreach (var v in value) writer.WriteStringValue(v);
        writer.WriteEndArray();
    }
}

/// <summary>Condition context that overlays extra values (inbound webhook payload, transition field input) on a ticket context.</summary>
public sealed class OverlayConditionContext(IConditionContext inner, IReadOnlyDictionary<string, object?> overlay) : IConditionContext
{
    public DateTime Now => inner.Now;

    public bool TryGetValue(string field, out object? value)
    {
        if (overlay.TryGetValue(field, out value)) return true;
        return inner.TryGetValue(field, out value);
    }

    public bool TryGetPrevious(string field, out object? previous) => inner.TryGetPrevious(field, out previous);
}

/// <summary>Errors that a retry may fix (concurrency, timeouts, network).</summary>
public static class TransientErrors
{
    public static bool IsTransient(Exception ex) => ex switch
    {
        Tickets.ConcurrencyConflictException => true,
        Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException => true,
        HttpRequestException => true,
        TimeoutException => true,
        TaskCanceledException => true,
        DomainException d when d.Code is "transient_failure" or "rate_limited" => true,
        Microsoft.Data.SqlClient.SqlException sql => sql.Number is 1205 or -2 or 40501 or 40613 or 49918 or 49919 or 49920,
        Microsoft.EntityFrameworkCore.DbUpdateException { InnerException: Microsoft.Data.SqlClient.SqlException s } => s.Number is 1205 or -2,
        _ => ex.InnerException is not null && IsTransient(ex.InnerException)
    };
}
