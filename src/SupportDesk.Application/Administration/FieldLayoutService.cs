using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Administration;
using SupportDesk.Contracts.Common;
using SupportDesk.Domain;
using SupportDesk.Domain.Configuration;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Administration;

/// <summary>Standard ticket fields the form builder can place (custom fields are added as "cf:key").</summary>
public static class LayoutFieldCatalog
{
    public sealed record StandardField(string Key, string Label, string Type, bool Agent, bool Portal, bool AlwaysRequired = false);

    public static readonly IReadOnlyList<StandardField> Standard =
    [
        new("subject", "Subject", "Text", true, false, true),
        new("description", "Description", "RichText", true, false),
        new("contactId", "Contact", "Lookup", true, false),
        new("customerId", "Customer", "Lookup", true, false),
        new("departmentId", "Department", "Lookup", true, true, true),
        new("teamId", "Team", "Lookup", true, false),
        new("assigneeId", "Assignee", "Lookup", true, false),
        new("channel", "Channel", "Picklist", true, false),
        new("priority", "Priority", "Picklist", true, true),
        new("severity", "Severity", "Picklist", true, false),
        new("categoryId", "Category", "Lookup", true, true),
        new("subcategoryId", "Subcategory", "Lookup", true, false),
        new("issueTypeId", "Issue type", "Lookup", true, false),
        new("productId", "Product", "Lookup", true, true),
        new("moduleId", "Module", "Lookup", true, false),
        new("versionId", "Version", "Lookup", true, false),
        new("installationId", "Installation", "Lookup", true, true),
        new("dueAt", "Due date", "DateTime", true, false),
        new("tags", "Tags", "Tags", true, false)
    ];
}

/// <summary>
/// Custom fields and form layouts (admin.ticket_config). Fields: types, options, validation (regex/min/max/length),
/// visibility (agent/portal), required always or only for given statuses, per-department applicability. Layouts:
/// drag-and-drop sections of standard and custom fields per kind (agent/portal), department, brand and channel.
/// </summary>
public sealed class FieldLayoutService(DeskDbContext db, RequestContext ctx, IAuditWriter audit, AdminChangeNotifier changes)
{
    private static readonly string[] Entities = ["Ticket", "Contact", "Customer"];
    private static readonly CustomFieldType[] OptionTypes = [CustomFieldType.Picklist, CustomFieldType.MultiPicklist];
    private static readonly CustomFieldType[] TextTypes = [CustomFieldType.Text, CustomFieldType.TextArea, CustomFieldType.Url, CustomFieldType.Email, CustomFieldType.Phone];

    // ───────────────────────────── Custom fields ─────────────────────────────

    public async Task<List<AdminCustomFieldDto>> ListFieldsAsync(string? entity, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminTicketConfig);
        var q = db.CustomFields.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(entity)) q = q.Where(f => f.Entity == entity);
        var fields = await q.OrderBy(f => f.Entity).ThenBy(f => f.SortOrder).ThenBy(f => f.Label).ToListAsync(ct);
        var layouts = await db.FormLayouts.AsNoTracking().Select(l => new { l.Entity, l.SectionsJson }).ToListAsync(ct);
        return fields.Select(f => MapField(f, layouts.Count(l => l.Entity == f.Entity && l.SectionsJson.Contains($"\"cf:{f.Key}\"")))).ToList();
    }

    public async Task<AdminCustomFieldDto> CreateFieldAsync(SaveCustomFieldRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminTicketConfig);
        var f = new CustomFieldDefinition
        {
            SortOrder = (await db.CustomFields.Where(x => x.Entity == r.Entity).MaxAsync(x => (int?)x.SortOrder, ct) ?? 0) + 1
        };
        await ApplyFieldAsync(f, r, isNew: true, ct);
        db.CustomFields.Add(f);
        audit.Write(AdminAuditActions.TicketConfigChanged, "CustomField", f.Id, $"Custom field '{f.Label}' ({f.Entity}.{f.Key}) created", new { f.FieldType, r.IsRequired, r.RequiredForStatusIds });
        changes.Changed("fields", f.Id);
        await db.SaveChangesAsync(ct);
        return MapField(f, 0);
    }

    public async Task<AdminCustomFieldDto> UpdateFieldAsync(Guid id, SaveCustomFieldRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminTicketConfig);
        var f = await db.CustomFields.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Custom field", id);
        AdminRules.ApplyRowVersion(db, f, r.RowVersion, "field");
        var before = MapField(f, 0);
        await ApplyFieldAsync(f, r, isNew: false, ct);
        audit.Write(AdminAuditActions.TicketConfigChanged, "CustomField", f.Id, $"Custom field '{f.Label}' ({f.Entity}.{f.Key}) updated",
            new { from = before with { RowVersion = "" }, to = r with { RowVersion = null } });
        changes.Changed("fields", f.Id);
        await db.SaveChangesAsync(ct);
        return (await ListFieldsAsync(f.Entity, ct)).First(x => x.Id == id);
    }

    /// <summary>Deletes the definition and removes it from layouts. Values already stored on records stay but are no longer shown.</summary>
    public async Task DeleteFieldAsync(Guid id, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminTicketConfig);
        var f = await db.CustomFields.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Custom field", id);
        var token = $"cf:{f.Key}";
        foreach (var layout in await db.FormLayouts.Where(l => l.Entity == f.Entity && l.SectionsJson.Contains($"\"{token}\"")).ToListAsync(ct))
            layout.SectionsJson = RemoveField(layout.SectionsJson, token);
        db.CustomFields.Remove(f);
        audit.Write(AdminAuditActions.TicketConfigChanged, "CustomField", f.Id, $"Custom field '{f.Label}' ({f.Entity}.{f.Key}) deleted");
        changes.Changed("fields", f.Id);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<AdminCustomFieldDto>> ReorderFieldsAsync(string entity, ReorderRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminTicketConfig);
        var rows = await db.CustomFields.Where(f => f.Entity == entity).ToListAsync(ct);
        TicketConfigService.ApplyOrder(rows, r.Ids, f => f.Id, (f, i) => f.SortOrder = i);
        changes.Changed("fields");
        await db.SaveChangesAsync(ct);
        return await ListFieldsAsync(entity, ct);
    }

    private async Task ApplyFieldAsync(CustomFieldDefinition f, SaveCustomFieldRequest r, bool isNew, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        var entity = Entities.FirstOrDefault(e => string.Equals(e, r.Entity, StringComparison.OrdinalIgnoreCase));
        if (entity is null) errors["entity"] = "Choose Ticket, Contact or Customer.";
        else if (!isNew && entity != f.Entity) errors["entity"] = "The entity of a field cannot change.";
        var key = (r.Key ?? "").Trim();
        if (!AdminRules.FieldKey().IsMatch(key)) errors["key"] = "Use 2–63 lower-case letters, digits or underscores, starting with a letter.";
        else if (!isNew && key != f.Key) errors["key"] = "The key of a field cannot change (stored values use it).";
        else if (isNew && entity is not null && await db.CustomFields.AnyAsync(x => x.Entity == entity && x.Key == key, ct)) errors["key"] = "Another field already uses this key.";
        var label = (r.Label ?? "").Trim();
        if (label.Length is < 1 or > 100) errors["label"] = "Enter a label of up to 100 characters.";
        var type = AdminRules.ParseEnum(r.FieldType, "fieldType", errors, CustomFieldType.Text);
        if (!isNew && !errors.ContainsKey("fieldType") && type != f.FieldType) errors["fieldType"] = "The type of a field cannot change. Create a new field instead.";

        var optionsJson = "[]";
        if (OptionTypes.Contains(type))
        {
            var options = ParseOptions(r.Options, out var optionError);
            if (optionError is not null) errors["options"] = optionError;
            else optionsJson = Json.Serialize(options.Select(o => new { value = o.Value, label = o.Label }));
            if (r.DefaultValue is { Length: > 0 } dv && options.All(o => o.Value != dv)) errors["defaultValue"] = "The default must be one of the options.";
        }
        else if (type == CustomFieldType.Lookup)
        {
            var target = r.Options is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("entity", out var e) ? e.GetString() : null;
            if (target is not ("Product" or "Customer" or "Contact" or "User" or "Installation")) errors["options"] = "Choose the record type to look up (Product, Customer, Contact, User or Installation).";
            else optionsJson = Json.Serialize(new { entity = target });
        }

        var regex = AdminRules.Clean(r.ValidationRegex, 500);
        if (regex is not null)
        {
            if (!TextTypes.Contains(type)) errors["validationRegex"] = "Patterns apply to text fields only.";
            else
            {
                try { _ = new Regex(regex, RegexOptions.None, TimeSpan.FromMilliseconds(100)); }
                catch (ArgumentException) { errors["validationRegex"] = "This is not a valid regular expression."; }
            }
        }
        if (r.MinValue is { } min && r.MaxValue is { } max && min > max) errors["maxValue"] = "The maximum must be at least the minimum.";
        if ((r.MinValue is not null || r.MaxValue is not null) && type is not (CustomFieldType.Number or CustomFieldType.Decimal or CustomFieldType.Currency))
            errors["minValue"] = "Minimum and maximum apply to number fields only.";
        if (r.MaxLength is { } ml && (ml is < 1 or > 10000 || !TextTypes.Contains(type))) errors["maxLength"] = "A length limit of 1–10000 applies to text fields only.";
        if (r.EditableInPortal && !r.VisibleInPortal) errors["editableInPortal"] = "A field must be visible in the portal to be editable there.";

        var departments = (r.DepartmentIds ?? []).Distinct().ToList();
        if (departments.Count > 0 && await db.Departments.CountAsync(d => departments.Contains(d.Id), ct) != departments.Count) errors["departmentIds"] = "One or more departments do not exist.";
        var statuses = (r.RequiredForStatusIds ?? []).Distinct().ToList();
        if (statuses.Count > 0)
        {
            if (entity != "Ticket") errors["requiredForStatusIds"] = "Status requirements apply to ticket fields only.";
            else if (await db.TicketStatuses.CountAsync(s => statuses.Contains(s.Id), ct) != statuses.Count) errors["requiredForStatusIds"] = "One or more statuses do not exist.";
        }
        if (errors.Count > 0) throw new ValidationException(errors);

        f.Entity = entity!;
        f.Key = key;
        f.Label = label;
        f.FieldType = type;
        f.OptionsJson = optionsJson;
        f.IsRequired = r.IsRequired;
        f.DefaultValue = AdminRules.Clean(r.DefaultValue, 400);
        f.HelpText = AdminRules.Clean(r.HelpText, 500);
        f.ValidationRegex = regex;
        f.MinValue = r.MinValue;
        f.MaxValue = r.MaxValue;
        f.MaxLength = r.MaxLength;
        f.VisibleInPortal = r.VisibleInPortal;
        f.EditableInPortal = r.EditableInPortal;
        f.DepartmentIdsJson = Json.Serialize(departments);
        f.RequiredForStatusIdsJson = Json.Serialize(statuses);
        f.IsActive = r.IsActive;
    }

    private static List<OptionDto> ParseOptions(JsonElement? raw, out string? error)
    {
        error = null;
        var list = new List<OptionDto>();
        if (raw is not { ValueKind: JsonValueKind.Array } arr)
        {
            error = "Add at least one option.";
            return list;
        }
        foreach (var o in arr.EnumerateArray())
        {
            var value = o.ValueKind == JsonValueKind.Object ? (o.TryGetProperty("value", out var v) ? v.ToString() : "") : o.ToString();
            var label = o.ValueKind == JsonValueKind.Object && o.TryGetProperty("label", out var l) ? l.ToString() : value;
            value = value.Trim();
            label = string.IsNullOrWhiteSpace(label) ? value : label.Trim();
            if (value.Length == 0) continue;
            if (value.Length > 100 || label.Length > 100) { error = "Option values and labels are limited to 100 characters."; return list; }
            if (list.Any(x => string.Equals(x.Value, value, StringComparison.OrdinalIgnoreCase))) { error = $"The option '{value}' appears twice."; return list; }
            list.Add(new OptionDto(value, label));
        }
        if (list.Count == 0) error = "Add at least one option.";
        else if (list.Count > 500) error = "At most 500 options.";
        return list;
    }

    private static AdminCustomFieldDto MapField(CustomFieldDefinition f, int usedInLayouts) => new(f.Id, f.Entity, f.Key, f.Label, f.FieldType.ToString(),
        Json.Element(f.OptionsJson, "[]"), f.IsRequired, f.DefaultValue, f.HelpText, f.ValidationRegex, f.MinValue, f.MaxValue, f.MaxLength, f.VisibleInPortal,
        f.EditableInPortal, Json.GuidList(f.DepartmentIdsJson), Json.GuidList(f.RequiredForStatusIdsJson), f.SortOrder, f.IsActive, usedInLayouts,
        AdminRules.Version(f.RowVersion));

    // ───────────────────────────── Layouts ─────────────────────────────

    public async Task<List<AdminLayoutDto>> ListLayoutsAsync(CancellationToken ct)
    {
        ctx.Require(Permissions.AdminTicketConfig);
        var rows = await db.FormLayouts.AsNoTracking().OrderBy(l => l.Kind).ThenByDescending(l => l.IsDefault).ThenBy(l => l.Name).ToListAsync(ct);
        return rows.Select(MapLayout).ToList();
    }

    public async Task<AdminLayoutDto> GetLayoutAsync(Guid id, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminTicketConfig);
        return MapLayout(await db.FormLayouts.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw new NotFoundException("Layout", id));
    }

    public async Task<List<LayoutFieldOptionDto>> LayoutFieldsAsync(CancellationToken ct)
    {
        ctx.Require(Permissions.AdminTicketConfig);
        var custom = await db.CustomFields.AsNoTracking().Where(f => f.Entity == "Ticket" && f.IsActive).OrderBy(f => f.SortOrder).ToListAsync(ct);
        return LayoutFieldCatalog.Standard.Select(s => new LayoutFieldOptionDto(s.Key, s.Label, s.Type, false, s.Agent, s.Portal, s.AlwaysRequired))
            .Concat(custom.Select(f => new LayoutFieldOptionDto($"cf:{f.Key}", f.Label, f.FieldType.ToString(), true, true, f.EditableInPortal, f.IsRequired)))
            .ToList();
    }

    public async Task<AdminLayoutDto> SaveLayoutAsync(Guid? id, SaveLayoutRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminTicketConfig);
        FormLayout layout;
        if (id is { } existing)
        {
            layout = await db.FormLayouts.FirstOrDefaultAsync(l => l.Id == existing, ct) ?? throw new NotFoundException("Layout", existing);
            AdminRules.ApplyRowVersion(db, layout, r.RowVersion, "layout");
        }
        else
        {
            layout = new FormLayout();
            db.FormLayouts.Add(layout);
        }
        var errors = new Dictionary<string, string>();
        if (!string.Equals(r.Entity, "Ticket", StringComparison.OrdinalIgnoreCase)) errors["entity"] = "Layouts are available for tickets.";
        var name = (r.Name ?? "").Trim();
        if (name.Length is < 1 or > 100) errors["name"] = "Enter a name of up to 100 characters.";
        var kind = AdminRules.ParseEnum(r.Kind, "kind", errors, LayoutKind.AgentForm);
        if (r.DepartmentId is { } d && !await db.Departments.AnyAsync(x => x.Id == d, ct)) errors["departmentId"] = "Unknown department.";
        if (r.BrandId is { } b)
        {
            if (kind != LayoutKind.PortalForm) errors["brandId"] = "Brand-specific layouts are portal forms.";
            else if (!await db.Brands.AnyAsync(x => x.Id == b, ct)) errors["brandId"] = "Unknown brand.";
        }
        string? channel = null;
        if (!string.IsNullOrWhiteSpace(r.Channel))
        {
            if (!Enum.TryParse<Channel>(r.Channel, true, out var ch)) errors["channel"] = "Unknown channel.";
            else if (kind == LayoutKind.PortalForm) errors["channel"] = "Portal forms always use the portal channel.";
            else channel = ch.ToString();
        }
        var fields = await LayoutFieldsAsync(ct);
        var sectionsJson = NormalizeSections(r.Sections, kind, fields, errors);
        if (errors.Count == 0 && r.IsActive)
        {
            var clash = await db.FormLayouts.AnyAsync(l => l.Id != layout.Id && l.IsActive && l.Entity == "Ticket" && l.Kind == kind && l.DepartmentId == r.DepartmentId
                                                           && l.BrandId == r.BrandId && l.Channel == channel, ct);
            if (clash) errors["departmentId"] = "Another active layout already covers this department, brand and channel.";
        }
        if (errors.Count > 0) throw new ValidationException(errors);

        var wasNew = id is null;
        layout.Entity = "Ticket";
        layout.Name = name;
        layout.Kind = kind;
        layout.DepartmentId = r.DepartmentId;
        layout.BrandId = r.BrandId;
        layout.Channel = channel;
        layout.SectionsJson = sectionsJson;
        layout.IsActive = r.IsActive;
        if (r.IsDefault)
            foreach (var other in await db.FormLayouts.Where(l => l.Id != layout.Id && l.Entity == "Ticket" && l.Kind == kind && l.IsDefault).ToListAsync(ct))
                other.IsDefault = false;
        layout.IsDefault = r.IsDefault && r.IsActive;
        audit.Write(AdminAuditActions.TicketConfigChanged, "FormLayout", layout.Id, $"Form layout '{name}' ({kind}) {(wasNew ? "created" : "updated")}",
            new { kind = kind.ToString(), r.DepartmentId, r.BrandId, channel, r.IsDefault, sections = Json.Element(sectionsJson, "[]") });
        changes.Changed("layouts", layout.Id);
        await db.SaveChangesAsync(ct);
        return MapLayout(layout);
    }

    public async Task DeleteLayoutAsync(Guid id, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminTicketConfig);
        var layout = await db.FormLayouts.FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw new NotFoundException("Layout", id);
        db.FormLayouts.Remove(layout);
        audit.Write(AdminAuditActions.TicketConfigChanged, "FormLayout", layout.Id, $"Form layout '{layout.Name}' deleted");
        changes.Changed("layouts", layout.Id);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Validates and re-serialises sections: known fields only, each field once, portal forms limited to portal fields.</summary>
    public static string NormalizeSections(JsonElement raw, LayoutKind kind, IReadOnlyList<LayoutFieldOptionDto> catalog, Dictionary<string, string> errors)
    {
        if (raw.ValueKind != JsonValueKind.Array || raw.GetArrayLength() == 0)
        {
            errors["sections"] = "Add at least one section.";
            return "[]";
        }
        if (raw.GetArrayLength() > 20)
        {
            errors["sections"] = "At most 20 sections.";
            return "[]";
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new JsonArray();
        var index = 0;
        foreach (var s in raw.EnumerateArray())
        {
            index++;
            if (s.ValueKind != JsonValueKind.Object) { errors["sections"] = $"Section {index} is not valid."; continue; }
            var title = s.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()!.Trim() : "";
            if (title.Length > 100) errors["sections"] = $"The title of section {index} is longer than 100 characters.";
            var columns = s.TryGetProperty("columns", out var c) && c.TryGetInt32(out var cols) ? Math.Clamp(cols, 1, 3) : 2;
            var fields = new JsonArray();
            if (s.TryGetProperty("fields", out var fs) && fs.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in fs.EnumerateArray())
                {
                    var key = f.ValueKind == JsonValueKind.Object && f.TryGetProperty("key", out var k) ? k.GetString() : f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                    var required = f.ValueKind == JsonValueKind.Object && f.TryGetProperty("required", out var rq) && rq.ValueKind == JsonValueKind.True;
                    var option = catalog.FirstOrDefault(o => o.Key == key);
                    if (key is null || option is null) { errors["sections"] = $"Unknown field '{key}' in section {index}."; continue; }
                    if (kind == LayoutKind.PortalForm ? !option.PortalForm : !option.AgentForm) { errors["sections"] = $"'{option.Label}' cannot be placed on a {(kind == LayoutKind.PortalForm ? "portal" : "agent")} form."; continue; }
                    if (!seen.Add(key)) { errors["sections"] = $"'{option.Label}' appears more than once."; continue; }
                    fields.Add(new JsonObject { ["key"] = key, ["required"] = required || option.AlwaysRequired });
                }
            }
            result.Add(new JsonObject { ["title"] = title, ["columns"] = columns, ["fields"] = fields });
        }
        return result.ToJsonString();
    }

    private static string RemoveField(string sectionsJson, string key)
    {
        try
        {
            var arr = JsonNode.Parse(sectionsJson) as JsonArray;
            if (arr is null) return sectionsJson;
            foreach (var section in arr.OfType<JsonObject>())
                if (section["fields"] is JsonArray fields)
                    foreach (var f in fields.ToList())
                        if ((f is JsonObject o ? o["key"]?.GetValue<string>() : f?.GetValue<string>()) == key) fields.Remove(f);
            return arr.ToJsonString();
        }
        catch (JsonException) { return sectionsJson; }
        catch (InvalidOperationException) { return sectionsJson; }
    }

    private static AdminLayoutDto MapLayout(FormLayout l) => new(l.Id, l.Entity, l.Name, l.Kind.ToString(), l.DepartmentId, l.BrandId, l.Channel,
        Json.Element(l.SectionsJson, "[]"), l.IsDefault, l.IsActive, l.UpdatedAt, AdminRules.Version(l.RowVersion));
}
