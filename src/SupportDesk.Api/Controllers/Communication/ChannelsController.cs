using Microsoft.AspNetCore.Mvc;
using SupportDesk.Application.Communication;
using SupportDesk.Contracts.Communication;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Api.Controllers.Communication;

/// <summary>Channel accounts (mailboxes, WhatsApp, SMS, chat widgets, voice lines, social pages) and the channel simulator. Requires admin.channels.</summary>
[Route("api/v1/channels")]
public sealed class ChannelsController(ChannelAccountService channels, ChannelSimulator simulator, MailboxPoller poller, RequestContext ctx) : DeskControllerBase
{
    [HttpGet]
    public Task<List<ChannelAccountDto>> List([FromQuery] string? type, CancellationToken ct) => channels.ListAsync(type, ct);

    [HttpGet("providers")]
    public IReadOnlyList<ChannelProviderDto> Providers() => channels.Providers();

    [HttpGet("{id:guid}")]
    public Task<ChannelAccountDto> Get(Guid id, CancellationToken ct) => channels.GetAsync(id, ct);

    [HttpPost]
    public async Task<ActionResult<ChannelAccountDto>> Create(CreateChannelAccountRequest request, CancellationToken ct)
    {
        var created = await channels.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPatch("{id:guid}")]
    public Task<ChannelAccountDto> Update(Guid id, UpdateChannelAccountRequest request, CancellationToken ct) => channels.UpdateAsync(id, request, ct);

    /// <summary>Deletes an unused channel; channels with history are deactivated (response: deleted = false).</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => Ok(new { deleted = await channels.DeleteAsync(id, ct) });

    [HttpPost("{id:guid}/test")]
    public Task<ChannelTestResultDto> Test(Guid id, CancellationToken ct) => channels.TestAsync(id, ct);

    [HttpPost("{id:guid}/rotate-webhook-key")]
    public Task<ChannelAccountDto> RotateWebhookKey(Guid id, CancellationToken ct) => channels.RotateWebhookKeyAsync(id, ct);

    /// <summary>Polls a mailbox immediately (instead of waiting for the next scheduled poll).</summary>
    [HttpPost("{id:guid}/poll")]
    public async Task<PollResult> Poll(Guid id, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminChannels);
        await channels.GetAsync(id, ct);
        return await poller.PollAsync(id, force: true, ct);
    }

    // ───── Simulator ─────

    [HttpPost("{id:guid}/simulator/email")]
    public Task<InboundResultDto> SimulateEmail(Guid id, SimulateEmailRequest request, CancellationToken ct) => simulator.EmailAsync(id, request, ct);

    [HttpPost("{id:guid}/simulator/message")]
    public Task<InboundResultDto> SimulateMessage(Guid id, SimulateMessageRequest request, CancellationToken ct) => simulator.MessageAsync(id, request, ct);

    [HttpPost("{id:guid}/simulator/call")]
    public Task<InboundResultDto> SimulateCall(Guid id, SimulateCallRequest request, CancellationToken ct) => simulator.CallAsync(id, request, ct);

    [HttpPost("{id:guid}/simulator/status")]
    public Task<InboundResultDto> SimulateStatus(Guid id, SimulateStatusRequest request, CancellationToken ct) => simulator.StatusAsync(id, request, ct);

    [HttpGet("{id:guid}/outbound")]
    public Task<List<OutboundMessageDto>> Outbound(Guid id, [FromQuery] int limit = 50, CancellationToken ct = default) => simulator.OutboundAsync(id, limit, ct);
}
