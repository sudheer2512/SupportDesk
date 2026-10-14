using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Communication;
using SupportDesk.Domain;
using SupportDesk.Domain.Communication;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Mobile;

/// <summary>One push target: a registered device row.</summary>
public sealed record PushTarget(Guid DeviceId, DevicePlatform Platform, string Token);

/// <summary>
/// Sends one push message to every active device of an agent or contact through <see cref="IPushSender"/> (which routes
/// per platform). The delivery counts as sent when at least one device accepted it; tokens the push service rejects
/// permanently (uninstalled app, expired token) are deactivated so they are never tried again.
/// </summary>
public sealed class PushFanout(DeskDbContext db, IPushSender push, ILogger<PushFanout> logger)
{
    public Task<List<PushTarget>> UserDevicesAsync(Guid userId, CancellationToken ct) =>
        Targets(db.PushDevices.AsNoTracking().Where(d => d.UserId == userId && d.IsActive), ct);

    public Task<List<PushTarget>> ContactDevicesAsync(Guid contactId, CancellationToken ct) =>
        Targets(db.PushDevices.AsNoTracking().Where(d => d.ContactId == contactId && d.IsActive), ct);

    private static Task<List<PushTarget>> Targets(IQueryable<PushDevice> q, CancellationToken ct) =>
        q.OrderByDescending(d => d.LastSeenAt).Take(20)
            .Select(d => new PushTarget(d.Id, d.Platform, d.Token)).ToListAsync(ct);

    public async Task<SendResult> SendAsync(IReadOnlyList<PushTarget> targets, string title, string body, IReadOnlyDictionary<string, string> data,
        CancellationToken ct)
    {
        if (targets.Count == 0) return SendResult.Ok("no registered devices");
        SendResult? lastFailure = null;
        var delivered = 0;
        var dead = new List<Guid>();
        var allPermanent = true;
        foreach (var t in targets)
        {
            SendResult result;
            try
            {
                result = await push.SendAsync(t.Platform, t.Token, title, body, data, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                result = SendResult.Fail($"{ex.GetType().Name}: {ex.Message}");
            }
            if (result.Success)
            {
                delivered++;
                continue;
            }
            lastFailure = result;
            if (result.DeadToken) dead.Add(t.DeviceId);
            else if (!result.Permanent) allPermanent = false;
        }
        if (dead.Count > 0)
        {
            var now = db.Clock.UtcNow;
            await db.PushDevices.Where(d => dead.Contains(d.Id)).ExecuteUpdateAsync(u => u
                .SetProperty(d => d.IsActive, false)
                .SetProperty(d => d.DeactivatedReason, "rejected by the push service")
                .SetProperty(d => d.LastSeenAt, now), ct);
            logger.LogInformation("Deactivated {Count} push device(s) rejected by the push service.", dead.Count);
        }
        if (delivered > 0) return SendResult.Ok($"{delivered}/{targets.Count} device(s)");
        // Every device failed: retry later unless no retry can help (dead tokens, rejected payload).
        return SendResult.Fail(lastFailure?.Error ?? "no device accepted the push", permanent: allPermanent);
    }
}
