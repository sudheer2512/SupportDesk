using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Integrations.Engineering;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Engineering;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain.Integration;
using SupportDesk.Domain.Tickets;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.Persistence;
using static SupportDesk.IntegrationTests.Automation.AutomationApi;

namespace SupportDesk.IntegrationTests.Engineering;

internal static class EngineeringKit
{
    public static readonly JsonSerializerOptions J = DeskApiFactory.Json;

    public static async Task<T> Ok<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(J))!;
    }

    public static async Task<EngineeringTrackerDto> LocalTrackerAsync(HttpClient admin) =>
        await Ok<EngineeringTrackerDto>(await admin.PostAsJsonAsync("/api/v1/engineering/admin/trackers", new SaveEngineeringTrackerRequest { Provider = "Local" }, J));

    public static async Task<EngineeringSettingsDto> SettingsAsync(HttpClient admin, bool notifyCustomer, Guid? escalationStatusId = null, bool substatus = true) =>
        await Ok<EngineeringSettingsDto>(await admin.PutAsJsonAsync("/api/v1/engineering/admin/settings",
            new SaveEngineeringSettingsRequest(true, true, true, notifyCustomer, null, substatus, escalationStatusId, null), J));

    public static async Task<EscalationResultDto> EscalateAsync(HttpClient client, Guid ticketId, Guid trackerId, string title = "Export times out", bool substatus = true) =>
        await Ok<EscalationResultDto>(await client.PostAsJsonAsync($"/api/v1/engineering/tickets/{ticketId}/escalate", new EscalateToEngineeringRequest
        {
            TrackerId = trackerId, ItemType = "Bug", Title = title, ReproStepsHtml = "<p>1. Export the Sales Register for a full year.</p>", Severity = "Major",
            SetSubstatus = substatus
        }, J));

    public static async Task<TicketEngineeringDto> PanelAsync(HttpClient client, Guid ticketId) =>
        (await client.GetFromJsonAsync<TicketEngineeringDto>($"/api/v1/engineering/tickets/{ticketId}", J))!;

    public static async Task<Guid> WorkItemIdAsync(DeskApiFactory factory, Guid tenantId, Guid trackerId, string externalId) =>
        await factory.InTenantAsync(tenantId, sp => sp.GetRequiredService<DeskDbContext>().Set<LocalWorkItem>().AsNoTracking()
            .Where(w => w.IntegrationId == trackerId && w.Number == int.Parse(externalId)).Select(w => w.Id).FirstAsync());

    public static string GitHubSignature(string secret, byte[] body) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();

    /// <summary>Links ticket → external item directly (no tracker call), as if escalated earlier.</summary>
    public static Task SeedExternalLinkAsync(DeskApiFactory factory, Guid tenantId, Guid trackerId, EngineeringProvider provider, Guid ticketId, string externalId, string title,
        string state) =>
        factory.InTenantAsync(tenantId, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            db.Set<EngineeringItem>().Add(new EngineeringItem
            {
                IntegrationId = trackerId, Provider = provider, ExternalId = externalId, Title = title, State = state, Url = $"https://example.test/items/{externalId}",
                LastSyncedAt = DateTime.UtcNow
            });
            db.EngineeringLinks.Add(new EngineeringLink
            {
                TicketId = ticketId, IntegrationId = trackerId, Provider = provider, ExternalId = externalId, Title = title, State = state, ItemType = EngineeringItemType.Bug,
                Url = $"https://example.test/items/{externalId}"
            });
            await db.SaveChangesAsync();
        });
}

[Collection("api")]
public sealed class EngineeringTests(DeskApiFactory factory)
{
    private static readonly JsonSerializerOptions J = EngineeringKit.J;

    private async Task<Guid> StatusIdAsync(Guid tenantId, string name) =>
        await factory.InTenantAsync(tenantId, sp => sp.GetRequiredService<DeskDbContext>().TicketStatuses.Where(s => s.Name == name).Select(s => s.Id).FirstAsync());

    [Fact]
    public async Task Escalation_creates_a_local_work_item_link_timeline_entry_and_engineering_substatus()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var tracker = await EngineeringKit.LocalTrackerAsync(admin);
        var waitingThirdParty = await StatusIdAsync(t.Id, "Waiting on Third Party");
        await EngineeringKit.SettingsAsync(admin, notifyCustomer: false, escalationStatusId: waitingThirdParty);
        var ticket = await CreateTicketAsync(admin, "Sales Register export fails");

        var prefill = await admin.GetFromJsonAsync<EscalationPrefillDto>($"/api/v1/engineering/tickets/{ticket.Id}/prefill", J);
        Assert.Equal("Sales Register export fails", prefill!.Title);
        Assert.Contains("Problem reported", prefill.ReproStepsHtml);
        Assert.Equal(1, prefill.CustomerImpactCount);

        var result = await EngineeringKit.EscalateAsync(admin, ticket.Id, tracker.Id);
        Assert.True(result.SubstatusApplied, result.Message);
        Assert.Equal("Local", result.Link.Provider);
        Assert.StartsWith("ENG-", result.Link.DisplayId);
        Assert.Equal("New", result.Link.State);
        Assert.Equal(1, result.Link.TicketCount);

        var after = await GetTicketAsync(admin, ticket.Id);
        Assert.Equal(waitingThirdParty, after.StatusId);
        Assert.Equal("Engineering", after.Substatus);

        var (events, workItem) = await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var ev = await db.TicketEvents.Where(e => e.TicketId == ticket.Id && e.Type == "EngineeringLinked").ToListAsync();
            var w = await db.Set<LocalWorkItem>().FirstAsync(x => x.IntegrationId == tracker.Id);
            return (ev, w);
        });
        var linked = Assert.Single(events);
        Assert.Contains(result.Link.DisplayId, linked.Summary);
        Assert.Equal("Export times out", workItem.Title);
        Assert.Contains("Export the Sales Register", workItem.DescriptionHtml);

        var panel = await EngineeringKit.PanelAsync(admin, ticket.Id);
        Assert.Single(panel.Links);
        Assert.True(panel.CanLink);
        Assert.Contains(panel.Trackers, x => x.Id == tracker.Id && x.SupportsAssignee);

        // The same item cannot be linked twice to one ticket.
        var again = await admin.PostAsJsonAsync($"/api/v1/engineering/tickets/{ticket.Id}/links", new LinkWorkItemRequest { TrackerId = tracker.Id, Reference = result.Link.DisplayId }, J);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Release_notifies_the_customers_of_every_linked_ticket_when_enabled()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var tracker = await EngineeringKit.LocalTrackerAsync(admin);
        await EngineeringKit.SettingsAsync(admin, notifyCustomer: true, substatus: false);
        var first = await CreateTicketAsync(admin, "Export hangs (customer 1)");
        var second = await CreateTicketAsync(admin, "Export hangs (customer 2)");
        var escalated = await EngineeringKit.EscalateAsync(admin, first.Id, tracker.Id, substatus: false);
        var linked = await EngineeringKit.Ok<EscalationResultDto>(await admin.PostAsJsonAsync($"/api/v1/engineering/tickets/{second.Id}/links",
            new LinkWorkItemRequest { TrackerId = tracker.Id, Reference = escalated.Link.DisplayId, SetSubstatus = false }, J));
        Assert.Equal(2, linked.Link.TicketCount);

        // "Other tickets affected" from the first ticket shows the second.
        var affected = await admin.GetFromJsonAsync<EngineeringAffectedTicketsDto>($"/api/v1/engineering/tickets/{first.Id}/links/{escalated.Link.Id}/tickets", J);
        Assert.Equal(second.Id, Assert.Single(affected!.Tickets).Id);

        var release = await EngineeringKit.Ok<EngineeringReleaseDto>(await admin.PostAsJsonAsync("/api/v1/engineering/releases", new SaveEngineeringReleaseRequest { TrackerId = tracker.Id, Name = "9.9.1" }, J));
        var itemId = await EngineeringKit.WorkItemIdAsync(factory, t.Id, tracker.Id, escalated.Link.ExternalId);
        var detail = (await admin.GetFromJsonAsync<EngineeringWorkItemDetailDto>($"/api/v1/engineering/work-items/{itemId}", J))!;
        Assert.Equal(2, detail.Tickets.Total);
        var updated = await EngineeringKit.Ok<EngineeringWorkItemDto>(await admin.PutAsJsonAsync($"/api/v1/engineering/work-items/{itemId}", new SaveEngineeringWorkItemRequest
        {
            Title = detail.Item.Title, Type = "Bug", Priority = "High", ReleaseId = release.Id, RowVersion = detail.Item.RowVersion
        }, J));
        var resolved = await EngineeringKit.Ok<EngineeringWorkItemDto>(await admin.PostAsJsonAsync($"/api/v1/engineering/work-items/{itemId}/move",
            new MoveEngineeringWorkItemRequest("Resolved", null, updated.RowVersion), J));
        Assert.Equal("Resolved", resolved.State);
        await factory.DrainOutboxAsync();

        var published = await EngineeringKit.Ok<EngineeringReleaseDto>(await admin.PostAsJsonAsync($"/api/v1/engineering/releases/{release.Id}/publish", new PublishEngineeringReleaseRequest(), J));
        Assert.NotNull(published.ReleasedAt);
        await factory.DrainOutboxAsync();

        foreach (var ticketId in new[] { first.Id, second.Id })
        {
            var (replies, notes, link) = await factory.InTenantAsync(t.Id, async sp =>
            {
                var db = sp.GetRequiredService<DeskDbContext>();
                var r = await db.TicketThreads.Where(x => x.TicketId == ticketId && x.Kind == ThreadKind.AgentReply).ToListAsync();
                var n = await db.TicketThreads.Where(x => x.TicketId == ticketId && x.Kind == ThreadKind.InternalNote).ToListAsync();
                var l = await db.EngineeringLinks.FirstAsync(x => x.TicketId == ticketId);
                return (r, n, l);
            });
            var reply = Assert.Single(replies);
            Assert.Contains("9.9.1", reply.BodyText);
            Assert.Equal(Domain.Visibility.Public, reply.Visibility);
            Assert.Contains(notes, n => n.BodyText!.Contains("resolved"));
            Assert.Contains(notes, n => n.BodyText!.Contains("released") && n.BodyText.Contains("9.9.1"));
            Assert.True(link.CustomerNotified);
            Assert.Equal("9.9.1", link.FixVersion);
        }

        // Processing the same events again (redelivery) does not reply twice.
        await factory.DrainOutboxAsync();
        var replyCount = await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().TicketThreads
            .CountAsync(x => (x.TicketId == first.Id || x.TicketId == second.Id) && x.Kind == ThreadKind.AgentReply));
        Assert.Equal(2, replyCount);
        var panel = await EngineeringKit.PanelAsync(admin, first.Id);
        Assert.True(Assert.Single(panel.Links).IsReleased);
    }

    [Fact]
    public async Task Release_does_not_reply_to_customers_when_auto_notify_is_off()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var tracker = await EngineeringKit.LocalTrackerAsync(admin);
        await EngineeringKit.SettingsAsync(admin, notifyCustomer: false, substatus: false);
        var ticket = await CreateTicketAsync(admin, "Quiet release");
        var escalated = await EngineeringKit.EscalateAsync(admin, ticket.Id, tracker.Id, substatus: false);
        var release = await EngineeringKit.Ok<EngineeringReleaseDto>(await admin.PostAsJsonAsync("/api/v1/engineering/releases", new SaveEngineeringReleaseRequest { TrackerId = tracker.Id, Name = "1.0.0" }, J));
        var itemId = await EngineeringKit.WorkItemIdAsync(factory, t.Id, tracker.Id, escalated.Link.ExternalId);
        var item = (await admin.GetFromJsonAsync<EngineeringWorkItemDetailDto>($"/api/v1/engineering/work-items/{itemId}", J))!.Item;
        await EngineeringKit.Ok<EngineeringWorkItemDto>(await admin.PutAsJsonAsync($"/api/v1/engineering/work-items/{itemId}",
            new SaveEngineeringWorkItemRequest { Title = item.Title, ReleaseId = release.Id, RowVersion = item.RowVersion }, J));
        await EngineeringKit.Ok<EngineeringReleaseDto>(await admin.PostAsJsonAsync($"/api/v1/engineering/releases/{release.Id}/publish", new PublishEngineeringReleaseRequest(), J));
        await factory.DrainOutboxAsync();

        var (replies, notes) = await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            return (await db.TicketThreads.CountAsync(x => x.TicketId == ticket.Id && x.Kind == ThreadKind.AgentReply),
                await db.TicketThreads.Where(x => x.TicketId == ticket.Id && x.Kind == ThreadKind.InternalNote).Select(x => x.BodyText).ToListAsync());
        });
        Assert.Equal(0, replies);
        Assert.Contains(notes, n => n!.Contains("1.0.0") && n.Contains("Let the customer know"));
    }

    [Fact]
    public async Task Deleted_work_item_orphans_the_link_and_keeps_the_last_known_data()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var tracker = await EngineeringKit.LocalTrackerAsync(admin);
        var ticket = await CreateTicketAsync(admin, "Orphan me");
        var escalated = await EngineeringKit.EscalateAsync(admin, ticket.Id, tracker.Id, "Crash on save", substatus: false);
        var itemId = await EngineeringKit.WorkItemIdAsync(factory, t.Id, tracker.Id, escalated.Link.ExternalId);
        var moved = await EngineeringKit.Ok<EngineeringWorkItemDto>(await admin.PostAsJsonAsync($"/api/v1/engineering/work-items/{itemId}/move", new MoveEngineeringWorkItemRequest("Active"), J));
        Assert.Equal("Active", moved.State);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/engineering/work-items/{itemId}")).StatusCode);
        await factory.DrainOutboxAsync();

        var link = Assert.Single((await EngineeringKit.PanelAsync(admin, ticket.Id)).Links);
        Assert.True(link.IsOrphaned);
        Assert.Equal("deleted", link.OrphanReason);
        Assert.Equal("Crash on save", link.Title);
        Assert.Equal("Active", link.State);
        var notes = await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().TicketThreads
            .Where(x => x.TicketId == ticket.Id && x.Kind == ThreadKind.InternalNote).Select(x => x.BodyText).ToListAsync());
        Assert.Contains(notes, n => n!.Contains("deleted or moved"));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/engineering/work-items/{itemId}")).StatusCode);
    }

    [Fact]
    public async Task GitHub_webhooks_require_a_valid_signature_and_redeliveries_are_idempotent()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var tracker = await EngineeringKit.Ok<EngineeringTrackerDto>(await admin.PostAsJsonAsync("/api/v1/engineering/admin/trackers", new SaveEngineeringTrackerRequest
        {
            Provider = "GitHub", Owner = "acme", Repository = "erp", AuthType = "Token", Token = "ghp_testtoken"
        }, J));
        Assert.False(string.IsNullOrEmpty(tracker.WebhookSecret));
        Assert.EndsWith($"/api/v1/engineering/hooks/github/{tracker.Id}", tracker.WebhookUrl);
        // The secret is shown once only.
        Assert.Null((await admin.GetFromJsonAsync<EngineeringTrackerDto>($"/api/v1/engineering/admin/trackers/{tracker.Id}", J))!.WebhookSecret);

        var ticket = await CreateTicketAsync(admin, "GitHub linked ticket");
        await EngineeringKit.SeedExternalLinkAsync(factory, t.Id, tracker.Id, EngineeringProvider.GitHub, ticket.Id, "12", "Export bug", "open");

        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            action = "closed",
            issue = new { number = 12, title = "Export bug", state = "closed", state_reason = "completed", html_url = "https://github.com/acme/erp/issues/12",
                labels = new[] { new { name = "bug" } }, milestone = new { title = "v8.0.1" }, updated_at = "2026-10-14T08:00:00Z" },
            repository = new { full_name = "acme/erp" }
        });
        var anon = factory.CreateClient();
        async Task<HttpResponseMessage> PostAsync(string delivery, string signature)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/engineering/hooks/github/{tracker.Id}") { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            request.Headers.Add("X-GitHub-Event", "issues");
            request.Headers.Add("X-GitHub-Delivery", delivery);
            request.Headers.Add("X-Hub-Signature-256", signature);
            return await anon.SendAsync(request);
        }

        var delivery = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(delivery, EngineeringKit.GitHubSignature("wrong-secret", body))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(delivery, "sha256=00")).StatusCode);

        var signature = EngineeringKit.GitHubSignature(tracker.WebhookSecret!, body);
        var first = await PostAsync(delivery, signature);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("processed", await first.Content.ReadAsStringAsync());
        var redelivered = await PostAsync(delivery, signature);
        Assert.Equal(HttpStatusCode.OK, redelivered.StatusCode);
        Assert.Contains("duplicate", await redelivered.Content.ReadAsStringAsync());
        await factory.DrainOutboxAsync();

        var link = Assert.Single((await EngineeringKit.PanelAsync(admin, ticket.Id)).Links);
        Assert.Equal("closed", link.State);
        Assert.True(link.IsResolved);
        Assert.Equal("v8.0.1", link.FixVersion);
        var (updates, notes, deliveries) = await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            return (await db.TicketEvents.CountAsync(e => e.TicketId == ticket.Id && e.Type == "EngineeringUpdated"),
                await db.TicketThreads.CountAsync(x => x.TicketId == ticket.Id && x.Kind == ThreadKind.InternalNote),
                await db.Set<EngineeringWebhookDelivery>().CountAsync(d => d.IntegrationId == tracker.Id));
        });
        Assert.Equal(1, updates);
        Assert.Equal(1, notes);
        Assert.Equal(1, deliveries);

        // A GitHub release whose tag matches the milestone marks the fix as released.
        var releaseBody = JsonSerializer.SerializeToUtf8Bytes(new
        {
            action = "published", release = new { tag_name = "v8.0.1", name = "8.0.1", draft = false, published_at = "2026-10-14T09:00:00Z" }, repository = new { full_name = "acme/erp" }
        });
        var releaseRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/engineering/hooks/github/{tracker.Id}") { Content = new ByteArrayContent(releaseBody) };
        releaseRequest.Headers.Add("X-GitHub-Event", "release");
        releaseRequest.Headers.Add("X-GitHub-Delivery", Guid.NewGuid().ToString());
        releaseRequest.Headers.Add("X-Hub-Signature-256", EngineeringKit.GitHubSignature(tracker.WebhookSecret!, releaseBody));
        Assert.Equal(HttpStatusCode.OK, (await anon.SendAsync(releaseRequest)).StatusCode);
        Assert.True(Assert.Single((await EngineeringKit.PanelAsync(admin, ticket.Id)).Links).IsReleased);
    }

    [Fact]
    public async Task Azure_DevOps_service_hook_checks_the_secret_and_orphans_deleted_items()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var tracker = await EngineeringKit.Ok<EngineeringTrackerDto>(await admin.PostAsJsonAsync("/api/v1/engineering/admin/trackers", new SaveEngineeringTrackerRequest
        {
            Provider = "AzureDevOps", BaseUrl = "https://tfs.example.test/tfs/DefaultCollection", Project = "ERP", AuthType = "Pat", Token = "pat-token"
        }, J));
        var ticket = await CreateTicketAsync(admin, "ADO linked ticket");
        await EngineeringKit.SeedExternalLinkAsync(factory, t.Id, tracker.Id, EngineeringProvider.AzureDevOps, ticket.Id, "4711", "Payroll PF rounding", "Active");

        var anon = factory.CreateClient();
        async Task<HttpResponseMessage> SendAsync(object payload, string? secret, bool basic = false)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/engineering/hooks/azure-devops/{tracker.Id}") { Content = JsonContent.Create(payload) };
            if (secret is not null)
            {
                if (basic) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"ado:{secret}")));
                else request.Headers.Add("X-SupportDesk-Secret", secret);
            }
            return await anon.SendAsync(request);
        }

        var updated = new
        {
            id = Guid.NewGuid().ToString(), eventType = "workitem.updated",
            resource = new
            {
                workItemId = 4711,
                revision = new
                {
                    id = 4711,
                    fields = new Dictionary<string, object>
                    {
                        ["System.Title"] = "Payroll PF rounding", ["System.State"] = "Resolved", ["System.TeamProject"] = "ERP", ["System.WorkItemType"] = "Bug",
                        ["Microsoft.VSTS.Build.IntegrationBuild"] = "8.0.2"
                    }
                }
            }
        };
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(updated, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(updated, "not-the-secret")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(updated, tracker.WebhookSecret, basic: true)).StatusCode);
        var link = Assert.Single((await EngineeringKit.PanelAsync(admin, ticket.Id)).Links);
        Assert.Equal("Resolved", link.State);
        Assert.Equal("8.0.2", link.FixVersion);
        Assert.True(link.IsResolved);

        var deleted = new { id = Guid.NewGuid().ToString(), eventType = "workitem.deleted", resource = new { id = 4711 } };
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(deleted, tracker.WebhookSecret)).StatusCode);
        await factory.DrainOutboxAsync();
        link = Assert.Single((await EngineeringKit.PanelAsync(admin, ticket.Id)).Links);
        Assert.True(link.IsOrphaned);
        Assert.Equal("Payroll PF rounding", link.Title);
        Assert.Equal("Resolved", link.State);
    }

    [Fact]
    public async Task Engineering_data_is_isolated_between_tenants()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        var adminA = await a.AdminClientAsync();
        var adminB = await b.AdminClientAsync();
        var trackerA = await EngineeringKit.LocalTrackerAsync(adminA);
        var ticketA = await CreateTicketAsync(adminA, "Tenant A defect");
        var escalated = await EngineeringKit.EscalateAsync(adminA, ticketA.Id, trackerA.Id, substatus: false);
        var itemA = await EngineeringKit.WorkItemIdAsync(factory, a.Id, trackerA.Id, escalated.Link.ExternalId);

        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/v1/engineering/tickets/{ticketA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/v1/engineering/work-items/{itemA}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/v1/engineering/admin/trackers/{trackerA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.DeleteAsync($"/api/v1/engineering/tickets/{ticketA.Id}/links/{escalated.Link.Id}")).StatusCode);
        // Tenant B cannot escalate its own ticket into tenant A's tracker.
        var ticketB = await CreateTicketAsync(adminB, "Tenant B defect");
        var cross = await adminB.PostAsJsonAsync($"/api/v1/engineering/tickets/{ticketB.Id}/escalate",
            new EscalateToEngineeringRequest { TrackerId = trackerA.Id, Title = "Sneaky" }, J);
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
        // B's board does not show A's items.
        var boardB = (await adminB.GetFromJsonAsync<EngineeringBoardDto>("/api/v1/engineering/board", J))!;
        Assert.Null(boardB.TrackerId);
        var itemsB = (await adminB.GetFromJsonAsync<PagedResult<EngineeringWorkItemDto>>("/api/v1/engineering/work-items?includeClosed=true", J))!;
        Assert.Empty(itemsB.Items);
        var itemsA = (await adminA.GetFromJsonAsync<PagedResult<EngineeringWorkItemDto>>("/api/v1/engineering/work-items", J))!;
        Assert.Contains(itemsA.Items, w => w.Id == itemA);
    }

    [Fact]
    public async Task Light_agents_can_work_the_board_but_not_escalate_and_agents_cannot_administer()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var tracker = await EngineeringKit.LocalTrackerAsync(admin);
        var (_, engineerEmail) = await t.CreateAgentAsync("Eng Engineer", "Light Agent");
        var (_, agentEmail) = await t.CreateAgentAsync("Sup Agent");
        var engineer = await t.AgentClientAsync(engineerEmail);
        var agent = await t.AgentClientAsync(agentEmail);

        var created = await EngineeringKit.Ok<EngineeringWorkItemDto>(await engineer.PostAsJsonAsync("/api/v1/engineering/work-items",
            new SaveEngineeringWorkItemRequest { TrackerId = tracker.Id, Title = "Refactor export pager", Type = "Task" }, J));
        Assert.Equal("New", created.State);
        var ticket = await CreateTicketAsync(admin, "Light agent ticket");
        var forbidden = await engineer.PostAsJsonAsync($"/api/v1/engineering/tickets/{ticket.Id}/escalate", new EscalateToEngineeringRequest { TrackerId = tracker.Id, Title = "x" }, J);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/v1/engineering/admin/trackers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync("/api/v1/engineering/work-items",
            new SaveEngineeringWorkItemRequest { TrackerId = tracker.Id, Title = "Agents only view" }, J)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync("/api/v1/engineering/board")).StatusCode);
    }
}
