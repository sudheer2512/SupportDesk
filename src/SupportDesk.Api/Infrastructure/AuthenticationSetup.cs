using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using SupportDesk.Application.Identity;

namespace SupportDesk.Api.Infrastructure;

public static class Policies
{
    /// <summary>Agents, administrators and integration clients acting on the agent API.</summary>
    public const string Agent = "agent";
    /// <summary>Customer portal contacts.</summary>
    public const string Contact = "contact";
    /// <summary>SaaS platform operators.</summary>
    public const string Platform = "platform";
}

public static class AuthSchemes
{
    public const string Bearer = JwtBearerDefaults.AuthenticationScheme;
    public const string ApiKey = "ApiKey";
    public const string Combined = "BearerOrApiKey";
}

public static class AuthenticationSetup
{
    public static IServiceCollection AddDeskAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(AuthSchemes.Combined)
            .AddPolicyScheme(AuthSchemes.Combined, "Bearer or API key", o =>
            {
                o.ForwardDefaultSelector = ctx => ctx.Request.Headers.ContainsKey(ApiKeyHandler.Header) ? AuthSchemes.ApiKey : AuthSchemes.Bearer;
            })
            .AddJwtBearer(AuthSchemes.Bearer, _ => { })
            .AddScheme<AuthenticationSchemeOptions, ApiKeyHandler>(AuthSchemes.ApiKey, _ => { });

        // Configure JWT validation from the signing key provider (resolved lazily from DI).
        services.AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureJwt>();

        services.AddAuthorization(o =>
        {
            o.AddPolicy(Policies.Agent, p => p.RequireAuthenticatedUser().RequireClaim(DeskClaims.Type, DeskClaims.TypeAgent, DeskClaims.TypeClient));
            o.AddPolicy(Policies.Contact, p => p.RequireAuthenticatedUser().RequireClaim(DeskClaims.Type, DeskClaims.TypeContact));
            o.AddPolicy(Policies.Platform, p => p.RequireAuthenticatedUser().RequireClaim(DeskClaims.Type, DeskClaims.TypePlatform));
            o.DefaultPolicy = o.GetPolicy(Policies.Agent)!;
        });
        return services;
    }

    private sealed class ConfigureJwt(SigningKeyProvider keys) : IConfigureNamedOptions<JwtBearerOptions>
    {
        public void Configure(string? name, JwtBearerOptions options)
        {
            if (name != AuthSchemes.Bearer) return;
            options.MapInboundClaims = false;
            options.TokenValidationParameters = keys.ValidationParameters();
            options.Events = new JwtBearerEvents
            {
                // SignalR sends the token in the query string for WebSockets/SSE.
                OnMessageReceived = ctx =>
                {
                    var token = ctx.Request.Query["access_token"];
                    if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs")) ctx.Token = token;
                    return Task.CompletedTask;
                }
            };
        }

        public void Configure(JwtBearerOptions options) => Configure(AuthSchemes.Bearer, options);
    }
}

/// <summary>Authenticates "X-Api-Key: sdk_…" keys into a client principal (type "client", tenant from the key record).</summary>
public sealed class ApiKeyHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ApiKeyService keys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Header = "X-Api-Key";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Header, out var values)) return AuthenticateResult.NoResult();
        var client = await keys.ValidateAsync(values.ToString(), Context.RequestAborted);
        if (client is null) return AuthenticateResult.Fail("Invalid API key.");
        var claims = new List<Claim>
        {
            new(DeskClaims.Subject, client.ClientId.ToString()),
            new(DeskClaims.Type, DeskClaims.TypeClient),
            new(DeskClaims.Tenant, client.TenantId.ToString()),
            new(DeskClaims.Name, client.Name),
            new(DeskClaims.Scope, string.Join(' ', client.Scopes)),
            new("api_key", "true")
        };
        if (client.ActAsUserId is { } u) claims.Add(new Claim("act_as", u.ToString()));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name, DeskClaims.Name, "role")), Scheme.Name));
    }
}
