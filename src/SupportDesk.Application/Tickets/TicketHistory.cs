using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Domain;
using SupportDesk.Domain.Configuration;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Tickets;

public static class TicketEventTypes
{
    public const string Created = "Created";
    public const string FieldsChanged = "FieldsChanged";
    public const string Assigned = "Assigned";
    public const string Transferred = "Transferred";
    public const string StatusChanged = "StatusChanged";
    public const string Escalated = "Escalated";
    public const string Resolved = "Resolved";
    public const string Reopened = "Reopened";
    public const string Closed = "Closed";
    public const string Merged = "Merged";
    public const string MergedInto = "MergedInto";
    public const string Split = "Split";
    public const string Cloned = "Cloned";
    public const string Linked = "Linked";
    public const string Unlinked = "Unlinked";
    public const string TagsChanged = "TagsChanged";
    public const string WatcherAdded = "WatcherAdded";
    public const string WatcherRemoved = "WatcherRemoved";
    public const string TaskChanged = "TaskChanged";
    public const string TimeLogged = "TimeLogged";
    public const string AttachmentAdded = "AttachmentAdded";
    public const string EntitlementEvaluated = "EntitlementEvaluated";
    public const string EntitlementOverridden = "EntitlementOverridden";
    public const string SlaApplied = "SlaApplied";
    public const string SlaEvent = "SlaEvent";
    public const string RuleApplied = "RuleApplied";
    public const string WorkflowTransition = "WorkflowTransition";
    public const string ApprovalRequested = "ApprovalRequested";
    public const string ApprovalDecided = "ApprovalDecided";
    public const string AiAction = "AiAction";
    public const string EngineeringLinked = "EngineeringLinked";
    public const string EngineeringUpdated = "EngineeringUpdated";
    public const string FollowUpScheduled = "FollowUpScheduled";
    public const string Deleted = "Deleted";
    public const string Restored = "Restored";
    public const string MarkedSpam = "MarkedSpam";
    public const string CustomerConfirmed = "CustomerConfirmed";
    public const string Satisfaction = "Satisfaction";
    public const string Archived = "Archived";
}

/// <summary>Records immutable ticket activity history (who did what, when, from/to values).</summary>
public sealed class TicketHistory(DeskDbContext db, RequestContext ctx)
{
    public TicketEvent Record(Ticket ticket, string type, string? summary, IReadOnlyList<FieldChange>? changes = null, object? details = null)
    {
        var ev = new TicketEvent
        {
            TicketId = ticket.Id,
            Type = type,
            ActorType = ctx.ActorType,
            ActorId = ctx.UserId ?? ctx.ContactId ?? ctx.ApiClientId,
            ActorName = ctx.ActorName,
            OccurredAt = db.Clock.UtcNow,
            Summary = summary,
            ChangesJson = changes is { Count: > 0 } ? Json.Serialize(changes) : null,
            DetailsJson = details is null ? null : Json.Serialize(details),
            RuleId = ctx.ExecutingRuleId,
            CorrelationId = ctx.CorrelationId
        };
        db.TicketEvents.Add(ev);
        return ev;
    }
}

/// <summary>Collects field changes while applying an update (old/new values rendered as strings).</summary>
public sealed class ChangeSet
{
    private readonly List<FieldChange> _changes = [];
    public IReadOnlyList<FieldChange> Changes => _changes;
    public bool Any => _changes.Count > 0;
    public bool Has(string field) => _changes.Any(c => c.Field == field);

    public bool Set<T>(string field, T current, T next, Action<T> apply, Func<T, string?>? display = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, next)) return false;
        _changes.Add(new FieldChange(field, Render(current, display), Render(next, display)));
        apply(next);
        return true;
    }

    public void Add(string field, string? from, string? to) => _changes.Add(new FieldChange(field, from, to));

    private static string? Render<T>(T value, Func<T, string?>? display) => display is not null ? display(value) : value switch
    {
        null => null,
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };
}

/// <summary>Validates and normalises custom field values against tenant definitions.</summary>
public sealed class CustomFieldValidator(DeskDbContext db)
{
    private List<CustomFieldDefinition>? _defs;

    public async Task<List<CustomFieldDefinition>> DefinitionsAsync(string entity, CancellationToken ct)
    {
        _defs ??= await db.CustomFields.AsNoTracking().Where(f => f.IsActive).OrderBy(f => f.SortOrder).ToListAsync(ct);
        return _defs.Where(f => f.Entity == entity).ToList();
    }

    /// <summary>Merges <paramref name="input"/> into the existing JSON, validating types, options and required fields.</summary>
    public async Task<string> MergeAsync(string entity, string existingJson, IReadOnlyDictionary<string, JsonElement>? input, Guid? departmentId,
        bool enforceRequired, bool portalOnly, CancellationToken ct)
    {
        var defs = await DefinitionsAsync(entity, ct);
        var values = Json.Object(existingJson);
        var errors = new Dictionary<string, string>();
        if (input is not null)
        {
            foreach (var (key, value) in input)
            {
                var def = defs.FirstOrDefault(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (def is null) { errors[key] = "Unknown field."; continue; }
                if (portalOnly && !def.EditableInPortal) { errors[key] = "This field cannot be set from the portal."; continue; }
                if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined || (value.ValueKind == JsonValueKind.String && value.GetString() == ""))
                {
                    values.Remove(def.Key);
                    continue;
                }
                var error = Validate(def, value);
                if (error is not null) errors[def.Key] = error;
                else values[def.Key] = value;
            }
        }
        if (enforceRequired)
        {
            foreach (var def in defs.Where(d => d.IsRequired && Applies(d, departmentId) && (!portalOnly || d.VisibleInPortal)))
                if (!values.ContainsKey(def.Key)) errors[def.Key] = $"{def.Label} is required.";
        }
        if (errors.Count > 0) throw new ValidationException(errors);
        return Json.Serialize(values);
    }

    private static bool Applies(CustomFieldDefinition def, Guid? departmentId)
    {
        var depts = Json.GuidList(def.DepartmentIdsJson);
        return depts.Count == 0 || (departmentId.HasValue && depts.Contains(departmentId.Value));
    }

    private static string? Validate(CustomFieldDefinition def, JsonElement v)
    {
        string? S() => v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
        switch (def.FieldType)
        {
            case CustomFieldType.Number:
                if (!(v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) || long.TryParse(S(), out n))) return "Must be a whole number.";
                if (def.MinValue is { } min && n < min || def.MaxValue is { } max && n > max) return "Out of range.";
                return null;
            case CustomFieldType.Decimal or CustomFieldType.Currency:
                if (!(v.ValueKind == JsonValueKind.Number || decimal.TryParse(S(), NumberStyles.Any, CultureInfo.InvariantCulture, out _))) return "Must be a number.";
                return null;
            case CustomFieldType.Date or CustomFieldType.DateTime:
                return DateTime.TryParse(S(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out _) ? null : "Must be a date.";
            case CustomFieldType.Checkbox:
                return v.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "Must be true or false.";
            case CustomFieldType.Picklist:
            {
                var options = Options(def);
                return options.Count == 0 || options.Contains(S() ?? "") ? null : "Not an allowed option.";
            }
            case CustomFieldType.MultiPicklist:
            {
                if (v.ValueKind != JsonValueKind.Array) return "Must be a list.";
                var options = Options(def);
                return options.Count == 0 || v.EnumerateArray().All(e => options.Contains(e.GetString() ?? "")) ? null : "Contains a value that is not allowed.";
            }
            case CustomFieldType.Email:
                return S() is { } e && e.Contains('@') && e.Length <= 320 ? null : "Must be an e-mail address.";
            case CustomFieldType.Url:
                return Uri.TryCreate(S(), UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http") ? null : "Must be a web address.";
            default:
            {
                var s = S() ?? "";
                if (def.MaxLength is { } maxLen && s.Length > maxLen) return $"At most {maxLen} characters.";
                if (def.ValidationRegex is { Length: > 0 } rx &&
                    !System.Text.RegularExpressions.Regex.IsMatch(s, rx, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100)))
                    return "Invalid format.";
                return s.Length > 4000 ? "Too long." : null;
            }
        }
    }

    private static HashSet<string> Options(CustomFieldDefinition def)
    {
        try
        {
            using var doc = JsonDocument.Parse(def.OptionsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            return doc.RootElement.EnumerateArray()
                .Select(o => o.ValueKind == JsonValueKind.Object && o.TryGetProperty("value", out var val) ? val.GetString() ?? "" : o.GetString() ?? "")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException) { return []; }
    }
}

/// <summary>Field-level validation errors (400 with an "errors" map).</summary>
public sealed class ValidationException(IReadOnlyDictionary<string, string> errors)
    : DomainException("validation_failed", "One or more fields are invalid: " + string.Join("; ", errors.Select(e => $"{e.Key}: {e.Value}")), 400)
{
    public IReadOnlyDictionary<string, string> Errors { get; } = errors;
}
