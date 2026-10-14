using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Tickets;

/// <summary>
/// Exposes a ticket (plus related customer/contact/contract data) to the shared condition evaluator used by SLA
/// policies, routing, automation rules and blueprint validations. Field names are stable and documented in the
/// automation designer ("cf:key" for custom fields, "customer.tier", "contact.isVip", "contract.type"…).
/// </summary>
public sealed class TicketConditionContext : IConditionContext
{
    private readonly Dictionary<string, object?> _values;
    private readonly Dictionary<string, object?> _previous;

    public DateTime Now { get; }

    private TicketConditionContext(Dictionary<string, object?> values, Dictionary<string, object?> previous, DateTime now)
    {
        _values = values;
        _previous = previous;
        Now = now;
    }

    public bool TryGetValue(string field, out object? value) => _values.TryGetValue(field, out value);
    public bool TryGetPrevious(string field, out object? previous) => _previous.TryGetValue(field, out previous);
    public IReadOnlyDictionary<string, object?> Values => _values;

    public static readonly IReadOnlyList<(string Field, string Label, string Type)> FieldCatalog =
    [
        ("subject", "Subject", "text"), ("description", "Description", "text"), ("priority", "Priority", "priority"), ("severity", "Severity", "enum"),
        ("statusId", "Status", "status"), ("status", "Status name", "text"), ("statusCategory", "Status category", "enum"), ("channel", "Channel", "enum"),
        ("departmentId", "Department", "department"), ("teamId", "Team", "team"), ("assigneeId", "Assignee", "user"), ("customerId", "Customer", "customer"),
        ("customer.tier", "Customer tier", "text"), ("customer.industry", "Customer industry", "text"), ("customer.region", "Service region", "text"),
        ("contactId", "Contact", "contact"), ("contact.isVip", "VIP contact", "bool"), ("contact.email", "Contact e-mail", "text"),
        ("productId", "Product", "product"), ("moduleId", "Module", "module"), ("versionId", "Version", "version"), ("installationId", "Installation", "installation"),
        ("categoryId", "Category", "category"), ("subcategoryId", "Subcategory", "category"), ("issueTypeId", "Issue type", "issueType"),
        ("tags", "Tags", "tags"), ("language", "Language", "text"), ("sentiment", "Sentiment", "enum"), ("slaState", "SLA state", "enum"),
        ("isEscalated", "Escalated", "bool"), ("entitlementStatus", "Entitlement", "enum"), ("contract.type", "Contract type", "enum"),
        ("brandId", "Brand", "brand"), ("createdAt", "Created", "datetime"), ("dueAt", "Resolution due", "datetime"), ("lastActivityAt", "Last activity", "datetime"),
        ("lastCustomerResponseAt", "Last customer reply", "datetime"), ("lastAgentResponseAt", "Last agent reply", "datetime"), ("reopenCount", "Reopen count", "number"),
        ("agentReplyCount", "Agent replies", "number"), ("customerReplyCount", "Customer replies", "number"), ("isUnassigned", "Unassigned", "bool"),
        ("hoursSinceCreated", "Hours since created", "number"), ("hoursSinceLastActivity", "Hours since last activity", "number"), ("csatScore", "CSAT score", "number")
    ];

    public static async Task<TicketConditionContext> BuildAsync(DeskDbContext db, Ticket t, IReadOnlyList<FieldChange>? changes, CancellationToken ct)
    {
        var now = db.Clock.UtcNow;
        var customer = t.CustomerId is null ? null : await db.Customers.AsNoTracking().Where(c => c.Id == t.CustomerId)
            .Select(c => new { c.Tier, c.Industry, c.ServiceRegion }).FirstOrDefaultAsync(ct);
        var contact = t.ContactId is null ? null : await db.Contacts.AsNoTracking().Where(c => c.Id == t.ContactId)
            .Select(c => new { c.IsVip, c.Email }).FirstOrDefaultAsync(ct);
        var contractType = t.ContractId is null ? null : await db.Contracts.AsNoTracking().Where(c => c.Id == t.ContractId).Select(c => (Domain.Service.ContractType?)c.Type).FirstOrDefaultAsync(ct);
        var statusName = await db.TicketStatuses.AsNoTracking().Where(s => s.Id == t.StatusId).Select(s => s.Name).FirstOrDefaultAsync(ct);
        var tags = t.Tags.Where(x => x.Tag is not null).Select(x => x.Tag!.Name).ToList();
        if (tags.Count == 0 && t.Tags.Count > 0)
        {
            var tagIds = t.Tags.Select(x => x.TagId).ToList();
            tags = await db.Tags.AsNoTracking().Where(x => tagIds.Contains(x.Id)).Select(x => x.Name).ToListAsync(ct);
        }

        var v = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = t.Id.ToString(), ["number"] = t.Number, ["subject"] = t.Subject, ["description"] = t.DescriptionText,
            ["priority"] = t.Priority.ToString(), ["severity"] = t.Severity?.ToString(), ["statusId"] = t.StatusId.ToString(), ["status"] = statusName,
            ["statusCategory"] = t.StatusCategory.ToString(), ["channel"] = t.Channel.ToString(), ["departmentId"] = t.DepartmentId.ToString(),
            ["teamId"] = t.TeamId?.ToString(), ["assigneeId"] = t.AssigneeId?.ToString(), ["customerId"] = t.CustomerId?.ToString(),
            ["customer.tier"] = customer?.Tier, ["customer.industry"] = customer?.Industry, ["customer.region"] = customer?.ServiceRegion,
            ["contactId"] = t.ContactId?.ToString(), ["contact.isVip"] = contact?.IsVip ?? false, ["contact.email"] = contact?.Email,
            ["productId"] = t.ProductId?.ToString(), ["moduleId"] = t.ModuleId?.ToString(), ["versionId"] = t.VersionId?.ToString(),
            ["installationId"] = t.InstallationId?.ToString(), ["categoryId"] = t.CategoryId?.ToString(), ["subcategoryId"] = t.SubcategoryId?.ToString(),
            ["issueTypeId"] = t.IssueTypeId?.ToString(), ["tags"] = tags, ["language"] = t.Language, ["sentiment"] = t.Sentiment.ToString(),
            ["slaState"] = t.SlaState.ToString(), ["isEscalated"] = t.IsEscalated, ["entitlementStatus"] = t.EntitlementStatus.ToString(),
            ["contract.type"] = contractType?.ToString(), ["brandId"] = t.BrandId?.ToString(), ["createdAt"] = t.CreatedAt, ["dueAt"] = t.DueAt,
            ["lastActivityAt"] = t.LastActivityAt, ["lastCustomerResponseAt"] = t.LastCustomerResponseAt, ["lastAgentResponseAt"] = t.LastAgentResponseAt,
            ["reopenCount"] = t.ReopenCount, ["agentReplyCount"] = t.AgentReplyCount, ["customerReplyCount"] = t.CustomerReplyCount,
            ["isUnassigned"] = t.AssigneeId is null, ["hoursSinceCreated"] = Math.Round((now - t.CreatedAt).TotalHours, 2),
            ["hoursSinceLastActivity"] = Math.Round((now - t.LastActivityAt).TotalHours, 2), ["csatScore"] = t.CsatScore
        };
        foreach (var (key, value) in Json.Object(t.CustomFieldsJson))
            v["cf:" + key] = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetDecimal(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Array => value.EnumerateArray().Select(x => x.ToString()).ToList(),
                _ => value.ToString()
            };

        var previous = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in changes ?? [])
        {
            var key = c.Field switch { "status" => "statusId", "assignee" => "assigneeId", "department" => "departmentId", _ => c.Field };
            previous[key] = c.From;
            if (c.Field == "status") previous["status"] = c.From;
            if (c.Field == "customFields") foreach (var k in v.Keys.Where(k => k.StartsWith("cf:")).ToList()) previous[k] = null;
        }
        return new TicketConditionContext(v, previous, now);
    }
}
