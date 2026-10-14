using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SupportDesk.Application.Communication;

namespace SupportDesk.Api.Controllers.Communication;

/// <summary>API-host wiring of the omnichannel module: chat hub, realtime bridge and the widget CORS policy.</summary>
public static class CommunicationApi
{
    public const string WidgetCorsPolicy = "chat-widget";

    public static IServiceCollection AddCommunicationApi(this IServiceCollection services)
    {
        services.AddSingleton<IChatRealtime, SignalRChatRealtime>();
        // The widget runs on customer websites: any origin, no credentials (visitor tokens travel in a header).
        services.AddCors(o => o.AddPolicy(WidgetCorsPolicy, p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
        return services;
    }

    public static WebApplication MapCommunicationApi(this WebApplication app)
    {
        app.MapHub<ChatHub>("/hubs/chat").RequireCors(WidgetCorsPolicy);
        return app;
    }
}

/// <summary>Serves the embeddable chat widget (/widget/widget.js) and its demo page (/widget/demo.html) from embedded resources.</summary>
[ApiController]
[AllowAnonymous]
[Route("widget")]
public sealed class WidgetController : ControllerBase
{
    private static readonly Assembly Assembly = typeof(WidgetController).Assembly;

    [HttpGet("widget.js")]
    public IActionResult Script() => Resource("widget.js", "text/javascript; charset=utf-8");

    [HttpGet("demo.html")]
    public IActionResult Demo() => Resource("demo.html", "text/html; charset=utf-8");

    private IActionResult Resource(string name, string contentType)
    {
        var stream = Assembly.GetManifestResourceStream($"Widget/{name}");
        if (stream is null) return NotFound();
        Response.Headers.CacheControl = "public, max-age=300";
        Response.Headers.AccessControlAllowOrigin = "*";
        return File(stream, contentType);
    }
}
