using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SupportDesk.Domain.Rules;

/// <summary>
/// Nested AND/OR condition tree shared by automation rules, SLA policies, blueprint validations, routing and views.
/// JSON: {"logic":"and","conditions":[{"field":"priority","op":"in","value":["High","Critical"]}],"groups":[{…}]}
/// </summary>
public sealed class ConditionGroup
{
    [JsonPropertyName("logic")] public string Logic { get; set; } = "and";
    [JsonPropertyName("conditions")] public List<Condition> Conditions { get; set; } = [];
    [JsonPropertyName("groups")] public List<ConditionGroup> Groups { get; set; } = [];

    [JsonIgnore] public bool IsEmpty => Conditions.Count == 0 && Groups.All(g => g.IsEmpty);

    public static ConditionGroup Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ConditionGroup();
        return JsonSerializer.Deserialize<ConditionGroup>(json, ConditionJson.Options) ?? new ConditionGroup();
    }

    public string ToJson() => JsonSerializer.Serialize(this, ConditionJson.Options);

    public IEnumerable<Condition> AllConditions() => Conditions.Concat(Groups.SelectMany(g => g.AllConditions()));
}

public sealed class Condition
{
    [JsonPropertyName("field")] public string Field { get; set; } = "";
    [JsonPropertyName("op")] public string Op { get; set; } = ConditionOps.Equals;
    [JsonPropertyName("value")] public JsonElement? Value { get; set; }
}

public static class ConditionJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public static class ConditionOps
{
    public new const string Equals = "eq";
    public const string NotEquals = "neq";
    public const string In = "in";
    public const string NotIn = "not_in";
    public const string Contains = "contains";
    public const string NotContains = "not_contains";
    public const string StartsWith = "starts_with";
    public const string EndsWith = "ends_with";
    public const string IsEmpty = "is_empty";
    public const string IsNotEmpty = "is_not_empty";
    public const string GreaterThan = "gt";
    public const string GreaterOrEqual = "gte";
    public const string LessThan = "lt";
    public const string LessOrEqual = "lte";
    public const string Between = "between";
    public const string Before = "before";
    public const string After = "after";
    /// <summary>Date within the last N minutes ("value": 120).</summary>
    public const string WithinLastMinutes = "within_last_minutes";
    /// <summary>Date older than N minutes.</summary>
    public const string OlderThanMinutes = "older_than_minutes";
    public const string Changed = "changed";
    public const string ChangedTo = "changed_to";
    public const string ChangedFrom = "changed_from";
    public const string Matches = "matches";
    public const string ContainsAny = "contains_any";

    public static readonly string[] All =
    [
        Equals, NotEquals, In, NotIn, Contains, NotContains, StartsWith, EndsWith, IsEmpty, IsNotEmpty, GreaterThan,
        GreaterOrEqual, LessThan, LessOrEqual, Between, Before, After, WithinLastMinutes, OlderThanMinutes, Changed,
        ChangedTo, ChangedFrom, Matches, ContainsAny
    ];
}

/// <summary>Supplies field values to the evaluator. Values: string, decimal/double/int/long, bool, DateTime (UTC), IEnumerable&lt;string&gt;, Guid or null.</summary>
public interface IConditionContext
{
    DateTime Now { get; }
    bool TryGetValue(string field, out object? value);
    /// <summary>Previous value for change operators; false when the field did not change in the triggering event.</summary>
    bool TryGetPrevious(string field, out object? previous);
}

/// <summary>Simple dictionary-backed context (tests, simulation, webhooks).</summary>
public sealed class DictionaryConditionContext(IDictionary<string, object?> values, DateTime now,
    IDictionary<string, object?>? previous = null) : IConditionContext
{
    public DateTime Now { get; } = now;
    public bool TryGetValue(string field, out object? value) => values.TryGetValue(field, out value);
    public bool TryGetPrevious(string field, out object? prev)
    {
        prev = null;
        return previous is not null && previous.TryGetValue(field, out prev);
    }
}

public sealed record ConditionTrace(string Field, string Op, string? Expected, string? Actual, bool Result);

public static class ConditionEvaluator
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static bool Evaluate(ConditionGroup group, IConditionContext ctx) => Evaluate(group, ctx, null);

    public static bool Evaluate(ConditionGroup group, IConditionContext ctx, List<ConditionTrace>? trace)
    {
        if (group.IsEmpty) return true;
        var isOr = string.Equals(group.Logic, "or", StringComparison.OrdinalIgnoreCase);
        var results = group.Conditions.Select(c => EvaluateOne(c, ctx, trace))
            .Concat(group.Groups.Where(g => !g.IsEmpty).Select(g => Evaluate(g, ctx, trace)));
        // Materialise so that a simulation trace records every condition (no short-circuit).
        var list = trace is null ? results : results.ToList();
        return isOr ? list.Any(r => r) : list.All(r => r);
    }

    public static bool EvaluateOne(Condition c, IConditionContext ctx, List<ConditionTrace>? trace = null)
    {
        ctx.TryGetValue(c.Field, out var actual);
        bool result;
        try
        {
            result = Apply(c, actual, ctx);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or RegexMatchTimeoutException or ArgumentException)
        {
            result = false;
        }
        trace?.Add(new ConditionTrace(c.Field, c.Op, c.Value?.ToString(), Describe(actual), result));
        return result;
    }

    private static bool Apply(Condition c, object? actual, IConditionContext ctx)
    {
        var op = c.Op.ToLowerInvariant();
        switch (op)
        {
            case ConditionOps.IsEmpty: return IsEmptyValue(actual);
            case ConditionOps.IsNotEmpty: return !IsEmptyValue(actual);
            case ConditionOps.Changed: return ctx.TryGetPrevious(c.Field, out _);
            case ConditionOps.ChangedTo:
                return ctx.TryGetPrevious(c.Field, out _) && EqualsAny(actual, ExpectedList(c.Value));
            case ConditionOps.ChangedFrom:
                return ctx.TryGetPrevious(c.Field, out var prev) && EqualsAny(prev, ExpectedList(c.Value));
        }

        if (actual is IEnumerable<string> list && actual is not string)
        {
            var items = list.ToList();
            var expected = ExpectedList(c.Value);
            return op switch
            {
                ConditionOps.Contains or ConditionOps.Equals or ConditionOps.In or ConditionOps.ContainsAny =>
                    expected.Any(e => items.Contains(e, StringComparer.OrdinalIgnoreCase)),
                ConditionOps.NotContains or ConditionOps.NotEquals or ConditionOps.NotIn =>
                    !expected.Any(e => items.Contains(e, StringComparer.OrdinalIgnoreCase)),
                _ => false
            };
        }

        switch (op)
        {
            case ConditionOps.Equals: return EqualsAny(actual, ExpectedList(c.Value));
            case ConditionOps.NotEquals: return !EqualsAny(actual, ExpectedList(c.Value));
            case ConditionOps.In: return EqualsAny(actual, ExpectedList(c.Value));
            case ConditionOps.NotIn: return !EqualsAny(actual, ExpectedList(c.Value));
            case ConditionOps.Contains:
                return AsString(actual) is { } s1 && s1.Contains(ExpectedString(c.Value), StringComparison.OrdinalIgnoreCase);
            case ConditionOps.ContainsAny:
                return AsString(actual) is { } s1b && ExpectedList(c.Value).Any(e => s1b.Contains(e, StringComparison.OrdinalIgnoreCase));
            case ConditionOps.NotContains:
                return !(AsString(actual) is { } s2 && s2.Contains(ExpectedString(c.Value), StringComparison.OrdinalIgnoreCase));
            case ConditionOps.StartsWith:
                return AsString(actual) is { } s3 && s3.StartsWith(ExpectedString(c.Value), StringComparison.OrdinalIgnoreCase);
            case ConditionOps.EndsWith:
                return AsString(actual) is { } s4 && s4.EndsWith(ExpectedString(c.Value), StringComparison.OrdinalIgnoreCase);
            case ConditionOps.Matches:
                return AsString(actual) is { } s5 && Regex.IsMatch(s5, ExpectedString(c.Value), RegexOptions.IgnoreCase, RegexTimeout);
            case ConditionOps.GreaterThan: return Compare(actual, c.Value) > 0;
            case ConditionOps.GreaterOrEqual: return Compare(actual, c.Value) >= 0;
            case ConditionOps.LessThan: return Compare(actual, c.Value) < 0;
            case ConditionOps.LessOrEqual: return Compare(actual, c.Value) <= 0;
            case ConditionOps.Between:
            {
                var bounds = c.Value is { ValueKind: JsonValueKind.Array } arr ? arr.EnumerateArray().ToList() : [];
                return bounds.Count == 2 && Compare(actual, bounds[0]) >= 0 && Compare(actual, bounds[1]) <= 0;
            }
            case ConditionOps.Before: return AsDate(actual) is { } d1 && d1 < ExpectedDate(c.Value, ctx.Now);
            case ConditionOps.After: return AsDate(actual) is { } d2 && d2 > ExpectedDate(c.Value, ctx.Now);
            case ConditionOps.WithinLastMinutes:
                return AsDate(actual) is { } d3 && d3 >= ctx.Now.AddMinutes(-ExpectedNumber(c.Value)) && d3 <= ctx.Now;
            case ConditionOps.OlderThanMinutes:
                return AsDate(actual) is { } d4 && d4 < ctx.Now.AddMinutes(-ExpectedNumber(c.Value));
            default:
                throw new InvalidOperationException($"Unknown operator '{c.Op}'.");
        }
    }

    public static bool IsEmptyValue(object? v) => v switch
    {
        null => true,
        string s => string.IsNullOrWhiteSpace(s),
        IEnumerable<string> l => !l.Any(),
        Guid g => g == Guid.Empty,
        _ => false
    };

    private static bool EqualsAny(object? actual, IReadOnlyList<string> expected)
    {
        var a = AsString(actual);
        if (a is null) return expected.Any(e => e.Length == 0 || e.Equals("null", StringComparison.OrdinalIgnoreCase));
        if (actual is bool b) return expected.Any(e => bool.TryParse(e, out var eb) && eb == b);
        if (IsNumeric(actual))
        {
            var n = Convert.ToDecimal(actual, CultureInfo.InvariantCulture);
            return expected.Any(e => decimal.TryParse(e, NumberStyles.Any, CultureInfo.InvariantCulture, out var en) && en == n);
        }
        return expected.Any(e => string.Equals(e, a, StringComparison.OrdinalIgnoreCase));
    }

    private static int Compare(object? actual, JsonElement? expected)
    {
        if (expected is null) throw new FormatException("missing value");
        return Compare(actual, expected.Value);
    }

    private static int Compare(object? actual, JsonElement expected)
    {
        if (actual is null) throw new FormatException("null");
        if (actual is DateTime dt) return dt.CompareTo(ParseDate(expected.ToString()));
        if (IsNumeric(actual))
        {
            var a = Convert.ToDecimal(actual, CultureInfo.InvariantCulture);
            var e = expected.ValueKind == JsonValueKind.Number ? expected.GetDecimal()
                : decimal.Parse(expected.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture);
            return a.CompareTo(e);
        }
        // Enum-like ordered values (priority) compare by rank when both parse.
        if (Enum.TryParse<TicketPriority>(actual.ToString(), true, out var pa) &&
            Enum.TryParse<TicketPriority>(expected.ToString(), true, out var pe))
            return ((int)pa).CompareTo((int)pe);
        return string.Compare(actual.ToString(), expected.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNumeric(object? v) => v is int or long or decimal or double or float or short;

    public static string? AsString(object? v) => v switch
    {
        null => null,
        string s => s,
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        Enum e => e.ToString(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString()
    };

    private static DateTime? AsDate(object? v) => v switch
    {
        DateTime d => d,
        string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) => d,
        _ => null
    };

    private static DateTime ExpectedDate(JsonElement? v, DateTime now)
    {
        var s = v?.ToString() ?? throw new FormatException("missing date");
        if (s.Equals("now", StringComparison.OrdinalIgnoreCase)) return now;
        return ParseDate(s);
    }

    private static DateTime ParseDate(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static double ExpectedNumber(JsonElement? v) =>
        v is { ValueKind: JsonValueKind.Number } n ? n.GetDouble()
            : double.Parse(v?.ToString() ?? "0", CultureInfo.InvariantCulture);

    private static string ExpectedString(JsonElement? v) => v is null ? "" :
        v.Value.ValueKind == JsonValueKind.String ? v.Value.GetString() ?? "" : v.Value.ToString();

    public static IReadOnlyList<string> ExpectedList(JsonElement? v)
    {
        if (v is null || v.Value.ValueKind == JsonValueKind.Null) return [""];
        if (v.Value.ValueKind == JsonValueKind.Array)
            return v.Value.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString()).ToList();
        return [ExpectedString(v)];
    }

    private static string? Describe(object? v) => v is not string && v is IEnumerable<string> l ? "[" + string.Join(",", l) + "]" : AsString(v);
}
