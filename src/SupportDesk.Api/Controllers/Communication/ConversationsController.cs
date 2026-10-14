using Microsoft.AspNetCore.Mvc;
using SupportDesk.Application.Communication;
using SupportDesk.Application.Platform;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Communication;

namespace SupportDesk.Api.Controllers.Communication;

/// <summary>Omnichannel inbox (all channels), conversation messages, delivery status and agent live-chat actions.</summary>
[Route("api/v1/conversations")]
public sealed class ConversationsController(ConversationQueryService queries, ChatService chat, FileService files) : DeskControllerBase
{
    [HttpGet]
    public Task<PagedResult<ConversationListItemDto>> List([FromQuery] ConversationQuery query, CancellationToken ct) => queries.ListAsync(query, ct);

    [HttpGet("{id:guid}")]
    public Task<ConversationDetailDto> Get(Guid id, CancellationToken ct) => queries.GetAsync(id, ct);

    [HttpGet("{id:guid}/messages")]
    public Task<PagedResult<ConversationMessageDto>> Messages(Guid id, [FromQuery] string? cursor, [FromQuery] int limit = 100, CancellationToken ct = default) =>
        queries.MessagesAsync(id, cursor, limit, ct);

    /// <summary>Accepts a waiting live chat (assigns the chat and its ticket to the caller).</summary>
    [HttpPost("{id:guid}/accept")]
    public async Task<ConversationDetailDto> Accept(Guid id, CancellationToken ct)
    {
        await chat.AcceptAsync(id, ct);
        return await queries.GetAsync(id, ct);
    }

    /// <summary>Hands a live chat to another agent and/or department (tickets.reply; department also tickets.transfer).
    /// 202 when the department transfer of the ticket needs approval (nothing moves); 409 chat_limit_reached for a full agent.</summary>
    [HttpPost("{id:guid}/transfer")]
    public async Task<ConversationDetailDto> Transfer(Guid id, TransferChatRequest request, CancellationToken ct)
    {
        await chat.TransferAsync(id, request, ct);
        return await queries.GetAsync(id, ct);
    }

    /// <summary>The caller's live-chat capacity (active chats vs. MaxConcurrentChats of the routing profile).</summary>
    [HttpGet("chat-capacity")]
    public Task<ChatCapacityDto> ChatCapacity(CancellationToken ct) => chat.CapacityAsync(ct);

    /// <summary>Sends a live-chat message to the visitor (recorded as a public reply on the chat's ticket).</summary>
    [HttpPost("{id:guid}/messages")]
    public Task<ConversationMessageDto> Send(Guid id, AgentChatMessageRequest request, CancellationToken ct) => chat.AgentSendAsync(id, request.Text, request.ClientRequestId, ct);

    [HttpPost("{id:guid}/typing")]
    public async Task<IActionResult> Typing(Guid id, [FromQuery] bool isTyping = true, CancellationToken ct = default)
    {
        await chat.AgentTypingAsync(id, isTyping, ct);
        return NoContent();
    }

    /// <summary>Ends a live chat; the transcript is added to the ticket timeline.</summary>
    [HttpPost("{id:guid}/close")]
    public async Task<ConversationDetailDto> Close(Guid id, [FromBody] CloseConversationRequest? request, CancellationToken ct)
    {
        await chat.CloseAsync(id, request?.Reason, ct);
        return await queries.GetAsync(id, ct);
    }

    /// <summary>Message detail with delivery status and status history.</summary>
    [HttpGet("messages/{messageId:guid}")]
    public Task<MessageDetailDto> Message(Guid messageId, CancellationToken ct) => queries.MessageAsync(messageId, ct);

    /// <summary>Original RFC 822 source of an inbound e-mail (including the quoted history removed from the ticket).</summary>
    [HttpGet("messages/{messageId:guid}/raw")]
    public async Task<IActionResult> Raw(Guid messageId, CancellationToken ct)
    {
        var fileId = await queries.RawFileAsync(messageId, ct);
        var (file, stream) = await files.OpenAsync(fileId, ct);
        return File(stream, "message/rfc822", file.FileName);
    }
}
