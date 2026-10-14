using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Tickets;

/// <summary>
/// Per-request name dictionaries for small configuration tables (statuses, departments, teams, users, products,
/// categories). Lists of tickets are mapped with a constant number of queries (no N+1).
/// </summary>
public sealed class TicketLookups(DeskDbContext db, TenantSettingsService settings)
{
    private Dictionary<Guid, (string Name, string Color, StatusCategory Category, string? CustomerLabel)>? _statuses;
    private Dictionary<Guid, string>? _departments, _teams, _users, _products, _categories, _modules, _versions, _issueTypes, _brands, _slaPolicies;
    private string? _prefix;

    public async Task LoadAsync(CancellationToken ct)
    {
        if (_statuses is not null) return;
        _statuses = await db.TicketStatuses.AsNoTracking().ToDictionaryAsync(s => s.Id, s => (s.Name, s.Color, s.Category, s.CustomerLabel), ct);
        _departments = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        _teams = await db.Teams.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        _users = await db.Users.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        _products = await db.Products.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        _categories = await db.TicketCategories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        _modules = await db.ProductModules.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        _versions = await db.ProductVersions.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Version, ct);
        _issueTypes = await db.IssueTypes.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        _brands = await db.Brands.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        _slaPolicies = await db.SlaPolicies.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        _prefix = (await settings.TicketsAsync(ct)).NumberPrefix;
    }

    public string Prefix => _prefix ?? "#";
    public string Display(long number) => $"{Prefix}{number}";
    public string Status(Guid id) => _statuses!.TryGetValue(id, out var s) ? s.Name : "Unknown";
    public string StatusColor(Guid id) => _statuses!.TryGetValue(id, out var s) ? s.Color : "#64748b";
    public string StatusForCustomer(Guid id) => _statuses!.TryGetValue(id, out var s) ? s.CustomerLabel ?? s.Name : "";
    public string? Department(Guid? id) => Name(_departments!, id);
    public string? Team(Guid? id) => Name(_teams!, id);
    public string? User(Guid? id) => Name(_users!, id);
    public string? Product(Guid? id) => Name(_products!, id);
    public string? Category(Guid? id) => Name(_categories!, id);
    public string? Module(Guid? id) => Name(_modules!, id);
    public string? Version(Guid? id) => Name(_versions!, id);
    public string? IssueType(Guid? id) => Name(_issueTypes!, id);
    public string? Brand(Guid? id) => Name(_brands!, id);
    public string? SlaPolicy(Guid? id) => Name(_slaPolicies!, id);

    private static string? Name(Dictionary<Guid, string> d, Guid? id) => id is { } g && d.TryGetValue(g, out var n) ? n : null;

    /// <summary>Maps tickets to list rows with batched customer/contact/tag lookups.</summary>
    public async Task<List<TicketListItemDto>> MapListAsync(IReadOnlyList<Ticket> tickets, CancellationToken ct)
    {
        await LoadAsync(ct);
        var customerIds = tickets.Where(t => t.CustomerId.HasValue).Select(t => t.CustomerId!.Value).Distinct().ToList();
        var contactIds = tickets.Where(t => t.ContactId.HasValue).Select(t => t.ContactId!.Value).Distinct().ToList();
        var ticketIds = tickets.Select(t => t.Id).ToList();
        var customers = await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(c => customerIds.Contains(c.Id)).Select(c => new { c.Id, c.Name, c.Tier }).ToDictionaryAsync(c => c.Id, ct);
        var contacts = await db.Contacts.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(c => contactIds.Contains(c.Id)).Select(c => new { c.Id, c.FullName }).ToDictionaryAsync(c => c.Id, c => c.FullName, ct);
        var tags = (await db.TicketTags.AsNoTracking().Where(tt => ticketIds.Contains(tt.TicketId)).Select(tt => new { tt.TicketId, tt.Tag!.Name }).ToListAsync(ct))
            .GroupBy(x => x.TicketId).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Name).OrderBy(n => n).ToList());

        return tickets.Select(t =>
        {
            var cust = t.CustomerId is { } cid && customers.TryGetValue(cid, out var c) ? c : null;
            return new TicketListItemDto(
                t.Id, t.Number, Display(t.Number), t.Subject, t.Channel.ToString(), t.Priority.ToString(), t.Severity?.ToString(), t.StatusId, Status(t.StatusId),
                t.StatusCategory.ToString(), StatusColor(t.StatusId), t.DepartmentId, Department(t.DepartmentId) ?? "", t.TeamId, Team(t.TeamId), t.AssigneeId,
                User(t.AssigneeId), t.CustomerId, cust?.Name, cust?.Tier, t.ContactId, t.ContactId is { } k && contacts.TryGetValue(k, out var cn) ? cn : null,
                Product(t.ProductId), Category(t.CategoryId), t.SlaState.ToString(), t.DueAt, t.FirstResponseDueAt, t.CreatedAt, t.LastActivityAt,
                t.LastCustomerResponseAt, t.IsEscalated, t.Sentiment.ToString(), t.AgentReplyCount, t.CustomerReplyCount,
                tags.TryGetValue(t.Id, out var tg) ? tg : [],
                t.LastCustomerResponseAt.HasValue && (!t.LastAgentResponseAt.HasValue || t.LastCustomerResponseAt > t.LastAgentResponseAt));
        }).ToList();
    }

    public static TicketPermissionsDto PermissionsFor(Ticket t, RequestContext ctx)
    {
        var open = t.StatusCategory.IsOpen();
        var merged = t.MergedIntoTicketId.HasValue;
        bool H(string p) => ctx.Has(p) && !merged;
        return new TicketPermissionsDto(
            Edit: H(Permissions.TicketsEdit), Reply: H(Permissions.TicketsReply), Note: H(Permissions.TicketsNote), Assign: H(Permissions.TicketsAssign),
            Transfer: H(Permissions.TicketsTransfer), Escalate: H(Permissions.TicketsEscalate) && open, Resolve: H(Permissions.TicketsResolve) && open,
            Reopen: H(Permissions.TicketsReopen) && !open, Merge: H(Permissions.TicketsMerge), Split: H(Permissions.TicketsSplit), Delete: H(Permissions.TicketsDelete),
            LogTime: H(Permissions.TicketsTime), ViewCosts: ctx.Has(Permissions.TicketsViewCosts), OverrideEntitlement: H(Permissions.TicketsOverrideEntitlement));
    }

    public static IReadOnlyList<FieldChangeDto> Changes(string? json) =>
        Json.DeserializeOr(json, new List<Domain.Events.FieldChange>()).Select(c => new FieldChangeDto(c.Field, c.From, c.To)).ToList();
}
