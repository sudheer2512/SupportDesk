using SupportDesk.Application.Common;
using SupportDesk.Domain.Platform;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Platform;

/// <summary>
/// Adds an event to the transactional outbox (saved together with the business change by the caller's SaveChanges).
/// Events carry correlation, causation and the automation chain depth of the scope that produced them.
/// </summary>
public interface IEventPublisher
{
    OutboxEvent Publish(string type, string? aggregateType, Guid? aggregateId, object payload);
}

public sealed class EventPublisher(DeskDbContext db, RequestContext ctx) : IEventPublisher
{
    public OutboxEvent Publish(string type, string? aggregateType, Guid? aggregateId, object payload)
    {
        var ev = new OutboxEvent
        {
            TenantId = ctx.HasTenant ? ctx.TenantId : null,
            Type = type,
            AggregateType = aggregateType,
            AggregateId = aggregateId,
            PayloadJson = Json.Serialize(payload),
            CorrelationId = ctx.CorrelationId,
            CausationId = ctx.CausationEventId,
            ChainDepth = ctx.ChainDepth,
            ActorId = ctx.UserId ?? ctx.ContactId ?? ctx.ApiClientId,
            ActorType = ctx.ActorType,
            OccurredAt = db.Clock.UtcNow,
            NextAttemptAt = db.Clock.UtcNow
        };
        db.OutboxEvents.Add(ev);
        return ev;
    }
}

/// <summary>
/// Handles committed outbox events. Each handler runs in its own DI scope with a system request context for the
/// event's tenant (ChainDepth = event depth + 1, CausationEventId = event id). A handler that has succeeded for an
/// event is never run again for it (OutboxDelivery), so handlers only need to be idempotent per failure retry.
/// </summary>
public interface IOutboxHandler
{
    string Name { get; }
    /// <summary>Lower runs first (e.g. SLA before automation before notifications).</summary>
    int Order => 100;
    bool CanHandle(string eventType);
    Task HandleAsync(OutboxEvent ev, CancellationToken ct);
}

public static class HandlerOrder
{
    public const int Metrics = 10;
    public const int Sla = 20;
    public const int Search = 30;
    public const int Automation = 40;
    public const int Ai = 50;
    public const int Notifications = 60;
    public const int Realtime = 70;
    public const int Webhooks = 80;
    public const int Integrations = 90;
}
