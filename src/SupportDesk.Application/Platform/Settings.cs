using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Domain.Platform;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Platform;

public static class SettingKeys
{
    public const string Security = "security";
    public const string Tickets = "tickets";
    public const string Sso = "sso";
    public const string Automation = "automation";
    public const string Notifications = "notifications";
    public const string Email = "email";
    public const string Localization = "localization";
}

public sealed record PasswordPolicy
{
    public int MinLength { get; init; } = 10;
    public bool RequireUpper { get; init; } = true;
    public bool RequireLower { get; init; } = true;
    public bool RequireDigit { get; init; } = true;
    public bool RequireSymbol { get; init; }
    public int HistoryCount { get; init; } = 5;
    public int ExpiryDays { get; init; }
    public int MaxFailedAttempts { get; init; } = 5;
    public int LockoutMinutes { get; init; } = 15;
}

public sealed record SessionPolicy
{
    public int AccessTokenMinutes { get; init; } = 15;
    public int IdleTimeoutMinutes { get; init; } = 480;
    public int AbsoluteLifetimeHours { get; init; } = 72;
    public int MaxConcurrentSessions { get; init; } = 10;
}

public sealed record SecuritySettings
{
    public PasswordPolicy Password { get; init; } = new();
    public SessionPolicy Session { get; init; } = new();
    public bool RequireMfaForAdmins { get; init; } = true;
    public bool RequireMfaForAll { get; init; }
    /// <summary>Shows one-time codes in API responses (demo/trial environments only; never in production tenants).</summary>
    public bool DemoMode { get; init; }
}

public sealed record TicketSettings
{
    public string NumberPrefix { get; init; } = "#";
    public long NumberStart { get; init; } = 1000;
    public bool RequireCustomerConfirmation { get; init; }
    public int AutoCloseResolvedAfterDays { get; init; } = 7;
    public int ReopenWindowDays { get; init; } = 30;
    public bool AllowCustomerReopen { get; init; } = true;
    public int SoftLockMinutes { get; init; } = 10;
    public bool CsatEnabled { get; init; } = true;
    public int CsatExpiryDays { get; init; } = 14;
    public bool AutoAcknowledge { get; init; } = true;
    public bool RequireResolutionCode { get; init; }
    public bool AllowAgentsToOverrideEntitlement { get; init; } = true;
}

public sealed record SsoSettings
{
    public bool Enabled { get; init; }
    public string? Authority { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecretProtected { get; init; }
    public string Scopes { get; init; } = "openid profile email";
    public string EmailClaim { get; init; } = "email";
    public bool AutoProvision { get; init; }
    public Guid? DefaultRoleId { get; init; }
    public bool EnforceSso { get; init; }
}

public sealed record AutomationSettings
{
    /// <summary>Maximum automation hops (event → rule action → event …) before further rules are skipped.</summary>
    public int MaxChainDepth { get; init; } = 5;
    /// <summary>Maximum executions of one rule on the same ticket per hour (runaway protection).</summary>
    public int MaxExecutionsPerTicketPerHour { get; init; } = 20;
}

public sealed record NotificationDefaults
{
    public bool EmailOnAssignment { get; init; } = true;
    public bool EmailOnMention { get; init; } = true;
    public bool EmailOnSlaBreach { get; init; } = true;
    public bool TeamsOnSlaBreach { get; init; }
    public string? TeamsWebhookUrlProtected { get; init; }
}

public sealed record EmailSettings
{
    /// <summary>Domain used for outbound Message-IDs.</summary>
    public string MessageIdDomain { get; init; } = "supportdesk.local";
    public bool UseReplyTokens { get; init; } = true;
    public bool StripQuotedText { get; init; } = true;
}

/// <summary>Typed, versioned tenant settings with per-scope memoisation.</summary>
public sealed class TenantSettingsService(DeskDbContext db, IAuditWriter audit)
{
    private readonly Dictionary<string, object> _memo = new();

    public async Task<T> GetAsync<T>(string key, CancellationToken ct = default) where T : notnull, new()
    {
        if (_memo.TryGetValue(key, out var cached)) return (T)cached;
        var row = await db.TenantSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        var value = row is null ? new T() : Json.DeserializeOr(row.ValueJson, new T());
        _memo[key] = value;
        return value;
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken ct = default) where T : notnull
    {
        var row = await db.TenantSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        var json = Json.Serialize(value);
        if (row is null)
        {
            db.TenantSettings.Add(new TenantSetting { Key = key, ValueJson = json });
        }
        else
        {
            if (row.ValueJson == json) return;
            audit.Write(AuditActions.SettingsChanged, "Setting", row.Id, $"Setting '{key}' changed (v{row.Version} → v{row.Version + 1})",
                new { key, from = Json.Element(row.ValueJson), to = Json.Element(json) });
            row.ValueJson = json;
            row.Version++;
        }
        _memo[key] = value;
        await db.SaveChangesAsync(ct);
    }

    public Task<SecuritySettings> SecurityAsync(CancellationToken ct = default) => GetAsync<SecuritySettings>(SettingKeys.Security, ct);
    public Task<TicketSettings> TicketsAsync(CancellationToken ct = default) => GetAsync<TicketSettings>(SettingKeys.Tickets, ct);
    public Task<AutomationSettings> AutomationAsync(CancellationToken ct = default) => GetAsync<AutomationSettings>(SettingKeys.Automation, ct);
    public Task<EmailSettings> EmailAsync(CancellationToken ct = default) => GetAsync<EmailSettings>(SettingKeys.Email, ct);
}
