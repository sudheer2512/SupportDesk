using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Domain;
using SupportDesk.Domain.Configuration;
using SupportDesk.Domain.Platform;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Administration;

/// <summary>Tenant setting keys owned by the administration module (stored in TenantSettings, versioned and audited).</summary>
public static class AdminSettingKeys
{
    public const string IpAllowList = "security.ip_allowlist";
    public const string NotificationPolicy = "notifications.policy";
    public const string TicketDisplay = "tickets.display";
}

/// <summary>Audit action names for administration changes not covered by the kernel catalog.</summary>
public static class AdminAuditActions
{
    public const string RoleCreated = "role.created";
    public const string RoleDeleted = "role.deleted";
    public const string UserRolesChanged = "user.roles_changed";
    public const string TicketConfigChanged = "config.ticket_changed";
    public const string BrandChanged = "brand.changed";
    public const string TemplateChanged = "notification.template_changed";
    public const string SecurityPolicyChanged = "security.policy_changed";
    public const string SsoChanged = "security.sso_changed";
    public const string OAuthClientRevoked = "integration.oauth_client_revoked";
}

/// <summary>Outbox event raised after any configuration change so open desks refresh their cached metadata.</summary>
public static class AdminEvents
{
    public const string ConfigurationChanged = "configuration.changed";
}

public sealed record ConfigurationChangedPayload(string Area, Guid? EntityId);

/// <summary>Pushes "config.changed" to every connected agent of the tenant (after commit) so the desk drops cached ticket metadata.</summary>
public sealed class ConfigurationChangedRealtimeHandler(IRealtimePublisher realtime) : IOutboxHandler
{
    public string Name => "admin-config-realtime";
    public int Order => HandlerOrder.Realtime;
    public bool CanHandle(string eventType) => eventType == AdminEvents.ConfigurationChanged;

    public async Task HandleAsync(OutboxEvent ev, CancellationToken ct)
    {
        if (ev.TenantId is not { } tenantId) return;
        var p = Json.Deserialize<ConfigurationChangedPayload>(ev.PayloadJson);
        await realtime.ToTenantAsync(tenantId, "config.changed", new { area = p?.Area ?? "configuration", entityId = p?.EntityId }, ct);
    }
}

public static partial class AdminRules
{
    [GeneratedRegex("^[a-z][a-z0-9_]{1,62}$")]
    public static partial Regex FieldKey();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    public static partial Regex HexColor();

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$")]
    public static partial Regex Slug();

    /// <summary>Host name: labels of letters/digits/hyphens, at least one dot, no scheme/port/path.</summary>
    [GeneratedRegex(@"^(?=.{4,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$")]
    public static partial Regex HostName();

    public static byte[] ParseRowVersion(string? rowVersion)
    {
        if (string.IsNullOrWhiteSpace(rowVersion))
            throw new ValidationException(new Dictionary<string, string> { ["rowVersion"] = "rowVersion is required for updates." });
        try { return Convert.FromBase64String(rowVersion); }
        catch (FormatException) { throw new ValidationException(new Dictionary<string, string> { ["rowVersion"] = "Invalid rowVersion." }); }
    }

    /// <summary>Checks the client's rowVersion against the loaded entity and makes EF compare it on save (optimistic concurrency).</summary>
    public static void ApplyRowVersion<T>(DeskDbContext db, T entity, string? rowVersion, string what) where T : class, IConcurrencyVersioned
    {
        var bytes = ParseRowVersion(rowVersion);
        if (!bytes.SequenceEqual(entity.RowVersion))
            throw new ConflictException(ErrorCodes.ConcurrencyConflict, $"The {what} was changed by someone else. Reload and try again.");
        db.Entry(entity).Property(x => x.RowVersion).OriginalValue = bytes;
    }

    public static string Version(byte[] rowVersion) => Convert.ToBase64String(rowVersion);

    public static string? Clean(string? s, int max)
    {
        var t = s?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > max ? t[..max] : t;
    }

    public static T ParseEnum<T>(string? value, string field, Dictionary<string, string> errors, T fallback) where T : struct, Enum
    {
        if (value is not null && Enum.TryParse<T>(value, true, out var parsed) && Enum.IsDefined(parsed)) return parsed;
        errors[field] = $"Choose one of: {string.Join(", ", Enum.GetNames<T>())}.";
        return fallback;
    }
}

/// <summary>Pure rule: which custom fields must be filled before a ticket may enter a status.</summary>
public static class CustomFieldRules
{
    /// <summary>Labels of active fields that apply to the department, are required for <paramref name="statusId"/> and are empty in <paramref name="valuesJson"/>.</summary>
    public static IReadOnlyList<string> MissingForStatus(IEnumerable<CustomFieldDefinition> definitions, string? valuesJson, Guid departmentId, Guid statusId)
    {
        var values = Json.Object(valuesJson);
        var missing = new List<string>();
        foreach (var def in definitions)
        {
            if (!def.IsActive || def.Entity != "Ticket") continue;
            if (!Json.GuidList(def.RequiredForStatusIdsJson).Contains(statusId)) continue;
            var depts = Json.GuidList(def.DepartmentIdsJson);
            if (depts.Count > 0 && !depts.Contains(departmentId)) continue;
            if (!values.TryGetValue(def.Key, out var v) || IsEmpty(v)) missing.Add(def.Label);
        }
        return missing;
    }

    private static bool IsEmpty(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()),
        JsonValueKind.Array => v.GetArrayLength() == 0,
        _ => false
    };
}

/// <summary>Helpers shared by the administration services.</summary>
public sealed class AdminChangeNotifier(IEventPublisher events)
{
    /// <summary>Adds a "configuration.changed" event to the current unit of work (desks refresh their metadata after commit).</summary>
    public void Changed(string area, Guid? entityId = null) =>
        events.Publish(AdminEvents.ConfigurationChanged, "Configuration", entityId, new ConfigurationChangedPayload(area, entityId));
}

/// <summary>Reads and writes administration-owned tenant settings with audit on every change (including the first save).</summary>
public sealed class AdminSettingsStore(DeskDbContext db, IAuditWriter audit)
{
    public async Task<(T Value, int Version)> GetAsync<T>(string key, CancellationToken ct) where T : new()
    {
        var row = await db.TenantSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        return row is null ? (new T(), 0) : (Json.DeserializeOr(row.ValueJson, new T()), row.Version);
    }

    /// <summary>
    /// Stages the change (caller saves). Returns false when nothing changed. <paramref name="auditValues"/> = false records
    /// only the key and version (for settings holding encrypted secrets; the caller audits a redacted view).
    /// </summary>
    public async Task<bool> StageAsync<T>(string key, T value, string summary, CancellationToken ct, bool auditValues = true) where T : notnull
    {
        var json = Json.Serialize(value);
        var row = await db.TenantSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            row = new TenantSetting { Key = key, ValueJson = json };
            db.TenantSettings.Add(row);
            audit.Write(AuditActions.SettingsChanged, "Setting", row.Id, $"{summary} (v1)", auditValues ? (object)new { key, to = Json.Element(json) } : new { key });
            return true;
        }
        if (row.ValueJson == json) return false;
        audit.Write(AuditActions.SettingsChanged, "Setting", row.Id, $"{summary} (v{row.Version} → v{row.Version + 1})",
            auditValues ? (object)new { key, from = Json.Element(row.ValueJson), to = Json.Element(json) } : new { key });
        row.ValueJson = json;
        row.Version++;
        return true;
    }
}
