using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Service;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Service;

/// <summary>Outcome of the eligibility rules for a customer/product/installation/asset combination (no side effects).</summary>
public sealed record EligibilityDecision(
    EntitlementStatus Status,
    Contract? Contract,
    Entitlement? Entitlement,
    string Note,
    string? Allowance,
    IReadOnlyList<Contract> ActiveContracts);

/// <summary>
/// Determines support eligibility when a ticket is created or its customer/product/installation changes.
/// Best match: an active entitlement for the installation, then the asset, then the product, then a general
/// (product-less) entitlement; incident and hour allowances are respected. Overridden tickets are left alone.
/// <see cref="DecideAsync"/> runs the same rules without touching a ticket (eligibility preview before creation).
/// </summary>
public sealed class EntitlementService(DeskDbContext db, TicketHistory history) : IEntitlementService
{
    public async Task EvaluateAsync(Ticket ticket, CancellationToken ct)
    {
        if (ticket.EntitlementStatus == EntitlementStatus.Overridden) return;
        var before = (ticket.EntitlementStatus, ticket.ContractId, ticket.EntitlementId);
        var decision = await DecideAsync(ticket.CustomerId, ticket.ProductId, ticket.InstallationId, ticket.AssetId, ct);
        Set(ticket, decision.Status, decision.Contract?.Id, decision.Entitlement?.Id, decision.Note);
        if (decision.Entitlement is { } match && before.EntitlementId != match.Id && match.IncidentsAllowed is not null)
            await db.Entitlements.Where(e => e.Id == match.Id).ExecuteUpdateAsync(u => u.SetProperty(e => e.IncidentsUsed, e => e.IncidentsUsed + 1), ct);
        Record(ticket, before);
    }

    public async Task<EligibilityDecision> DecideAsync(Guid? customerId, Guid? productId, Guid? installationId, Guid? assetId, CancellationToken ct)
    {
        if (customerId is not { } cid) return new EligibilityDecision(EntitlementStatus.Unknown, null, null, "No customer on the ticket.", null, []);
        var today = DateOnly.FromDateTime(db.Clock.UtcNow);
        var contracts = await db.Contracts.AsNoTracking().Where(c => c.CustomerId == cid).ToListAsync(ct);
        var active = contracts.Where(c => c.Status == ContractStatus.Active && c.StartDate <= today && c.EndDate >= today).ToList();
        var activeIds = active.Select(c => c.Id).ToList();
        var entitlements = await db.Entitlements.AsNoTracking()
            .Where(e => activeIds.Contains(e.ContractId) && e.IsActive && e.StartDate <= today && e.EndDate >= today).ToListAsync(ct);

        static bool HasAllowance(Entitlement e) =>
            (e.IncidentsAllowed is null || e.IncidentsUsed < e.IncidentsAllowed) && (e.HoursAllowed is null || e.HoursUsed < e.HoursAllowed);

        var match = entitlements.Where(HasAllowance).OrderByDescending(e =>
                (installationId != null && e.InstallationId == installationId ? 8 : 0) +
                (assetId != null && e.AssetId == assetId ? 6 : 0) +
                (productId != null && e.ProductId == productId ? 4 : 0) +
                (e.ProductId == null && e.InstallationId == null && e.AssetId == null ? 1 : 0))
            .FirstOrDefault(e =>
                (e.InstallationId == null || e.InstallationId == installationId) &&
                (e.AssetId == null || e.AssetId == assetId) &&
                (e.ProductId == null || e.ProductId == productId));

        if (match is not null)
        {
            var contract = active.First(c => c.Id == match.ContractId);
            var allowance = match.HoursAllowed is { } h ? $"{h - match.HoursUsed:0.#} h left" :
                match.IncidentsAllowed is { } i ? $"{i - match.IncidentsUsed} incidents left" : "unlimited";
            return new EligibilityDecision(EntitlementStatus.Entitled, contract, match,
                $"{contract.Type} {contract.Number} — {match.Name} ({allowance}, until {contract.EndDate:yyyy-MM-dd})", allowance, active);
        }
        if (active.FirstOrDefault(c => !entitlements.Any(e => e.ContractId == c.Id)) is { } blanket)
        {
            // A contract without explicit entitlements covers the customer generally.
            return new EligibilityDecision(EntitlementStatus.Entitled, blanket, null,
                $"{blanket.Type} {blanket.Number} (customer-wide, until {blanket.EndDate:yyyy-MM-dd})", "customer-wide", active);
        }
        if (entitlements.Any(e => !HasAllowance(e)))
            return new EligibilityDecision(EntitlementStatus.NotEntitled, null, null, "The covering entitlement has used up its incidents or hours.", null, active);
        if (contracts.Where(c => c.EndDate < today).OrderByDescending(c => c.EndDate).FirstOrDefault() is { } expired)
            return new EligibilityDecision(EntitlementStatus.Expired, expired, null, $"{expired.Type} {expired.Number} expired on {expired.EndDate:yyyy-MM-dd}.", null, active);
        return new EligibilityDecision(EntitlementStatus.NotEntitled, null, null, "No active contract or warranty covers this request.", null, active);
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
