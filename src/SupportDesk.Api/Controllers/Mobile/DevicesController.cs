using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SupportDesk.Api.Infrastructure;
using SupportDesk.Application.Mobile;
using SupportDesk.Application.Portal;
using SupportDesk.Contracts.Mobile;

namespace SupportDesk.Api.Controllers.Mobile;

/// <summary>Push-notification devices of the signed-in agent (mobile app).</summary>
[Route("api/v1/me/devices")]
public sealed class DevicesController(PushDeviceService devices) : DeskControllerBase
{
    [HttpGet]
    public Task<List<PushDeviceDto>> List(CancellationToken ct) => devices.ListAsync(ct);

    /// <summary>Registers or refreshes this install's push token (call after every sign-in and when the OS rotates the token).</summary>
    [HttpPost]
    public Task<PushDeviceDto> Register(RegisterDeviceRequest request, CancellationToken ct) => devices.RegisterAsync(request, ct);

    /// <summary>Stops pushes to this token (call before signing out).</summary>
    [HttpPost("unregister")]
    public async Task<IActionResult> Unregister(UnregisterDeviceRequest request, CancellationToken ct)
    {
        await devices.UnregisterAsync(request, ct);
        return NoContent();
    }
}

/// <summary>Push-notification devices of the signed-in portal contact (mobile app, customer mode).</summary>
[ApiController]
[Route("api/v1/portal/{brand}/devices")]
[Authorize(Policy = Policies.Contact)]
[EnableRateLimiting(HostingSetup.ApiLimiter)]
[Produces("application/json")]
public sealed class PortalDevicesController(PortalContext portal, PushDeviceService devices) : ControllerBase
{
    [HttpGet]
    public async Task<List<PushDeviceDto>> List(string brand, CancellationToken ct)
    {
        await portal.ResolveAsync(brand, requireContact: true, ct);
        return await devices.ListAsync(ct);
    }

    [HttpPost]
    public async Task<PushDeviceDto> Register(string brand, RegisterDeviceRequest request, CancellationToken ct)
    {
        await portal.ResolveAsync(brand, requireContact: true, ct);
        return await devices.RegisterAsync(request, ct);
    }

    [HttpPost("unregister")]
    public async Task<IActionResult> Unregister(string brand, UnregisterDeviceRequest request, CancellationToken ct)
    {
        await portal.ResolveAsync(brand, requireContact: true, ct);
        await devices.UnregisterAsync(request, ct);
        return NoContent();
    }
}
