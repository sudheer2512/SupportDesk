using Microsoft.EntityFrameworkCore;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Tickets;

/// <summary>
/// Look-ups for the agent workbench: global search (tickets, customers, contacts) and the customer / contact /
/// installation pickers of the ticket form. Tickets are scoped with <see cref="TicketAccess.VisibleTo"/>; customers honour
/// the agent's customer scope (account-managed agents only see their own accounts).
/// </summary>
public sealed class WorkbenchSearchService(DeskDbContext db, RequestContext ctx, TicketQueryService queries)
{
    private const int MaxLimit = 50;

    public async Task<QuickSearchResultDto> SearchAsync(string? q, int limit, CancellationToken ct = default)
    {
        var term = (q ?? "").Trim();
        if (term.Length < 2 && !long.TryParse(term.TrimStart('#'), out _)) return new QuickSearchResultDto([], [], []);
        limit = Math.Clamp(limit, 1, 20);
        var tickets = ctx.Has(Permissions.TicketsView)
            ? (await queries.ListAsync(new TicketQuery { Q = term, Limit = limit, Sort = "lastActivity", Dir = "desc" }, ct)).Items
            : [];
        var canSeeCustomers = CanLookUpCustomers();
        var customers = canSeeCustomers ? await CustomersAsync(term, limit, ct) : [];
        var contacts = canSeeCustomers ? await ContactsAsync(term, null, limit, ct) : [];
        return new QuickSearchResultDto(tickets, customers, contacts);
    }

    public async Task<List<CustomerLookupDto>> CustomersAsync(string? q, int limit, CancellationToken ct = default)
    {
        RequireCustomerLookup();
        var term = (q ?? "").Trim();
        var query = ScopedCustomers().Where(c => c.Status != CustomerStatus.Inactive);
        if (term.Length > 0) query = query.Where(c => c.Name.Contains(term) || (c.Code != null && c.Code.Contains(term)) || (c.Email != null && c.Email.Contains(term)));
        var rows = await query.OrderBy(c => c.Name).Take(Math.Clamp(limit, 1, MaxLimit))
            .Select(c => new { c.Id, c.Name, c.Code, c.Tier, c.Industry, c.Status }).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var open = await queries.Scoped().Where(t => t.CustomerId != null && ids.Contains(t.CustomerId.Value) && t.StatusCategory < StatusCategory.Resolved)
            .GroupBy(t => t.CustomerId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return rows.Select(r => new CustomerLookupDto(r.Id, r.Name, r.Code, r.Tier, r.Industry, r.Status.ToString(), open.GetValueOrDefault(r.Id))).ToList();
    }

    public async Task<List<ContactLookupDto>> ContactsAsync(string? q, Guid? customerId, int limit, CancellationToken ct = default)
    {
        RequireCustomerLookup();
        var term = (q ?? "").Trim();
        var customers = ScopedCustomers().Select(c => c.Id);
        var query = db.Contacts.AsNoTracking().Where(c => c.CustomerId == null || customers.Contains(c.CustomerId.Value));
        if (customerId is { } cid) query = query.Where(c => c.CustomerId == cid);
        if (term.Length > 0)
            query = query.Where(c => c.FullName.Contains(term) || (c.Email != null && c.Email.Contains(term)) || (c.Phone != null && c.Phone.Contains(term)) ||
                                     (c.Mobile != null && c.Mobile.Contains(term)));
        var rows = await query.OrderByDescending(c => c.IsPrimary).ThenBy(c => c.FullName).Take(Math.Clamp(limit, 1, MaxLimit))
            .Select(c => new
            {
                c.Id, c.FullName, c.Email, Phone = c.Phone ?? c.Mobile, c.JobTitle, c.IsVip, c.CustomerId, c.LocationId,
                CustomerName = db.Customers.Where(x => x.Id == c.CustomerId).Select(x => x.Name).FirstOrDefault(),
                LocationName = db.CustomerLocations.Where(x => x.Id == c.LocationId).Select(x => x.Name).FirstOrDefault()
            }).ToListAsync(ct);
        return rows.Select(r => new ContactLookupDto(r.Id, r.FullName, r.Email, r.Phone, r.JobTitle, r.IsVip, r.CustomerId, r.CustomerName, r.LocationId,
            r.LocationName)).ToList();
    }

    public async Task<List<InstallationLookupDto>> InstallationsAsync(Guid customerId, Guid? productId, CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.ProductsView, Permissions.TicketsCreate, Permissions.TicketsEdit);
        if (!await ScopedCustomers().AnyAsync(c => c.Id == customerId, ct)) throw new NotFoundException("Customer", customerId);
        var query = db.Installations.AsNoTracking().Where(i => i.CustomerId == customerId);
        if (productId is { } pid) query = query.Where(i => i.ProductId == pid);
        var rows = await query.OrderBy(i => i.Name)
            .Select(i => new
            {
                i.Id, i.Name, i.ProductId, i.VersionId, i.Environment, i.Status, i.LocationId,
                ProductName = db.Products.Where(p => p.Id == i.ProductId).Select(p => p.Name).FirstOrDefault(),
                Version = db.ProductVersions.Where(v => v.Id == i.VersionId).Select(v => v.Version).FirstOrDefault()
            }).Take(200).ToListAsync(ct);
        return rows.Select(r => new InstallationLookupDto(r.Id, r.Name, r.ProductId, r.ProductName ?? "", r.VersionId, r.Version, r.Environment.ToString(),
            r.Status.ToString(), r.LocationId)).ToList();
    }

    private bool CanLookUpCustomers() =>
        ctx.Has(Permissions.CustomersView) || ctx.Has(Permissions.TicketsCreate) || ctx.Has(Permissions.TicketsEdit);

    private void RequireCustomerLookup() => ctx.RequireAny(Permissions.CustomersView, Permissions.TicketsCreate, Permissions.TicketsEdit);

    private IQueryable<Customer> ScopedCustomers()
    {
        var q = db.Customers.AsNoTracking();
        if (!ctx.IsSystem && ctx.CustomerScope == CustomerScope.AccountManaged) q = q.Where(c => c.AccountManagerUserId == ctx.UserId);
        return q;
    }
}
