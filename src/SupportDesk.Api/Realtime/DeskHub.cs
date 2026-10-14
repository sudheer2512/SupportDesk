using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using SupportDesk.Api.Infrastructure;
using SupportDesk.Application.Common;
using SupportDesk.Application.Identity;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Organisation;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Api.Realtime;

public static class Groups
{
    public static string Tenant(Guid t) => $"tenant:{t:N}";
    public static string User(Guid t, Guid u) => $"user:{t:N}:{u:N}";
    public static string Department(Guid t, Guid d) => $"dept:{t:N}:{d:N}";
    public static string Ticket(Guid t, Guid id) => $"ticket:{t:N}:{id:N}";
    public static string Contact(Guid t, Guid c) => $"contact:{t:N}:{c:N}";
    public static string Conversation(Guid t, Guid c) => $"conversation:{t:N}:{c:N}";
}

public sealed record PresenceEntry(string ConnectionId, Guid UserId, string Name, string Activity, DateTime SeenAt);

/// <summary>
/// Who is viewing / replying to each ticket. Stored in the distributed cache (Redis in production) so presence and
/// collision warnings work across API instances; entries expire if a client stops sending heartbeats.
/// </summary>
public sealed class PresenceStore(IDistributedCache cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static string Key(Guid tenant, Guid ticket) => $"presence:{tenant:N}:{ticket:N}";

    public async Task<List<PresenceEntry>> UpdateAsync(Guid tenant, Guid ticket, Func<List<PresenceEntry>, List<PresenceEntry>> change)
    {
        await Gate.WaitAsync();
        try
        {
            var raw = await cache.GetStringAsync(Key(tenant, ticket));
            var list = Json.DeserializeOr(raw, new List<PresenceEntry>()).Where(e => DateTime.UtcNow - e.SeenAt < Ttl).ToList();
            list = change(list);
            await cache.SetStringAsync(Key(tenant, ticket), Json.Serialize(list), new DistributedCacheEntryOptions { SlidingExpiration = Ttl });
            return list;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static object Viewers(Guid ticket, IEnumerable<PresenceEntry> entries) => new
    {
        ticketId = ticket,
        viewers = entries.GroupBy(e => e.UserId).Select(g =>
        {
            var activity = g.Any(e => e.Activity == "replying") ? "replying" : g.Any(e => e.Activity == "noting") ? "noting" : "viewing";
            return new { userId = g.Key, name = g.First().Name, activity };
        })
    };
}

/// <summary>Populates the scoped RequestContext for every hub invocation from the connection's validated principal.</summary>
public sealed class RequestContextHubFilter : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocation, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        await PopulateAsync(invocation.Context.User, invocation.ServiceProvider);
        return await next(invocation);
    }

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        await PopulateAsync(context.Context.User, context.ServiceProvider);
        await next(context);
    }

    public async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        await PopulateAsync(context.Context.User, context.ServiceProvider);
        await next(context, exception);
    }

    private static async Task PopulateAsync(ClaimsPrincipal? user, IServiceProvider sp)
    {
        if (user is null) return;
        var ctx = sp.GetRequiredService<RequestContext>();
        if (ctx.HasTenant) return;
        var type = user.FindFirstValue(DeskClaims.Type);
        if (!Guid.TryParse(user.FindFirstValue(DeskClaims.Tenant), out var tenant) || !Guid.TryParse(user.FindFirstValue(DeskClaims.Subject), out var subject)) return;
        if (type == DeskClaims.TypeAgent) await sp.GetRequiredService<PrincipalLoader>().ApplyAgentAsync(tenant, subject);
        else if (type == DeskClaims.TypeContact) ctx.SetContact(tenant, subject, user.FindFirstValue(DeskClaims.Name) ?? "Customer");
    }
}

/// <summary>
/// Agent hub (/hubs/desk): live ticket changes, conversation updates, presence and collision indicators,
/// notifications, SLA alerts and agent availability. Group membership is derived from the server-side principal.
/// </summary>
[Authorize(Policy = Policies.Agent)]
public sealed class DeskHub(RequestContext ctx, DeskDbContext db, TicketService tickets, PresenceStore presence) : Hub
{
    public override async Task OnConnectedAsync()
    {
        if (!ctx.HasTenant || ctx.UserId is not { } uid) { Context.Abort(); return; }
        await Groups.AddToGroupAsync(Context.ConnectionId, Realtime.Groups.Tenant(ctx.TenantId));
        await Groups.AddToGroupAsync(Context.ConnectionId, Realtime.Groups.User(ctx.TenantId, uid));
        var departments = ctx.TicketScope == TicketScope.All
            ? await db.Departments.AsNoTracking().Select(d => d.Id).ToListAsync()
            : ctx.DepartmentIds.ToList();
        foreach (var d in departments) await Groups.AddToGroupAsync(Context.ConnectionId, Realtime.Groups.Department(ctx.TenantId, d));
        Context.Items["tickets"] = new HashSet<Guid>();
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (ctx.HasTenant && Context.Items["tickets"] is HashSet<Guid> watched)
            foreach (var ticketId in watched.ToList()) await LeaveTicket(ticketId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Subscribes to a ticket's live updates (after an access check) and announces presence.</summary>
    public async Task JoinTicket(Guid ticketId)
    {
        var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ticketId) ?? throw new HubException("Ticket not found.");
        try { await tickets.EnsureVisibleAsync(ticket, default); }
        catch (DomainException) { throw new HubException("Ticket not found."); }
        await Groups.AddToGroupAsync(Context.ConnectionId, Realtime.Groups.Ticket(ctx.TenantId, ticketId));
        if (Context.Items["tickets"] is HashSet<Guid> watched) watched.Add(ticketId);
        await SetActivity(ticketId, "viewing");
    }

    public async Task LeaveTicket(Guid ticketId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, Realtime.Groups.Ticket(ctx.TenantId, ticketId));
        if (Context.Items["tickets"] is HashSet<Guid> watched) watched.Remove(ticketId);
        var list = await presence.UpdateAsync(ctx.TenantId, ticketId, l => l.Where(e => e.ConnectionId != Context.ConnectionId).ToList());
        await Clients.Group(Realtime.Groups.Ticket(ctx.TenantId, ticketId)).SendAsync("ticket.presence", PresenceStore.Viewers(ticketId, list));
    }

    /// <summary>viewing | replying | noting — other viewers see who is composing (collision detection).</summary>
    public async Task SetActivity(Guid ticketId, string activity)
    {
        if (Context.Items["tickets"] is not HashSet<Guid> watched || !watched.Contains(ticketId)) return;
        var a = activity is "replying" or "noting" ? activity : "viewing";
        var entry = new PresenceEntry(Context.ConnectionId, ctx.UserId!.Value, ctx.ActorName ?? "Agent", a, DateTime.UtcNow);
        var list = await presence.UpdateAsync(ctx.TenantId, ticketId, l => [.. l.Where(e => e.ConnectionId != Context.ConnectionId), entry]);
        await Clients.Group(Realtime.Groups.Ticket(ctx.TenantId, ticketId)).SendAsync("ticket.presence", PresenceStore.Viewers(ticketId, list));
    }

    public async Task SetAvailability(string availability)
    {
        if (!Enum.TryParse<AgentAvailability>(availability, true, out var value)) throw new HubException("Unknown availability.");
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.UserId == ctx.UserId);
        if (agent is null) return;
        agent.Availability = value;
        agent.AvailabilityChangedAt = db.Clock.UtcNow;
        await db.SaveChangesAsync();
        await Clients.Group(Realtime.Groups.Tenant(ctx.TenantId)).SendAsync("agent.availability", new { userId = ctx.UserId, availability = value.ToString() });
    }
}

/// <summary>Customer hub (/hubs/portal): live updates for the contact's own tickets and conversations.</summary>
[Authorize(Policy = Policies.Contact)]
public sealed class PortalHub(RequestContext ctx) : Hub
{
    public override async Task OnConnectedAsync()
    {
        if (!ctx.HasTenant || ctx.ContactId is not { } cid) { Context.Abort(); return; }
        await Groups.AddToGroupAsync(Context.ConnectionId, Realtime.Groups.Contact(ctx.TenantId, cid));
        await base.OnConnectedAsync();
    }
}

/// <summary>SignalR implementation of the application's realtime port.</summary>
public sealed class SignalRRealtimePublisher(IHubContext<DeskHub> desk, IHubContext<PortalHub> portal) : IRealtimePublisher
{
    public Task ToTenantAsync(Guid tenantId, string eventName, object payload, CancellationToken ct = default) =>
        desk.Clients.Group(Groups.Tenant(tenantId)).SendAsync(eventName, payload, ct);

    public Task ToUsersAsync(Guid tenantId, IEnumerable<Guid> userIds, string eventName, object payload, CancellationToken ct = default) =>
        desk.Clients.Groups(userIds.Select(u => Groups.User(tenantId, u)).ToList()).SendAsync(eventName, payload, ct);

    public Task ToTicketAsync(Guid tenantId, Guid ticketId, string eventName, object payload, CancellationToken ct = default) =>
        desk.Clients.Group(Groups.Ticket(tenantId, ticketId)).SendAsync(eventName, payload, ct);

    public Task ToDepartmentsAsync(Guid tenantId, IEnumerable<Guid> departmentIds, string eventName, object payload, CancellationToken ct = default) =>
        desk.Clients.Groups(departmentIds.Select(d => Groups.Department(tenantId, d)).ToList()).SendAsync(eventName, payload, ct);

    public Task ToContactAsync(Guid tenantId, Guid contactId, string eventName, object payload, CancellationToken ct = default) =>
        portal.Clients.Group(Groups.Contact(tenantId, contactId)).SendAsync(eventName, payload, ct);

    public Task ToConversationAsync(Guid tenantId, Guid conversationId, string eventName, object payload, CancellationToken ct = default) =>
        Task.WhenAll(desk.Clients.Group(Groups.Conversation(tenantId, conversationId)).SendAsync(eventName, payload, ct),
            portal.Clients.Group(Groups.Conversation(tenantId, conversationId)).SendAsync(eventName, payload, ct));
}
