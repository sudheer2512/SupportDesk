using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Integrations.Engineering;

/// <summary>Adds the ticket's engineering links (last known state) to the workbench context panel.</summary>
public sealed class EngineeringContextContributor(DeskDbContext db) : ITicketContextContributor
{
    public async Task ContributeAsync(Ticket ticket, TicketContextBuilder context, CancellationToken ct)
    {
        var links = await db.EngineeringLinks.AsNoTracking().Where(l => l.TicketId == ticket.Id).OrderBy(l => l.CreatedAt).Take(20).ToListAsync(ct);
        foreach (var l in links)
            context.EngineeringLinks.Add(new EngineeringLinkDto(l.Id, TrackerProviders.Name(l.Provider), l.ItemType.ToString(), l.ExternalId, l.Url, l.Title, l.State,
                l.AssignedTo, l.FixVersion, l.IsResolved, l.LastSyncedAt));
    }
}
