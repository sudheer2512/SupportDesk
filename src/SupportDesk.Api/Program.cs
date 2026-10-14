using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using SupportDesk.Api.Controllers.Communication;
using SupportDesk.Api.Infrastructure;
using SupportDesk.Api.Realtime;
using SupportDesk.Application;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Seed;
using SupportDesk.Domain;
using SupportDesk.Integrations;
using SupportDesk.Persistence.Infrastructure;
using SupportDesk.Workers;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

if (!builder.Environment.IsDevelopment()) builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);

// A pinnable clock (used by the demo seeder to create historical data); behaves as the system clock otherwise.
builder.Services.AddSingleton<MutableClock>();
builder.Services.AddSingleton<IClock>(sp => sp.GetRequiredService<MutableClock>());

builder.Services.AddDeskPersistence(config);
builder.Services.AddDeskApplication(config);
builder.Services.AddDeskIntegrations(config);
builder.Services.AddDeskWorkers(config);

builder.Services.AddSingleton<IRealtimePublisher, SignalRRealtimePublisher>();
builder.Services.AddSingleton<PresenceStore>();
var signalR = builder.Services.AddSignalR(o =>
{
    o.AddFilter<RequestContextHubFilter>();
    o.MaximumReceiveMessageSize = 64 * 1024;
}).AddJsonProtocol(o => HostingSetup.ConfigureJson(o.PayloadSerializerOptions));
if (config["Redis:Configuration"] is { Length: > 0 } redis) signalR.AddStackExchangeRedis(redis, o => o.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("supportdesk"));

builder.Services.AddCommunicationApi();
builder.Services.AddDeskAuthentication();
builder.Services.AddDeskRateLimiting(config);
builder.Services.AddDeskOpenApi();
builder.Services.AddDeskTelemetry(config);
builder.Services.AddDeskHealthChecks(config);
builder.Services.AddExceptionHandler<DeskExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddControllers().AddJsonOptions(o => HostingSetup.ConfigureJson(o.JsonSerializerOptions));
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    // Trusted reverse proxies. Default: loopback only (Nginx/IIS on the same host), so a client talking to Kestrel directly
    // cannot forge X-Forwarded-For to bypass the agent IP allow-list or per-IP rate limits. Remote load balancers must be
    // listed; ForwardedHeaders:TrustAll is only for platforms whose edge always overwrites the header.
    var knownProxies = config.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
    var knownNetworks = config.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [];
    if (config.GetValue("ForwardedHeaders:TrustAll", false) || knownProxies.Length > 0 || knownNetworks.Length > 0)
    {
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    }
    foreach (var p in knownProxies)
        if (System.Net.IPAddress.TryParse(p, out var proxy)) o.KnownProxies.Add(proxy);
    foreach (var n in knownNetworks)
        if (System.Net.IPNetwork.TryParse(n, out var network)) o.KnownIPNetworks.Add(network);
});
var corsOrigins = config.GetSection("Cors:Origins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (corsOrigins.Length > 0) p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));

var app = builder.Build();

// Database: migrations (shared + dedicated tenant databases), global catalogs and optional demo data.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<SupportDesk.Application.Tenancy.PlatformCatalogService>().EnsureAsync();
    if (config.GetValue("Seed:Demo", app.Environment.IsDevelopment()))
        await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedAsync();
}

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseDeskSecurityHeaders(app.Environment);
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // Hashed bundles are immutable; HTML is always revalidated.
        if (!ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            ctx.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    }
});
app.UseCors();
// Authentication first, so the API limiter partitions by the signed-in user / API client (not by IP: many users share an
// office NAT, and one busy integration must not starve the agents behind it). Anonymous endpoints still limit by IP.
app.UseAuthentication();
app.UseRateLimiter();
app.UseMiddleware<RequestContextMiddleware>();
app.UseAuthorization();

app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();
app.UseSwaggerUI(o =>
{
    o.RoutePrefix = "docs";
    o.SwaggerEndpoint("/openapi/v1.json", "SupportDesk API v1");
    o.DocumentTitle = "SupportDesk API";
});
app.MapDeskHealthChecks();
app.MapControllers();
app.MapHub<DeskHub>("/hubs/desk");
app.MapHub<PortalHub>("/hubs/portal");
app.MapCommunicationApi();
app.MapSpas();

app.Run();

/// <summary>Entry point marker for WebApplicationFactory in integration tests.</summary>
public partial class Program;
