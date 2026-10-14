using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Service;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Service;

/// <summary>
/// Determines support eligibility when a ticket is created or its customer/product/installation changes.
/// Best match: an active entitlement for the installation, then the asset, then the product, then a general
/// (product-less) entitlement; incident and hour allowances are respected. Overridden tickets are left alone.
/// </summary>
public sealed class EntitlementService(DeskDbContext db, TicketHistory history) : IEntitlementService
{
    public async Task EvaluateAsync(Ticket ticket, CancellationToken ct)
    {
        if (ticket.EntitlementStatus == EntitlementStatus.Overridden) return;
        var before = (ticket.EntitlementStatus, ticket.ContractId, ticket.EntitlementId);
        if (ticket.CustomerId is not { } customerId)
        {
            Set(ticket, EntitlementStatus.Unknown, null, null, "No customer on the ticket.");
            Record(ticket, before);
            return;
        }
        var today = DateOnly.FromDateTime(db.Clock.UtcNow);
        var contracts = await db.Contracts.AsNoTracking().Where(c => c.CustomerId == customerId).ToListAsync(ct);
        var active = contracts.Where(c => c.Status == ContractStatus.Active && c.StartDate <= today && c.EndDate >= today).ToList();
        var activeIds = active.Select(c => c.Id).ToList();
        var entitlements = await db.Entitlements.AsNoTracking()
            .Where(e => activeIds.Contains(e.ContractId) && e.IsActive && e.StartDate <= today && e.EndDate >= today).ToListAsync(ct);

        static bool HasAllowance(Entitlement e) =>
            (e.IncidentsAllowed is null || e.IncidentsUsed < e.IncidentsAllowed) && (e.HoursAllowed is null || e.HoursUsed < e.HoursAllowed);

        var match = entitlements.Where(HasAllowance).OrderByDescending(e =>
                (ticket.InstallationId != null && e.InstallationId == ticket.InstallationId ? 8 : 0) +
                (ticket.AssetId != null && e.AssetId == ticket.AssetId ? 6 : 0) +
                (ticket.ProductId != null && e.ProductId == ticket.ProductId ? 4 : 0) +
                (e.ProductId == null && e.InstallationId == null && e.AssetId == null ? 1 : 0))
            .FirstOrDefault(e =>
                (e.InstallationId == null || e.InstallationId == ticket.InstallationId) &&
                (e.AssetId == null || e.AssetId == ticket.AssetId) &&
                (e.ProductId == null || e.ProductId == ticket.ProductId));

        if (match is not null)
        {
            var contract = active.First(c => c.Id == match.ContractId);
            var allowance = match.HoursAllowed is { } h ? $"{h - match.HoursUsed:0.#} h left" :
                match.IncidentsAllowed is { } i ? $"{i - match.IncidentsUsed} incidents left" : "unlimited";
            Set(ticket, EntitlementStatus.Entitled, contract.Id, match.Id, $"{contract.Type} {contract.Number} — {match.Name} ({allowance}, until {contract.EndDate:yyyy-MM-dd})");
            if (before.EntitlementId != match.Id && match.IncidentsAllowed is not null)
                await db.Entitlements.Where(e => e.Id == match.Id).ExecuteUpdateAsync(u => u.SetProperty(e => e.IncidentsUsed, e => e.IncidentsUsed + 1), ct);
        }
        else if (active.FirstOrDefault(c => !entitlements.Any(e => e.ContractId == c.Id)) is { } blanket)
        {
            // A contract without explicit entitlements covers the customer generally.
            Set(ticket, EntitlementStatus.Entitled, blanket.Id, null, $"{blanket.Type} {blanket.Number} (customer-wide, until {blanket.EndDate:yyyy-MM-dd})");
        }
        else if (entitlements.Any(e => !HasAllowance(e)))
        {
            Set(ticket, EntitlementStatus.NotEntitled, null, null, "The covering entitlement has used up its incidents or hours.");
        }
        else if (contracts.Where(c => c.EndDate < today).OrderByDescending(c => c.EndDate).FirstOrDefault() is { } expired)
        {
            Set(ticket, EntitlementStatus.Expired, expired.Id, null, $"{expired.Type} {expired.Number} expired on {expired.EndDate:yyyy-MM-dd}.");
        }
        else
        {
            Set(ticket, EntitlementStatus.NotEntitled, null, null, "No active contract or warranty covers this request.");
        }
        Record(ticket, before);
    }

    private static void Set(Ticket t, EntitlementStatus status, Guid? contractId, Guid? entitlementId, string note)
    {
        t.EntitlementStatus = status;
        t.ContractId = contractId;
        t.EntitlementId = entitlementId;
        t.EntitlementNote = note;
    }

    private void Record(Ticket t, (EntitlementStatus Status, Guid? ContractId, Guid? EntitlementId) before)
    {
        if (before.Status == t.EntitlementStatus && before.ContractId == t.ContractId) return;
        history.Record(t, TicketEventTypes.EntitlementEvaluated, $"Support eligibility: {t.EntitlementStatus} — {t.EntitlementNote}",
            [new FieldChange("entitlementStatus", before.Status.ToString(), t.EntitlementStatus.ToString())]);
    }
}
