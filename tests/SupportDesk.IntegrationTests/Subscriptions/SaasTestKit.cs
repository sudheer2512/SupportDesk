using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Platform;
using SupportDesk.Contracts.Auth;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Platform;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.Persistence;

namespace SupportDesk.IntegrationTests.Subscriptions;

/// <summary>Helpers shared by the SaaS / compliance / configuration-transfer tests.</summary>
internal static class SaasTestKit
{
    public static readonly System.Text.Json.JsonSerializerOptions J = DeskApiFactory.Json;

    /// <summary>Creates a platform operator and returns a client authenticated with a platform token.</summary>
    public static async Task<HttpClient> PlatformClientAsync(this DeskApiFactory factory)
    {
        var email = $"op-{Guid.NewGuid():N}@platform.test";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
            db.PlatformOperators.Add(new PlatformOperator
            {
                Email = email, DisplayName = "Test Operator", PasswordHash = scope.ServiceProvider.GetRequiredService<PasswordService>().Hash(DeskApiFactory.Password)
            });
            await db.SaveChangesAsync();
        }
        var anonymous = factory.CreateClient();
        var response = await anonymous.PostAsJsonAsync("/api/v1/platform/auth/login", new PlatformLoginRequest(email, DeskApiFactory.Password, null), J);
        var body = await response.Content.ReadFromJsonAsync<PlatformLoginResponse>(J);
        Assert.True(body?.AccessToken is not null, $"Platform login failed: {response.StatusCode}");
        return factory.ClientWithToken(body!.AccessToken!);
    }

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(J))!;
    }

    public static async Task<string> CodeAsync(this HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(J);
        return problem.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
    }

    /// <summary>Creates a customer + contact in the tenant and a ticket raised by that contact.</summary>
    public static async Task<(Guid CustomerId, Guid ContactId, TicketDetailDto Ticket)> ContactWithTicketAsync(this DeskApiFactory factory, TestTenant t, HttpClient admin,
        string name = "Erin Example")
    {
        var (customerId, contactId) = await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var customer = new Customer { Name = $"Customer of {name}", Code = "C-" + Guid.NewGuid().ToString("N")[..6] };
            db.Customers.Add(customer);
            var email = $"{name.Replace(' ', '.').ToLowerInvariant()}.{Guid.NewGuid().ToString("N")[..4]}@example.test";
            var contact = new Contact { CustomerId = customer.Id, FullName = name, Email = email, EmailNormalized = email, Phone = "+91 98450 12345" };
            db.Contacts.Add(contact);
            await db.SaveChangesAsync();
            return (customer.Id, contact.Id);
        });
        var ticket = await (await admin.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest
        {
            Subject = $"Printer jammed for {name}", DescriptionHtml = $"<p>Hello, this is {name}. My phone is +91 98450 12345.</p>", Channel = "Email",
            CustomerId = customerId, ContactId = contactId, SkipRouting = true
        }, J)).ReadAsync<TicketDetailDto>();
        return (customerId, contactId, ticket);
    }

    public static Task<T> InCatalogAsync<T>(this DeskApiFactory factory, Func<DeskDbContext, Task<T>> work)
    {
        var scope = factory.Services.CreateScope();
        return Run();

        async Task<T> Run()
        {
            using (scope)
            {
                var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
                return await work(db);
            }
        }
    }

    public static Task<int> InvoiceCountAsync(this DeskApiFactory factory, Guid tenantId) =>
        factory.InCatalogAsync(db => db.AllTenants<BillingInvoice>().CountAsync(i => i.TenantId == tenantId && i.Kind == InvoiceKind.Subscription));
}
