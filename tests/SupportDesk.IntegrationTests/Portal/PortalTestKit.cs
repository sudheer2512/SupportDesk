using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Platform;
using SupportDesk.Contracts.Auth;
using SupportDesk.Contracts.Knowledge;
using SupportDesk.Contracts.Portal;
using SupportDesk.Domain.Customers;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.Persistence;

namespace SupportDesk.IntegrationTests.Portal;

/// <summary>Helpers for portal tests: customers with locations, portal contacts and contact-authenticated clients.</summary>
public static class PortalTestKit
{
    public static readonly System.Text.Json.JsonSerializerOptions J = DeskApiFactory.Json;

    public sealed record TestCustomer(Guid Id, Guid HeadOffice, Guid Branch);

    public static Task<TestCustomer> CustomerAsync(this DeskApiFactory f, TestTenant t, string name, string tier = "Gold") =>
        f.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var customer = new Customer { Name = name, Tier = tier };
            var ho = new CustomerLocation { CustomerId = customer.Id, Name = $"{name} HO", IsHeadOffice = true };
            var branch = new CustomerLocation { CustomerId = customer.Id, Name = $"{name} Branch" };
            db.Customers.Add(customer);
            db.CustomerLocations.AddRange(ho, branch);
            await db.SaveChangesAsync();
            return new TestCustomer(customer.Id, ho.Id, branch.Id);
        });

    /// <summary>Creates a portal contact and returns an authenticated client for it.</summary>
    public static async Task<(Guid ContactId, HttpClient Client)> ContactAsync(this DeskApiFactory f, TestTenant t, Guid? customerId, Guid? locationId,
        PortalAccessScope scope, string name)
    {
        var email = $"{new string(name.ToLowerInvariant().Where(char.IsLetter).Take(12).ToArray())}.{Guid.NewGuid():N}@cust.test";
        var id = await f.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var contact = new Contact
            {
                FullName = name, Email = email, EmailNormalized = email, CustomerId = customerId, LocationId = locationId, PortalAccess = scope,
                PortalPasswordHash = sp.GetRequiredService<PasswordService>().Hash(DeskApiFactory.Password)
            };
            db.Contacts.Add(contact);
            await db.SaveChangesAsync();
            return contact.Id;
        });
        var client = f.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("X-Client", "mobile");
        var login = await client.PostAsJsonAsync($"/api/v1/portal/{t.Slug}/auth/login", new PortalLoginRequest(email, DeskApiFactory.Password), J);
        var body = await login.Content.ReadFromJsonAsync<PortalLoginResponse>(J);
        if (body?.AccessToken is null) throw new InvalidOperationException($"Portal login failed: {login.StatusCode} {body?.Message}");
        return (id, f.ClientWithToken(body.AccessToken));
    }

    public static async Task<PortalTicketCreatedDto> CreatePortalTicketAsync(this HttpClient contact, string slug, string subject)
    {
        var r = await contact.PostAsJsonAsync($"/api/v1/portal/{slug}/tickets",
            new PortalCreateTicketRequest { Subject = subject, DescriptionHtml = $"<p>{subject} — details</p>" }, J);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"Create failed: {r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        return (await r.Content.ReadFromJsonAsync<PortalTicketCreatedDto>(J))!;
    }

    public static async Task<ArticleDetailDto> PublishedArticleAsync(this HttpClient admin, string title, string body, string visibility = "Public",
        IReadOnlyList<ArticleAccessRuleInput>? rules = null)
    {
        var created = await admin.PostAsJsonAsync("/api/v1/kb/articles", new SaveArticleRequest
        {
            Title = title, BodyHtml = body, Visibility = visibility, ArticleType = "HowTo", AccessRules = rules
        }, J);
        if (!created.IsSuccessStatusCode) throw new InvalidOperationException($"Create article failed: {await created.Content.ReadAsStringAsync()}");
        var a = (await created.Content.ReadFromJsonAsync<ArticleDetailDto>(J))!;
        var published = await admin.PostAsJsonAsync($"/api/v1/kb/articles/{a.Id}/transition", new ArticleTransitionRequest("publish"), J);
        if (!published.IsSuccessStatusCode) throw new InvalidOperationException($"Publish failed: {await published.Content.ReadAsStringAsync()}");
        return (await published.Content.ReadFromJsonAsync<ArticleDetailDto>(J))!;
    }
}
