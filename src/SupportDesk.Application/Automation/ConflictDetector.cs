using System.Globalization;
using System.Text.Json;
using SupportDesk.Domain.Rules;

namespace SupportDesk.Application.Automation;

public sealed record RuleForAnalysis(Guid Id, string Name, string Trigger, int Order, bool StopProcessing, RuleDefinition Definition);

public sealed record RuleConflict(string Kind, string Trigger, Guid RuleId, string RuleName, Guid OtherRuleId, string OtherRuleName, string? Field, string Explanation);

public static class ConflictKinds
{
    public const string FieldConflict = "field_conflict";
    public const string AssignmentConflict = "assignment_conflict";
    public const string TagConflict = "tag_conflict";
    public const string StopProcessing = "stop_processing";
}

/// <summary>
/// Static analysis of active rules on the same trigger. Two rules are compared only when their conditions can match the
/// same ticket (a conservative satisfiability check over AND-ed equality, set, emptiness and range constraints — when in
/// doubt they are assumed to overlap). Reported: both set the same field to different values, assign to different owners,
/// add vs. remove the same tag, or an earlier rule stops processing so a later one never runs.
/// </summary>
public static class ConflictDetector
{
    private static readonly HashSet<string> MultiValued = new(["tags"], StringComparer.OrdinalIgnoreCase);

    public static List<RuleConflict> Detect(IReadOnlyList<RuleForAnalysis> rules)
    {
        var result = new List<RuleConflict>();
        foreach (var group in rules.GroupBy(r => r.Trigger))
        {
            var ordered = group.OrderBy(r => r.Order).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            for (var i = 0; i < ordered.Count; i++)
                for (var j = i + 1; j < ordered.Count; j++)
                    result.AddRange(Compare(ordered[i], ordered[j]));
        }
        return result;
    }

    /// <summary>Conflicts of <paramref name="rule"/> against the other rules (designer warnings for one rule).</summary>
    public static List<RuleConflict> DetectFor(RuleForAnalysis rule, IEnumerable<RuleForAnalysis> others) =>
        Detect(others.Where(o => o.Id != rule.Id && o.Trigger == rule.Trigger).Append(rule).ToList())
            .Where(c => c.RuleId == rule.Id || c.OtherRuleId == rule.Id).ToList();

    /// <summary>A runs before B.</summary>
    private static IEnumerable<RuleConflict> Compare(RuleForAnalysis a, RuleForAnalysis b)
    {
        if (!MayOverlap(a.Definition.Conditions, b.Definition.Conditions)) yield break;
        var trigger = a.Trigger;

        if (a.StopProcessing)
        {
            yield return new RuleConflict(ConflictKinds.StopProcessing, trigger, a.Id, a.Name, b.Id, b.Name, null,
                $"'{a.Name}' runs first and stops processing, so '{b.Name}' never runs on tickets that match both rules.");
            yield break; // B never runs together with A — field conflicts are moot.
        }

        var ea = Effects(a.Definition.Actions);
        var eb = Effects(b.Definition.Actions);
        foreach (var (field, va) in ea.Fields)
        {
            if (!eb.Fields.TryGetValue(field, out var vb) || string.Equals(va.Value, vb.Value, StringComparison.OrdinalIgnoreCase)) continue;
            yield return new RuleConflict(ConflictKinds.FieldConflict, trigger, a.Id, a.Name, b.Id, b.Name, field,
                $"Both rules set {va.Label}: '{a.Name}' sets {va.Display} and '{b.Name}' sets {vb.Display}. On tickets matching both, '{b.Name}' runs later and wins.");
        }
        if (ea.Owner is { } oa && eb.Owner is { } ob && !string.Equals(oa.Value, ob.Value, StringComparison.OrdinalIgnoreCase))
            yield return new RuleConflict(ConflictKinds.AssignmentConflict, trigger, a.Id, a.Name, b.Id, b.Name, "assignee",
                $"The rules assign to different owners: '{a.Name}' → {oa.Display}, '{b.Name}' → {ob.Display}. On tickets matching both the ticket is reassigned twice and '{b.Name}' wins.");
        foreach (var tag in ea.TagsAdded.Intersect(eb.TagsRemoved, StringComparer.OrdinalIgnoreCase))
            yield return new RuleConflict(ConflictKinds.TagConflict, trigger, a.Id, a.Name, b.Id, b.Name, "tags",
                $"'{a.Name}' adds the tag '{tag}' and '{b.Name}' removes it.");
        foreach (var tag in ea.TagsRemoved.Intersect(eb.TagsAdded, StringComparer.OrdinalIgnoreCase))
            yield return new RuleConflict(ConflictKinds.TagConflict, trigger, a.Id, a.Name, b.Id, b.Name, "tags",
                $"'{a.Name}' removes the tag '{tag}' and '{b.Name}' adds it again.");
    }

    // ───────────────────────────── Action effects ─────────────────────────────

    private sealed record Effect(string Label, string Value, string Display);

    private sealed class EffectSet
    {
        public Dictionary<string, Effect> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Effect? Owner { get; set; }
        public HashSet<string> TagsAdded { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> TagsRemoved { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static EffectSet Effects(IEnumerable<RuleAction> actions)
    {
        var e = new EffectSet();
        foreach (var a in actions)
        {
            var p = a.P;
            switch (a.Type)
            {
                case ActionTypes.SetPriority when p.Str("priority") is { } v: e.Fields["priority"] = new("priority", v, v); break;
                case ActionTypes.SetSeverity when p.Str("severity") is { } v: e.Fields["severity"] = new("severity", v, v); break;
                case ActionTypes.SetStatus when p.Str("statusId") is { } v:
                    e.Fields["status"] = new("status", v + "/" + p.Str("substatus"), $"status {v}{(p.Str("substatus") is { } s ? $" ({s})" : "")}");
                    break;
                case ActionTypes.SetSubstatus when p.Str("substatus") is { } v: e.Fields["substatus"] = new("substatus", v, v); break;
                case ActionTypes.SetCategory when p.Str("categoryId") is { } v:
                    e.Fields["category"] = new("category", v + "/" + p.Str("subcategoryId"), $"category {v}");
                    break;
                case ActionTypes.SetIssueType when p.Str("issueTypeId") is { } v: e.Fields["issueType"] = new("issue type", v, v); break;
                case ActionTypes.TransferDepartment when p.Str("departmentId") is { } v: e.Fields["department"] = new("department", v, $"department {v}"); break;
                case ActionTypes.SetCustomField when p.Str("key") is { } key:
                {
                    var k = key.StartsWith("cf:", StringComparison.OrdinalIgnoreCase) ? key[3..] : key;
                    var raw = p.TryGet("value", out var val) ? val.ValueKind == JsonValueKind.String ? val.GetString() ?? "" : val.GetRawText() : "null";
                    e.Fields["cf:" + k] = new($"custom field '{k}'", raw, raw);
                    break;
                }
                case ActionTypes.Assign:
                    if (p.Guid("userId") is { } user) e.Owner = new("assignee", "user:" + user, $"agent {user}");
                    else if (p.Bool("route")) e.Owner = new("assignee", "route:" + p.Str("teamId"), p.Guid("teamId") is { } rt ? $"routing within team {rt}" : "automatic routing");
                    else if (p.Guid("teamId") is { } team) e.Owner = new("assignee", "team:" + team, $"team {team}");
                    break;
                case ActionTypes.AddTags:
                    foreach (var t in p.StrList("tags")) { e.TagsAdded.Add(t.Trim()); e.TagsRemoved.Remove(t.Trim()); }
                    break;
                case ActionTypes.RemoveTags:
                    foreach (var t in p.StrList("tags")) { e.TagsRemoved.Add(t.Trim()); e.TagsAdded.Remove(t.Trim()); }
                    break;
            }
        }
        return e;
    }

    // ───────────────────────────── Condition overlap ─────────────────────────────

    private sealed class Constraint
    {
        public HashSet<string>? Allowed;
        public HashSet<string> Excluded { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool? Empty;
        public decimal? Min, Max;
        public bool MinInclusive = true, MaxInclusive = true;
        /// <summary>The constraints contradict each other (e.g. "is empty" and "is not empty").</summary>
        public bool Contradiction;
    }

    /// <summary>False only when the two condition trees provably cannot match the same ticket.</summary>
    public static bool MayOverlap(ConditionGroup a, ConditionGroup b)
    {
        var ca = Constraints(a);
        var cb = Constraints(b);
        foreach (var field in ca.Keys.Intersect(cb.Keys, StringComparer.OrdinalIgnoreCase))
            if (!Satisfiable(field, Merge(field, ca[field], cb[field]))) return false;
        return true;
    }

    /// <summary>Constraints that every matching ticket must satisfy (from AND-ed conditions; OR branches are ignored conservatively).</summary>
    private static Dictionary<string, Constraint> Constraints(ConditionGroup g)
    {
        var map = new Dictionary<string, Constraint>(StringComparer.OrdinalIgnoreCase);
        Collect(g, map);
        return map;
    }

    private static void Collect(ConditionGroup g, Dictionary<string, Constraint> map)
    {
        var isAnd = !string.Equals(g.Logic, "or", StringComparison.OrdinalIgnoreCase);
        var single = g.Conditions.Count + g.Groups.Count(x => !x.IsEmpty) == 1;
        if (!isAnd && !single) return;
        foreach (var c in g.Conditions) Apply(c, map);
        foreach (var child in g.Groups) Collect(child, map);
    }

    private static void Apply(Condition c, Dictionary<string, Constraint> map)
    {
        var field = c.Field;
        if (string.IsNullOrWhiteSpace(field)) return;
        if (!map.TryGetValue(field, out var k)) map[field] = k = new Constraint();
        var values = c.Value is null ? [] : ConditionEvaluator.ExpectedList(c.Value).Select(v => Normalize(field, v)).ToList();
        switch (c.Op?.ToLowerInvariant())
        {
            case ConditionOps.Equals or ConditionOps.In or ConditionOps.ChangedTo:
                if (c.Value is null) { SetEmpty(k, true); break; }
                var set = new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
                if (MultiValued.Contains(field)) { k.Allowed ??= set; break; } // "has any of" — keep the first requirement
                k.Allowed = k.Allowed is null ? set : new HashSet<string>(k.Allowed.Intersect(set, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
                break;
            case ConditionOps.Contains or ConditionOps.ContainsAny when MultiValued.Contains(field):
                k.Allowed ??= new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
                break;
            case ConditionOps.NotEquals or ConditionOps.NotIn:
                if (c.Value is null) { SetEmpty(k, false); break; }
                foreach (var v in values) k.Excluded.Add(v);
                break;
            case ConditionOps.NotContains when MultiValued.Contains(field):
                foreach (var v in values) k.Excluded.Add(v);
                break;
            case ConditionOps.IsEmpty: SetEmpty(k, true); break;
            case ConditionOps.IsNotEmpty: SetEmpty(k, false); break;
            case ConditionOps.GreaterThan or ConditionOps.GreaterOrEqual or ConditionOps.LessThan or ConditionOps.LessOrEqual:
                if (values.Count == 1 && Number(values[0]) is { } n)
                {
                    var op = c.Op!.ToLowerInvariant();
                    if (op is ConditionOps.GreaterThan or ConditionOps.GreaterOrEqual) Lower(k, n, op == ConditionOps.GreaterOrEqual);
                    else Upper(k, n, op == ConditionOps.LessOrEqual);
                }
                break;
            case ConditionOps.Between when values.Count == 2 && Number(values[0]) is { } lo && Number(values[1]) is { } hi:
                Lower(k, lo, true);
                Upper(k, hi, true);
                break;
        }
    }

    private static void SetEmpty(Constraint k, bool empty)
    {
        if (k.Empty is { } current && current != empty) k.Contradiction = true;
        else k.Empty = empty;
    }

    private static void Lower(Constraint k, decimal n, bool inclusive)
    {
        if (k.Min is null || n > k.Min || (n == k.Min && !inclusive)) { k.Min = n; k.MinInclusive = inclusive; }
    }

    private static void Upper(Constraint k, decimal n, bool inclusive)
    {
        if (k.Max is null || n < k.Max || (n == k.Max && !inclusive)) { k.Max = n; k.MaxInclusive = inclusive; }
    }

    private static Constraint Merge(string field, Constraint a, Constraint b)
    {
        var m = new Constraint();
        if (a.Allowed is not null && b.Allowed is not null)
            m.Allowed = MultiValued.Contains(field)
                ? new HashSet<string>(a.Allowed.Concat(b.Allowed), StringComparer.OrdinalIgnoreCase) // tickets can carry several tags
                : new HashSet<string>(a.Allowed.Intersect(b.Allowed, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        else m.Allowed = a.Allowed ?? b.Allowed;
        foreach (var v in a.Excluded.Concat(b.Excluded)) m.Excluded.Add(v);
        m.Contradiction = a.Contradiction || b.Contradiction || (a.Empty is { } ea && b.Empty is { } eb && ea != eb);
        m.Empty = a.Empty ?? b.Empty;
        m.Min = a.Min; m.MinInclusive = a.MinInclusive;
        m.Max = a.Max; m.MaxInclusive = a.MaxInclusive;
        if (b.Min is { } bmin) Lower(m, bmin, b.MinInclusive);
        if (b.Max is { } bmax) Upper(m, bmax, b.MaxInclusive);
        // For multi-valued fields each rule's "has any of" must survive the other rule's exclusions.
        if (MultiValued.Contains(field))
        {
            if (a.Allowed is { Count: > 0 } && a.Allowed.All(v => b.Excluded.Contains(v))) m.Allowed = [];
            if (b.Allowed is { Count: > 0 } && b.Allowed.All(v => a.Excluded.Contains(v))) m.Allowed = [];
        }
        return m;
    }

    private static bool Satisfiable(string field, Constraint m)
    {
        if (m.Contradiction) return false;
        if (m.Allowed is { Count: 0 }) return false;
        if (!MultiValued.Contains(field) && m.Allowed is { } allowed)
        {
            var remaining = allowed.Where(v => !m.Excluded.Contains(v)).ToList();
            if (remaining.Count == 0) return false;
            if (m.Empty == true && remaining.All(v => v.Length > 0 && !v.Equals("null", StringComparison.OrdinalIgnoreCase))) return false;
            if (m.Min is not null || m.Max is not null)
            {
                var numeric = remaining.Select(Number).ToList();
                if (numeric.All(n => n is not null) && !numeric.Any(n => InRange(n!.Value, m))) return false;
            }
        }
        if (m.Min is { } min && m.Max is { } max && (min > max || (min == max && !(m.MinInclusive && m.MaxInclusive)))) return false;
        if (m.Empty == true && (m.Min is not null || m.Max is not null)) return false;
        return true;
    }

    private static bool InRange(decimal n, Constraint m) =>
        (m.Min is null || n > m.Min || (n == m.Min && m.MinInclusive)) && (m.Max is null || n < m.Max || (n == m.Max && m.MaxInclusive));

    private static string Normalize(string field, string value)
    {
        var v = value.Trim();
        if (field.Equals("priority", StringComparison.OrdinalIgnoreCase) && Enum.TryParse<Domain.TicketPriority>(v, true, out var p))
            return ((int)p).ToString(CultureInfo.InvariantCulture);
        return v.ToLowerInvariant();
    }

    private static decimal? Number(string v) =>
        decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
}
