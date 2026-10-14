using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using SupportDesk.Application.Identity;
using SupportDesk.Contracts.Auth;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain.Customers;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.IntegrationTests.Portal;
using static SupportDesk.IntegrationTests.Security.SecurityTestKit;

namespace SupportDesk.IntegrationTests.Security;

/// <summary>
/// CSRF posture (cookie attributes, bearer-only state changes, anti-forgery header on cookie-borne refresh/logout),
/// JWT tampering (alg none, wrong key, algorithm confusion, expiry, issuer/audience, tenant claim) and login rate limiting.
/// </summary>
[Collection("api")]
public sealed class SessionAndTokenTests(DeskApiFactory factory)
{
    private static string B64Url(string json) => Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(json));

    private static string? Cookie(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues("Set-Cookie", out var values) ? values.FirstOrDefault(v => v.StartsWith(name + "=", StringComparison.Ordinal)) : null;

    [Fact]
    public async Task Refresh_cookies_are_http_only_strict_and_path_scoped()
    {
        var t = await factory.CreateTenantAsync();
        var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
        var login = await browser.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(t.Slug, t.AdminEmail, DeskApiFactory.Password), J);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = (await login.Content.ReadFromJsonAsync<LoginResponse>(J))!;
        Assert.NotNull(body.AccessToken);
        Assert.Null(body.RefreshToken); // web clients never see the refresh token
        var cookie = Cookie(login, "sd_rt");
        Assert.NotNull(cookie);
        var attributes = cookie!.ToLowerInvariant();
        Assert.Contains("httponly", attributes);
        Assert.Contains("samesite=strict", attributes);
        Assert.Contains("path=/api/v1/auth", attributes);
        Assert.Contains("secure", attributes);

        // Portal contacts get the same treatment on their own path.
        var customer = await factory.CustomerAsync(t, "Cookie Retail");
        var email = $"cookie.{Guid.NewGuid():N}@cust.test";
        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<SupportDesk.Persistence.DeskDbContext>();
            db.Contacts.Add(new Contact
            {
                FullName = "Cookie Contact", Email = email, EmailNormalized = email, CustomerId = customer.Id, PortalAccess = PortalAccessScope.Own,
                PortalPasswordHash = sp.GetRequiredService<SupportDesk.Application.Platform.PasswordService>().Hash(DeskApiFactory.Password)
            });
            await db.SaveChangesAsync();
        });
        var portal = await browser.PostAsJsonAsync($"/api/v1/portal/{t.Slug}/auth/login", new PortalLoginRequest(email, DeskApiFactory.Password), J);
        var portalCookie = Cookie(portal, "sd_prt")?.ToLowerInvariant();
        Assert.NotNull(portalCookie);
        Assert.Contains("httponly", portalCookie);
        Assert.Contains("samesite=strict", portalCookie);
        Assert.Contains("path=/api/v1/portal", portalCookie);
    }

    [Fact]
    public async Task State_changes_need_the_bearer_token_and_cookie_refresh_and_logout_need_the_anti_forgery_header()
    {
        var t = await factory.CreateTenantAsync();
        var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await browser.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(t.Slug, t.AdminEmail, DeskApiFactory.Password), J);
        var cookie = Cookie(login, "sd_rt")!.Split(';')[0];

        // A forged cross-site request carries only the cookie: every state-changing endpoint refuses it.
        HttpRequestMessage CookieOnly(HttpMethod method, string url, object? payload = null)
        {
            var request = new HttpRequestMessage(method, url) { Content = payload is null ? null : JsonContent.Create(payload, options: J) };
            request.Headers.Add("Cookie", cookie);
            return request;
        }
        foreach (var (method, url, payload) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Post, "/api/v1/tickets", new CreateTicketRequest { Subject = "CSRF", Channel = "Phone" }),
                     (HttpMethod.Put, "/api/v1/me/profile", new { displayName = "Pwned" }),
                     (HttpMethod.Post, "/api/v1/auth/change-password", new { currentPassword = DeskApiFactory.Password, newPassword = "Pwned@Pass2026!" }),
                     (HttpMethod.Post, "/api/v1/auth/mfa/disable", null),
                     (HttpMethod.Post, "/api/v1/tickets/bulk", new { ids = Array.Empty<Guid>(), action = "delete" })
                 })
            Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SendAsync(CookieOnly(method, url, payload))).StatusCode);

        // The cookie can only be exchanged for a token with the custom header a cross-site form cannot set.
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.SendAsync(CookieOnly(HttpMethod.Post, "/api/v1/auth/refresh"))).StatusCode);
        var wrongHeader = CookieOnly(HttpMethod.Post, "/api/v1/auth/refresh");
        wrongHeader.Headers.Add("X-Requested-With", "XMLHttpRequest");
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.SendAsync(wrongHeader)).StatusCode);

        // Cookie-only logout without the header does not end the session (no CSRF logout)…
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.SendAsync(CookieOnly(HttpMethod.Post, "/api/v1/auth/logout"))).StatusCode);
        var refresh = CookieOnly(HttpMethod.Post, "/api/v1/auth/refresh");
        refresh.Headers.Add("X-Requested-With", "SupportDesk");
        var refreshed = await browser.SendAsync(refresh);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.NotNull((await refreshed.Content.ReadFromJsonAsync<LoginResponse>(J))!.AccessToken);

        // …while the app's own logout (header present) does.
        var rotated = Cookie(refreshed, "sd_rt")!.Split(';')[0];
        var logout = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        logout.Headers.Add("Cookie", rotated);
        logout.Headers.Add("X-Requested-With", "SupportDesk");
        Assert.Equal(HttpStatusCode.NoContent, (await browser.SendAsync(logout)).StatusCode);
        var after = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        after.Headers.Add("Cookie", rotated);
        after.Headers.Add("X-Requested-With", "SupportDesk");
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SendAsync(after)).StatusCode);
    }

    [Fact]
    public async Task Tampered_forged_and_expired_tokens_are_rejected()
    {
        var t = await factory.CreateTenantAsync();
        var other = await factory.CreateTenantAsync();
        var token = await factory.LoginAsync(t.Slug, t.AdminEmail);
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientWithToken(token).GetAsync("/api/v1/me")).StatusCode);

        var parts = token.Split('.');
        var original = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var keys = factory.Services.GetRequiredService<SigningKeyProvider>();
        var now = DateTime.UtcNow;
        string Claim(string type) => original.Claims.First(c => c.Type == type).Value;
        List<Claim> ClaimsWith(string? tenant = null, bool withSession = true, string? subject = null) =>
            original.Claims.Where(c => c.Type is not ("tid" or "sid" or "sub" or "exp" or "nbf" or "iat" or "iss" or "aud"))
                .Append(new Claim("sub", subject ?? Claim("sub")))
                .Append(new Claim("tid", tenant ?? Claim("tid")))
                .Concat(withSession ? [new Claim("sid", Claim("sid"))] : [])
                .ToList();
        string Sign(IEnumerable<Claim> claims, SigningCredentials credentials, DateTime? notBefore = null, DateTime? expires = null,
            string issuer = SigningKeyProvider.Issuer, string audience = SigningKeyProvider.Audience) =>
            new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer, audience, claims, notBefore ?? now.AddMinutes(-1), expires ?? now.AddMinutes(10), credentials));

        using var foreignKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = keys.Key.ECDsa.ExportParameters(false);
        var payload = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(parts[1]));
        var forged = new Dictionary<string, string>
        {
            ["alg none"] = $"{B64Url("""{"alg":"none","typ":"JWT"}""")}.{parts[1]}.",
            ["alg None, signature kept"] = $"{B64Url("""{"alg":"None","typ":"JWT"}""")}.{parts[1]}.{parts[2]}",
            ["tenant claim edited"] = $"{parts[0]}.{B64Url(payload.Replace(Claim("tid"), other.Id.ToString()))}.{parts[2]}",
            ["type claim edited"] = $"{parts[0]}.{B64Url(payload.Replace("\"typ\":\"agent\"", "\"typ\":\"platform\""))}.{parts[2]}",
            ["signature stripped"] = $"{parts[0]}.{parts[1]}.",
            ["foreign key"] = Sign(ClaimsWith(), new SigningCredentials(new ECDsaSecurityKey(foreignKey), SecurityAlgorithms.EcdsaSha256)),
            ["HS256 with the public key (algorithm confusion)"] = Sign(ClaimsWith(),
                new SigningCredentials(new SymmetricSecurityKey([.. publicKey.Q.X!, .. publicKey.Q.Y!]), SecurityAlgorithms.HmacSha256)),
            ["expired"] = Sign(ClaimsWith(), keys.Credentials, now.AddHours(-2), now.AddHours(-1)),
            ["not yet valid"] = Sign(ClaimsWith(), keys.Credentials, now.AddHours(1), now.AddHours(2)),
            ["wrong issuer"] = Sign(ClaimsWith(), keys.Credentials, issuer: "https://evil.example"),
            ["wrong audience"] = Sign(ClaimsWith(), keys.Credentials, audience: "another-api"),
            // Validly signed but pointing at another tenant: the session/user do not exist there.
            ["other tenant (real key)"] = Sign(ClaimsWith(tenant: other.Id.ToString()), keys.Credentials),
            ["other tenant without session (real key)"] = Sign(ClaimsWith(tenant: other.Id.ToString(), withSession: false), keys.Credentials),
            ["unknown subject (real key)"] = Sign(ClaimsWith(withSession: false, subject: Guid.NewGuid().ToString()), keys.Credentials)
        };
        foreach (var (name, jwt) in forged)
        {
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            var read = await client.GetAsync("/api/v1/tickets");
            Assert.True(read.StatusCode == HttpStatusCode.Unauthorized, $"{name}: GET returned {(int)read.StatusCode}");
            var write = await client.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest { Subject = "forged", Channel = "Phone" }, J);
            Assert.True(write.StatusCode == HttpStatusCode.Unauthorized, $"{name}: POST returned {(int)write.StatusCode}");
        }
        // The other tenant never received a ticket.
        var otherAdmin = await other.AdminClientAsync();
        var list = await otherAdmin.GetJsonAsync("/api/v1/tickets?limit=50");
        Assert.Empty(list!["items"]!.AsArray());
    }

    [Fact]
    public async Task Api_rate_limits_apply_per_signed_in_caller_not_per_ip()
    {
        var t = await factory.CreateTenantAsync();
        var adminToken = await factory.LoginAsync(t.Slug, t.AdminEmail);
        var (_, agentEmail) = await t.CreateAgentAsync("Busy Neighbour");
        var agentToken = await factory.LoginAsync(t.Slug, agentEmail);

        // A host on the same database with a tiny API budget; both callers come from the same (test) IP.
        await using var limited = factory.WithWebHostBuilder(b => b.UseSetting("RateLimits:ApiPerMinute", "8"));
        HttpClient Client(string token)
        {
            var c = limited.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return c;
        }
        var admin = Client(adminToken);
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++) statuses.Add((await admin.GetAsync("/api/v1/tickets?limit=1")).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);

        // The other user behind the same address still has their own budget.
        Assert.Equal(HttpStatusCode.OK, (await Client(agentToken).GetAsync("/api/v1/tickets?limit=1")).StatusCode);
    }

    [Fact]
    public async Task Login_endpoints_are_rate_limited_per_client()
    {
        // A host on the same database with a tight authentication limit (the shared fixture disables limits).
        await using var limited = factory.WithWebHostBuilder(b => b.UseSetting("RateLimits:AuthPerMinute", "3"));
        var client = limited.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var codes = new List<HttpStatusCode>();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 5; i++)
        {
            last = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("no-such-tenant", "nobody@example.test", "wrong"), J);
            codes.Add(last.StatusCode);
        }
        Assert.Equal(new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests },
            codes);
        Assert.True(last!.Headers.RetryAfter is not null || last.Headers.Contains("Retry-After"), "429 carries Retry-After");
        Assert.Equal("rate_limited", (await last.JsonAsync())!["code"]!.GetValue<string>());

        // The same budget covers MFA verification and the portal sign-in.
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await client.PostAsJsonAsync("/api/v1/auth/mfa", new MfaVerifyRequest(Guid.NewGuid(), "123456", "email"), J)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await client.PostAsJsonAsync("/api/v1/portal/any-brand/auth/login", new PortalLoginRequest("x@example.test", "wrong"), J)).StatusCode);
    }
}
