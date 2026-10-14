using System.Globalization;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Tickets;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Automation;

public enum TemplateEncoding { Text, Html, Json, Url }

/// <summary>
/// Replaces {{placeholders}} (ticket.number, contact.name, cf:key, webhook.field…) in action templates. Values are encoded
/// for the target: HTML-encoded in notes/replies (which are sanitised afterwards anyway), JSON-escaped in webhook bodies,
/// URL-escaped in URLs. Unknown placeholders render as empty text.
/// </summary>
public static partial class TemplateRenderer
{
    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_.:\-]+)\s*\}\}")]
    private static partial Regex Placeholder();

    private static readonly JavaScriptEncoder JsonEncoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    public static string Render(string? template, IReadOnlyDictionary<string, string?> values, TemplateEncoding encoding)
    {
        if (string.IsNullOrEmpty(template)) return "";
        return Placeholder().Replace(template, m =>
        {
            values.TryGetValue(m.Groups[1].Value, out var v);
            v ??= "";
            return encoding switch
            {
                TemplateEncoding.Html => WebUtility.HtmlEncode(v),
                TemplateEncoding.Json => JsonEncodedText.Encode(v, JsonEncoder).ToString(),
                TemplateEncoding.Url => Uri.EscapeDataString(v),
                _ => v
            };
        });
    }

    /// <summary>Renders every string leaf of a JSON value (ERP change sets, custom field values).</summary>
    public static JsonElement RenderJson(JsonElement element, IReadOnlyDictionary<string, string?> values)
    {
        var node = JsonNode.Parse(element.GetRawText());
        node = RenderNode(node, values);
        return JsonSerializer.SerializeToElement(node);
    }

    private static JsonNode? RenderNode(JsonNode? node, IReadOnlyDictionary<string, string?> values)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList()) obj[key] = RenderNode(obj[key]?.DeepClone(), values);
                return obj;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++) arr[i] = RenderNode(arr[i]?.DeepClone(), values);
                return arr;
            case JsonValue v when v.TryGetValue<string>(out var s):
                return JsonValue.Create(Render(s, values, TemplateEncoding.Text));
            default:
                return node;
        }
    }

    public static bool HasPlaceholders(string? text) => text is not null && Placeholder().IsMatch(text);
}

/// <summary>Builds the placeholder dictionary for a ticket (condition fields, display names, rule and webhook values).</summary>
public static class TemplateValues
{
    public static async Task<Dictionary<string, string?>> BuildAsync(DeskDbContext db, TicketLookups lookups, Guid ticketId, string sourceName,
        IReadOnlyDictionary<string, object?>? extra, CancellationToken ct)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        await lookups.LoadAsync(ct);
        var t = await db.Tickets.AsNoTracking().Include(x => x.Tags).ThenInclude(x => x.Tag).FirstOrDefaultAsync(x => x.Id == ticketId, ct);
        if (t is not null)
        {
            var cond = await TicketConditionContext.BuildAsync(db, t, null, ct);
            foreach (var (k, v) in cond.Values) values[k] = Stringify(v);
            var contact = t.ContactId is null ? null : await db.Contacts.AsNoTracking().Where(c => c.Id == t.ContactId)
                .Select(c => new { c.FullName, c.FirstName, c.Email, c.Phone }).FirstOrDefaultAsync(ct);
            var customer = t.CustomerId is null ? null : await db.Customers.AsNoTracking().Where(c => c.Id == t.CustomerId)
                .Select(c => new { c.Name, c.Tier, c.Code }).FirstOrDefaultAsync(ct);
            values["ticket.id"] = t.Id.ToString();
            values["ticket.number"] = lookups.Display(t.Number);
            values["ticket.rawNumber"] = t.Number.ToString(CultureInfo.InvariantCulture);
            values["ticket.subject"] = t.Subject;
            values["ticket.description"] = t.DescriptionText;
            values["ticket.priority"] = t.Priority.ToString();
            values["ticket.severity"] = t.Severity?.ToString();
            values["ticket.status"] = lookups.Status(t.StatusId);
            values["ticket.statusCategory"] = t.StatusCategory.ToString();
            values["ticket.channel"] = t.Channel.ToString();
            values["ticket.link"] = $"/tickets/{t.Id}";
            values["ticket.createdAt"] = t.CreatedAt.ToString("O", CultureInfo.InvariantCulture);
            values["ticket.dueAt"] = t.DueAt?.ToString("O", CultureInfo.InvariantCulture);
            values["ticket.tags"] = string.Join(", ", t.Tags.Where(x => x.Tag is not null).Select(x => x.Tag!.Name));
            values["contact.name"] = contact?.FullName;
            values["contact.firstName"] = contact?.FirstName ?? contact?.FullName?.Split(' ')[0];
            values["contact.email"] = contact?.Email;
            values["contact.phone"] = contact?.Phone;
            values["customer.name"] = customer?.Name;
            values["customer.code"] = customer?.Code;
            values["customer.tier"] = customer?.Tier;
            values["assignee.name"] = lookups.User(t.AssigneeId);
            values["team.name"] = lookups.Team(t.TeamId);
            values["department.name"] = lookups.Department(t.DepartmentId);
            values["category.name"] = lookups.Category(t.CategoryId);
            values["product.name"] = lookups.Product(t.ProductId);
            values["blueprintState"] = t.WorkflowStateKey;
        }
        values["rule.name"] = sourceName;
        values["now"] = db.Clock.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        if (extra is not null)
            foreach (var (k, v) in extra) values[k] = Stringify(v);
        return values;
    }

    public static string? Stringify(object? v) => v switch
    {
        null => null,
        string s => s,
        IEnumerable<string> list => string.Join(", ", list),
        _ => ConditionEvaluator.AsString(v)
    };

    /// <summary>Flattens a JSON payload into "prefix.key" values (scalars and nested objects up to depth 3).</summary>
    public static Dictionary<string, object?> Flatten(JsonElement payload, string prefix)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        Walk(payload, prefix, 0);
        return result;

        void Walk(JsonElement e, string path, int depth)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object when depth < 3:
                    foreach (var p in e.EnumerateObject()) Walk(p.Value, $"{path}.{p.Name}", depth + 1);
                    break;
                case JsonValueKind.Array:
                    result[path] = e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()).ToList();
                    break;
                case JsonValueKind.String: result[path] = e.GetString(); break;
                case JsonValueKind.Number: result[path] = e.TryGetDecimal(out var d) ? d : e.GetDouble(); break;
                case JsonValueKind.True: result[path] = true; break;
                case JsonValueKind.False: result[path] = false; break;
                case JsonValueKind.Null: result[path] = null; break;
                default: result[path] = e.GetRawText(); break;
            }
        }
    }
}
