using Microsoft.EntityFrameworkCore;
using SupportDesk.Domain;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Communication;

/// <summary>
/// Appends channel-generated entries (call logs, chat transcripts, system notices) to a ticket conversation. Sequence
/// numbers are allocated under the ticket's rowversion exactly like the ticket conversation service, with retries on
/// concurrent writers. Idempotent per <c>clientRequestId</c>.
/// </summary>
public sealed class ThreadAppender(DeskDbContext db)
{
    private const int MaxRetries = 4;

    public async Task<TicketThread> AppendAsync(Guid ticketId, Func<Ticket, TicketThread> build, string clientRequestId, CancellationToken ct)
    {
        if (await db.TicketThreads.FirstOrDefaultAsync(t => t.ClientRequestId == clientRequestId, ct) is { } existing) return existing;
        for (var attempt = 1; ; attempt++)
        {
            var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct) ?? throw new NotFoundException("Ticket", ticketId);
            var thread = build(ticket);
            thread.TicketId = ticket.Id;
            thread.Seq = ticket.LastThreadSeq + 1;
            thread.ClientRequestId = clientRequestId;
            if (thread.CreatedAt == default) thread.CreatedAt = db.Clock.UtcNow;
            db.TicketThreads.Add(thread);
            ticket.LastThreadSeq = thread.Seq;
            ticket.LastActivityAt = db.Clock.UtcNow;
            try
            {
                await db.SaveChangesAsync(ct);
                return thread;
            }
            catch (DbUpdateException) when (attempt < MaxRetries)
            {
                db.ChangeTracker.Clear();
                if (await db.TicketThreads.FirstOrDefaultAsync(t => t.ClientRequestId == clientRequestId, ct) is { } dup) return dup;
            }
        }
    }
}
