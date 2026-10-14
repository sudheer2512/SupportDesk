using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Mobile;
using SupportDesk.Domain;
using SupportDesk.Domain.Communication;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Mobile;

/// <summary>
/// Push-token registration for the mobile app. A token identifies one app install, so the newest registration wins:
/// registering a token that another user/contact of the tenant held moves it to the caller (the device changed hands
/// or modes). Rows are tenant-owned (query filter + write guard); the caller's identity always comes from the
/// <see cref="RequestContext"/>, never from the request. Unregistering only ever touches the caller's own rows.
/// </summary>
public sealed class PushDeviceService(DeskDbContext db, RequestContext ctx)
{
    public const int MaxTokenLength = 450;
    public const int MaxAppVersionLength = 50;
    public const string AgentApp = "agent";
    public const string CustomerApp = "customer";

    public async Task<PushDeviceDto> RegisterAsync(RegisterDeviceRequest r, CancellationToken ct = default)
    {
        var (userId, contactId, appKind) = Caller();
        var errors = new Dictionary<string, string>();
        if (!Enum.TryParse<DevicePlatform>(r.Platform?.Trim(), true, out var platform) || !Enum.IsDefined(platform))
            errors["platform"] = "Use Android, iOS or Web.";
        var token = (r.Token ?? "").Trim();
        if (token.Length == 0) errors["token"] = "A push token is required.";
        else if (token.Length > MaxTokenLength || token.Any(char.IsWhiteSpace)) errors["token"] = "This is not a valid push token.";
        var version = string.IsNullOrWhiteSpace(r.AppVersion) ? null : r.AppVersion.Trim();
        if (version is { Length: > MaxAppVersionLength }) errors["appVersion"] = $"At most {MaxAppVersionLength} characters.";
        if (errors.Count > 0) throw new ValidationException(errors);

        for (var attempt = 1; ; attempt++)
        {
            var now = db.Clock.UtcNow;
            var device = await db.PushDevices.FirstOrDefaultAsync(d => d.Token == token, ct);
            if (device is null)
            {
                device = new PushDevice { Token = token, RegisteredAt = now };
                db.PushDevices.Add(device);
            }
            else if (device.UserId != userId || device.ContactId != contactId)
            {
                // The install moved to another identity (sign-out without unregister, or agent ⇄ customer mode).
                device.RegisteredAt = now;
            }
            device.UserId = userId;
            device.ContactId = contactId;
            device.AppKind = appKind;
            device.Platform = platform;
            device.AppVersion = version;
            device.IsActive = true;
            device.DeactivatedReason = null;
            device.LastSeenAt = now;
            try
            {
                await db.SaveChangesAsync(ct);
                return Map(device);
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                // A concurrent registration of the same token won the unique index — load it and update instead.
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>Deactivates the caller's registration of the token. Unknown tokens and other people's devices are a no-op.</summary>
    public async Task UnregisterAsync(UnregisterDeviceRequest r, CancellationToken ct = default)
    {
        var (userId, contactId, _) = Caller();
        var token = (r.Token ?? "").Trim();
        if (token.Length == 0) throw new ValidationException(new Dictionary<string, string> { ["token"] = "A push token is required." });
        var now = db.Clock.UtcNow;
        await Own(db.PushDevices.Where(d => d.Token == token && d.IsActive), userId, contactId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(d => d.IsActive, false)
                .SetProperty(d => d.DeactivatedReason, "unregistered by the app")
                .SetProperty(d => d.LastSeenAt, now), ct);
    }

    public async Task<List<PushDeviceDto>> ListAsync(CancellationToken ct = default)
    {
        var (userId, contactId, _) = Caller();
        var rows = await Own(db.PushDevices.AsNoTracking(), userId, contactId)
            .OrderByDescending(d => d.LastSeenAt).Take(50).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    private static IQueryable<PushDevice> Own(IQueryable<PushDevice> q, Guid? userId, Guid? contactId) =>
        userId is { } u ? q.Where(d => d.UserId == u) : q.Where(d => d.ContactId == contactId);

    private (Guid? UserId, Guid? ContactId, string AppKind) Caller()
    {
        if (ctx.IsAgent && ctx.UserId is { } uid) return (uid, null, AgentApp);
        if (ctx.IsContact && ctx.ContactId is { } cid) return (null, cid, CustomerApp);
        throw new ForbiddenException("Only a signed-in agent or portal contact can register a device.");
    }

    public static PushDeviceDto Map(PushDevice d) => new(d.Id, d.Platform.ToString(), Hint(d.Token), d.AppKind, d.AppVersion, d.IsActive,
        d.RegisteredAt, d.LastSeenAt);

    private static string Hint(string token) => token.Length <= 6 ? "…" : "…" + token[^6..];
}
