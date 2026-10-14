using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Customers;
using SupportDesk.Domain;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Customers;

/// <summary>Service visits and scheduled activities (on-site visits, maintenance, trainings, upgrades…) with a calendar range query.</summary>
public sealed class ServiceActivityService(DeskDbContext db, RequestContext ctx, IEventPublisher events, TenantSettingsService settings)
{
    public async Task<List<ServiceActivityDto>> ListAsync(ServiceActivityQuery q, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersView);
        var query = db.ServiceActivities.AsNoTracking();
        if (CustomerAccess.IsRestricted(ctx))
        {
            var ids = CustomerAccess.VisibleCustomerIds(db, ctx);
            query = query.Where(a => ids.Contains(a.CustomerId));
        }
        if (q.CustomerId is { } cid) query = query.Where(a => a.CustomerId == cid);
        if (q.AssignedUserId is { } uid) query = query.Where(a => a.AssignedUserId == uid);
        if (q.From is { } from) query = query.Where(a => a.ScheduledEnd >= from);
        if (q.To is { } to) query = query.Where(a => a.ScheduledStart < to);
        var statuses = Parse.Enums<ServiceActivityStatus>(q.Status);
        if (statuses.Count > 0) query = query.Where(a => statuses.Contains(a.Status));
        var types = Parse.Enums<ServiceActivityType>(q.Type);
        if (types.Count > 0) query = query.Where(a => types.Contains(a.Type));
        var rows = await query.OrderBy(a => a.ScheduledStart).Take(Math.Clamp(q.Limit ?? 500, 1, 1000)).ToListAsync(ct);
        return await MapAsync(rows, ct);
    }

    public async Task<List<ServiceActivityDto>> MapAsync(IReadOnlyList<ServiceActivity> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var customerIds = rows.Select(a => a.CustomerId).Distinct().ToList();
        var customers = await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var locationIds = rows.Where(a => a.LocationId != null).Select(a => a.LocationId!.Value).Distinct().ToList();
        var locations = await db.CustomerLocations.AsNoTracking().Where(l => locationIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var ticketIds = rows.Where(a => a.TicketId != null).Select(a => a.TicketId!.Value).Distinct().ToList();
        var tickets = await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(t => ticketIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Number, ct);
        var instIds = rows.Where(a => a.InstallationId != null).Select(a => a.InstallationId!.Value).Distinct().ToList();
        var installs = await db.Installations.AsNoTracking().Where(i => instIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Name, ct);
        var contractIds = rows.Where(a => a.ContractId != null).Select(a => a.ContractId!.Value).Distinct().ToList();
        var contracts = await db.Contracts.AsNoTracking().Where(c => contractIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Number, ct);
        var userIds = rows.Where(a => a.AssignedUserId != null).Select(a => a.AssignedUserId!.Value).Distinct().ToList();
        var users = await db.Users.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var prefix = (await settings.TicketsAsync(ct)).NumberPrefix;
        return rows.Select(a => new ServiceActivityDto(a.Id, a.CustomerId, customers.GetValueOrDefault(a.CustomerId) ?? "", a.LocationId,
            a.LocationId is { } l ? locations.GetValueOrDefault(l) : null, a.TicketId, a.TicketId is { } t && tickets.TryGetValue(t, out var n) ? $"{prefix}{n}" : null,
            a.InstallationId, a.InstallationId is { } i ? installs.GetValueOrDefault(i) : null, a.ContractId, a.ContractId is { } c ? contracts.GetValueOrDefault(c) : null,
            a.Type.ToString(), a.Title, a.Description, a.ScheduledStart, a.ScheduledEnd, a.AssignedUserId, a.AssignedUserId is { } u ? users.GetValueOrDefault(u) : null,
            a.Status.ToString(), a.Outcome, a.VisibleToCustomer, a.CreatedAt)).ToList();
    }

    public async Task<ServiceActivityDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersView);
        var a = await db.ServiceActivities.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Activity", id);
        await CustomerAccess.EnsureVisibleAsync(db, ctx, a.CustomerId, ct);
        return (await MapAsync([a], ct))[0];
    }

    public async Task<ServiceActivityDto> SaveAsync(Guid? id, SaveServiceActivityRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersManage);
        await CustomerAccess.EnsureVisibleAsync(db, ctx, r.CustomerId, ct);
        var errors = new Dictionary<string, string>();
        var title = Parse.Clean(r.Title, 300);
        if (title is null) errors["title"] = "A title is required.";
        if (r.ScheduledEnd <= r.ScheduledStart) errors["scheduledEnd"] = "The end must be after the start.";
        if (r.ScheduledEnd - r.ScheduledStart > TimeSpan.FromDays(31)) errors["scheduledEnd"] = "An activity can span at most 31 days.";
        if (r.LocationId is { } lid && !await db.CustomerLocations.AnyAsync(l => l.Id == lid && l.CustomerId == r.CustomerId, ct)) errors["locationId"] = "The location does not belong to the customer.";
        if (r.TicketId is { } tid && !await db.Tickets.AnyAsync(t => t.Id == tid && t.CustomerId == r.CustomerId, ct)) errors["ticketId"] = "The ticket does not belong to the customer.";
        if (r.InstallationId is { } iid && !await db.Installations.AnyAsync(i => i.Id == iid && i.CustomerId == r.CustomerId, ct)) errors["installationId"] = "The installation does not belong to the customer.";
        if (r.ContractId is { } cid && !await db.Contracts.AnyAsync(c => c.Id == cid && c.CustomerId == r.CustomerId, ct)) errors["contractId"] = "The contract does not belong to the customer.";
        if (r.AssignedUserId is { } uid && !await db.Users.AnyAsync(u => u.Id == uid && u.IsActive, ct)) errors["assignedUserId"] = "Unknown or inactive user.";
        if (errors.Count > 0) throw new ValidationException(errors);
        ServiceActivity a;
        if (id is { } aid)
        {
            a = await db.ServiceActivities.FirstOrDefaultAsync(x => x.Id == aid, ct) ?? throw new NotFoundException("Activity", aid);
            await CustomerAccess.EnsureVisibleAsync(db, ctx, a.CustomerId, ct);
        }
        else db.ServiceActivities.Add(a = new ServiceActivity());
        a.CustomerId = r.CustomerId;
        a.LocationId = r.LocationId;
        a.TicketId = r.TicketId;
        a.InstallationId = r.InstallationId;
        a.ContractId = r.ContractId;
        a.Type = Parse.Enum(r.Type, ServiceActivityType.OnsiteVisit, "type");
        a.Title = title!;
        a.Description = Parse.Clean(r.Description, 8000);
        a.ScheduledStart = r.ScheduledStart;
        a.ScheduledEnd = r.ScheduledEnd;
        a.AssignedUserId = r.AssignedUserId;
        a.Status = Parse.Enum(r.Status, id is null ? ServiceActivityStatus.Scheduled : a.Status, "status");
        a.Outcome = Parse.Clean(r.Outcome, 8000);
        a.VisibleToCustomer = r.VisibleToCustomer;
        events.Publish(EventTypes.CustomerActivity, "Customer", a.CustomerId, new EntityEventPayload(a.Id, a.Title,
            [new FieldChange("serviceActivity", null, $"{a.Type} {a.Status} {a.ScheduledStart:O}")]));
        await db.SaveChangesAsync(ct);
        return (await MapAsync([a], ct))[0];
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersManage);
        var a = await db.ServiceActivities.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Activity", id);
        await CustomerAccess.EnsureVisibleAsync(db, ctx, a.CustomerId, ct);
        db.ServiceActivities.Remove(a);
        await db.SaveChangesAsync(ct);
    }
}
