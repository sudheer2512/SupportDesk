using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SupportDesk.Api.Infrastructure;
using SupportDesk.Application.Automation;
using SupportDesk.Contracts.Automation;
using SupportDesk.Contracts.Common;

namespace SupportDesk.Api.Controllers.Automation;

/// <summary>Outbound webhook subscriptions (admin.integrations): signed event deliveries, delivery log, redelivery and test ping.</summary>
[Route("api/v1/webhook-subscriptions")]
public sealed class WebhookSubscriptionsController(WebhookService webhooks) : DeskControllerBase
{
    [HttpGet]
    public Task<List<WebhookDto>> List(CancellationToken ct) => webhooks.ListAsync(ct);

    [HttpGet("event-types")]
    public WebhookEventTypesDto EventTypes() => new(WebhookService.SubscribableEvents);

    [HttpPost]
    public async Task<ActionResult<WebhookDto>> Create(SaveWebhookRequest request, CancellationToken ct)
    {
        var hook = await webhooks.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = hook.Id }, hook);
    }

    [HttpGet("{id:guid}")]
    public Task<WebhookDto> Get(Guid id, CancellationToken ct) => webhooks.GetAsync(id, ct);

    [HttpPut("{id:guid}")]
    public Task<WebhookDto> Update(Guid id, SaveWebhookRequest request, CancellationToken ct) => webhooks.UpdateAsync(id, request, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await webhooks.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/enable")]
    public Task<WebhookDto> Enable(Guid id, CancellationToken ct) => webhooks.SetActiveAsync(id, true, ct);

    [HttpPost("{id:guid}/disable")]
    public Task<WebhookDto> Disable(Guid id, CancellationToken ct) => webhooks.SetActiveAsync(id, false, ct);

    /// <summary>Issues a new signing secret (returned once).</summary>
    [HttpPost("{id:guid}/rotate-secret")]
    public Task<WebhookDto> RotateSecret(Guid id, CancellationToken ct) => webhooks.RotateSecretAsync(id, ct);

    [HttpGet("{id:guid}/deliveries")]
    public Task<PageResult<WebhookDeliveryDto>> Deliveries(Guid id, [FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        CancellationToken ct = default) => webhooks.DeliveriesAsync(id, status, page, pageSize, ct);

    [HttpGet("{id:guid}/deliveries/{deliveryId:guid}")]
    public Task<WebhookDeliveryDto> Delivery(Guid id, Guid deliveryId, CancellationToken ct) => webhooks.DeliveryAsync(id, deliveryId, ct);

    [HttpPost("{id:guid}/deliveries/{deliveryId:guid}/redeliver")]
    public Task<WebhookDeliveryDto> Redeliver(Guid id, Guid deliveryId, CancellationToken ct) => webhooks.RedeliverAsync(id, deliveryId, ct);

    /// <summary>Sends a signed "webhook.ping" immediately and returns the result.</summary>
    [HttpPost("{id:guid}/ping")]
    public Task<WebhookDeliveryDto> Ping(Guid id, CancellationToken ct) => webhooks.PingAsync(id, ct);
}

/// <summary>
/// Inbound webhook trigger for automation rules. Anonymous: authenticated by the rule's secret in X-Webhook-Secret;
/// the tenant is resolved from the rule. Optional Idempotency-Key header de-duplicates retries.
/// </summary>
[Route("api/v1/automation/webhooks")]
[AllowAnonymous]
[EnableRateLimiting(HostingSetup.PublicLimiter)]
public sealed class InboundWebhooksController(InboundWebhookService inbound) : DeskControllerBase
{
    public const string SecretHeader = "X-Webhook-Secret";

    [HttpPost("{webhookKey}")]
    [RequestSizeLimit(256 * 1024)]
    [Consumes("application/json")]
    public async Task<ActionResult<InboundWebhookResultDto>> Receive(string webhookKey, [FromBody] JsonElement payload, CancellationToken ct)
    {
        var result = await inbound.HandleAsync(webhookKey, Request.Headers[SecretHeader].ToString(), Request.Headers["Idempotency-Key"].ToString(), payload, ct);
        return Accepted(result);
    }
}
