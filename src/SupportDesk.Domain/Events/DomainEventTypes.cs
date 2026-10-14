namespace SupportDesk.Domain.Events;

/// <summary>
/// Outbox event type names. These are also the public webhook event names and the automation trigger names
/// (see Automation.RuleTriggers) so one event stream drives automation, SLA, notifications, realtime and webhooks.
/// </summary>
public static class EventTypes
{
    public const string TicketCreated = "ticket.created";
    public const string TicketUpdated = "ticket.updated";
    public const string TicketStatusChanged = "ticket.status_changed";
    public const string TicketAssigned = "ticket.assigned";
    public const string TicketReplyAdded = "ticket.reply_added";
    public const string TicketCustomerReplied = "ticket.customer_replied";
    public const string TicketNoteAdded = "ticket.note_added";
    public const string TicketMerged = "ticket.merged";
    public const string TicketEscalated = "ticket.escalated";
    public const string TicketDeleted = "ticket.deleted";
    public const string TicketViewed = "ticket.viewed";

    public const string SlaWarning = "sla.warning";
    public const string SlaBreached = "sla.breached";
    public const string SlaMet = "sla.met";
    public const string SlaEscalated = "sla.escalated";

    public const string ContactCreated = "contact.created";
    public const string ContactUpdated = "contact.updated";
    public const string ContactMerged = "contact.merged";
    public const string CustomerCreated = "customer.created";
    public const string CustomerUpdated = "customer.updated";
    public const string CustomerActivity = "customer.activity";

    public const string ArticlePublished = "article.published";
    public const string ArticleUpdated = "article.updated";

    public const string MessageReceived = "message.received";
    public const string MessageQueued = "message.queued";
    public const string MessageStatusChanged = "message.status_changed";
    public const string ConversationUpdated = "conversation.updated";
    public const string ChatMessage = "chat.message";

    public const string NotificationCreated = "notification.created";
    public const string ApprovalRequested = "approval.requested";
    public const string ApprovalDecided = "approval.decided";
    public const string AiProposalCreated = "ai.proposal_created";
    public const string EngineeringItemUpdated = "engineering.item_updated";
    public const string SatisfactionReceived = "satisfaction.received";

    public static readonly string[] Public =
    [
        TicketCreated, TicketUpdated, TicketStatusChanged, TicketAssigned, TicketReplyAdded, TicketCustomerReplied,
        TicketNoteAdded, TicketMerged, TicketEscalated, TicketDeleted, SlaWarning, SlaBreached, SlaMet, ContactCreated,
        ContactUpdated, ContactMerged, CustomerCreated, CustomerUpdated, ArticlePublished, MessageReceived,
        MessageStatusChanged, ApprovalRequested, ApprovalDecided, SatisfactionReceived, EngineeringItemUpdated
    ];
}

public sealed record FieldChange(string Field, string? From, string? To);

/// <summary>Payload for ticket.* events.</summary>
public sealed record TicketEventPayload(
    Guid TicketId,
    long Number,
    IReadOnlyList<FieldChange>? Changes = null,
    Guid? ThreadId = null,
    string? Note = null,
    /// <summary>Resolve action only: whether the agent chose to notify the customer (null = not a resolve action).</summary>
    bool? NotifyCustomer = null);

public sealed record SlaEventPayload(Guid TicketId, long Number, Guid InstanceId, Guid MilestoneId, SlaMilestoneKind Milestone, DateTime DueAt, int EscalationLevel = 0);

public sealed record EntityEventPayload(Guid Id, string? Name = null, IReadOnlyList<FieldChange>? Changes = null);
