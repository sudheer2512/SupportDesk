using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SupportDesk.Contracts.Automation;
using SupportDesk.Contracts.Tickets;
using SupportDesk.IntegrationTests.Infrastructure;

namespace SupportDesk.IntegrationTests.Automation;

internal static class AutomationApi
{
    public static readonly JsonSerializerOptions J = DeskApiFactory.Json;

    public static JsonElement Definition(object value) => JsonSerializer.SerializeToElement(value, J);

    public static object Cond(string field, string op, object? value = null) => value is null ? new { field, op } : new { field, op, value };

    public static object Act(string type, object? parameters = null) => parameters is null ? new { type } : new { type, @params = parameters };

    public static async Task<RuleDetailDto> CreateRuleAsync(HttpClient admin, string name, string trigger, object definition, int? order = null,
        bool stop = false, int? interval = null, bool publish = true)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/automation/rules", new SaveRuleRequest
        {
            Name = name, Trigger = trigger, Definition = Definition(definition), ExecutionOrder = order, StopProcessing = stop, ScheduleIntervalMinutes = interval,
            Publish = publish
        }, J);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RuleDetailDto>(J))!;
    }

    public static async Task<TicketDetailDto> CreateTicketAsync(HttpClient client, string subject, string? priority = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest
        {
            Subject = subject, DescriptionHtml = $"<p>{subject}</p>", Channel = "Phone", Priority = priority,
            NewContact = new NewContactDto("Jane Customer", $"jane.{Guid.NewGuid():N}@example.test", null, null)
        }, J);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TicketDetailDto>(J))!;
    }

    public static async Task<TicketDetailDto> GetTicketAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<TicketDetailDto>($"/api/v1/tickets/{id}", J))!;
}

/// <summary>A real local HTTP endpoint (Kestrel on a random loopback port) that records requests and fails the first N.</summary>
internal sealed class TestWebhookEndpoint : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _failuresLeft;

    public sealed record ReceivedRequest(IReadOnlyDictionary<string, string> Headers, string Body);

    public ConcurrentQueue<ReceivedRequest> Requests { get; } = new();
    public string Url { get; private set; } = "";

    private TestWebhookEndpoint(WebApplication app, int failFirst)
    {
        _app = app;
        _failuresLeft = failFirst;
    }

    public static async Task<TestWebhookEndpoint> StartAsync(int failFirst)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var endpoint = new TestWebhookEndpoint(app, failFirst);
        app.MapPost("/hook", async (HttpContext http) =>
        {
            using var reader = new StreamReader(http.Request.Body);
            var body = await reader.ReadToEndAsync();
            endpoint.Requests.Enqueue(new ReceivedRequest(
                http.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase), body));
            return Interlocked.Decrement(ref endpoint._failuresLeft) >= 0 ? Results.StatusCode(500) : Results.Ok(new { received = true });
        });
        await app.StartAsync();
        endpoint.Url = app.Urls.First().TrimEnd('/') + "/hook";
        return endpoint;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
