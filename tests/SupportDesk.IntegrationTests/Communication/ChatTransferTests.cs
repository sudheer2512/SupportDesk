using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Communication;
using SupportDesk.Application.Notifications;
using SupportDesk.Contracts.Communication;
using SupportDesk.Contracts.Tickets;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.Persistence;

namespace SupportDesk.IntegrationTests.Communication;

/// <summary>Live chat: transfer to an agent or department, MaxConcurrentChats enforcement and chat deep links.</summary>
[Collection("api")]
public sealed class ChatTransferTests(DeskApiFactory factory)
{
    private static readonly JsonSerializerOptions J = DeskApiFactory.Json;

    private static async Task<T> Ok<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(J))!;
    }

    private static async Task<string> WidgetAsync(HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/channels", new CreateChannelAccountRequest
        {
            Type = "Chat", Provider = ChannelProviders.Widget, Name = "Website chat", Settings = JsonSerializer.SerializeToElement(new { botEnabled = true })
        }, J);
        return (await Ok<ChannelAccountDto>(response)).Address;
    }

    /// <summary>Starts a chat as an anonymous visitor; with no assistant configured it waits for an agent at once.</summary>
    private async Task<Guid> StartChatAsync(string widget, string name)
    {
        var visitor = factory.CreateClient();
        var session = await Ok<ChatSessionDto>(await visitor.PostAsJsonAsync($"/api/v1/chat/{widget}/sessions", new StartChatRequest { Name = name, Message = $"{name} needs help" }, J));
        Assert.Equal("WaitingForAgent", session.Status);
        return session.ConversationId;
    }

    private Task SetChatLimitAsync(TestTenant t, Guid userId, int limit) => factory.InTenantAsync(t.Id, async sp =>
    {
        var db = sp.GetRequiredService<DeskDbContext>();
        var agent = await db.Agents.FirstAsync(a => a.UserId == userId);
        agent.MaxConcurrentChats = limit;
        await db.SaveChangesAsync();
    });

    private Task<T> Db<T>(TestTenant t, Func<DeskDbContext, Task<T>> query) => factory.InTenantAsync(t.Id, sp => query(sp.GetRequiredService<DeskDbContext>()));

    [Fact]
    public async Task Accepting_beyond_the_chat_limit_is_refused_with_chat_limit_reached()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var widget = await WidgetAsync(admin);
        var (agentId, agentEmail) = await t.CreateAgentAsync("Chat Agent");
        await SetChatLimitAsync(t, agentId, 1);
        var agent = await t.AgentClientAsync(agentEmail);
        var first = await StartChatAsync(widget, "First visitor");
        var second = await StartChatAsync(widget, "Second visitor");

        var before = await agent.GetFromJsonAsync<ChatCapacityDto>("/api/v1/conversations/chat-capacity", J);
        Assert.Equal(new ChatCapacityDto(0, 1, false), before);
        Assert.Equal(HttpStatusCode.OK, (await agent.PostAsync($"/api/v1/conversations/{first}/accept", null)).StatusCode);
        var full = await agent.GetFromJsonAsync<ChatCapacityDto>("/api/v1/conversations/chat-capacity", J);
        Assert.Equal(new ChatCapacityDto(1, 1, true), full);

        var refused = await agent.PostAsync($"/api/v1/conversations/{second}/accept", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains(ChatService.ChatLimitReached, await refused.Content.ReadAsStringAsync());
        // Replying to the waiting chat would take it over: the same limit applies.
        Assert.Equal(HttpStatusCode.Conflict,
            (await agent.PostAsJsonAsync($"/api/v1/conversations/{second}/messages", new AgentChatMessageRequest("Hello?"), J)).StatusCode);

        // Ending a chat frees a slot.
        Assert.Equal(HttpStatusCode.OK, (await agent.PostAsJsonAsync($"/api/v1/conversations/{first}/close", new CloseConversationRequest(), J)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await agent.PostAsync($"/api/v1/conversations/{second}/accept", null)).StatusCode);
    }

    [Fact]
    public async Task Chat_transfers_to_another_agent_with_its_ticket_a_system_line_and_a_notification()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var widget = await WidgetAsync(admin);
        var (_, firstEmail) = await t.CreateAgentAsync("First Agent");
        var (secondId, _) = await t.CreateAgentAsync("Second Agent");
        var (fullId, _) = await t.CreateAgentAsync("Busy Agent");
        await SetChatLimitAsync(t, fullId, 0);
        var first = await t.AgentClientAsync(firstEmail);
        var chat = await StartChatAsync(widget, "Transfer visitor");
        Assert.Equal(HttpStatusCode.OK, (await first.PostAsync($"/api/v1/conversations/{chat}/accept", null)).StatusCode);

        // Chat-waiting notifications open the chat console, not a non-existent /conversations route.
        var waitingLinks = await Db(t, db => db.Notifications.Where(n => n.Type == NotificationTypes.ChatWaiting && n.EntityId == chat).Select(n => n.Link).ToListAsync());
        Assert.NotEmpty(waitingLinks);
        Assert.All(waitingLinks, l => Assert.Equal($"/inbox/chat?c={chat}", l));

        var refused = await first.PostAsJsonAsync($"/api/v1/conversations/{chat}/transfer", new TransferChatRequest(fullId, null), J);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains(ChatService.ChatLimitReached, await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await first.PostAsJsonAsync($"/api/v1/conversations/{chat}/transfer", new TransferChatRequest(null, null), J)).StatusCode);

        var moved = await Ok<ConversationDetailDto>(await first.PostAsJsonAsync($"/api/v1/conversations/{chat}/transfer",
            new TransferChatRequest(secondId, null, "Needs the GST specialist"), J));
        Assert.Equal("AgentActive", moved.Summary.Status);
        Assert.Equal(secondId, moved.Summary.AssignedUserId);
        Assert.Contains(moved.Messages, m => m.Author == "system" && m.BodyText == "Transferred to Second Agent.");

        var ticket = await admin.GetFromJsonAsync<TicketDetailDto>($"/api/v1/tickets/{moved.Summary.TicketId}", J);
        Assert.Equal(secondId, ticket!.AssigneeId);
        var notice = await Db(t, db => db.Notifications.SingleAsync(n => n.UserId == secondId && n.Type == NotificationTypes.ChatTransferred));
        Assert.Equal($"/inbox/chat?c={chat}", notice.Link);
        Assert.Contains("Needs the GST specialist", notice.Body);

        // The first agent no longer owns the chat: only the owner or a supervisor may transfer it again.
        Assert.Equal(HttpStatusCode.Forbidden, (await first.PostAsJsonAsync($"/api/v1/conversations/{chat}/transfer", new TransferChatRequest(fullId, null), J)).StatusCode);
    }

    [Fact]
    public async Task Department_transfer_needs_tickets_transfer_and_moves_the_ticket()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var widget = await WidgetAsync(admin);
        var (_, agentEmail) = await t.CreateAgentAsync("Plain Agent");
        var (_, supervisorEmail) = await t.CreateAgentAsync("Chat Supervisor", "Supervisor", t.SupportDepartmentId, t.BillingDepartmentId);
        await t.CreateAgentAsync("Billing Agent", "Agent", t.BillingDepartmentId);
        var agent = await t.AgentClientAsync(agentEmail);
        var supervisor = await t.AgentClientAsync(supervisorEmail);
        var chat = await StartChatAsync(widget, "Billing question");
        Assert.Equal(HttpStatusCode.OK, (await agent.PostAsync($"/api/v1/conversations/{chat}/accept", null)).StatusCode);

        // Agents lack tickets.transfer.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PostAsJsonAsync($"/api/v1/conversations/{chat}/transfer", new TransferChatRequest(null, t.BillingDepartmentId), J)).StatusCode);

        var moved = await Ok<ConversationDetailDto>(await supervisor.PostAsJsonAsync($"/api/v1/conversations/{chat}/transfer",
            new TransferChatRequest(null, t.BillingDepartmentId, "Invoice dispute"), J));
        Assert.Equal("WaitingForAgent", moved.Summary.Status);
        Assert.Null(moved.Summary.AssignedUserId);
        Assert.Contains(moved.Messages, m => m.Author == "system" && m.BodyText == "Transferred to Billing.");
        var ticket = await admin.GetFromJsonAsync<TicketDetailDto>($"/api/v1/tickets/{moved.Summary.TicketId}", J);
        Assert.Equal(t.BillingDepartmentId, ticket!.DepartmentId);
        var timeline = await admin.GetFromJsonAsync<List<TimelineItemDto>>($"/api/v1/tickets/{ticket.Id}/timeline", J);
        Assert.Contains(timeline!, i => i.Event?.Type == "Transferred");
    }
}
