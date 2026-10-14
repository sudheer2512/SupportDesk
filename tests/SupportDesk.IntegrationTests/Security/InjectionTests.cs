using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain.Customers;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.IntegrationTests.Portal;
using SupportDesk.Persistence;
using static SupportDesk.IntegrationTests.Security.SecurityTestKit;

namespace SupportDesk.IntegrationTests.Security;

/// <summary>
/// SQL injection attempts through list filters, sorting, keyset cursors, global/quick/portal search, saved-view
/// conditions and analytics parameters: every response is a handled 200 (with no extra rows) or a 4xx — never a 500 —
/// and the data is untouched afterwards.
/// </summary>
[Collection("api")]
public sealed class InjectionTests(DeskApiFactory factory)
{
    private static readonly string[] Payloads =
    [
        "' OR '1'='1",
        "' OR 1=1 --",
        "'; DROP TABLE Tickets; --",
        "\"; DELETE FROM Tickets; --",
        "1; WAITFOR DELAY '0:0:5' --",
        "' UNION SELECT name, NULL FROM sys.tables --",
        "%' OR Subject LIKE '%",
        "[a-z]%_\\",
        "') OR ('a'='a",
        "0x27204f5220313d31",
        "ʼ OR 1=1 --",
        new string('\'', 600)
    ];

    private static string Q(string s) => Uri.EscapeDataString(s);

    private static async Task AssertHandledAsync(HttpClient client, string url)
    {
        var r = await client.GetAsync(url);
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity,
            $"GET {url} → {(int)r.StatusCode}: {body}");
    }

    [Fact]
    public async Task Ticket_list_filters_sort_and_search_never_fail_or_leak()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        await admin.CreateTicketAsync("Printer offline at branch");
        await admin.CreateTicketAsync("Invoice total mismatch");
        var before = await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().Tickets.CountAsync());

        foreach (var p in Payloads)
        {
            // Free text: parameterised LIKE — matches nothing, returns nothing extra.
            var list = await admin.GetAsync($"/api/v1/tickets?q={Q(p)}&limit=50");
            var json = await list.JsonAsync(HttpStatusCode.OK);
            Assert.Empty(json!["items"]!.AsArray());

            foreach (var param in new[] { "statusId", "statusCategory", "priority", "assigneeId", "departmentId", "teamId", "channel", "tag", "slaState", "sort", "dir" })
                await AssertHandledAsync(admin, $"/api/v1/tickets?{param}={Q(p)}&limit=5");
            foreach (var param in new[] { "customerId", "contactId", "productId", "viewId", "createdFrom", "createdTo", "limit" })
                await AssertHandledAsync(admin, $"/api/v1/tickets?{param}={Q(p)}");
            await AssertHandledAsync(admin, $"/api/v1/search?q={Q(p)}");
            await AssertHandledAsync(admin, $"/api/v1/search?q=printer&types={Q(p)}");
            await AssertHandledAsync(admin, $"/api/v1/search/quick?q={Q(p)}");
            await AssertHandledAsync(admin, $"/api/v1/search/customers?q={Q(p)}");
            await AssertHandledAsync(admin, $"/api/v1/customers?q={Q(p)}");
            await AssertHandledAsync(admin, $"/api/v1/contacts?q={Q(p)}");
            await AssertHandledAsync(admin, $"/api/v1/kb/articles?q={Q(p)}");
        }

        var after = await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().Tickets.CountAsync());
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Tampered_keyset_cursors_are_rejected_as_bad_requests()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        for (var i = 0; i < 3; i++) await admin.CreateTicketAsync($"Cursor ticket {i}");
        var first = await admin.GetJsonAsync("/api/v1/tickets?limit=1&sort=created");
        Assert.NotNull(first!["nextCursor"]?.GetValue<string>());

        static string Cursor(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var tampered = new[]
        {
            "not-base64-$$$",
            Cursor("{\"v\":\"' OR 1=1 --\",\"id\":\"00000000-0000-0000-0000-000000000000\"}"),
            Cursor("{\"v\":\"2026-13-45\",\"id\":\"00000000-0000-0000-0000-000000000000\"}"),
            Cursor("{\"v\":\"99999999999999999999999\",\"id\":\"00000000-0000-0000-0000-000000000000\"}"),
            Cursor("{\"id\":\"00000000-0000-0000-0000-000000000000\"}"),
            Cursor("{\"v\":null}"),
            Cursor("[1,2,3]"),
            Cursor("null")
        };
        foreach (var cursor in tampered)
        foreach (var sort in new[] { "created", "number", "due", "priority", "lastActivity" })
        {
            var r = await admin.GetAsync($"/api/v1/tickets?limit=5&sort={sort}&cursor={Q(cursor)}");
            await AssertClientErrorAsync(r, HttpStatusCode.BadRequest);
        }
        await AssertClientErrorAsync(await admin.GetAsync($"/api/v1/analytics/drilldown?metric=ticket_count&cursor={Q(tampered[1])}"),
            HttpStatusCode.BadRequest, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Saved_view_conditions_cannot_inject()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        await admin.CreateTicketAsync("Printer offline at branch");

        async Task<HttpResponseMessage> SaveViewAsync(object filter) =>
            await admin.PostAsJsonAsync("/api/v1/views", new SaveViewRequest("tickets", $"Injected {Guid.NewGuid():N}"[..20], "Personal",
                JsonSerializer.SerializeToElement(filter, J), null, null, false, 0), J);

        foreach (var p in Payloads.Take(6))
        {
            // Hostile values: stored as data, matched as parameters.
            var saved = await SaveViewAsync(new { logic = "and", conditions = new[] { new { field = "subject", op = "contains", value = p } } });
            if (saved.IsSuccessStatusCode)
            {
                var viewId = (await saved.JsonAsync())!["id"]!.GetValue<string>();
                var list = await admin.GetAsync($"/api/v1/tickets?viewId={viewId}");
                Assert.Empty((await list.JsonAsync(HttpStatusCode.OK))!["items"]!.AsArray());
            }
            else await AssertClientErrorAsync(saved, HttpStatusCode.BadRequest);

            // Hostile field names and operators are rejected, not interpolated.
            foreach (var condition in new object[]
                     {
                         new { field = $"subject{p}", op = "contains", value = "x" },
                         new { field = "subject", op = p, value = "x" },
                         new { field = "subject", op = "matches", value = "(a+)+$" + p }
                     })
            {
                var r = await SaveViewAsync(new { logic = "and", conditions = new[] { condition } });
                if (r.IsSuccessStatusCode)
                {
                    var viewId = (await r.JsonAsync())!["id"]!.GetValue<string>();
                    await AssertHandledAsync(admin, $"/api/v1/tickets?viewId={viewId}");
                }
                else await AssertClientErrorAsync(r, HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity);
            }
        }
        Assert.Equal(1, await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().Tickets.CountAsync()));
    }

    [Fact]
    public async Task Analytics_and_portal_search_parameters_cannot_inject()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var customer = await factory.CustomerAsync(t, "Injection Retail");
        var (_, contact) = await factory.ContactAsync(t, customer.Id, customer.HeadOffice, PortalAccessScope.Own, "Injection Contact");
        var anonymous = factory.CreateClient();

        foreach (var p in Payloads)
        {
            foreach (var param in new[] { "metric", "metrics", "dimension", "period", "from", "to", "compare", "sort", "department", "team", "customer", "priority", "channel", "limit" })
            {
                await AssertHandledAsync(admin, $"/api/v1/analytics/query?metric=ticket_count&{param}={Q(p)}");
                await AssertHandledAsync(admin, $"/api/v1/analytics/breakdown?metrics=ticket_count&dimension=department&{param}={Q(p)}");
            }
            await AssertHandledAsync(admin, $"/api/v1/analytics/reports/{Q(p)}");
            await AssertHandledAsync(anonymous, $"/api/v1/portal/{t.Slug}/search?q={Q(p)}");
            await AssertHandledAsync(anonymous, $"/api/v1/portal/{t.Slug}/kb/articles?q={Q(p)}");
            await AssertHandledAsync(anonymous, $"/api/v1/portal/{t.Slug}/kb/suggest?q={Q(p)}");
            await AssertHandledAsync(anonymous, $"/api/v1/portal/{Q(p)}/config");
            await AssertHandledAsync(contact, $"/api/v1/portal/{t.Slug}/tickets?status={Q(p)}&q={Q(p)}&sort={Q(p)}");
        }
        // The analytics query endpoint answers normally afterwards.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/analytics/query?metric=ticket_count")).StatusCode);
    }
}
