using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SupportDesk.Application.Identity;
using SupportDesk.Persistence;
using SupportDesk.Workers;

namespace SupportDesk.Api.Infrastructure;

public static class HostingSetup
{
    public const string AuthLimiter = "auth";
    public const string ApiLimiter = "api";
    public const string PublicLimiter = "public";

    public static void ConfigureJson(JsonSerializerOptions o)
    {
        o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.Converters.Add(new JsonStringEnumConverter());
        o.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
    }

    /// <summary>Per-principal rate limits: tight on authentication endpoints, per tenant/client elsewhere.</summary>
    public static IServiceCollection AddDeskRateLimiting(this IServiceCollection services, IConfiguration config)
    {
        var authPerMinute = config.GetValue("RateLimits:AuthPerMinute", 20);
        var apiPerMinute = config.GetValue("RateLimits:ApiPerMinute", 1200);
        var publicPerMinute = config.GetValue("RateLimits:PublicPerMinute", 120);
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = 429;
            o.OnRejected = async (ctx, ct) =>
            {
                ctx.HttpContext.Response.ContentType = "application/problem+json";
                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry)) ctx.HttpContext.Response.Headers.RetryAfter = ((int)retry.TotalSeconds).ToString();
                await ctx.HttpContext.Response.WriteAsJsonAsync(new { title = "Too many requests. Slow down and try again shortly.", status = 429, code = "rate_limited" }, ct);
            };
            o.AddPolicy(AuthLimiter, http => RateLimitPartition.GetFixedWindowLimiter(
                "auth:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = authPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.AddPolicy(PublicLimiter, http => RateLimitPartition.GetFixedWindowLimiter(
                "public:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = publicPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.AddPolicy(ApiLimiter, http =>
            {
                var sub = http.User.FindFirstValue(DeskClaims.Subject);
                var key = sub is null ? "ip:" + http.Connection.RemoteIpAddress : "sub:" + sub;
                return RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = apiPerMinute / 4, TokensPerPeriod = apiPerMinute / 60 + 1, ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                    QueueLimit = 0, AutoReplenishment = true
                });
            });
        });
        return services;
    }

    public static IServiceCollection AddDeskOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi("v1", o =>
        {
            o.AddDocumentTransformer((doc, _, _) =>
            {
                doc.Info = new OpenApiInfo
                {
                    Title = "SupportDesk API",
                    Version = "v1",
                    Description = "Multi-tenant customer service platform: tickets, conversations, customers, products, SLA, automation, knowledge, AI, analytics and administration. " +
                                  "Authenticate with a bearer token (POST /api/v1/auth/login, or POST /oauth/token for client credentials) or an X-Api-Key header."
                };
                doc.Components ??= new OpenApiComponents();
                doc.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                doc.Components.SecuritySchemes["bearer"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" };
                doc.Components.SecuritySchemes["apiKey"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = ApiKeyHandler.Header };
                doc.Security ??= [];
                doc.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("bearer", doc)] = [] });
                doc.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("apiKey", doc)] = [] });
                return Task.CompletedTask;
            });
        });
        return services;
    }

    public static IServiceCollection AddDeskTelemetry(this IServiceCollection services, IConfiguration config)
    {
        var endpoint = config["OpenTelemetry:OtlpEndpoint"];
        var console = config.GetValue("OpenTelemetry:Console", false);
        if (string.IsNullOrWhiteSpace(endpoint) && !console) return services;
        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("SupportDesk.Api"))
            .WithTracing(t =>
            {
                t.AddAspNetCoreInstrumentation(o => o.Filter = http => !http.Request.Path.StartsWithSegments("/health"))
                    .AddHttpClientInstrumentation()
                    .AddSource(OutboxProcessor.Activity.Name, JobProcessor.Activity.Name);
                if (!string.IsNullOrWhiteSpace(endpoint)) t.AddOtlpExporter(o => o.Endpoint = new Uri(endpoint));
                if (console) t.AddConsoleExporter();
            });
        return services;
    }

    public static IServiceCollection AddDeskHealthChecks(this IServiceCollection services, IConfiguration config)
    {
        var hc = services.AddHealthChecks().AddDbContextCheck<DeskDbContext>("database", tags: ["ready"]);
        return services;
    }

    public static void MapDeskHealthChecks(this WebApplication app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("ready"),
            ResponseWriter = async (http, report) =>
            {
                http.Response.ContentType = "application/json";
                await http.Response.WriteAsJsonAsync(new
                {
                    status = report.Status.ToString(),
                    checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString(), durationMs = (int)e.Value.Duration.TotalMilliseconds })
                });
            }
        }).AllowAnonymous();
    }

    /// <summary>Security headers for API and SPA responses (CSP tuned for the Angular apps and SignalR).</summary>
    public static IApplicationBuilder UseDeskSecurityHeaders(this IApplicationBuilder app, IHostEnvironment env) => app.Use(async (http, next) =>
    {
        var h = http.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["Referrer-Policy"] = "strict-origin-when-cross-origin";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        var path = http.Request.Path;
        if (path.StartsWithSegments("/api") || path.StartsWithSegments("/oauth") || path.StartsWithSegments("/.well-known"))
        {
            // API responses are data, never documents: nothing may load, run or frame them. File endpoints override this
            // with "sandbox; default-src 'none'" (and any download without its own policy still gets this one).
            h["X-Frame-Options"] = "DENY";
            h["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
        }
        else if (path.StartsWithSegments("/widget"))
        {
            // The chat widget is embedded on customer websites.
            h["Content-Security-Policy"] = "default-src 'self'; connect-src 'self' ws: wss:; img-src 'self' data: https:; style-src 'self' 'unsafe-inline'; frame-ancestors *";
        }
        else if (!path.StartsWithSegments("/api") && !path.StartsWithSegments("/docs") && !path.StartsWithSegments("/openapi"))
        {
            h["X-Frame-Options"] = "DENY";
            h["Content-Security-Policy"] =
                "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob: https:; font-src 'self' data:; " +
                "connect-src 'self' ws: wss:; frame-src https://www.youtube.com https://www.youtube-nocookie.com https://player.vimeo.com; frame-ancestors 'none'; " +
                "base-uri 'self'; form-action 'self'; object-src 'none'";
        }
        if (!env.IsDevelopment()) h["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
        await next();
    });

    /// <summary>Serves the desk SPA at / and the portal SPA at /portal, falling back to index.html for client routes.</summary>
    public static void MapSpas(this WebApplication app)
    {
        var root = app.Environment.WebRootPath;
        if (root is null) return;
        // ":nonfile" — the portal's own bundles (/portal/main-*.js, *.css) must reach the static file middleware, which
        // skips requests that already matched an endpoint; a missing file then 404s instead of returning index.html.
        app.MapFallback("/portal/{**path:nonfile}", async http =>
        {
            var file = Path.Combine(root, "portal", "index.html");
            if (!File.Exists(file)) { http.Response.StatusCode = 404; return; }
            http.Response.ContentType = "text/html; charset=utf-8";
            http.Response.Headers.CacheControl = "no-cache";
            await http.Response.SendFileAsync(file);
        }).AllowAnonymous();
        app.MapFallback(async http =>
        {
            var path = http.Request.Path.Value ?? "";
            if (path.StartsWith("/api/") || path.StartsWith("/hubs/") || path.StartsWith("/oauth/") || Path.HasExtension(path))
            {
                http.Response.StatusCode = 404;
                return;
            }
            var file = Path.Combine(root, "index.html");
            if (!File.Exists(file))
            {
                http.Response.ContentType = "text/plain";
                await http.Response.WriteAsync("SupportDesk API is running. Build the web clients (cd clients && npm run build) to serve the UI.");
                return;
            }
            http.Response.ContentType = "text/html; charset=utf-8";
            http.Response.Headers.CacheControl = "no-cache";
            await http.Response.SendFileAsync(file);
        }).AllowAnonymous();
    }
}
