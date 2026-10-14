using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SupportDesk.Api.Infrastructure;
using SupportDesk.Application.Communication;
using SupportDesk.Contracts.Communication;

namespace SupportDesk.Api.Controllers.Communication;

/// <summary>
/// Anonymous live-chat endpoints used by the embeddable widget. Sessions are authorised with the signed visitor token
/// returned when the chat starts (header X-Visitor-Token). Cross-origin by design (the widget runs on customer sites).
/// </summary>
[ApiController]
[AllowAnonymous]
[EnableCors(CommunicationApi.WidgetCorsPolicy)]
[EnableRateLimiting(HostingSetup.PublicLimiter)]
[Route("api/v1/chat/{widgetKey}")]
[Produces("application/json")]
public sealed class ChatController(ChatService chat) : ControllerBase
{
    public const string TokenHeader = "X-Visitor-Token";

    private string? Token => Request.Headers.TryGetValue(TokenHeader, out var t) ? t.ToString() : null;

    [HttpGet("config")]
    public Task<ChatWidgetConfigDto> Config(string widgetKey, CancellationToken ct) => chat.ConfigAsync(widgetKey, ct);

    [HttpPost("sessions")]
    public async Task<ActionResult<ChatSessionDto>> Start(string widgetKey, StartChatRequest request, CancellationToken ct) =>
        StatusCode(201, await chat.StartAsync(widgetKey, request, ct));

    [HttpGet("sessions/{id:guid}")]
    public Task<ChatStateDto> State(string widgetKey, Guid id, [FromQuery] DateTime? after, CancellationToken ct) => chat.StateAsync(id, Token, after, ct);

    [HttpPost("sessions/{id:guid}/messages")]
    public Task<ChatStateDto> Send(string widgetKey, Guid id, VisitorMessageRequest request, CancellationToken ct) => chat.VisitorMessageAsync(id, Token, request.Text, ct);

    [HttpPost("sessions/{id:guid}/handoff")]
    public Task<ChatStateDto> Handoff(string widgetKey, Guid id, CancellationToken ct) => chat.RequestHumanAsync(id, Token, ct);

    [HttpPost("sessions/{id:guid}/typing")]
    public async Task<IActionResult> Typing(string widgetKey, Guid id, [FromQuery] bool isTyping = true, CancellationToken ct = default)
    {
        await chat.VisitorTypingAsync(id, Token, isTyping, ct);
        return NoContent();
    }

    [HttpPost("sessions/{id:guid}/verify")]
    public Task<ChatVerifyResponse> RequestVerification(string widgetKey, Guid id, ChatVerifyRequest request, CancellationToken ct) =>
        chat.RequestVerificationAsync(id, Token, request.Email, ct);

    [HttpPost("sessions/{id:guid}/verify/confirm")]
    public Task<ChatVerifyResponse> ConfirmVerification(string widgetKey, Guid id, ChatVerifyConfirmRequest request, CancellationToken ct) =>
        chat.ConfirmVerificationAsync(id, Token, request.Code, ct);

    [HttpPost("sessions/{id:guid}/close")]
    public Task<ChatStateDto> Close(string widgetKey, Guid id, CancellationToken ct) => chat.VisitorCloseAsync(id, Token, ct);

    [HttpPost("sessions/{id:guid}/rating")]
    public async Task<IActionResult> Rate(string widgetKey, Guid id, ChatRatingRequest request, CancellationToken ct)
    {
        await chat.RateAsync(id, Token, request.Rating, request.Comment, ct);
        return NoContent();
    }
}

/// <summary>Public customer-satisfaction survey (link from the survey e-mail/message). The token is single-purpose and expires.</summary>
[ApiController]
[AllowAnonymous]
[EnableRateLimiting(HostingSetup.PublicLimiter)]
[Route("api/v1/public/csat/{token}")]
[Produces("application/json")]
public sealed class CsatController(CsatService csat) : ControllerBase
{
    [HttpGet]
    public Task<CsatSurveyDto> Get(string token, CancellationToken ct) => csat.GetAsync(token, ct);

    [HttpPost]
    public Task<CsatSurveyDto> Submit(string token, CsatSubmitRequest request, CancellationToken ct) => csat.SubmitAsync(token, request, ct);
}
