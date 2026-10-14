using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Customers;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Products;
using SupportDesk.Domain;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Service;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;
using SupportDesk.Persistence.Infrastructure;

namespace SupportDesk.Application.Service;

/// <summary>
/// Support contracts (warranty, AMC, annual support, prepaid hours, T&amp;M, subscription) and their entitlements;
/// renewals pipeline, consumption from time entries and eligibility preview. Any change that can affect support
/// eligibility enqueues a durable job that re-evaluates the customer's open tickets. Reads need contracts.view,
/// changes contracts.manage (audited); everything respects the caller's customer scope.
/// </summary>
public sealed class ContractService(
    DeskDbContext db,
    RequestContext ctx,
    IJobQueue jobs,
    IAuditWriter audit,
    IEventPublisher events,
    TenantCounterService counters,
    EntitlementService entitlements,
    TenantSettingsService settings)
{
    public const int MaxLimit = 200;

    private IQueryable<Contract> Visible()
    {
        var q = db.Contracts.AsNoTracking();
        if (!CustomerAccess.IsRestricted(ctx)) return q;
        var ids = CustomerAccess.VisibleCustomerIds(db, ctx);
        return q.Where(c => ids.Contains(c.CustomerId));
    }

    private DateOnly Today => DateOnly.FromDateTime(db.Clock.UtcNow);

    // ───────────────────────────── List ─────────────────────────────

    public async Task<PagedResult<ContractListItemDto>> ListAsync(ContractQuery q, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ContractsView);
        var query = Visible();
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = q.Q.Trim();
            var customerIds = db.Customers.Where(c => c.Name.Contains(term) || (c.Code != null && c.Code.Contains(term))).Select(c => c.Id);
            query = query.Where(c => c.Number.Contains(term) || c.Name.Contains(term) || (c.ErpReference != null && c.ErpReference.Contains(term)) || customerIds.Contains(c.CustomerId));
        }
        if (q.CustomerId is { } cid) query = query.Where(c => c.CustomerId == cid);
        var types = Parse.Enums<ContractType>(q.Type);
        if (types.Count > 0) query = query.Where(c => types.Contains(c.Type));
        var statuses = Parse.Enums<ContractStatus>(q.Status);
        if (statuses.Count > 0) query = query.Where(c => statuses.Contains(c.Status));
        if (q.ExpiringWithinDays is { } days)
        {
            var today = Today;
            var until = today.AddDays(days);
            query = query.Where(c => c.Status == ContractStatus.Active && c.EndDate >= today && c.EndDate <= until);
        }

        var limit = Math.Clamp(q.Limit ?? 50, 1, MaxLimit);
        int? total = q.IncludeTotal == true ? await query.CountAsync(ct) : null;
        var cursor = DbExtensions.DecodeCursor(q.Cursor);
        IQueryable<Contract> ordered;
        Func<Contract, string> keyOf;
        switch ((q.Sort ?? "endDate").ToLowerInvariant())
        {
            case "number":
            {
                var asc = Parse.Asc(q.Dir, true);
                if (cursor is not null)
                {
                    var v = cursor.V;
                    query = asc ? query.Where(c => string.Compare(c.Number, v) > 0 || (c.Number == v && c.Id.CompareTo(cursor.Id) > 0))
                                : query.Where(c => string.Compare(c.Number, v) < 0 || (c.Number == v && c.Id.CompareTo(cursor.Id) < 0));
                }
                ordered = asc ? query.OrderBy(c => c.Number).ThenBy(c => c.Id) : query.OrderByDescending(c => c.Number).ThenByDescending(c => c.Id);
                keyOf = c => c.Number;
                break;
            }
            case "updated":
            {
                if (cursor is not null)
                {
                    var v = Parse.Iso(cursor.V);
                    query = query.Where(c => c.UpdatedAt < v || (c.UpdatedAt == v && c.Id.CompareTo(cursor.Id) < 0));
                }
                ordered = query.OrderByDescending(c => c.UpdatedAt).ThenByDescending(c => c.Id);
                keyOf = c => Parse.Iso(c.UpdatedAt);
                break;
            }
            default:
            {
                var asc = Parse.Asc(q.Dir, true);
                if (cursor is not null)
                {
                    var v = DateOnly.ParseExact(cursor.V, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    query = asc ? query.Where(c => c.EndDate > v || (c.EndDate == v && c.Id.CompareTo(cursor.Id) > 0))
                                : query.Where(c => c.EndDate < v || (c.EndDate == v && c.Id.CompareTo(cursor.Id) < 0));
                }
                ordered = asc ? query.OrderBy(c => c.EndDate).ThenBy(c => c.Id) : query.OrderByDescending(c => c.EndDate).ThenByDescending(c => c.Id);
                keyOf = c => c.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                break;
            }
        }
        var page = await ordered.Take(limit + 1).ToListAsync(ct);
        var hasMore = page.Count > limit;
        if (hasMore) page.RemoveAt(page.Count - 1);
        return new PagedResult<ContractListItemDto>(await MapListAsync(page, ct), hasMore ? DbExtensions.EncodeCursor(keyOf(page[^1]), page[^1].Id) : null, total);
    }

    public async Task<List<ContractListItemDto>> ForCustomerAsync(Guid customerId, CancellationToken ct = default)
    {
        var rows = await Visible().Where(c => c.CustomerId == customerId).OrderByDescending(c => c.Status == ContractStatus.Active).ThenByDescending(c => c.EndDate)
            .Take(100).ToListAsync(ct);
        return await MapListAsync(rows, ct);
    }

    public async Task<List<ContractListItemDto>> MapListAsync(IReadOnlyList<Contract> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var today = Today;
        var ids = rows.Select(c => c.Id).ToList();
        var customerIds = rows.Select(c => c.CustomerId).Distinct().ToList();
        var customers = await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(c => customerIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name, c.AccountManagerUserId }).ToDictionaryAsync(c => c.Id, ct);
        var managerIds = customers.Values.Where(c => c.AccountManagerUserId != null).Select(c => c.AccountManagerUserId!.Value).Distinct().ToList();
        var managers = await db.Users.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(u => managerIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var policyIds = rows.Where(c => c.SlaPolicyId != null).Select(c => c.SlaPolicyId!.Value).Distinct().ToList();
        var policies = await db.SlaPolicies.AsNoTracking().Where(p => policyIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var entitlementCounts = await db.Entitlements.AsNoTracking().Where(e => ids.Contains(e.ContractId)).GroupBy(e => e.ContractId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return rows.Select(c =>
        {
            var customer = customers.GetValueOrDefault(c.CustomerId);
            var am = customer?.AccountManagerUserId;
            return new ContractListItemDto(c.Id, c.Number, c.Name, c.CustomerId, customer?.Name ?? "", c.Type.ToString(), EffectiveStatus(c, today).ToString(), c.StartDate,
                c.EndDate, c.EndDate.DayNumber - today.DayNumber, c.IsBillable, c.ContractValue, c.Currency, c.PrepaidHours, c.ConsumedHours, c.AutoRenew, c.RenewalDate,
                c.SlaPolicyId, c.SlaPolicyId is { } p ? policies.GetValueOrDefault(p) : null, entitlementCounts.GetValueOrDefault(c.Id), c.ErpReference, am,
                am is { } a ? managers.GetValueOrDefault(a) : null);
        }).ToList();
    }

    /// <summary>An Active contract past its end date reads as Expired even before the lifecycle task flips it.</summary>
    public static ContractStatus EffectiveStatus(Contract c, DateOnly today) =>
        c.Status == ContractStatus.Active && c.EndDate < today ? ContractStatus.Expired : c.Status;

    // ───────────────────────────── Detail ─────────────────────────────

    public async Task<ContractDetailDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ContractsView);
        var c = await Visible().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Contract", id);
        var summary = (await MapListAsync([c], ct))[0];
        var calendar = c.CalendarId is null ? null : await db.BusinessCalendars.AsNoTracking().Where(x => x.Id == c.CalendarId).Select(x => x.Name).FirstOrDefaultAsync(ct);
        var ents = await EntitlementsAsync(c.Id, ct);
        var consumption = await ConsumptionSummaryAsync(c, ct);
        return new ContractDetailDto(summary, c.CalendarId, calendar, ctx.Has(Permissions.TicketsViewCosts) || ctx.Has(Permissions.ContractsManage) ? c.HourlyRate : null,
            Json.Element(c.CoverageJson), c.Notes, c.ErpContractId, ents, consumption, c.CreatedAt, c.UpdatedAt, Parse.RowVersion(c.RowVersion),
            new ContractPermissionsDto(ctx.Has(Permissions.ContractsManage), ctx.Has(Permissions.TicketsViewCosts)));
    }

    public async Task<List<EntitlementDto>> EntitlementsAsync(Guid contractId, CancellationToken ct) =>
        await MapEntitlementsAsync(await db.Entitlements.AsNoTracking().Where(e => e.ContractId == contractId).OrderBy(e => e.Name).ToListAsync(ct), ct);

    public async Task<List<EntitlementDto>> EntitlementsForCustomerAsync(Guid customerId, CancellationToken ct)
    {
        var contractIds = Visible().Where(c => c.CustomerId == customerId).Select(c => c.Id);
        var rows = await db.Entitlements.AsNoTracking().Where(e => contractIds.Contains(e.ContractId)).OrderByDescending(e => e.EndDate).ThenBy(e => e.Name).Take(200).ToListAsync(ct);
        return await MapEntitlementsAsync(rows, ct);
    }

    private async Task<List<EntitlementDto>> MapEntitlementsAsync(List<Entitlement> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var today = Today;
        var contractIds = rows.Select(e => e.ContractId).Distinct().ToList();
        var numbers = await db.Contracts.AsNoTracking().Where(c => contractIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Number, ct);
        var products = await db.Products.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var instIds = rows.Where(e => e.InstallationId != null).Select(e => e.InstallationId!.Value).Distinct().ToList();
        var installs = await db.Installations.AsNoTracking().Where(i => instIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Name, ct);
        var assetIds = rows.Where(e => e.AssetId != null).Select(e => e.AssetId!.Value).Distinct().ToList();
        var assets = await db.CustomerAssets.AsNoTracking().Where(a => assetIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        return rows.Select(e =>
        {
            double? usage = e.HoursAllowed is > 0 ? (double)(e.HoursUsed / e.HoursAllowed.Value) * 100 :
                e.IncidentsAllowed is > 0 ? e.IncidentsUsed * 100.0 / e.IncidentsAllowed.Value : null;
            var exhausted = (e.IncidentsAllowed is { } ia && e.IncidentsUsed >= ia) || (e.HoursAllowed is { } ha && e.HoursUsed >= ha);
            return new EntitlementDto(e.Id, e.ContractId, numbers.GetValueOrDefault(e.ContractId), e.CustomerId, e.Name, e.Type.ToString(), e.ProductId,
                e.ProductId is { } p ? products.GetValueOrDefault(p) : null, e.InstallationId, e.InstallationId is { } i ? installs.GetValueOrDefault(i) : null,
                e.AssetId, e.AssetId is { } a ? assets.GetValueOrDefault(a) : null, e.IncidentsAllowed, e.IncidentsUsed, e.HoursAllowed, e.HoursUsed, e.StartDate,
                e.EndDate, e.IsActive && e.EndDate >= today, usage is null ? null : Math.Round(usage.Value, 1), exhausted);
        }).ToList();
    }

    // ───────────────────────────── Create / update / delete ─────────────────────────────

    public async Task<Contract> CreateAsync(SaveContractRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ContractsManage);
        await CustomerAccess.EnsureVisibleAsync(db, ctx, r.CustomerId, ct);
        var c = new Contract { CustomerId = r.CustomerId };
        await ApplyAsync(c, r, isNew: true, ct);
        db.Contracts.Add(c);
        foreach (var e in r.Entitlements ?? [])
        {
            var entitlement = new Entitlement { ContractId = c.Id, CustomerId = c.CustomerId };
            await ApplyEntitlementAsync(entitlement, c, e, ct);
            db.Entitlements.Add(entitlement);
        }
        audit.Write(CustomerAuditActions.ContractChanged, "Contract", c.Id, $"Contract {c.Number} created for customer {r.CustomerId}",
            new { c.Type, c.Status, c.StartDate, c.EndDate, c.ContractValue });
        events.Publish(EventTypes.CustomerUpdated, "Customer", c.CustomerId, new EntityEventPayload(c.CustomerId, null, [new FieldChange("contract", null, c.Number)]));
        QueueEligibility(c.CustomerId);
        await db.SaveChangesAsync(ct);
        return c;
    }

    public async Task<Contract> UpdateAsync(Guid id, SaveContractRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ContractsManage);
        var c = await db.Contracts.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Contract", id);
        await CustomerAccess.EnsureVisibleAsync(db, ctx, c.CustomerId, ct);
        if (r.CustomerId != c.CustomerId) throw new ValidationException(new Dictionary<string, string> { ["customerId"] = "A contract cannot move to another customer." });
        db.Entry(c).Property(x => x.RowVersion).OriginalValue = Parse.RowVersion(r.RowVersion, "contract");
        var changes = await ApplyAsync(c, r, isNew: false, ct);
        if (changes.Count > 0)
        {
            audit.Write(CustomerAuditActions.ContractChanged, "Contract", c.Id, $"Contract {c.Number} updated", changes);
            QueueEligibility(c.CustomerId);
        }
        await db.SaveChangesAsync(ct);
        return c;
    }

    private async Task<List<FieldChange>> ApplyAsync(Contract c, SaveContractRequest r, bool isNew, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        var name = Parse.Clean(r.Name, 250);
        if (name is null) errors["name"] = "A name is required.";
        var type = Parse.Enum(r.Type, ContractType.Amc, "type");
        var status = Parse.Enum(r.Status, isNew ? ContractStatus.Active : c.Status, "status");
        if (r.EndDate < r.StartDate) errors["endDate"] = "The end date must be on or after the start date.";
        if (r.SlaPolicyId is { } sp && !await db.SlaPolicies.AnyAsync(p => p.Id == sp, ct)) errors["slaPolicyId"] = "Unknown SLA policy.";
        if (r.CalendarId is { } cal && !await db.BusinessCalendars.AnyAsync(x => x.Id == cal, ct)) errors["calendarId"] = "Unknown support-hours calendar.";
        if (type == ContractType.PrepaidHours && r.PrepaidHours is null or <= 0) errors["prepaidHours"] = "Prepaid-hours contracts need the number of hours.";
        if (r.PrepaidHours is < 0) errors["prepaidHours"] = "Hours cannot be negative.";
        if (r.HourlyRate is < 0) errors["hourlyRate"] = "The rate cannot be negative.";
        if (r.ContractValue is < 0) errors["contractValue"] = "The value cannot be negative.";
        var number = Parse.Clean(r.Number, 60);
        if (number is not null && await db.Contracts.AnyAsync(x => x.Number == number && x.Id != c.Id, ct)) errors["number"] = $"Contract number {number} is already used.";
        var coverage = r.Coverage is { ValueKind: JsonValueKind.Object } cov ? cov.GetRawText() : isNew ? "{}" : c.CoverageJson;
        if (errors.Count > 0) throw new ValidationException(errors);

        if (number is null && isNew)
        {
            var prefix = type switch
            {
                ContractType.Amc => "AMC", ContractType.Warranty => "WAR", ContractType.AnnualSupport => "ASC", ContractType.PrepaidHours => "PPH",
                ContractType.TimeAndMaterial => "TM", _ => "SUB"
            };
            number = $"{prefix}-{r.StartDate.Year}-{await counters.NextAsync("contract", 1, ct):0000}";
            while (await db.Contracts.AnyAsync(x => x.Number == number, ct))
                number = $"{prefix}-{r.StartDate.Year}-{await counters.NextAsync("contract", 1, ct):0000}";
        }

        var set = new ChangeSet();
        if (number is not null) set.Set("number", c.Number, number, v => c.Number = v);
        set.Set("name", c.Name, name!, v => c.Name = v);
        set.Set("type", c.Type, type, v => c.Type = v);
        set.Set("status", c.Status, status, v => c.Status = v);
        set.Set("startDate", c.StartDate, r.StartDate, v => c.StartDate = v);
        set.Set("endDate", c.EndDate, r.EndDate, v => c.EndDate = v);
        set.Set("slaPolicyId", c.SlaPolicyId, r.SlaPolicyId, v => c.SlaPolicyId = v);
        set.Set("calendarId", c.CalendarId, r.CalendarId, v => c.CalendarId = v);
        set.Set("prepaidHours", c.PrepaidHours, r.PrepaidHours, v => c.PrepaidHours = v);
        set.Set("isBillable", c.IsBillable, r.IsBillable, v => c.IsBillable = v);
        set.Set("hourlyRate", c.HourlyRate, r.HourlyRate, v => c.HourlyRate = v);
        set.Set("contractValue", c.ContractValue, r.ContractValue, v => c.ContractValue = v);
        set.Set("currency", c.Currency, (Parse.Clean(r.Currency, 3) ?? "INR").ToUpperInvariant(), v => c.Currency = v);
        set.Set("autoRenew", c.AutoRenew, r.AutoRenew, v => c.AutoRenew = v);
        set.Set("renewalDate", c.RenewalDate, r.RenewalDate ?? (r.AutoRenew ? r.EndDate.AddDays(1) : null), v => c.RenewalDate = v);
        set.Set("coverage", c.CoverageJson, coverage, v => c.CoverageJson = v);
        set.Set("erpReference", c.ErpReference, Parse.Clean(r.ErpReference, 100), v => c.ErpReference = v);
        set.Set("notes", c.Notes, Parse.Clean(r.Notes, 8000), v => c.Notes = v);
        return set.Changes.ToList();
    }

    /// <summary>Cancels a contract (kept for history); removes it entirely only when nothing references it.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ContractsManage);
        var c = await db.Contracts.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Contract", id);
        await CustomerAccess.EnsureVisibleAsync(db, ctx, c.CustomerId, ct);
        var referenced = await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AnyAsync(t => t.ContractId == id, ct) ||
                         await db.TimeEntries.AnyAsync(t => t.ContractId == id, ct);
        if (referenced) c.Status = ContractStatus.Cancelled;
        else db.Contracts.Remove(c);
        audit.Write(CustomerAuditActions.ContractDeleted, "Contract", c.Id, referenced ? $"Contract {c.Number} cancelled" : $"Contract {c.Number} deleted");
        QueueEligibility(c.CustomerId);
        await db.SaveChangesAsync(ct);
    }

    // ───────────────────────────── Entitlements ─────────────────────────────

    public async Task<EntitlementDto> SaveEntitlementAsync(Guid contractId, Guid? id, SaveEntitlementRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ContractsManage);
        var c = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == contractId, ct) ?? throw new NotFoundException("Contract", contractId);
        await CustomerAccess.EnsureVisibleAsync(db, ctx, c.CustomerId, ct);
        Entitlement e;
        if (id is { } eid) e = await db.Entitlements.FirstOrDefaultAsync(x => x.Id == eid && x.ContractId == contractId, ct) ?? throw new NotFoundException("Entitlement", eid);
        else db.Entitlements.Add(e = new Entitlement { ContractId = contractId, CustomerId = c.CustomerId });
        await ApplyEntitlementAsync(e, c, r, ct);
        audit.Write(CustomerAuditActions.ContractChanged, "Contract", contractId, $"Entitlement '{e.Name}' {(id is null ? "added to" : "updated on")} {c.Number}");
        QueueEligibility(c.CustomerId);
        await db.SaveChangesAsync(ct);
        return (await EntitlementsAsync(contractId, ct)).First(x => x.Id == e.Id);
    }

    private async Task ApplyEntitlementAsync(Entitlement e, Contract c, SaveEntitlementRequest r, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        var name = Parse.Clean(r.Name, 250);
        if (name is null) errors["name"] = "A name is required.";
        var type = Parse.Enum(r.Type, EntitlementType.Support, "type");
        if (r.ProductId is { } pid && !await db.Products.AnyAsync(p => p.Id == pid, ct)) errors["productId"] = "Unknown product.";
        if (r.InstallationId is { } iid && !await db.Installations.AnyAsync(i => i.Id == iid && i.CustomerId == c.CustomerId, ct))
            errors["installationId"] = "The installation does not belong to the contract's customer.";
        if (r.AssetId is { } aid && !await db.CustomerAssets.AnyAsync(a => a.Id == aid && a.CustomerId == c.CustomerId, ct))
            errors["assetId"] = "The asset does not belong to the contract's customer.";
        if (r.IncidentsAllowed is < 0) errors["incidentsAllowed"] = "Cannot be negative.";
        if (r.HoursAllowed is < 0) errors["hoursAllowed"] = "Cannot be negative.";
        if (type == EntitlementType.Hours && r.HoursAllowed is null) errors["hoursAllowed"] = "Hour entitlements need an hour allowance.";
        if (type == EntitlementType.Incidents && r.IncidentsAllowed is null) errors["incidentsAllowed"] = "Incident entitlements need an incident allowance.";
        var start = r.StartDate ?? c.StartDate;
        var end = r.EndDate ?? c.EndDate;
        if (end < start) errors["endDate"] = "The end date must be on or after the start date.";
        if (errors.Count > 0) throw new ValidationException(errors);
        e.Name = name!;
        e.Type = type;
        e.ProductId = r.ProductId;
        e.InstallationId = r.InstallationId;
        e.AssetId = r.AssetId;
        e.IncidentsAllowed = r.IncidentsAllowed;
        e.HoursAllowed = r.HoursAllowed;
        e.StartDate = start;
        e.EndDate = end;
        e.IsActive = r.IsActive;
    }

    public async Task DeleteEntitlementAsync(Guid contractId, Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ContractsManage);
        var c = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == contractId, ct) ?? throw new NotFoundException("Contract", contractId);
        await CustomerAccess.EnsureVisibleAsync(db, ctx, c.CustomerId, ct);
        var e = await db.Entitlements.FirstOrDefaultAsync(x => x.Id == id && x.ContractId == contractId, ct) ?? throw new NotFoundException("Entitlement", id);
        if (await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AnyAsync(t => t.EntitlementId == id, ct) || await db.TimeEntries.AnyAsync(t => t.EntitlementId == id, ct))
            e.IsActive = false;
        else db.Entitlements.Remove(e);
        audit.Write(CustomerAuditActions.ContractChanged, "Contract", contractId, $"Entitlement '{e.Name}' removed from {c.Number}");
        QueueEligibility(c.CustomerId);
        await db.SaveChangesAsync(ct);
    }

    private void QueueEligibility(Guid customerId) =>
        jobs.Enqueue(ContractEligibilityJobHandler.JobType, new ContractEligibilityPayload(customerId), db.Clock.UtcNow.AddSeconds(2));

    // ───────────────────────────── Renewals & consumption ─────────────────────────────

    public async Task<List<ContractRenewalDto>> RenewalsAsync(int days, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ContractsView);
        var today = Today;
        var until = today.AddDays(Math.Clamp(days, 1, 730));
        var rows = await Visible().Where(c => (c.Status == ContractStatus.Active && c.EndDate <= until) ||
                                              (c.Status == ContractStatus.Expired && c.EndDate >= today.AddDays(-60)))
            .OrderBy(c => c.EndDate).Take(500).ToListAsync(ct);
        var items = await MapListAsync(rows, ct);
        var customerIds = rows.Select(r => r.CustomerId).Distinct().ToList();
        var open = await db.Tickets.AsNoTracking().Where(t => t.CustomerId != null && customerIds.Contains(t.CustomerId.Value) && t.StatusCategory < StatusCategory.Resolved)
            .GroupBy(t => t.CustomerId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return items.Select(i => new ContractRenewalDto(i, i.DaysToExpiry,
            i.DaysToExpiry < 0 ? "Expired" : i.DaysToExpiry <= 7 ? "Critical" : i.DaysToExpiry <= 30 ? "Soon" : "Upcoming", open.GetValueOrDefault(i.CustomerId))).ToList();
    }

    private IQueryable<TimeEntry> EntriesFor(Contract c)
    {
        var coveredTickets = db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).Where(t => t.ContractId == c.Id).Select(t => t.Id);
        return db.TimeEntries.AsNoTracking().Where(te => te.ContractId == c.Id || (te.ContractId == null && coveredTickets.Contains(te.TicketId)));
    }

    private async Task<ContractConsumptionSummaryDto> ConsumptionSummaryAsync(Contract c, CancellationToken ct)
    {
        var entries = await EntriesFor(c).Select(te => new { te.Minutes, te.IsBillable, te.BillableAmount, te.TicketId }).ToListAsync(ct);
        var billable = entries.Where(e => e.IsBillable).Sum(e => e.Minutes) / 60m;
        var nonBillable = entries.Where(e => !e.IsBillable).Sum(e => e.Minutes) / 60m;
        var consumed = Math.Max(c.ConsumedHours, billable);
        var tickets = await db.Tickets.CountAsync(t => t.ContractId == c.Id, ct);
        return new ContractConsumptionSummaryDto(c.PrepaidHours, Math.Round(consumed, 2), c.PrepaidHours is { } p ? Math.Round(p - consumed, 2) : null,
            Math.Round(billable, 2), Math.Round(nonBillable, 2), ctx.Has(Permissions.TicketsViewCosts) ? entries.Sum(e => e.BillableAmount) : 0,
            c.PrepaidHours is > 0 ? Math.Round((double)(consumed / c.PrepaidHours.Value) * 100, 1) : null, tickets);
    }

    public async Task<ConsumptionReportDto> ConsumptionAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ContractsView);
        var c = await Visible().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Contract", id);
        var summary = await ConsumptionSummaryAsync(c, ct);
        var rows = await EntriesFor(c).OrderByDescending(te => te.StartedAt).Take(2000).ToListAsync(ct);
        var ticketIds = rows.Select(r => r.TicketId).Distinct().ToList();
        var tickets = await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(t => ticketIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Number, t.Subject }).ToDictionaryAsync(t => t.Id, ct);
        var userIds = rows.Select(r => r.UserId).Distinct().ToList();
        var users = await db.Users.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var showCosts = ctx.Has(Permissions.TicketsViewCosts);
        var byMonth = rows.GroupBy(r => r.StartedAt.ToString("yyyy-MM", CultureInfo.InvariantCulture)).OrderBy(g => g.Key)
            .Select(g => new ConsumptionMonthDto(g.Key, Math.Round(g.Sum(x => x.Minutes) / 60m, 2), Math.Round(g.Where(x => x.IsBillable).Sum(x => x.Minutes) / 60m, 2),
                showCosts ? g.Sum(x => x.BillableAmount) : 0)).ToList();
        var byAgent = rows.GroupBy(r => r.UserId).Select(g => new ConsumptionAgentDto(g.Key, users.GetValueOrDefault(g.Key) ?? "Agent",
            Math.Round(g.Sum(x => x.Minutes) / 60m, 2), Math.Round(g.Where(x => x.IsBillable).Sum(x => x.Minutes) / 60m, 2))).OrderByDescending(a => a.Hours).ToList();
        var prefix = (await settings.TicketsAsync(ct)).NumberPrefix;
        var entries = rows.Take(500).Select(r =>
        {
            var t = tickets.GetValueOrDefault(r.TicketId);
            return new ConsumptionEntryDto(r.Id, r.TicketId, t is null ? "" : $"{prefix}{t.Number}", t?.Subject ?? "", users.GetValueOrDefault(r.UserId) ?? "Agent", r.StartedAt,
                r.Minutes, r.IsBillable, r.ActivityType, showCosts ? r.BillableAmount : 0);
        }).ToList();
        return new ConsumptionReportDto(c.Id, c.Number, summary, byMonth, byAgent, entries);
    }

    // ───────────────────────────── Eligibility preview & form metadata ─────────────────────────────

    public async Task<EligibilityPreviewDto> PreviewEligibilityAsync(Guid customerId, Guid? productId, Guid? installationId, Guid? assetId, CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.TicketsCreate, Permissions.ContractsView, Permissions.CustomersView);
        await CustomerAccess.EnsureVisibleAsync(db, ctx, customerId, ct);
        if (installationId is { } iid)
        {
            var inst = await db.Installations.AsNoTracking().FirstOrDefaultAsync(i => i.Id == iid && i.CustomerId == customerId, ct)
                       ?? throw new ValidationException(new Dictionary<string, string> { ["installationId"] = "The installation does not belong to the customer." });
            productId ??= inst.ProductId;
        }
        var d = await entitlements.DecideAsync(customerId, productId, installationId, assetId, ct);
        var policy = d.Contract?.SlaPolicyId is { } pid ? await db.SlaPolicies.AsNoTracking().Where(p => p.Id == pid).Select(p => p.Name).FirstOrDefaultAsync(ct) : null;
        var active = ctx.Has(Permissions.ContractsView) ? await MapListAsync(d.ActiveContracts.ToList(), ct) : [];
        return new EligibilityPreviewDto(d.Status.ToString(), d.Note, d.Contract?.Id, d.Contract?.Number, d.Contract?.Type.ToString(), d.Entitlement?.Id,
            d.Entitlement?.Name, d.Allowance, d.Status == EntitlementStatus.Entitled ? d.Contract?.EndDate : null, d.Contract?.SlaPolicyId, policy,
            d.Contract?.IsBillable ?? false, active);
    }

    public async Task<ContractFormMetaDto> FormMetaAsync(CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.ContractsView, Permissions.ContractsManage);
        var policies = await db.SlaPolicies.AsNoTracking().OrderBy(p => p.Name).Select(p => new IdName(p.Id, p.Name)).ToListAsync(ct);
        var calendars = await db.BusinessCalendars.AsNoTracking().OrderBy(c => c.Name).Select(c => new IdName(c.Id, c.Name)).ToListAsync(ct);
        return new ContractFormMetaDto(Enum.GetNames<ContractType>(), Enum.GetNames<ContractStatus>(), Enum.GetNames<EntitlementType>(), policies, calendars);
    }
}
