using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SupportDesk.Application.Communication;

namespace SupportDesk.Api.Controllers.Communication;

/// <summary>
/// Anonymous provider callbacks. The webhook key identifies the channel (and tenant); the provider adapter verifies the
/// signature (X-Hub-Signature-256, X-Twilio-Signature, X-Signature) before anything is stored. Redeliveries are idempotent.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/v1/channels/webhooks/{provider}/{webhookKey}")]
public sealed class ChannelWebhooksController(WebhookIngress ingress) : ControllerBase
{
    private const int MaxBody = 2 * 1024 * 1024;

    /// <summary>Subscription handshake (Meta: hub.mode=subscribe&amp;hub.verify_token=…&amp;hub.challenge=…).</summary>
    [HttpGet]
    public Task<IActionResult> Verify(string provider, string webhookKey, CancellationToken ct) => HandleAsync(provider, webhookKey, [], ct);

    [HttpPost]
    [RequestSizeLimit(MaxBody)]
    public async Task<IActionResult> Receive(string provider, string webhookKey, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, ct);
        if (ms.Length > MaxBody) return StatusCode(413);
        return await HandleAsync(provider, webhookKey, ms.ToArray(), ct);
    }

    private async Task<IActionResult> HandleAsync(string provider, string webhookKey, byte[] body, CancellationToken ct)
    {
        var request = new WebhookRequest(
            Request.Method,
            $"{Request.Scheme}://{Request.Host}{Request.PathBase}{Request.Path}{Request.QueryString}",
            Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString()),
            Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
            body,
            Request.ContentType);
        var result = await ingress.HandleAsync(provider, webhookKey, request, ct);
        return new ContentResult { StatusCode = result.StatusCode, Content = result.Body ?? "", ContentType = result.ContentType };
    }
}
