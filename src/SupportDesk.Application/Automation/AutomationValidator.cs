using System.Text.Json;
using System.Text.RegularExpressions;
using SupportDesk.Application.Tickets;
using SupportDesk.Domain.Automation;
using SupportDesk.Domain.Rules;

namespace SupportDesk.Application.Automation;

public sealed record ValidationIssue(string Path, string Message, bool IsWarning = false);

/// <summary>Tenant configuration used to check ids referenced by rule and blueprint definitions. Null sets are not checked.</summary>
public sealed class AutomationReferenceData
{
    public HashSet<Guid> Users { get; init; } = [];
    /// <summary>Active full agents (not light agents) — can own tickets.</summary>
    public HashSet<Guid> AssignableUsers { get; init; } = [];
    public HashSet<Guid> Teams { get; init; } = [];
    public HashSet<Guid> Departments { get; init; } = [];
    public Dictionary<Guid, IReadOnlyList<string>> Statuses { get; init; } = [];
    public Dictionary<Guid, Guid?> Categories { get; init; } = [];
    public HashSet<Guid> IssueTypes { get; init; } = [];
    public HashSet<Guid> Roles { get; init; } = [];
    public HashSet<Guid> ReplyTemplates { get; init; } = [];
    public HashSet<string> CustomFieldKeys { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Validates rule and blueprint definitions at publish time: condition fields/operators/values, action parameters
/// (types, required values, URLs, enums) and — when reference data is supplied — that referenced records exist.
/// Pure (no I/O) so it is unit-testable.
/// </summary>
public static partial class AutomationValidator
{
    public const int MaxActions = 25;
    public const int MaxConditions = 50;
    public const int MaxDepth = 5;

    private static readonly HashSet<string> NoValueOps = [ConditionOps.IsEmpty, ConditionOps.IsNotEmpty, ConditionOps.Changed];
    private static readonly HashSet<string> ChangeOps = [ConditionOps.Changed, ConditionOps.ChangedTo, ConditionOps.ChangedFrom];
    private static readonly HashSet<string> StandardFields =
        new(AutomationConditions.AllFields.Select(f => f.Field).Concat(["id", "number"]), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ForbiddenHeaders = new(["host", "content-length", "transfer-encoding", "connection"], StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"\{\{[^}]*\}\}")] private static partial Regex PlaceholderRegex();
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,49}$")] private static partial Regex KeyRegex();

    // ───────────────────────────── Rules ─────────────────────────────

    public static List<ValidationIssue> ValidateRule(string trigger, RuleDefinition def, AutomationReferenceData? refs)
    {
        var issues = new List<ValidationIssue>();
        if (!RuleTriggers.All.Contains(trigger)) issues.Add(new("trigger", $"Unknown trigger '{trigger}'."));
        if (def.Trigger?.Fields is { Count: > 0 } fields)
        {
            if (trigger != RuleTriggers.TicketUpdated)
                issues.Add(new("trigger.fields", "Field filters only apply to the 'ticket.updated' trigger.", true));
            foreach (var f in fields.Where(f => !StandardFields.Contains(f) && !f.StartsWith("cf:", StringComparison.OrdinalIgnoreCase) && f is not ("customFields" or "tags" or "status" or "assignee" or "department" or "watchers")))
                issues.Add(new("trigger.fields", $"Unknown field '{f}'."));
        }
        issues.AddRange(ValidateConditions(def.Conditions, "conditions", trigger, refs));
        if (def.Actions.Count == 0) issues.Add(new("actions", "Add at least one action."));
        issues.AddRange(ValidateActions(def.Actions, "actions", refs));
        if (trigger == RuleTriggers.Scheduled && def.Conditions.IsEmpty)
            issues.Add(new("conditions", "A scheduled rule without conditions acts on every open ticket on every run.", true));
        return issues;
    }

    public static List<ValidationIssue> ValidateConditions(ConditionGroup group, string path, string context, AutomationReferenceData? refs)
    {
        var issues = new List<ValidationIssue>();
        var count = 0;
        Walk(group, path, 1);
        if (count > MaxConditions) issues.Add(new(path, $"At most {MaxConditions} conditions are allowed."));
        return issues;

        void Walk(ConditionGroup g, string p, int depth)
        {
            if (depth > MaxDepth) { issues.Add(new(p, $"Condition groups can be nested at most {MaxDepth} levels deep.")); return; }
            if (!string.Equals(g.Logic, "and", StringComparison.OrdinalIgnoreCase) && !string.Equals(g.Logic, "or", StringComparison.OrdinalIgnoreCase))
                issues.Add(new($"{p}.logic", "Logic must be 'and' or 'or'."));
            for (var i = 0; i < g.Conditions.Count; i++)
            {
                count++;
                ValidateCondition(g.Conditions[i], $"{p}.conditions[{i}]", context, refs, issues);
            }
            for (var i = 0; i < g.Groups.Count; i++) Walk(g.Groups[i], $"{p}.groups[{i}]", depth + 1);
        }
    }

    private static void ValidateCondition(Condition c, string path, string context, AutomationReferenceData? refs, List<ValidationIssue> issues)
    {
        var field = c.Field?.Trim() ?? "";
        var op = (c.Op ?? "").ToLowerInvariant();
        if (field.Length == 0) { issues.Add(new($"{path}.field", "Choose a field.")); return; }
        if (field.StartsWith("cf:", StringComparison.OrdinalIgnoreCase))
        {
            if (refs is not null && !refs.CustomFieldKeys.Contains(field[3..])) issues.Add(new($"{path}.field", $"Unknown custom field '{field[3..]}'."));
        }
        else if (field.StartsWith("webhook.", StringComparison.OrdinalIgnoreCase))
        {
            if (context != RuleTriggers.Webhook) issues.Add(new($"{path}.field", "Webhook payload fields are only available to webhook-triggered rules."));
        }
        else if (!StandardFields.Contains(field)) issues.Add(new($"{path}.field", $"Unknown field '{field}'."));

        if (!ConditionOps.All.Contains(op)) { issues.Add(new($"{path}.op", $"Unknown operator '{c.Op}'.")); return; }
        if (ChangeOps.Contains(op) && !AutomationCatalog.EventTriggers.Contains(context))
            issues.Add(new($"{path}.op", "'changed' operators only apply to event-triggered rules."));
        if (NoValueOps.Contains(op)) return;
        var value = c.Value;
        if (value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (op is not (ConditionOps.Equals or ConditionOps.NotEquals)) issues.Add(new($"{path}.value", "A value is required."));
            return;
        }
        switch (op)
        {
            case ConditionOps.WithinLastMinutes or ConditionOps.OlderThanMinutes:
                if (!(TryNumber(value.Value, out var minutes) && minutes > 0)) issues.Add(new($"{path}.value", "Enter a number of minutes greater than zero."));
                break;
            case ConditionOps.Between:
                if (value.Value.ValueKind != JsonValueKind.Array || value.Value.GetArrayLength() != 2) issues.Add(new($"{path}.value", "Between needs two values [from, to]."));
                break;
            case ConditionOps.Matches:
                try { _ = new Regex(value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() ?? "" : value.Value.ToString(), RegexOptions.None, TimeSpan.FromMilliseconds(100)); }
                catch (ArgumentException) { issues.Add(new($"{path}.value", "The pattern is not a valid regular expression.")); }
                break;
            case ConditionOps.GreaterThan or ConditionOps.GreaterOrEqual or ConditionOps.LessThan or ConditionOps.LessOrEqual:
                if (value.Value.ValueKind == JsonValueKind.Array) issues.Add(new($"{path}.value", "Enter a single value."));
                break;
        }
        if (field.Equals("priority", StringComparison.OrdinalIgnoreCase) && op is ConditionOps.Equals or ConditionOps.NotEquals or ConditionOps.In or ConditionOps.NotIn)
            foreach (var v in ConditionEvaluator.ExpectedList(value).Where(v => v.Length > 0 && !AutomationCatalog.Priorities.Contains(v, StringComparer.OrdinalIgnoreCase)))
                issues.Add(new($"{path}.value", $"'{v}' is not a priority."));
    }

    private static bool TryNumber(JsonElement v, out double n)
    {
        n = 0;
        return v.ValueKind == JsonValueKind.Number ? v.TryGetDouble(out n)
            : v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out n);
    }

    // ───────────────────────────── Actions ─────────────────────────────

    public static List<ValidationIssue> ValidateActions(IReadOnlyList<RuleAction> actions, string path, AutomationReferenceData? refs)
    {
        var issues = new List<ValidationIssue>();
        if (actions.Count > MaxActions) issues.Add(new(path, $"At most {MaxActions} actions are allowed."));
        for (var i = 0; i < actions.Count; i++) ValidateAction(actions[i], $"{path}[{i}]", refs, issues);
        return issues;
    }

    public static List<ValidationIssue> ValidateAction(RuleAction action, string path, AutomationReferenceData? refs)
    {
        var issues = new List<ValidationIssue>();
        ValidateAction(action, path, refs, issues);
        return issues;
    }

    private static void ValidateAction(RuleAction a, string path, AutomationReferenceData? refs, List<ValidationIssue> issues)
    {
        if (!AutomationCatalog.ByType.TryGetValue(a.Type ?? "", out var spec))
        {
            issues.Add(new($"{path}.type", $"Unknown action type '{a.Type}'."));
            return;
        }
        if (a.Params is { } raw && raw.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined))
        {
            issues.Add(new($"{path}.params", "Parameters must be an object."));
            return;
        }
        var p = a.P;
        void Err(string param, string message) => issues.Add(new($"{path}.params.{param}", message));
        void Required(string param, string label)
        {
            if (!p.Has(param) || (p.Str(param) is { } s && string.IsNullOrWhiteSpace(s))) Err(param, $"{label} is required.");
        }
        void GuidParam(string param, string label, bool required, Func<Guid, bool>? exists = null)
        {
            if (!p.Has(param)) { if (required) Err(param, $"{label} is required."); return; }
            var g = p.Guid(param);
            if (g is null) { Err(param, $"{label} must be an id."); return; }
            if (refs is not null && exists is not null && !exists(g.Value)) Err(param, $"{label} does not exist or is inactive.");
        }
        void EnumParam(string param, string label, IReadOnlyList<string> options, bool required)
        {
            var v = p.Str(param);
            if (string.IsNullOrWhiteSpace(v)) { if (required) Err(param, $"{label} is required."); return; }
            if (!options.Contains(v, StringComparer.OrdinalIgnoreCase)) Err(param, $"'{v}' is not a valid {label.ToLowerInvariant()} ({string.Join(", ", options)}).");
        }
        void MaxLen(string param, int max)
        {
            if (p.Str(param) is { } s && s.Length > max) Err(param, $"At most {max} characters.");
        }

        switch (spec.Type)
        {
            case ActionTypes.Assign:
                if (!p.Has("userId") && !p.Has("teamId") && !p.Bool("route")) Err("userId", "Choose an agent, a team, or routing.");
                GuidParam("userId", "Agent", false, refs is null ? null : refs.AssignableUsers.Contains);
                GuidParam("teamId", "Team", false, refs is null ? null : refs.Teams.Contains);
                break;
            case ActionTypes.TransferDepartment:
                GuidParam("departmentId", "Department", true, refs is null ? null : refs.Departments.Contains);
                GuidParam("teamId", "Team", false, refs is null ? null : refs.Teams.Contains);
                break;
            case ActionTypes.SetPriority: EnumParam("priority", "Priority", AutomationCatalog.Priorities, true); break;
            case ActionTypes.SetSeverity: EnumParam("severity", "Severity", AutomationCatalog.Severities, true); break;
            case ActionTypes.SetStatus:
            {
                GuidParam("statusId", "Status", true, refs is null ? null : refs.Statuses.ContainsKey);
                if (refs is not null && p.Guid("statusId") is { } sid && p.Str("substatus") is { Length: > 0 } sub && refs.Statuses.TryGetValue(sid, out var subs) &&
                    subs.Count > 0 && !subs.Contains(sub, StringComparer.OrdinalIgnoreCase))
                    Err("substatus", $"'{sub}' is not a substatus of this status.");
                break;
            }
            case ActionTypes.SetSubstatus: Required("substatus", "Substatus"); break;
            case ActionTypes.SetCategory:
            {
                GuidParam("categoryId", "Category", true, refs is null ? null : refs.Categories.ContainsKey);
                GuidParam("subcategoryId", "Subcategory", false, refs is null ? null : refs.Categories.ContainsKey);
                if (refs is not null && p.Guid("categoryId") is { } cat && p.Guid("subcategoryId") is { } subCat &&
                    refs.Categories.TryGetValue(subCat, out var parent) && parent != cat)
                    Err("subcategoryId", "The subcategory does not belong to the category.");
                break;
            }
            case ActionTypes.SetIssueType: GuidParam("issueTypeId", "Issue type", true, refs is null ? null : refs.IssueTypes.Contains); break;
            case ActionTypes.SetCustomField:
                Required("key", "Field");
                if (!p.TryGet("value", out _)) Err("value", "A value is required (use null to clear).");
                if (refs is not null && p.Str("key") is { Length: > 0 } key && !refs.CustomFieldKeys.Contains(key.StartsWith("cf:", StringComparison.OrdinalIgnoreCase) ? key[3..] : key))
                    Err("key", $"Unknown custom field '{key}'.");
                break;
            case ActionTypes.AddTags or ActionTypes.RemoveTags:
            {
                var tags = p.StrList("tags");
                if (tags.Count == 0) Err("tags", "Add at least one tag.");
                if (tags.Any(t => t.Trim().Length > 50)) Err("tags", "Tags are at most 50 characters.");
                break;
            }
            case ActionTypes.AddNote:
                Required("bodyHtml", "Note");
                MaxLen("bodyHtml", 20000);
                break;
            case ActionTypes.ReplyWithTemplate: GuidParam("templateId", "Reply template", true, refs is null ? null : refs.ReplyTemplates.Contains); break;
            case ActionTypes.AddWatcher:
            {
                var users = p.GuidList("userIds");
                if (p.Guid("userId") is { } single) users.Add(single);
                if (users.Count == 0) Err("userIds", "Choose at least one agent.");
                if (refs is not null && users.Any(u => !refs.Users.Contains(u))) Err("userIds", "One of the agents does not exist or is inactive.");
                if (p.Str("kind") is { Length: > 0 }) EnumParam("kind", "Kind", ["Watcher", "Collaborator", "Specialist"], false);
                break;
            }
            case ActionTypes.NotifyAgents:
            {
                var recipients = p.StrList("recipients");
                var users = p.GuidList("userIds");
                var roles = p.GuidList("roleIds");
                if (recipients.Count == 0 && users.Count == 0 && roles.Count == 0) Err("recipients", "Choose who to notify.");
                foreach (var r in recipients.Where(r => !AutomationCatalog.NotifyRecipients.Contains(r, StringComparer.OrdinalIgnoreCase)))
                    Err("recipients", $"Unknown recipient '{r}'.");
                if (refs is not null && users.Any(u => !refs.Users.Contains(u))) Err("userIds", "One of the agents does not exist or is inactive.");
                if (refs is not null && roles.Any(r => !refs.Roles.Contains(r))) Err("roleIds", "One of the roles does not exist.");
                Required("title", "Title");
                MaxLen("title", 300);
                if (p.Str("severity") is { Length: > 0 }) EnumParam("severity", "Severity", ["Info", "Warning", "Critical"], false);
                break;
            }
            case ActionTypes.SendMessage:
            {
                EnumParam("channel", "Channel", AutomationCatalog.MessageChannels, true);
                EnumParam("to", "Recipient", ["contact", "assignee", "user", "address"], true);
                var to = p.Str("to")?.ToLowerInvariant();
                if (to == "user") GuidParam("userId", "Agent", true, refs is null ? null : refs.Users.Contains);
                if (to == "address") Required("address", "Address");
                if (string.Equals(p.Str("channel"), "Email", StringComparison.OrdinalIgnoreCase)) Required("subject", "Subject");
                Required("bodyHtml", "Message");
                MaxLen("bodyHtml", 20000);
                break;
            }
            case ActionTypes.CreateTask:
                Required("title", "Title");
                MaxLen("title", 300);
                if (p.Str("assigneeId") is { Length: > 0 } assigneeRaw && assigneeRaw != "assignee") GuidParam("assigneeId", "Assignee", false, refs is null ? null : refs.Users.Contains);
                if (p.Has("dueInHours") && p.Double("dueInHours") is not (>= 0 and <= 8760)) Err("dueInHours", "Enter between 0 and 8760 hours.");
                break;
            case ActionTypes.CreateChildTicket:
                Required("subject", "Subject");
                MaxLen("subject", 500);
                GuidParam("departmentId", "Department", false, refs is null ? null : refs.Departments.Contains);
                if (p.Str("priority") is { Length: > 0 }) EnumParam("priority", "Priority", AutomationCatalog.Priorities, false);
                break;
            case ActionTypes.InvokeWebhook:
            {
                EnumParam("method", "Method", AutomationCatalog.HttpMethods, true);
                var url = p.Str("url");
                if (string.IsNullOrWhiteSpace(url)) Err("url", "A URL is required.");
                else if (!IsValidHttpUrl(PlaceholderRegex().Replace(url, "x"))) Err("url", "Enter an absolute http(s) URL without credentials.");
                if (p.TryGet("headers", out var headers) && headers.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                    Err("headers", "Headers must be an object of name/value pairs.");
                foreach (var h in p.StrMap("headers").Keys)
                {
                    if (ForbiddenHeaders.Contains(h)) Err("headers", $"Header '{h}' cannot be set.");
                    else if (!IsValidHeaderName(h)) Err("headers", $"'{h}' is not a valid header name.");
                }
                if (p.Has("timeoutSeconds") && p.Int("timeoutSeconds") is not (>= 1 and <= 30)) Err("timeoutSeconds", "Enter between 1 and 30 seconds.");
                MaxLen("body", 65536);
                if (p.Str("secret") is { Length: > 0 and < 16 } s && !s.StartsWith(SecretValues.Prefix, StringComparison.Ordinal))
                    Err("secret", "Use a signing secret of at least 16 characters.");
                break;
            }
            case ActionTypes.UpdateErpRecord:
                Required("system", "System");
                Required("recordType", "Record type");
                if (!p.Has("recordNumber") && !p.Bool("useOriginReference")) Err("recordNumber", "Give a record number or use the ticket's origin record.");
                if (!p.TryGet("changes", out var changes) || changes.ValueKind != JsonValueKind.Object) Err("changes", "Changes must be an object.");
                break;
            case ActionTypes.RequestApproval:
                Required("reason", "Reason");
                GuidParam("approverUserId", "Approver", false, refs is null ? null : refs.Users.Contains);
                GuidParam("approverRoleId", "Approver role", false, refs is null ? null : refs.Roles.Contains);
                if (p.Has("expiresInDays") && p.Int("expiresInDays") is not (>= 1 and <= 90)) Err("expiresInDays", "Enter between 1 and 90 days.");
                break;
            case ActionTypes.Escalate:
                Required("reason", "Reason");
                GuidParam("toUserId", "Agent", false, refs is null ? null : refs.Users.Contains);
                break;
        }
    }

    public static bool IsValidHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && string.IsNullOrEmpty(u.UserInfo);

    private static bool IsValidHeaderName(string name) => name.Length is > 0 and <= 100 && name.All(ch => ch is > ' ' and < (char)127 && ch != ':');

    // ───────────────────────────── Blueprints ─────────────────────────────

    public static List<ValidationIssue> ValidateBlueprint(BlueprintDefinition def, AutomationReferenceData? refs)
    {
        var issues = new List<ValidationIssue>();
        if (def.States.Count == 0) { issues.Add(new("states", "Add at least one state.")); return issues; }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < def.States.Count; i++)
        {
            var s = def.States[i];
            var path = $"states[{i}]";
            if (!KeyRegex().IsMatch(s.Key ?? "")) issues.Add(new($"{path}.key", "Use a key of lowercase letters, digits, '-' or '_' (max 50)."));
            else if (!keys.Add(s.Key!)) issues.Add(new($"{path}.key", $"Duplicate state key '{s.Key}'."));
            if (string.IsNullOrWhiteSpace(s.Name)) issues.Add(new($"{path}.name", "A state name is required."));
            if (s.StatusId is { } sid && refs is not null)
            {
                if (!refs.Statuses.TryGetValue(sid, out var subs)) issues.Add(new($"{path}.statusId", "The status does not exist or is inactive."));
                else if (s.Substatus is { Length: > 0 } sub && subs.Count > 0 && !subs.Contains(sub, StringComparer.OrdinalIgnoreCase))
                    issues.Add(new($"{path}.substatus", $"'{sub}' is not a substatus of this status."));
            }
            if (s.StatusId is null) issues.Add(new($"{path}.statusId", "No status: entering this state keeps the ticket's current status.", true));
        }
        var initial = def.States.Where(s => s.IsInitial).ToList();
        if (initial.Count != 1) issues.Add(new("states", initial.Count == 0 ? "Mark exactly one state as initial." : "Only one state can be initial."));
        if (!def.States.Any(s => s.IsFinal)) issues.Add(new("states", "No final state: tickets can never complete this blueprint.", true));

        var transitionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < def.Transitions.Count; i++)
        {
            var t = def.Transitions[i];
            var path = $"transitions[{i}]";
            if (!KeyRegex().IsMatch(t.Key ?? "")) issues.Add(new($"{path}.key", "Use a key of lowercase letters, digits, '-' or '_' (max 50)."));
            else if (!transitionKeys.Add(t.Key!)) issues.Add(new($"{path}.key", $"Duplicate transition key '{t.Key}'."));
            if (string.IsNullOrWhiteSpace(t.Name)) issues.Add(new($"{path}.name", "A transition name is required."));
            if (t.From.Count == 0) issues.Add(new($"{path}.from", "Choose the state(s) this transition starts from."));
            foreach (var f in t.From.Where(f => f != "*"))
            {
                var from = def.State(f);
                if (from is null) issues.Add(new($"{path}.from", $"Unknown state '{f}'."));
                else if (from.IsFinal) issues.Add(new($"{path}.from", $"'{from.Name}' is a final state and cannot have outgoing transitions."));
            }
            if (def.State(t.To) is null) issues.Add(new($"{path}.to", $"Unknown state '{t.To}'."));
            else if (def.State(t.To)!.IsInitial && t.From.Count == 1 && string.Equals(t.From[0], t.To, StringComparison.OrdinalIgnoreCase))
                issues.Add(new($"{path}.to", "The transition goes back to the same state.", true));
            if (refs is not null)
                foreach (var r in t.AllowedRoleIds.Where(r => !refs.Roles.Contains(r)))
                    issues.Add(new($"{path}.allowedRoleIds", $"Role {r} does not exist."));
            foreach (var f in t.RequiredFields)
            {
                if (f.StartsWith("cf:", StringComparison.OrdinalIgnoreCase))
                {
                    if (refs is not null && !refs.CustomFieldKeys.Contains(f[3..])) issues.Add(new($"{path}.requiredFields", $"Unknown custom field '{f[3..]}'."));
                }
                else if (!AutomationCatalog.RequirableFields.ContainsKey(f)) issues.Add(new($"{path}.requiredFields", $"'{f}' cannot be a mandatory field."));
            }
            if (t.Validation is { IsEmpty: false } validation)
                issues.AddRange(ValidateConditions(validation, $"{path}.validation", "blueprint", refs));
            if (t.Approval is { } approval && refs is not null)
            {
                if (approval.RoleId is { } role && !refs.Roles.Contains(role)) issues.Add(new($"{path}.approval.roleId", "The approver role does not exist."));
                if (approval.UserId is { } user && !refs.Users.Contains(user)) issues.Add(new($"{path}.approval.userId", "The approver does not exist or is inactive."));
            }
            issues.AddRange(ValidateActions(t.Actions, $"{path}.actions", refs));
        }

        // Reachability from the initial state and dead ends.
        if (initial.Count == 1)
        {
            var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { initial[0].Key };
            var queue = new Queue<string>([initial[0].Key]);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (def.State(current) is { IsFinal: true }) continue;
                foreach (var t in def.From(current))
                    if (def.State(t.To) is { } to && reached.Add(to.Key)) queue.Enqueue(to.Key);
            }
            foreach (var s in def.States.Where(s => !string.IsNullOrEmpty(s.Key) && !reached.Contains(s.Key)))
                issues.Add(new("states", $"State '{s.Name}' cannot be reached from the initial state."));
            foreach (var s in def.States.Where(s => !s.IsFinal && !string.IsNullOrEmpty(s.Key) && !def.From(s.Key).Any()))
                issues.Add(new("states", $"State '{s.Name}' has no outgoing transition and is not final (tickets would be stuck)."));
        }
        return issues;
    }
}

/// <summary>Secrets embedded in definitions (webhook signing secrets, sensitive headers) are stored encrypted with this prefix.</summary>
public static class SecretValues
{
    public const string Prefix = "enc:";
    public const string Mask = "••••••";

    private static readonly string[] SensitiveHeaderHints = ["authorization", "api-key", "apikey", "token", "secret", "password", "cookie"];

    public static bool IsSensitiveHeader(string name) => SensitiveHeaderHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase));
}
