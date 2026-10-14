using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Integrations.Collaboration;
using SupportDesk.Contracts.Auth;
using SupportDesk.Contracts.Collaboration;
using SupportDesk.Contracts.Engineering;
using SupportDesk.Domain.Tickets;
using SupportDesk.Integrations;
using SupportDesk.IntegrationTests.Automation;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;
using SupportDesk.Workers;
using static SupportDesk.IntegrationTests.Automation.AutomationApi;

namespace SupportDesk.IntegrationTests.Engineering;

/// <summary>In-memory stand-in for Azure DevOps / GitHub: records requests and answers from a delegate (no network).</summary>
internal sealed class FakeTrackerHandler : HttpMessageHandler
{
    public sealed record Recorded(HttpMethod Method, string Url, string? Authorization, string? ContentType, string Body);

    public ConcurrentQueue<Recorded> Requests { get; } = new();
    public Func<HttpRequestMessage, string, HttpResponseMessage> Respond { get; set; } = (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Enqueue(new Recorded(request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(),
            request.Content?.Headers.ContentType?.MediaType, body));
        return Respond(request, body);
    }

    // Shared by every client the factory builds: never disposed by handler rotation.
    protected override void Dispose(bool disposing) { }

    public static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
}

[Collection("api")]
public sealed class TrackerHttpAndCollaborationTests(DeskApiFactory factory)
{
    private static readonly JsonSerializerOptions J = EngineeringKit.J;

    private static async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> host, string tenant, string email)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("X-Client", "mobile");
        var login = await (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(tenant, email, DeskApiFactory.Password), J)).Content.ReadFromJsonAsync<LoginResponse>(J);
        var authed = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken ?? throw new InvalidOperationException(login.Status));
        return authed;
    }

    private static async Task DrainOutboxAsync(WebApplicationFactory<Program> host)
    {
        var processor = host.Services.GetRequiredService<OutboxProcessor>();
        for (var i = 0; i < 50 && await processor.ProcessBatchAsync(100, default) > 0; i++) { }
    }

    private static object AdoWorkItem(int id, string state, string? fixVersion = null) => new
    {
        id, rev = 3,
        fields = new Dictionary<string, object?>
        {
            ["System.Title"] = "Sales Register export times out", ["System.State"] = state, ["System.WorkItemType"] = "Bug", ["System.TeamProject"] = "ERP",
            ["System.AssignedTo"] = new { displayName = "Dev Kumar", uniqueName = "dev@example.test" }, ["System.IterationPath"] = "ERP\\Sprint 42",
            ["Microsoft.VSTS.Build.IntegrationBuild"] = fixVersion, ["System.Tags"] = "SupportDesk; export"
        },
        relations = new[] { new { rel = "ArtifactLink", url = "vstfs:///Git/PullRequestId/p%2Fr%2F88", attributes = new { name = "Pull Request" } } },
        _links = new { html = new { href = $"https://dev.azure.com/acme/ERP/_workitems/edit/{id}" } }
    };

    [Fact]
    public async Task Azure_DevOps_escalation_and_polling_run_through_the_HTTP_adapter()
    {
        var fake = new FakeTrackerHandler();
        var state = "New";
        var exists = true;
        fake.Respond = (request, body) =>
        {
            var url = request.RequestUri!.ToString();
            if (request.Method == HttpMethod.Post && url.Contains("/ERP/_apis/wit/workitems/$Bug", StringComparison.Ordinal)) return FakeTrackerHandler.Json(AdoWorkItem(501, "New"));
            if (request.Method == HttpMethod.Get && url.Contains("/_apis/wit/workitems?ids=501", StringComparison.Ordinal))
                return FakeTrackerHandler.Json(new { count = 1, value = exists ? new object?[] { AdoWorkItem(501, state, "8.0.2") } : [null] });
            if (url.Contains("/_apis/projects", StringComparison.Ordinal)) return FakeTrackerHandler.Json(new { value = new[] { new { id = "p1", name = "ERP" } } });
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddHttpClient(EngineeringAdapters.ClientName).ConfigurePrimaryHttpMessageHandler(() => fake)));
        var t = await factory.CreateTenantAsync();
        var admin = await LoginAsync(host, t.Slug, t.AdminEmail);

        var tracker = await EngineeringKit.Ok<EngineeringTrackerDto>(await admin.PostAsJsonAsync("/api/v1/engineering/admin/trackers", new SaveEngineeringTrackerRequest
        {
            Provider = "AzureDevOps", BaseUrl = "https://dev.azure.com/acme", Project = "ERP", AuthType = "Pat", Token = "secret-pat"
        }, J));
        var test = await EngineeringKit.Ok<TrackerTestResultDto>(await admin.PostAsync($"/api/v1/engineering/admin/trackers/{tracker.Id}/test", null));
        Assert.True(test.Ok, test.Message);
        Assert.Contains(test.Projects, p => p.Name == "ERP");

        var ticket = await CreateTicketAsync(admin, "Export hangs (ADO)");
        var escalated = await EngineeringKit.EscalateAsync(admin, ticket.Id, tracker.Id, "Sales Register export times out", substatus: false);
        Assert.Equal("501", escalated.Link.ExternalId);
        Assert.Equal("AzureDevOps", escalated.Link.Provider);
        Assert.Equal("https://dev.azure.com/acme/ERP/_workitems/edit/501", escalated.Link.Url);

        var create = Assert.Single(fake.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal("application/json-patch+json", create.ContentType);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":secret-pat")), create.Authorization);
        Assert.Contains("api-version=7.1", create.Url);
        using (var patch = JsonDocument.Parse(create.Body))
        {
            var ops = patch.RootElement.EnumerateArray().ToList();
            Assert.Contains(ops, o => o.GetProperty("path").GetString() == "/fields/System.Title" && o.GetProperty("value").GetString() == "Sales Register export times out");
            Assert.Contains(ops, o => o.GetProperty("path").GetString() == "/fields/Microsoft.VSTS.TCM.ReproSteps");
            Assert.Contains(ops, o => o.GetProperty("path").GetString() == "/fields/Microsoft.VSTS.Common.Severity" && o.GetProperty("value").GetString() == "2 - High");
            Assert.Contains(ops, o => o.GetProperty("path").GetString() == "/relations/-");
        }

        // Polling fallback: the item was resolved with a fix version.
        state = "Resolved";
        await host.Services.GetRequiredService<RecurringTaskProcessor>().RunNowAsync("engineering-sync", default);
        await DrainOutboxAsync(host);
        var link = Assert.Single((await EngineeringKit.PanelAsync(admin, ticket.Id)).Links);
        Assert.Equal("Resolved", link.State);
        Assert.True(link.IsResolved);
        Assert.Equal("8.0.2", link.FixVersion);
        Assert.Equal("Dev Kumar", link.AssignedTo);
        Assert.Contains(link.Artifacts, a => a.Kind == "PullRequest" && a.Id == "88");
        var notes = await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().TicketThreads
            .Where(x => x.TicketId == ticket.Id && x.Kind == ThreadKind.InternalNote).Select(x => x.BodyText).ToListAsync());
        Assert.Contains(notes, n => n!.Contains("#501") && n.Contains("resolved"));

        // Deleted in Azure DevOps (errorPolicy=omit returns null): the link is orphaned and keeps its data.
        exists = false;
        var sync = await EngineeringKit.Ok<TrackerSyncResultDto>(await admin.PostAsync($"/api/v1/engineering/admin/trackers/{tracker.Id}/sync", null));
        Assert.Equal(1, sync.Orphaned);
        link = Assert.Single((await EngineeringKit.PanelAsync(admin, ticket.Id)).Links);
        Assert.True(link.IsOrphaned);
        Assert.Equal("Resolved", link.State);
        Assert.Equal("Sales Register export times out", link.Title);

        // Calls are logged with the token never stored.
        var logs = await admin.GetFromJsonAsync<List<SupportDesk.Contracts.Erp.IntegrationLogDto>>($"/api/v1/engineering/admin/trackers/{tracker.Id}/logs", J);
        Assert.Contains(logs!, l => l.Operation == "workitem.create" && l.Success);
        Assert.DoesNotContain(logs!, l => (l.RequestExcerpt ?? "").Contains("secret-pat") || (l.Url ?? "").Contains("secret-pat"));
    }

    [Fact]
    public async Task Teams_channel_receives_an_adaptive_card_with_a_deep_link_for_urgent_tickets()
    {
        await using var endpoint = await TestWebhookEndpoint.StartAsync(failFirst: 0);
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var channel = await EngineeringKit.Ok<CollaborationChannelDto>(await admin.PostAsJsonAsync("/api/v1/collaboration/channels", new SaveCollaborationChannelRequest
        {
            Provider = "Teams", Name = "#support-urgent", WebhookUrl = endpoint.Url, Events = ["ticket.urgent", "ticket.escalated"], MinPriority = "High"
        }, J));
        Assert.StartsWith("127.0.0.1/", channel.UrlHint);
        Assert.DoesNotContain("http", channel.UrlHint);

        await CreateTicketAsync(admin, "Routine question", "Low");
        var urgent = await CreateTicketAsync(admin, "Billing module down at main campus", "Critical");
        await factory.DrainOutboxAsync();
        await factory.DrainJobsAsync();

        var request = Assert.Single(endpoint.Requests);
        using var doc = JsonDocument.Parse(request.Body);
        var card = doc.RootElement.GetProperty("attachments")[0].GetProperty("content");
        Assert.Equal("AdaptiveCard", card.GetProperty("type").GetString());
        Assert.Contains("Billing module down", card.GetProperty("body")[0].GetProperty("text").GetString());
        var action = card.GetProperty("actions")[0];
        Assert.Equal("Action.OpenUrl", action.GetProperty("type").GetString());
        Assert.EndsWith($"/tickets/{urgent.Id}", action.GetProperty("url").GetString());

        var logs = await admin.GetFromJsonAsync<List<CollaborationLogDto>>($"/api/v1/collaboration/channels/{channel.Id}/logs", J);
        Assert.Contains(logs!, l => l.Success && l.Operation == "collaboration.ticket.urgent");
        var test = await EngineeringKit.Ok<CollaborationSendResultDto>(await admin.PostAsync($"/api/v1/collaboration/channels/{channel.Id}/test", null));
        Assert.True(test.Success, test.Error);
        Assert.Equal(2, endpoint.Requests.Count);
    }

    [Fact]
    public async Task Collaboration_sender_blocks_private_network_urls_unless_allowed()
    {
        await using var endpoint = await TestWebhookEndpoint.StartAsync(failFirst: 0);
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        // Allowed in this host (Automation:Webhooks:AllowPrivateNetworks=true): a loopback endpoint works.
        var channel = await EngineeringKit.Ok<CollaborationChannelDto>(await admin.PostAsJsonAsync("/api/v1/collaboration/channels", new SaveCollaborationChannelRequest
        {
            Provider = "Slack", Name = "#eng", WebhookUrl = endpoint.Url, Events = ["ticket.urgent"]
        }, J));
        var allowed = await EngineeringKit.Ok<CollaborationSendResultDto>(await admin.PostAsync($"/api/v1/collaboration/channels/{channel.Id}/test", null));
        Assert.True(allowed.Success, allowed.Error);
        Assert.Single(endpoint.Requests);
        using (var slack = JsonDocument.Parse(endpoint.Requests.First().Body))
            Assert.Equal("header", slack.RootElement.GetProperty("blocks")[0].GetProperty("type").GetString());

        // A host with the default policy (private networks not allowed) refuses the same channel and private URLs.
        var strict = factory.WithWebHostBuilder(b => b.UseSetting("Automation:Webhooks:AllowPrivateNetworks", "false"));
        using (var scope = strict.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<RequestContext>().SetSystem(t.Id, "Test");
            var service = scope.ServiceProvider.GetRequiredService<CollaborationService>();
            var blocked = await service.TestAsync(channel.Id);
            Assert.False(blocked.Success);
            Assert.NotNull(blocked.Error);
            var privateUrl = await Assert.ThrowsAsync<SupportDesk.Application.Tickets.ValidationException>(() => service.CreateAsync(new SaveCollaborationChannelRequest
            {
                Provider = "Teams", Name = "internal", WebhookUrl = "https://10.0.0.5/webhook", Events = ["sla.breached"]
            }));
            Assert.Contains("not allowed", privateUrl.Errors["webhookUrl"]);
            var metadata = await Assert.ThrowsAsync<SupportDesk.Application.Tickets.ValidationException>(() => service.CreateAsync(new SaveCollaborationChannelRequest
            {
                Provider = "Teams", Name = "meta", WebhookUrl = "https://169.254.169.254/latest", Events = ["sla.breached"]
            }));
            Assert.Contains("webhookUrl", metadata.Errors.Keys);
        }
        Assert.Single(endpoint.Requests);
        var lastError = await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().Set<SupportDesk.Domain.Integration.CollaborationChannel>()
            .Where(c => c.Id == channel.Id).Select(c => c.LastError).FirstAsync());
        Assert.NotNull(lastError);
    }

    [Fact]
    public async Task Agents_can_share_a_ticket_to_a_channel()
    {
        await using var endpoint = await TestWebhookEndpoint.StartAsync(failFirst: 0);
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var channel = await EngineeringKit.Ok<CollaborationChannelDto>(await admin.PostAsJsonAsync("/api/v1/collaboration/channels", new SaveCollaborationChannelRequest
        {
            Provider = "Teams", Name = "#swarm", WebhookUrl = endpoint.Url, Events = ["sla.breached"]
        }, J));
        var (_, email) = await t.CreateAgentAsync("Share Agent");
        var agent = await t.AgentClientAsync(email);
        var ticket = await CreateTicketAsync(admin, "Need a second pair of eyes");
        var targets = await agent.GetFromJsonAsync<List<CollaborationShareTargetDto>>("/api/v1/collaboration/share-targets", J);
        Assert.Contains(targets!, x => x.Id == channel.Id);
        var shared = await EngineeringKit.Ok<CollaborationSendResultDto>(await agent.PostAsJsonAsync($"/api/v1/collaboration/tickets/{ticket.Id}/share",
            new ShareTicketToChannelRequest(channel.Id, "Anyone seen this before?"), J));
        Assert.True(shared.Success, shared.Error);
        Assert.Contains("Anyone seen this before?", Assert.Single(endpoint.Requests).Body);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/v1/collaboration/channels")).StatusCode);
    }
}
