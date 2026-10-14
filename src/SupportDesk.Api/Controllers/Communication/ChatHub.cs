using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SupportDesk.Application.Communication;
using SupportDesk.Domain;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Api.Controllers.Communication;

/// <summary>
/// Live-chat hub (/hubs/chat). Visitors connect anonymously with ?conversation={id}&amp;visitor_token={token} (validated on
/// connect and on every call) and join only their conversation. Agents connect with their access token and join
/// conversations after the same visibility checks as the REST API. Events: chat.message, chat.status, chat.typing.
/// </summary>
[AllowAnonymous]
public sealed class ChatHub(RequestContext ctx, ChatService chat) : Hub
{
    public static string Group(Guid tenantId, Guid conversationId) => $"chat:{tenantId:N}:{conversationId:N}";

    public override async Task OnConnectedAsync()
    {
        if (ctx.IsAgent || (ctx.HasTenant && ctx.UserId is not null))
        {
            Context.Items["agent"] = true;
            await base.OnConnectedAsync();
            return;
        }
        var http = Context.GetHttpContext();
        var token = http?.Request.Query["visitor_token"].ToString();
        if (!Guid.TryParse(http?.Request.Query["conversation"].ToString(), out var conversationId) || string.IsNullOrEmpty(token))
        {
            Context.Abort();
            return;
        }
        try
        {
            var conv = await chat.AuthenticateVisitorAsync(conversationId, token, Context.ConnectionAborted);
            Context.Items["conversation"] = conv.Id;
            Context.Items["token"] = token;
            await Groups.AddToGroupAsync(Context.ConnectionId, Group(ctx.TenantId, conv.Id));
            await base.OnConnectedAsync();
        }
        catch (DomainException)
        {
            Context.Abort();
        }
    }

    // ───── Visitor ─────

    public async Task<object> Send(string text) => await Guard(async () =>
    {
        var (id, token) = Visitor();
        return await chat.VisitorMessageAsync(id, token, text, Context.ConnectionAborted);
    });

    public async Task Typing(bool isTyping) => await Guard(async () =>
    {
        var (id, token) = Visitor();
        await chat.VisitorTypingAsync(id, token, isTyping, Context.ConnectionAborted);
        return 0;
    });

    // ───── Agents ─────

    public async Task JoinConversation(Guid conversationId) => await Guard(async () =>
    {
        RequireAgent();
        var conv = await chat.LoadAgentConversationAsync(conversationId, Context.ConnectionAborted);
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(ctx.TenantId, conv.Id));
        return 0;
    });

    public async Task LeaveConversation(Guid conversationId)
    {
        RequireAgent();
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(ctx.TenantId, conversationId));
    }

    public async Task<object> AgentSend(Guid conversationId, string text) => await Guard(async () =>
        await chat.AgentSendAsync(conversationId, text, null, Context.ConnectionAborted));

    public async Task AgentTyping(Guid conversationId, bool isTyping) => await Guard(async () =>
    {
        await chat.AgentTypingAsync(conversationId, isTyping, Context.ConnectionAborted);
        return 0;
    });

    private (Guid Id, string Token) Visitor()
    {
        if (Context.Items.TryGetValue("conversation", out var c) && c is Guid id && Context.Items.TryGetValue("token", out var t) && t is string token) return (id, token);
        throw new HubException("This connection is not a chat visitor session.");
    }

    private void RequireAgent()
    {
        if (!Context.Items.ContainsKey("agent") || !ctx.HasTenant) throw new HubException("Agent access required.");
    }

    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (DomainException ex) { throw new HubException(ex.Message); }
    }
}

/// <summary>SignalR implementation of the chat realtime port (visitors and agents in the conversation group).</summary>
public sealed class SignalRChatRealtime(IHubContext<ChatHub> hub) : IChatRealtime
{
    public Task MessageAsync(Guid tenantId, Guid conversationId, object message, CancellationToken ct = default) =>
        hub.Clients.Group(ChatHub.Group(tenantId, conversationId)).SendAsync("chat.message", message, conversationId, ct);

    public Task StatusAsync(Guid tenantId, Guid conversationId, object status, CancellationToken ct = default) =>
        hub.Clients.Group(ChatHub.Group(tenantId, conversationId)).SendAsync("chat.status", status, ct);

    public Task TypingAsync(Guid tenantId, Guid conversationId, object typing, CancellationToken ct = default) =>
        hub.Clients.Group(ChatHub.Group(tenantId, conversationId)).SendAsync("chat.typing", typing, ct);
}
