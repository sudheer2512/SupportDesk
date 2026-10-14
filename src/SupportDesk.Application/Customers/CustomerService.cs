using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Customers;
using SupportDesk.Domain;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Service;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Customers;

public static class CustomerAuditActions
{
    public const string CustomerDeleted = "customer.deleted";
    public const string CustomerRestored = "customer.restored";
    public const string ContactDeleted = "contact.deleted";
    public const string PortalInvited = "contact.portal_invited";
    public const string PortalReset = "contact.portal_reset";
    public const string PortalAccessChanged = "contact.portal_access_changed";
    public const string ContractChanged = "contract.changed";
    public const string ContractDeleted = "contract.deleted";
    public const string ErpSync = "integration.erp_sync";
}

/// <summary>
/// Customer organisations: keyset-paged search, detail with hierarchy roll-up, create/update (optimistic concurrency,
/// custom fields validated against the "Customer" definitions, unique e-mail domains for contact auto-linking), soft
/// delete/restore, groups, locations (branches) and customer departments. Enforces customers.view/manage and the
/// caller's customer scope.
/// </summary>
public sealed partial class CustomerService(
    DeskDbContext db,
    RequestContext ctx,
    IEventPublisher events,
    IAuditWriter audit,
    CustomFieldValidator customFields)
{
    public const int MaxLimit = 200;
    private static readonly string[] DefaultTiers = ["Platinum", "Gold", "Silver", "Bronze", "Standard"];

    // ───────────────────────────── List / search ─────────────────────────────

    public async Task<PagedResult<CustomerListItemDto>> ListAsync(CustomerQuery q, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersView);
        var query = db.Customers.AsNoTracking().VisibleTo(ctx);

        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = q.Q.Trim();
            query = query.Where(c => c.Name.Contains(term) || (c.Code != null && c.Code.Contains(term)) || (c.ErpCode != null && c.ErpCode.Contains(term)) ||
                                     c.DomainsJson.Contains(term) || (c.Email != null && c.Email.Contains(term)) || (c.Phone != null && c.Phone.Contains(term)));
        }
        var statuses = Parse.Enums<CustomerStatus>(q.Status);
        if (statuses.Count > 0) query = query.Where(c => statuses.Contains(c.Status));
        var tiers = Parse.List(q.Tier);
        if (tiers.Count > 0) query = query.Where(c => tiers.Contains(c.Tier));
        var regions = Parse.List(q.Region);
        if (regions.Count > 0) query = query.Where(c => c.ServiceRegion != null && regions.Contains(c.ServiceRegion));
        var industries = Parse.List(q.Industry);
        if (industries.Count > 0) query = query.Where(c => c.Industry != null && industries.Contains(c.Industry));
        if (!string.IsNullOrWhiteSpace(q.Category)) query = query.Where(c => c.Category == q.Category);
        if (q.GroupId is { } g) query = query.Where(c => c.CustomerGroupId == g);
        if (q.AccountManagerId is { } am) query = query.Where(c => c.AccountManagerUserId == am);
        if (q.Mine == true) query = query.Where(c => c.AccountManagerUserId == ctx.UserId);
        if (q.ParentId is { } parent) query = query.Where(c => c.ParentCustomerId == parent);
        if (q.TopLevelOnly == true) query = query.Where(c => c.ParentCustomerId == null);
        if (q.HasOpenTickets == true) query = query.Where(c => db.Tickets.Any(t => t.CustomerId == c.Id && t.StatusCategory < StatusCategory.Resolved));

        var limit = Math.Clamp(q.Limit ?? 50, 1, MaxLimit);
        int? total = q.IncludeTotal == true ? await query.CountAsync(ct) : null;
        var cursor = DbExtensions.DecodeCursor(q.Cursor);
        var sort = (q.Sort ?? "name").ToLowerInvariant();
        IQueryable<Customer> ordered;
        Func<Customer, string> keyOf;
        switch (sort)
        {
            case "created" or "updated":
            {
                var desc = !Parse.Asc(q.Dir, false);
                var byCreated = sort == "created";
                if (cursor is not null)
                {
                    var v = Parse.Iso(cursor.V);
                    query = byCreated
                        ? desc ? query.Where(c => c.CreatedAt < v || (c.CreatedAt == v && c.Id.CompareTo(cursor.Id) < 0)) : query.Where(c => c.CreatedAt > v || (c.CreatedAt == v && c.Id.CompareTo(cursor.Id) > 0))
                        : desc ? query.Where(c => c.UpdatedAt < v || (c.UpdatedAt == v && c.Id.CompareTo(cursor.Id) < 0)) : query.Where(c => c.UpdatedAt > v || (c.UpdatedAt == v && c.Id.CompareTo(cursor.Id) > 0));
                }
                ordered = byCreated
                    ? desc ? query.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id) : query.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
                    : desc ? query.OrderByDescending(c => c.UpdatedAt).ThenByDescending(c => c.Id) : query.OrderBy(c => c.UpdatedAt).ThenBy(c => c.Id);
                keyOf = byCreated ? c => Parse.Iso(c.CreatedAt) : c => Parse.Iso(c.UpdatedAt);
                break;
            }
            case "code":
            {
                var asc = Parse.Asc(q.Dir, true);
                if (cursor is not null)
                {
                    var v = cursor.V;
                    query = asc ? query.Where(c => string.Compare(c.Code ?? "", v) > 0 || ((c.Code ?? "") == v && c.Id.CompareTo(cursor.Id) > 0))
                                : query.Where(c => string.Compare(c.Code ?? "", v) < 0 || ((c.Code ?? "") == v && c.Id.CompareTo(cursor.Id) < 0));
                }
                ordered = asc ? query.OrderBy(c => c.Code ?? "").ThenBy(c => c.Id) : query.OrderByDescending(c => c.Code ?? "").ThenByDescending(c => c.Id);
                keyOf = c => c.Code ?? "";
                break;
            }
            default:
            {
                var asc = Parse.Asc(q.Dir, true);
                if (cursor is not null)
                {
                    var v = cursor.V;
                    query = asc ? query.Where(c => string.Compare(c.Name, v) > 0 || (c.Name == v && c.Id.CompareTo(cursor.Id) > 0))
                                : query.Where(c => string.Compare(c.Name, v) < 0 || (c.Name == v && c.Id.CompareTo(cursor.Id) < 0));
                }
                ordered = asc ? query.OrderBy(c => c.Name).ThenBy(c => c.Id) : query.OrderByDescending(c => c.Name).ThenByDescending(c => c.Id);
                keyOf = c => c.Name;
                break;
            }
        }

        var page = await ordered.Take(limit + 1).ToListAsync(ct);
        var hasMore = page.Count > limit;
        if (hasMore) page.RemoveAt(page.Count - 1);
        var items = await MapListAsync(page, ct);
        return new PagedResult<CustomerListItemDto>(items, hasMore ? DbExtensions.EncodeCursor(keyOf(page[^1]), page[^1].Id) : null, total);
    }

    public async Task<List<CustomerListItemDto>> MapListAsync(IReadOnlyList<Customer> page, CancellationToken ct)
    {
        if (page.Count == 0) return [];
        var ids = page.Select(c => c.Id).ToList();
        var groupIds = page.Where(c => c.CustomerGroupId != null).Select(c => c.CustomerGroupId!.Value).Distinct().ToList();
        var parentIds = page.Where(c => c.ParentCustomerId != null).Select(c => c.ParentCustomerId!.Value).Distinct().ToList();
        var groups = await db.CustomerGroups.AsNoTracking().Where(x => groupIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var parents = await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(x => parentIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var children = await db.Customers.AsNoTracking().Where(x => x.ParentCustomerId != null && ids.Contains(x.ParentCustomerId.Value))
            .GroupBy(x => x.ParentCustomerId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var contacts = await db.Contacts.AsNoTracking().Where(x => x.CustomerId != null && ids.Contains(x.CustomerId.Value) && x.Status == ContactStatus.Active)
            .GroupBy(x => x.CustomerId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var open = await db.Tickets.AsNoTracking().Where(t => t.CustomerId != null && ids.Contains(t.CustomerId.Value) && t.StatusCategory < StatusCategory.Resolved)
            .GroupBy(t => t.CustomerId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var users = await UserNamesAsync(page.Select(c => c.AccountManagerUserId), ct);
        return page.Select(c => new CustomerListItemDto(c.Id, c.Name, c.Code, c.Status.ToString(), c.Tier, c.Category, c.Industry, c.ServiceRegion,
            c.CustomerGroupId, c.CustomerGroupId is { } gid ? groups.GetValueOrDefault(gid) : null, c.AccountManagerUserId,
            c.AccountManagerUserId is { } uid ? users.GetValueOrDefault(uid) : null, c.ParentCustomerId,
            c.ParentCustomerId is { } pid ? parents.GetValueOrDefault(pid) : null, children.GetValueOrDefault(c.Id), contacts.GetValueOrDefault(c.Id),
            open.GetValueOrDefault(c.Id), c.ErpCode, c.CreatedAt, c.UpdatedAt)).ToList();
    }

    public async Task<Dictionary<Guid, string>> UserNamesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        if (list.Count == 0) return [];
        return await db.Users.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(u => list.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    public async Task<CustomerFacetsDto> FacetsAsync(CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersView);
        var visible = db.Customers.AsNoTracking().VisibleTo(ctx);
        var rows = await visible.Select(c => new { c.Tier, c.ServiceRegion, c.Industry, c.Category, c.AccountManagerUserId }).ToListAsync(ct);
        var groups = await db.CustomerGroups.AsNoTracking().OrderBy(g => g.Name).Select(g => new IdName(g.Id, g.Name)).ToListAsync(ct);
        var managers = await UserNamesAsync(rows.Select(r => r.AccountManagerUserId), ct);
        static List<string> Distinct(IEnumerable<string?> values) =>
            values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var tiers = DefaultTiers.Concat(rows.Select(r => r.Tier)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new CustomerFacetsDto(tiers, Distinct(rows.Select(r => r.ServiceRegion)), Distinct(rows.Select(r => r.Industry)),
            Distinct(rows.Select(r => r.Category)), groups, managers.Select(m => new IdName(m.Key, m.Value)).OrderBy(m => m.Name).ToList(),
            Enum.GetNames<CustomerStatus>());
    }

    // ───────────────────────────── Detail ─────────────────────────────

    public async Task<CustomerDetailDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersView);
        var c = await CustomerAccess.LoadVisibleAsync(db, ctx, id, ct, track: false);
        return await MapDetailAsync(c, ct);
    }

    public async Task<CustomerDetailDto> MapDetailAsync(Customer c, CancellationToken ct)
    {
        var group = c.CustomerGroupId is null ? null : await db.CustomerGroups.AsNoTracking().Where(g => g.Id == c.CustomerGroupId).Select(g => g.Name).FirstOrDefaultAsync(ct);

        // Ancestors (nearest first, max 10 levels).
        var ancestors = new List<IdName>();
        var parentId = c.ParentCustomerId;
        for (var depth = 0; parentId is { } pid && depth < 10; depth++)
        {
            var p = await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(x => x.Id == pid)
                .Select(x => new { x.Id, x.Name, x.ParentCustomerId }).FirstOrDefaultAsync(ct);
            if (p is null || ancestors.Any(a => a.Id == p.Id)) break;
            ancestors.Add(new IdName(p.Id, p.Name));
            parentId = p.ParentCustomerId;
        }

        var childRows = await db.Customers.AsNoTracking().Where(x => x.ParentCustomerId == c.Id).OrderBy(x => x.Name).ToListAsync(ct);
        var childIds = childRows.Select(x => x.Id).ToList();
        var childOpen = await db.Tickets.AsNoTracking().Where(t => t.CustomerId != null && childIds.Contains(t.CustomerId.Value) && t.StatusCategory < StatusCategory.Resolved)
            .GroupBy(t => t.CustomerId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var grandChildren = await db.Customers.AsNoTracking().Where(x => x.ParentCustomerId != null && childIds.Contains(x.ParentCustomerId.Value))
            .GroupBy(x => x.ParentCustomerId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var children = childRows.Select(x => new CustomerChildDto(x.Id, x.Name, x.Code, x.Tier, x.Status.ToString(), childOpen.GetValueOrDefault(x.Id),
            grandChildren.GetValueOrDefault(x.Id))).ToList();

        var rollup = await RollupAsync(c.Id, ct);
        var users = await UserNamesAsync([c.AccountManagerUserId], ct);
        var permissions = new CustomerPermissionsDto(ctx.Has(Permissions.CustomersManage), ctx.Has(Permissions.CustomersManage), ctx.Has(Permissions.CustomersManage),
            ctx.Has(Permissions.ContactsMerge), ctx.Has(Permissions.ContractsView), ctx.Has(Permissions.ContractsManage), ctx.Has(Permissions.ProductsView),
            ctx.Has(Permissions.ProductsManage), ctx.Has(Permissions.TicketsCreate));
        return new CustomerDetailDto(c.Id, c.Name, c.Code, c.Status.ToString(), c.Tier, c.Category, c.Industry, c.ServiceRegion, c.CustomerGroupId, group,
            c.AccountManagerUserId, c.AccountManagerUserId is { } am ? users.GetValueOrDefault(am) : null, c.ParentCustomerId,
            ancestors.FirstOrDefault()?.Name, ancestors, children, rollup, c.PreferredLanguage, c.TimeZone, c.Website, c.Phone, c.Email,
            Json.StringList(c.DomainsJson), Json.DeserializeOr(c.CommunicationPrefsJson, new CommunicationPreferencesDto()), c.Notes,
            Json.Element(c.CustomFieldsJson), c.ErpCustomerId, c.ErpCode, c.ErpSyncedAt, c.IsDeleted, c.CreatedAt, c.UpdatedAt,
            Parse.RowVersion(c.RowVersion), permissions);
    }

    /// <summary>The customer plus all descendants (breadth-first, cycle-safe, max 2,000 nodes).</summary>
    public async Task<List<Guid>> SubtreeAsync(Guid rootId, CancellationToken ct)
    {
        var all = new List<Guid> { rootId };
        var seen = new HashSet<Guid> { rootId };
        var frontier = new List<Guid> { rootId };
        while (frontier.Count > 0 && all.Count < 2000)
        {
            var next = await db.Customers.AsNoTracking().Where(x => x.ParentCustomerId != null && frontier.Contains(x.ParentCustomerId.Value))
                .Select(x => x.Id).ToListAsync(ct);
            frontier = next.Where(seen.Add).ToList();
            all.AddRange(frontier);
        }
        return all;
    }

    private async Task<CustomerRollupDto> RollupAsync(Guid id, CancellationToken ct)
    {
        var ids = await SubtreeAsync(id, ct);
        var today = DateOnly.FromDateTime(db.Clock.UtcNow);
        var contacts = await db.Contacts.CountAsync(k => k.CustomerId != null && ids.Contains(k.CustomerId.Value) && k.Status == ContactStatus.Active, ct);
        var locations = await db.CustomerLocations.CountAsync(l => ids.Contains(l.CustomerId), ct);
        var open = await db.Tickets.CountAsync(t => t.CustomerId != null && ids.Contains(t.CustomerId.Value) && t.StatusCategory < StatusCategory.Resolved, ct);
        var installations = await db.Installations.CountAsync(i => ids.Contains(i.CustomerId), ct);
        var contracts = await db.Contracts.CountAsync(x => ids.Contains(x.CustomerId) && x.Status == ContractStatus.Active && x.EndDate >= today, ct);
        return new CustomerRollupDto(ids.Count - 1, contacts, locations, open, installations, contracts);
    }

    // ───────────────────────────── Create / update / delete ─────────────────────────────

    public async Task<Customer> CreateAsync(SaveCustomerRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersManage);
        var c = new Customer();
        await ApplyAsync(c, r, isNew: true, ct);
        db.Customers.Add(c);
        events.Publish(EventTypes.CustomerCreated, "Customer", c.Id, new EntityEventPayload(c.Id, c.Name));
        await db.SaveChangesAsync(ct);
        return c;
    }

    public async Task<Customer> UpdateAsync(Guid id, SaveCustomerRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersManage);
        var c = await CustomerAccess.LoadVisibleAsync(db, ctx, id, ct);
        db.Entry(c).Property(x => x.RowVersion).OriginalValue = Parse.RowVersion(r.RowVersion, "customer");
        var changes = await ApplyAsync(c, r, isNew: false, ct);
        if (changes.Count > 0)
            events.Publish(EventTypes.CustomerUpdated, "Customer", c.Id, new EntityEventPayload(c.Id, c.Name, changes));
        await db.SaveChangesAsync(ct);
        return c;
    }

    private async Task<List<FieldChange>> ApplyAsync(Customer c, SaveCustomerRequest r, bool isNew, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        var name = Parse.Clean(r.Name, 300);
        if (name is null) errors["name"] = "A name is required.";
        var code = Parse.Clean(r.Code, 40);
        if (code is not null && await db.Customers.AnyAsync(x => x.Code == code && x.Id != c.Id, ct)) errors["code"] = $"Code '{code}' is already used by another customer.";
        var status = Parse.Enum(r.Status, isNew ? CustomerStatus.Active : c.Status, "status");
        var tier = Parse.Clean(r.Tier, 40) ?? (isNew ? "Standard" : c.Tier);

        if (r.ParentCustomerId is { } parentId)
        {
            if (parentId == c.Id) errors["parentCustomerId"] = "A customer cannot be its own parent.";
            else if (!await db.Customers.AnyAsync(x => x.Id == parentId, ct)) errors["parentCustomerId"] = "Unknown parent customer.";
            else if (!isNew && (await SubtreeAsync(c.Id, ct)).Contains(parentId)) errors["parentCustomerId"] = "The parent cannot be one of this customer's own subsidiaries.";
        }
        if (r.CustomerGroupId is { } gid && !await db.CustomerGroups.AnyAsync(x => x.Id == gid, ct)) errors["customerGroupId"] = "Unknown customer group.";
        if (r.AccountManagerUserId is { } amId && !await db.Users.AnyAsync(u => u.Id == amId && u.IsActive && u.Kind != UserKind.LightAgent, ct))
            errors["accountManagerUserId"] = "The account manager must be an active agent.";
        if (CustomerAccess.IsRestricted(ctx) && r.AccountManagerUserId != ctx.UserId)
            errors["accountManagerUserId"] = "With account-managed access you can only manage customers assigned to you.";

        var domains = new List<string>();
        foreach (var raw in r.Domains ?? [])
        {
            var d = raw.Trim().TrimStart('@').ToLowerInvariant();
            if (d.Length == 0) continue;
            if (!DomainRegex().IsMatch(d)) { errors["domains"] = $"'{raw}' is not a valid domain."; continue; }
            if (!domains.Contains(d)) domains.Add(d);
        }
        foreach (var d in domains)
        {
            var quoted = $"\"{d}\"";
            var owner = await db.Customers.AsNoTracking().Where(x => x.Id != c.Id && x.DomainsJson.Contains(quoted)).Select(x => x.Name).FirstOrDefaultAsync(ct);
            if (owner is not null) errors["domains"] = $"The domain {d} is already linked to {owner}.";
        }
        if (Parse.Clean(r.Email) is { } email && TextNormalization.NormalizeEmail(email) is null) errors["email"] = "Enter a valid e-mail address.";
        if (r.Website is { Length: > 0 } site && !Uri.TryCreate(site.Contains("://") ? site : "https://" + site, UriKind.Absolute, out _)) errors["website"] = "Enter a valid web address.";

        string customFieldsJson = c.CustomFieldsJson;
        try
        {
            customFieldsJson = await customFields.MergeAsync("Customer", c.CustomFieldsJson, r.CustomFields, null, enforceRequired: !ctx.IsSystem, portalOnly: false, ct);
        }
        catch (ValidationException v)
        {
            foreach (var (k, m) in v.Errors) errors[$"customFields.{k}"] = m;
        }
        if (r.ErpCustomerId is { } erpId && await db.Customers.AnyAsync(x => x.ErpCustomerId == erpId && x.Id != c.Id, ct))
            errors["erpCustomerId"] = "Another customer is already linked to this ERP customer.";
        if (errors.Count > 0) throw new ValidationException(errors);

        var set = new ChangeSet();
        set.Set("name", c.Name, name!, v => c.Name = v);
        set.Set("code", c.Code, code, v => c.Code = v);
        set.Set("status", c.Status, status, v => c.Status = v);
        set.Set("tier", c.Tier, tier, v => c.Tier = v);
        set.Set("parentCustomerId", c.ParentCustomerId, r.ParentCustomerId, v => c.ParentCustomerId = v);
        set.Set("customerGroupId", c.CustomerGroupId, r.CustomerGroupId, v => c.CustomerGroupId = v);
        set.Set("category", c.Category, Parse.Clean(r.Category, 100), v => c.Category = v);
        set.Set("industry", c.Industry, Parse.Clean(r.Industry, 100), v => c.Industry = v);
        set.Set("accountManagerUserId", c.AccountManagerUserId, r.AccountManagerUserId, v => c.AccountManagerUserId = v);
        set.Set("serviceRegion", c.ServiceRegion, Parse.Clean(r.ServiceRegion, 100), v => c.ServiceRegion = v);
        set.Set("preferredLanguage", c.PreferredLanguage, Parse.Clean(r.PreferredLanguage, 20) ?? "en", v => c.PreferredLanguage = v);
        set.Set("timeZone", c.TimeZone, Parse.Clean(r.TimeZone, 100), v => c.TimeZone = v);
        set.Set("website", c.Website, Parse.Clean(r.Website, 300), v => c.Website = v);
        set.Set("phone", c.Phone, Parse.Clean(r.Phone, 50), v => c.Phone = v);
        set.Set("email", c.Email, Parse.Clean(r.Email, 300), v => c.Email = v);
        set.Set("domains", c.DomainsJson, Json.Serialize(domains), v => c.DomainsJson = v);
        set.Set("communicationPreferences", c.CommunicationPrefsJson,
            r.CommunicationPreferences is null ? c.CommunicationPrefsJson : Json.Serialize(r.CommunicationPreferences), v => c.CommunicationPrefsJson = v);
        set.Set("notes", c.Notes, Parse.Clean(r.Notes, 8000), v => c.Notes = v);
        set.Set("customFields", c.CustomFieldsJson, customFieldsJson, v => c.CustomFieldsJson = v);
        if (r.ErpCustomerId is not null || isNew) set.Set("erpCustomerId", c.ErpCustomerId, r.ErpCustomerId, v => c.ErpCustomerId = v);
        if (r.ErpCode is not null || isNew) set.Set("erpCode", c.ErpCode, Parse.Clean(r.ErpCode, 40), v => c.ErpCode = v);
        return set.Changes.ToList();
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersManage);
        var c = await CustomerAccess.LoadVisibleAsync(db, ctx, id, ct);
        if (await db.Customers.AnyAsync(x => x.ParentCustomerId == id, ct))
            throw new ConflictException("has_children", "Move or delete the subsidiaries of this customer first.");
        c.IsDeleted = true;
        c.DeletedAt = db.Clock.UtcNow;
        audit.Write(CustomerAuditActions.CustomerDeleted, "Customer", c.Id, $"Customer '{c.Name}' deleted");
        events.Publish(EventTypes.CustomerUpdated, "Customer", c.Id, new EntityEventPayload(c.Id, c.Name, [new FieldChange("isDeleted", "false", "true")]));
        await db.SaveChangesAsync(ct);
    }

    public async Task<Customer> RestoreAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersManage);
        var c = await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).VisibleTo(ctx).FirstOrDefaultAsync(x => x.Id == id && x.IsDeleted, ct)
                ?? throw new NotFoundException("Customer", id);
        if (c.Code is not null && await db.Customers.AnyAsync(x => x.Code == c.Code, ct)) c.Code = null;
        c.IsDeleted = false;
        c.DeletedAt = null;
        audit.Write(CustomerAuditActions.CustomerRestored, "Customer", c.Id, $"Customer '{c.Name}' restored");
        events.Publish(EventTypes.CustomerUpdated, "Customer", c.Id, new EntityEventPayload(c.Id, c.Name, [new FieldChange("isDeleted", "true", "false")]));
        await db.SaveChangesAsync(ct);
        return c;
    }

    // ───────────────────────────── Groups ─────────────────────────────

    public async Task<List<CustomerGroupDto>> GroupsAsync(CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersView);
        var visibleIds = CustomerAccess.VisibleCustomerIds(db, ctx);
        return await db.CustomerGroups.AsNoTracking().OrderBy(g => g.Name)
            .Select(g => new CustomerGroupDto(g.Id, g.Name, g.Description, db.Customers.Count(c => c.CustomerGroupId == g.Id && visibleIds.Contains(c.Id))))
            .ToListAsync(ct);
    }

    public async Task<CustomerGroupDto> SaveGroupAsync(Guid? id, SaveCustomerGroupRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersManage);
        var name = Parse.Clean(r.Name, 200) ?? throw new ValidationException(new Dictionary<string, string> { ["name"] = "A name is required." });
        if (await db.CustomerGroups.AnyAsync(g => g.Name == name && g.Id != id, ct))
            throw new ValidationException(new Dictionary<string, string> { ["name"] = "A group with this name already exists." });
        CustomerGroup group;
        if (id is { } gid) group = await db.CustomerGroups.FirstOrDefaultAsync(g => g.Id == gid, ct) ?? throw new NotFoundException("Customer group", gid);
        else db.CustomerGroups.Add(group = new CustomerGroup());
        group.Name = name;
        group.Description = Parse.Clean(r.Description, 2000);
        await db.SaveChangesAsync(ct);
        return new CustomerGroupDto(group.Id, group.Name, group.Description, await db.Customers.CountAsync(c => c.CustomerGroupId == group.Id, ct));
    }

    public async Task DeleteGroupAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersManage);
        var group = await db.CustomerGroups.FirstOrDefaultAsync(g => g.Id == id, ct) ?? throw new NotFoundException("Customer group", id);
        await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).Where(c => c.CustomerGroupId == id)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.CustomerGroupId, (Guid?)null), ct);
        db.CustomerGroups.Remove(group);
        await db.SaveChangesAsync(ct);
    }

    // ───────────────────────────── Locations ─────────────────────────────

    public async Task<List<LocationDto>> LocationsAsync(Guid customerId, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersView);
        await CustomerAccess.EnsureVisibleAsync(db, ctx, customerId, ct);
        return await db.CustomerLocations.AsNoTracking().Where(l => l.CustomerId == customerId)
            .OrderByDescending(l => l.IsHeadOffice).ThenBy(l => l.Name)
            .Select(l => new LocationDto(l.Id, l.CustomerId, l.Name, l.Code, l.AddressLine1, l.AddressLine2, l.City, l.State, l.Country, l.PostalCode, l.Phone,
                l.TimeZone, l.ServiceRegion, l.IsHeadOffice, l.IsActive, l.ErpLocationId,
                db.Contacts.Count(k => k.LocationId == l.Id && k.Status == ContactStatus.Active), db.Installations.Count(i => i.LocationId == l.Id)))
            .ToListAsync(ct);
    }

    public async Task<LocationDto> SaveLocationAsync(Guid customerId, Guid? id, SaveLocationRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersManage);
        await CustomerAccess.EnsureVisibleAsync(db, ctx, customerId, ct);
        var name = Parse.Clean(r.Name, 200) ?? throw new ValidationException(new Dictionary<string, string> { ["name"] = "A name is required." });
        CustomerLocation l;
        if (id is { } lid)
            l = await db.CustomerLocations.FirstOrDefaultAsync(x => x.Id == lid && x.CustomerId == customerId, ct) ?? throw new NotFoundException("Location", lid);
        else db.CustomerLocations.Add(l = new CustomerLocation { CustomerId = customerId });
        l.Name = name;
        l.Code = Parse.Clean(r.Code, 40);
        l.AddressLine1 = Parse.Clean(r.AddressLine1, 300);
        l.AddressLine2 = Parse.Clean(r.AddressLine2, 300);
        l.City = Parse.Clean(r.City, 120);
        l.State = Parse.Clean(r.State, 120);
        l.Country = Parse.Clean(r.Country, 120);
        l.PostalCode = Parse.Clean(r.PostalCode, 20);
        l.Phone = Parse.Clean(r.Phone, 50);
        l.TimeZone = Parse.Clean(r.TimeZone, 100);
        l.ServiceRegion = Parse.Clean(r.ServiceRegion, 100);
        l.IsActive = r.IsActive;
        if (r.IsHeadOffice && !l.IsHeadOffice)
            await db.CustomerLocations.Where(x => x.CustomerId == customerId && x.Id != l.Id && x.IsHeadOffice)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.IsHeadOffice, false), ct);
        l.IsHeadOffice = r.IsHeadOffice;
        events.Publish(EventTypes.CustomerUpdated, "Customer", customerId, new EntityEventPayload(customerId, null, [new FieldChange("location", null, l.Name)]));
        await db.SaveChangesAsync(ct);
        return (await LocationsAsync(customerId, ct)).First(x => x.Id == l.Id);
    }

    public async Task DeleteLocationAsync(Guid customerId, Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersManage);
        await CustomerAccess.EnsureVisibleAsync(db, ctx, customerId, ct);
        var l = await db.CustomerLocations.FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == customerId, ct) ?? throw new NotFoundException("Location", id);
        var inUse = await db.Contacts.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AnyAsync(k => k.LocationId == id, ct) ||
                    await db.Installations.AnyAsync(i => i.LocationId == id, ct) ||
                    await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AnyAsync(t => t.LocationId == id, ct);
        if (inUse)
        {
            // Referenced by history: deactivate instead of deleting.
            l.IsActive = false;
        }
        else db.CustomerLocations.Remove(l);
        await db.SaveChangesAsync(ct);
    }

    // ───────────────────────────── Customer departments ─────────────────────────────

    public async Task<List<CustomerDepartmentDto>> DepartmentsAsync(Guid customerId, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersView);
        await CustomerAccess.EnsureVisibleAsync(db, ctx, customerId, ct);
        return await db.CustomerDepartments.AsNoTracking().Where(d => d.CustomerId == customerId).OrderBy(d => d.Name)
            .Select(d => new CustomerDepartmentDto(d.Id, d.CustomerId, d.Name, db.Contacts.Count(k => k.CustomerDepartmentId == d.Id && k.Status == ContactStatus.Active)))
            .ToListAsync(ct);
    }

    public async Task<CustomerDepartmentDto> SaveDepartmentAsync(Guid customerId, Guid? id, SaveCustomerDepartmentRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersManage);
        await CustomerAccess.EnsureVisibleAsync(db, ctx, customerId, ct);
        var name = Parse.Clean(r.Name, 200) ?? throw new ValidationException(new Dictionary<string, string> { ["name"] = "A name is required." });
        if (await db.CustomerDepartments.AnyAsync(d => d.CustomerId == customerId && d.Name == name && d.Id != id, ct))
            throw new ValidationException(new Dictionary<string, string> { ["name"] = "This department already exists." });
        CustomerDepartment dep;
        if (id is { } did)
            dep = await db.CustomerDepartments.FirstOrDefaultAsync(x => x.Id == did && x.CustomerId == customerId, ct) ?? throw new NotFoundException("Department", did);
        else db.CustomerDepartments.Add(dep = new CustomerDepartment { CustomerId = customerId });
        dep.Name = name;
        await db.SaveChangesAsync(ct);
        return (await DepartmentsAsync(customerId, ct)).First(x => x.Id == dep.Id);
    }

    public async Task DeleteDepartmentAsync(Guid customerId, Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersManage);
        await CustomerAccess.EnsureVisibleAsync(db, ctx, customerId, ct);
        var dep = await db.CustomerDepartments.FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == customerId, ct) ?? throw new NotFoundException("Department", id);
        await db.Contacts.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).Where(k => k.CustomerDepartmentId == id)
            .ExecuteUpdateAsync(u => u.SetProperty(k => k.CustomerDepartmentId, (Guid?)null), ct);
        db.CustomerDepartments.Remove(dep);
        await db.SaveChangesAsync(ct);
    }

    [GeneratedRegex(@"^(?=.{3,253}$)([a-z0-9](-?[a-z0-9])*\.)+[a-z]{2,63}$")]
    private static partial Regex DomainRegex();
}
