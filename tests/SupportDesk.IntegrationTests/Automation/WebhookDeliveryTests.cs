using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Contracts.Automation;
using SupportDesk.Contracts.Common;
using SupportDesk.Domain.Integration;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.Persistence;
using static SupportDesk.IntegrationTests.Automation.AutomationApi;

namespace SupportDesk.IntegrationTests.Automation;

[Collection("api")]
public sealed class WebhookDeliveryTests(DeskApiFactory factory)
{
    private const string Secret = "test-signing-secret-0123456789";

    private static string ExpectedSignature(string body) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    [Fact]
    public async Task Failed_delivery_is_retried_and_requests_are_signed()
    {
        await using var endpoint = await TestWebhookEndpoint.StartAsync(failFirst: 1);
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var created = await admin.PostAsJsonAsync("/api/v1/webhook-subscriptions",
            new SaveWebhookRequest { Name = "Test receiver", Url = endpoint.Url, Events = ["ticket.created"], Secret = Secret }, J);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var hook = (await created.Content.ReadFromJsonAsync<WebhookDto>(J))!;
        Assert.Equal(Secret, hook.Secret);

        var ticket = await CreateTicketAsync(admin, "Webhook test ticket");
        await factory.DrainOutboxAsync();   // creates the delivery + durable job
        await factory.DrainJobsAsync();     // first attempt → HTTP 500

        var deliveries = await admin.GetFromJsonAsync<PageResult<WebhookDeliveryDto>>($"/api/v1/webhook-subscriptions/{hook.Id}/deliveries", J);
        var first = Assert.Single(deliveries!.Items);
        Assert.Equal("Failed", first.Status);
        Assert.Equal(1, first.Attempts);
        Assert.Equal(500, first.ResponseCode);
        Assert.True(first.NextAttemptAt > DateTime.UtcNow);
        Assert.Equal("Failing", (await admin.GetFromJsonAsync<WebhookDto>($"/api/v1/webhook-subscriptions/{hook.Id}", J))!.Status);

        factory.Clock.Pin(DateTime.UtcNow.AddMinutes(5));
        try { await factory.DrainJobsAsync(); }   // retry after backoff → HTTP 200
        finally { factory.Clock.Unpin(); }

        deliveries = await admin.GetFromJsonAsync<PageResult<WebhookDeliveryDto>>($"/api/v1/webhook-subscriptions/{hook.Id}/deliveries", J);
        var delivered = Assert.Single(deliveries!.Items);
        Assert.Equal("Succeeded", delivered.Status);
        Assert.Equal(2, delivered.Attempts);
        Assert.Equal(200, delivered.ResponseCode);
        var after = (await admin.GetFromJsonAsync<WebhookDto>($"/api/v1/webhook-subscriptions/{hook.Id}", J))!;
        Assert.Equal("Active", after.Status);
        Assert.Equal(0, after.ConsecutiveFailures);
        Assert.Null(after.Secret);

        // Both attempts carried the same signed envelope.
        Assert.Equal(2, endpoint.Requests.Count);
        foreach (var request in endpoint.Requests)
        {
            Assert.Equal("ticket.created", request.Headers["X-SupportDesk-Event"]);
            Assert.Equal(delivered.Id.ToString(), request.Headers["X-SupportDesk-Delivery"]);
            Assert.Equal(ExpectedSignature(request.Body), request.Headers["X-SupportDesk-Signature"]);
            using var doc = JsonDocument.Parse(request.Body);
            var root = doc.RootElement;
            Assert.Equal("ticket.created", root.GetProperty("type").GetString());
            Assert.Equal(t.Id.ToString(), root.GetProperty("tenantId").GetString());
            Assert.Equal(delivered.EventId.ToString(), root.GetProperty("id").GetString());
            Assert.True(root.TryGetProperty("occurredAt", out _));
            Assert.Equal(ticket.Id, root.GetProperty("data").GetProperty("ticketId").GetGuid());
            Assert.Equal(ticket.Number, root.GetProperty("data").GetProperty("ticket").GetProperty("number").GetInt64());
        }

        // Each call is logged with a redacted excerpt.
        var logs = await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().Set<IntegrationLog>().AsNoTracking()
            .Where(l => l.System == "Webhook").OrderBy(l => l.CreatedAt).ToListAsync());
        Assert.Equal(2, logs.Count);
        Assert.False(logs[0].Success);
        Assert.True(logs[1].Success);

        // Manual redelivery and a test ping both go out signed.
        var redeliver = await admin.PostAsync($"/api/v1/webhook-subscriptions/{hook.Id}/deliveries/{delivered.Id}/redeliver", null);
        Assert.Equal(HttpStatusCode.OK, redeliver.StatusCode);
        await factory.DrainJobsAsync();
        Assert.Equal(3, endpoint.Requests.Count);
        var ping = await admin.PostAsync($"/api/v1/webhook-subscriptions/{hook.Id}/ping", null);
        var pingResult = (await ping.Content.ReadFromJsonAsync<WebhookDeliveryDto>(J))!;
        Assert.Equal("Succeeded", pingResult.Status);
        var last = endpoint.Requests.Last();
        Assert.Equal("webhook.ping", last.Headers["X-SupportDesk-Event"]);
        Assert.Equal(ExpectedSignature(last.Body), last.Headers["X-SupportDesk-Signature"]);
    }

    [Fact]
    public async Task Subscriptions_validate_events_and_urls_and_require_admin_integrations()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var bad = await admin.PostAsJsonAsync("/api/v1/webhook-subscriptions",
            new SaveWebhookRequest { Name = "Bad", Url = "ftp://example.com", Events = ["ticket.viewed"] }, J);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var body = await bad.Content.ReadAsStringAsync();
        Assert.Contains("url", body);
        Assert.Contains("ticket.viewed", body);

        var agent = await t.CreateAgentAsync("Plain Agent");
        var agentClient = await t.AgentClientAsync(agent.Email);
        Assert.Equal(HttpStatusCode.Forbidden, (await agentClient.GetAsync("/api/v1/webhook-subscriptions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agentClient.GetAsync("/api/v1/automation/rules")).StatusCode);
    }
}
