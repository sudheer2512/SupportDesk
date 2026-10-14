using System.Text.Json;
using SupportDesk.Domain;

namespace SupportDesk.Application.Platform;

// Cross-module ports. Each has a "null" default registered by the kernel (TryAdd); the owning module registers the
// real implementation (a later registration wins). Callers must handle the null behaviour gracefully.

public sealed record Citation(int Index, string SourceType, Guid SourceId, string Title, string? Url, string? Snippet);

public sealed record AssistantReply(string Text, IReadOnlyList<Citation> Citations, bool SuggestHandoff, double Confidence);

/// <summary>Automated assistant for chat/WhatsApp conversations and the portal (implemented by the AI module).</summary>
public interface IConversationAssistant
{
    /// <summary>Returns null when no assistant is configured or it has nothing confident to say (hand off to a human).</summary>
    Task<AssistantReply?> ReplyAsync(Guid? conversationId, string customerMessage, string? locale, bool authenticatedCustomer, CancellationToken ct);
}

/// <summary>AI operations invoked by automation rules (implemented by the AI module).</summary>
public interface IAiActionGateway
{
    Task<string> ClassifyTicketAsync(Guid ticketId, bool apply, CancellationToken ct);
    Task<string> SummarizeTicketAsync(Guid ticketId, CancellationToken ct);
}

/// <summary>Updates a related ERP record (implemented by the integrations module).</summary>
public interface IErpRecordUpdater
{
    Task<string> UpdateRecordAsync(string system, string recordType, Guid? recordId, string? recordNumber, JsonElement changes, CancellationToken ct);
}

public sealed record OutboundMessageRequest(
    Channel Channel,
    string? To,
    Guid? ContactId,
    Guid? UserId,
    string Subject,
    string BodyHtml,
    Guid? TicketId = null,
    string? TemplateName = null);

/// <summary>Sends an ad-hoc notification message to a contact or agent through a channel (implemented by the omnichannel module).</summary>
public interface IOutboundMessenger
{
    Task<string> SendAsync(OutboundMessageRequest request, CancellationToken ct);
}

public enum KnowledgeAudience { Agent, AuthenticatedCustomer, Public }

public sealed record KnowledgeHit(string SourceType, Guid SourceId, string Title, string? Snippet, string Url, double Score, string Visibility);

/// <summary>Knowledge search respecting visibility (implemented by the knowledge module; the AI module adds semantic ranking).</summary>
public interface IKnowledgeSearch
{
    Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, KnowledgeAudience audience, Guid? customerId, int take, CancellationToken ct);
}

/// <summary>Creates a draft knowledge article (implemented by the knowledge module).</summary>
public interface IArticleDraftWriter
{
    Task<Guid> CreateDraftAsync(string title, string bodyHtml, string? summary, Guid? sourceTicketId, CancellationToken ct);
}

public sealed class NullConversationAssistant : IConversationAssistant
{
    public Task<AssistantReply?> ReplyAsync(Guid? conversationId, string customerMessage, string? locale, bool authenticatedCustomer, CancellationToken ct) =>
        Task.FromResult<AssistantReply?>(null);
}

public sealed class NullAiActionGateway : IAiActionGateway
{
    public Task<string> ClassifyTicketAsync(Guid ticketId, bool apply, CancellationToken ct) => Task.FromResult("AI is not available.");
    public Task<string> SummarizeTicketAsync(Guid ticketId, CancellationToken ct) => Task.FromResult("AI is not available.");
}

public sealed class NullErpRecordUpdater : IErpRecordUpdater
{
    public Task<string> UpdateRecordAsync(string system, string recordType, Guid? recordId, string? recordNumber, JsonElement changes, CancellationToken ct) =>
        throw new DomainException("integration_unavailable", $"No connector is configured for {system}.");
}

public sealed class NullOutboundMessenger : IOutboundMessenger
{
    public Task<string> SendAsync(OutboundMessageRequest request, CancellationToken ct) =>
        throw new DomainException("channel_unavailable", $"No outbound {request.Channel} channel is configured.");
}

public sealed class NullKnowledgeSearch : IKnowledgeSearch
{
    public Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, KnowledgeAudience audience, Guid? customerId, int take, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<KnowledgeHit>>([]);
}

public sealed class NullArticleDraftWriter : IArticleDraftWriter
{
    public Task<Guid> CreateDraftAsync(string title, string bodyHtml, string? summary, Guid? sourceTicketId, CancellationToken ct) =>
        throw new DomainException("knowledge_unavailable", "The knowledge base module is not available.");
}
