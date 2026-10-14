using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Compliance;
using SupportDesk.Domain;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Administration.Compliance;

/// <summary>
/// Evaluates active legal holds. A hold covers a ticket when every criterion it specifies matches: customer, contact,
/// the listed tickets and the creation-date range. Held tickets (and their messages and attachments) are skipped by
/// retention; contacts with held tickets — or named by a hold directly — cannot be erased.
/// </summary>
public sealed class LegalHoldMatcher(DeskDbContext db)
{
    public async Task<List<LegalHold>> ActiveAsync(CancellationToken ct) =>
        await db.LegalHolds.AsNoTracking().Where(h => h.ReleasedAt == null).ToListAsync(ct);

    public IQueryable<Ticket> Covered(LegalHold h)
    {
        var q = db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsQueryable();
        if (h.CustomerId is { } customer) q = q.Where(t => t.CustomerId == customer);
        if (h.ContactId is { } contact) q = q.Where(t => t.ContactId == contact);
        var ids = Json.GuidList(h.TicketIdsJson);
        if (ids.Count > 0) q = q.Where(t => ids.Contains(t.Id));
        if (h.FromDate is { } from) q = q.Where(t => t.CreatedAt >= from);
        if (h.ToDate is { } to) q = q.Where(t => t.CreatedAt <= to);
        return q;
    }

    /// <summary>Ids of every ticket covered by an active hold.</summary>
    public async Task<HashSet<Guid>> HeldTicketIdsAsync(CancellationToken ct)
    {
        var held = new HashSet<Guid>();
        foreach (var hold in await ActiveAsync(ct))
            held.UnionWith(await Covered(hold).Select(t => t.Id).ToListAsync(ct));
        return held;
    }

    public async Task<LegalHold?> HoldBlockingContactAsync(Guid contactId, CancellationToken ct)
    {
        var contact = await db.Contacts.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().FirstOrDefaultAsync(c => c.Id == contactId, ct);
        if (contact is null) return null;
        foreach (var hold in await ActiveAsync(ct))
        {
            if (hold.ContactId == contactId) return hold;
            if (hold.ContactId is null && hold.CustomerId is not null && hold.CustomerId == contact.CustomerId && Json.GuidList(hold.TicketIdsJson).Count == 0
                && hold.FromDate is null && hold.ToDate is null) return hold;
            if (await Covered(hold).AnyAsync(t => t.ContactId == contactId, ct)) return hold;
        }
        return null;
    }
}

/// <summary>Legal holds (admin.compliance): create, list with coverage, release. Every change is audited.</summary>
public sealed class LegalHoldService(DeskDbContext db, RequestContext ctx, IAuditWriter audit, LegalHoldMatcher matcher)
{
    public async Task<List<LegalHoldDto>> ListAsync(bool includeReleased, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminCompliance);
        var q = db.LegalHolds.AsNoTracking();
        if (!includeReleased) q = q.Where(h => h.ReleasedAt == null);
        var holds = await q.OrderByDescending(h => h.CreatedAt).Take(500).ToListAsync(ct);
        var result = new List<LegalHoldDto>();
        foreach (var h in holds) result.Add(await ToDtoAsync(h, ct));
        return result;
    }

    public async Task<LegalHoldDto> CreateAsync(SaveLegalHoldRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminCompliance);
        var errors = new Dictionary<string, string>();
        var name = r.Name?.Trim() ?? "";
        var reason = r.Reason?.Trim() ?? "";
        if (name.Length is < 3 or > 200) errors["name"] = "3–200 characters.";
        if (reason.Length is < 5 or > 2000) errors["reason"] = "Describe the matter or case reference (5–2000 characters).";
        var numbers = (r.TicketNumbers ?? []).Distinct().ToList();
        if (r.CustomerId is null && r.ContactId is null && numbers.Count == 0 && r.FromDate is null && r.ToDate is null)
            errors["scope"] = "Choose at least one of customer, contact, tickets or a date range.";
        if (r.FromDate is { } f && r.ToDate is { } t && f > t) errors["toDate"] = "The end date must be after the start date.";
        if (r.CustomerId is { } cid && !await db.Customers.AnyAsync(c => c.Id == cid, ct)) errors["customerId"] = "Customer not found.";
        if (r.ContactId is { } kid && !await db.Contacts.AnyAsync(c => c.Id == kid, ct)) errors["contactId"] = "Contact not found.";
        var ticketIds = numbers.Count == 0 ? [] : await db.Tickets.Where(x => numbers.Contains(x.Number)).Select(x => new { x.Id, x.Number }).ToListAsync(ct);
        var missing = numbers.Except(ticketIds.Select(x => x.Number)).ToList();
        if (missing.Count > 0) errors["ticketNumbers"] = $"Unknown ticket number(s): {string.Join(", ", missing)}.";
        if (errors.Count > 0) throw new ValidationException(errors);

        var hold = new LegalHold
        {
            Name = name, Reason = reason, CustomerId = r.CustomerId, ContactId = r.ContactId,
            TicketIdsJson = ticketIds.Count == 0 ? null : Json.Serialize(ticketIds.Select(x => x.Id)),
            FromDate = r.FromDate is { } from ? DateTime.SpecifyKind(from, DateTimeKind.Utc) : null,
            ToDate = r.ToDate is { } to ? DateTime.SpecifyKind(to, DateTimeKind.Utc) : null
        };
        db.LegalHolds.Add(hold);
        audit.Write(AuditActions.LegalHoldChanged, "LegalHold", hold.Id, $"Legal hold '{name}' placed",
            new { after = new { name, reason, r.CustomerId, r.ContactId, ticketNumbers = numbers, hold.FromDate, hold.ToDate } });
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(hold, ct);
    }

    public async Task<LegalHoldDto> ReleaseAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminCompliance);
        var hold = await db.LegalHolds.FirstOrDefaultAsync(h => h.Id == id, ct) ?? throw new NotFoundException("Legal hold", id);
        if (hold.ReleasedAt is not null) throw new ConflictException("hold_released", "This legal hold was already released.");
        hold.ReleasedAt = db.Clock.UtcNow;
        hold.ReleasedById = ctx.UserId;
        audit.Write(AuditActions.LegalHoldChanged, "LegalHold", hold.Id, $"Legal hold '{hold.Name}' released",
            new { before = new { active = true }, after = new { active = false, hold.ReleasedAt } });
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(hold, ct);
    }

    private async Task<LegalHoldDto> ToDtoAsync(LegalHold h, CancellationToken ct)
    {
        var ids = Json.GuidList(h.TicketIdsJson);
        var numbers = ids.Count == 0 ? [] : await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).Where(t => ids.Contains(t.Id))
            .Select(t => t.Number).OrderBy(n => n).ToListAsync(ct);
        var customer = h.CustomerId is { } cid ? await db.Customers.Where(c => c.Id == cid).Select(c => c.Name).FirstOrDefaultAsync(ct) : null;
        var contact = h.ContactId is { } kid
            ? await db.Contacts.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).Where(c => c.Id == kid).Select(c => c.FullName).FirstOrDefaultAsync(ct)
            : null;
        var creator = h.CreatedById is { } uid ? await db.Users.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).Where(u => u.Id == uid).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) : null;
        var covered = await matcher.Covered(h).LongCountAsync(ct);
        return new LegalHoldDto(h.Id, h.Name, h.Reason, h.CustomerId, customer, h.ContactId, contact, numbers, h.FromDate, h.ToDate, h.ReleasedAt is null,
            h.CreatedAt, creator, h.ReleasedAt, covered);
    }
}
