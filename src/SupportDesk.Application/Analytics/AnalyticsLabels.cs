using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Tickets;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Analytics;

/// <summary>Display names for dimension keys (ids → names, enum names → words), loaded once per request.</summary>
public sealed partial class AnalyticsLabels(DeskDbContext db, TicketLookups lookups)
{
    private Dictionary<string, string>? _rootCauses;
    private readonly Dictionary<Guid, string> _customers = new();

    public async Task LoadAsync(CancellationToken ct)
    {
        await lookups.LoadAsync(ct);
        _rootCauses ??= await db.LookupValues.AsNoTracking().Where(v => v.Type == "root_cause")
            .ToDictionaryAsync(v => v.Value, v => v.Label, StringComparer.OrdinalIgnoreCase, ct);
    }

    /// <summary>Loads customer names for the given ids (other dimensions use the cached lookups).</summary>
    public async Task LoadCustomersAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var missing = ids.Where(i => !_customers.ContainsKey(i)).Distinct().ToList();
        if (missing.Count == 0) return;
        foreach (var chunk in missing.Chunk(500))
        {
            var names = await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(c => chunk.Contains(c.Id))
                .Select(c => new { c.Id, c.Name }).ToListAsync(ct);
            foreach (var n in names) _customers[n.Id] = n.Name;
        }
    }

    public TicketLookups Lookups => lookups;

    public string Label(string dimension, string? key)
    {
        if (string.IsNullOrEmpty(key) || key == MetricCatalog.NoneKey) return None(dimension);
        Guid.TryParse(key, out var id);
        return dimension switch
        {
            "department" => lookups.Department(id) ?? "Unknown department",
            "team" => lookups.Team(id) ?? "Unknown team",
            "assignee" => lookups.User(id) ?? "Unknown agent",
            "customer" => _customers.GetValueOrDefault(id, "Unknown customer"),
            "product" => lookups.Product(id) ?? "Unknown product",
            "module" => lookups.Module(id) ?? "Unknown module",
            "version" => lookups.Version(id) ?? "Unknown version",
            "category" => lookups.Category(id) ?? "Unknown category",
            "issue_type" => lookups.IssueType(id) ?? "Unknown issue type",
            "root_cause" => _rootCauses is not null && _rootCauses.TryGetValue(key, out var rc) ? rc : Words(key.Replace('_', ' ')),
            "channel" or "priority" or "status_category" or "sentiment" => Words(key),
            _ => key
        };
    }

    public string? Name(string field, Guid id) => field switch
    {
        "department" => lookups.Department(id),
        "team" => lookups.Team(id),
        "assignee" => lookups.User(id),
        "customer" => _customers.GetValueOrDefault(id),
        "product" => lookups.Product(id),
        "module" => lookups.Module(id),
        "version" => lookups.Version(id),
        "category" => lookups.Category(id),
        _ => null
    };

    public static string None(string dimension) => dimension switch
    {
        "assignee" => "Unassigned",
        "team" => "No team",
        "customer" => "No customer",
        "customer_tier" => "No tier",
        "product" => "No product",
        "module" => "No module",
        "version" => "No version",
        "category" => "Uncategorised",
        "issue_type" => "No issue type",
        "root_cause" => "No root cause",
        _ => "(none)"
    };

    /// <summary>"InProgress" → "In progress", "WhatsApp" stays, "WalkIn" → "Walk in".</summary>
    public static string Words(string value)
    {
        if (value is "WhatsApp" or "Sms" or "Api" or "Erp") return value switch { "Sms" => "SMS", "Api" => "API", "Erp" => "ERP", _ => value };
        var s = CamelBoundary().Replace(value, " $1").Trim();
        return s.Length <= 1 ? s : s[0] + s[1..].ToLowerInvariant();
    }

    [GeneratedRegex("(?<=[a-z])([A-Z])")]
    private static partial Regex CamelBoundary();
}
