using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Common;
using SupportDesk.Domain;
using SupportDesk.Domain.Platform;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Platform;

/// <summary>
/// Writes tamper-evident audit records for sensitive administrative and support activities. Rows are inserted
/// unsealed inside the business transaction; <see cref="AuditSealerTask"/> assigns the per-tenant sequence and
/// SHA-256 hash chain afterwards, and a database trigger rejects changes to sealed rows.
/// </summary>
public interface IAuditWriter
{
    AuditLog Write(string action, string? entityType, Guid? entityId, string? summary, object? changes = null);
}

public sealed class AuditWriter(DeskDbContext db, RequestContext ctx) : IAuditWriter
{
    public AuditLog Write(string action, string? entityType, Guid? entityId, string? summary, object? changes = null)
    {
        var log = new AuditLog
        {
            TenantId = ctx.HasTenant ? ctx.TenantId : null,
            OccurredAt = db.Clock.UtcNow,
            ActorType = ctx.ActorType,
            ActorId = ctx.UserId ?? ctx.ContactId ?? ctx.ApiClientId,
            ActorName = ctx.ActorName,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Summary = summary is null ? null : TextTrim(summary, 1000),
            ChangesJson = changes is null ? null : Json.Serialize(changes),
            IpAddress = ctx.IpAddress,
            UserAgent = ctx.UserAgent is null ? null : TextTrim(ctx.UserAgent, 300),
            CorrelationId = ctx.CorrelationId
        };
        db.AuditLogs.Add(log);
        return log;
    }

    private static string TextTrim(string s, int max) => s.Length <= max ? s : s[..max];
}

public static class AuditLogExtensions
{
    /// <summary>
    /// Attributes an unsealed row to a principal the request context does not carry yet (a sign-in is written before the
    /// session exists, so the context still says System).
    /// </summary>
    public static AuditLog AttributeTo(this AuditLog log, ActorType actorType, Guid actorId, string actorName)
    {
        log.ActorType = actorType;
        log.ActorId = actorId;
        log.ActorName = actorName;
        return log;
    }
}

public static class AuditChain
{
    public static string ComputeHash(string prevHash, AuditLog log)
    {
        var canonical = string.Join('|',
            log.Sequence.ToString(CultureInfo.InvariantCulture),
            log.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            log.TenantId?.ToString("N") ?? "",
            log.ActorType.ToString(),
            log.ActorId?.ToString("N") ?? "",
            log.ActorName ?? "",
            log.Action,
            log.EntityType ?? "",
            log.EntityId?.ToString("N") ?? "",
            log.Summary ?? "",
            log.ChangesJson ?? "",
            log.IpAddress ?? "",
            prevHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

public sealed record AuditVerificationResult(bool Valid, long Checked, long? BrokenAtSequence, string? Message);

/// <summary>Seals pending audit rows into per-tenant hash chains, in every database (shared and dedicated).</summary>
public sealed class AuditSealerTask(TenantScopeRunner runner) : IRecurringTask
{
    public string Name => "audit-sealer";
    public TimeSpan Interval => TimeSpan.FromSeconds(5);

    public Task RunAsync(CancellationToken ct) =>
        runner.ForEachDatabaseAsync("Audit sealer", (sp, _, token) => SealAsync(sp.GetRequiredService<DeskDbContext>(), token), ct);

    private static async Task SealAsync(DeskDbContext db, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var batch = await db.AuditLogs.Where(a => a.Hash == "").OrderBy(a => a.OccurredAt).ThenBy(a => a.Id).Take(500).ToListAsync(ct);
            if (batch.Count == 0) return;
            foreach (var group in batch.GroupBy(a => a.TenantId))
            {
                var last = await db.AuditLogs.AsNoTracking()
                    .Where(a => a.TenantId == group.Key && a.Hash != "")
                    .OrderByDescending(a => a.Sequence)
                    .Select(a => new { a.Sequence, a.Hash })
                    .FirstOrDefaultAsync(ct);
                var seq = last?.Sequence ?? 0;
                var prev = last?.Hash ?? "genesis";
                foreach (var log in group)
                {
                    log.Sequence = ++seq;
                    log.PrevHash = prev;
                    log.Hash = AuditChain.ComputeHash(prev, log);
                    prev = log.Hash;
                }
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
    }
}

public sealed class AuditVerifier(DeskDbContext db)
{
    public async Task<AuditVerificationResult> VerifyAsync(Guid? tenantId, CancellationToken ct = default)
    {
        long checkedCount = 0;
        var prev = "genesis";
        long lastSeq = 0;
        const int page = 2000;
        while (true)
        {
            var rows = await db.AuditLogs.AsNoTracking()
                .Where(a => a.TenantId == tenantId && a.Hash != "" && a.Sequence > lastSeq)
                .OrderBy(a => a.Sequence).Take(page).ToListAsync(ct);
            if (rows.Count == 0) break;
            foreach (var row in rows)
            {
                if (row.Sequence != lastSeq + 1)
                    return new AuditVerificationResult(false, checkedCount, row.Sequence, $"Sequence gap before {row.Sequence} (expected {lastSeq + 1}).");
                if (row.PrevHash != prev)
                    return new AuditVerificationResult(false, checkedCount, row.Sequence, "Previous-hash link does not match.");
                var expected = AuditChain.ComputeHash(prev, row);
                if (expected != row.Hash)
                    return new AuditVerificationResult(false, checkedCount, row.Sequence, "Row content does not match its hash (modified).");
                prev = row.Hash;
                lastSeq = row.Sequence;
                checkedCount++;
            }
        }
        return new AuditVerificationResult(true, checkedCount, null, checkedCount == 0 ? "No sealed audit records yet." : "Chain intact.");
    }
}

public static class AuditActions
{
    public const string LoginSucceeded = "auth.login";
    public const string LoginFailed = "auth.login_failed";
    public const string Logout = "auth.logout";
    public const string MfaEnabled = "auth.mfa_enabled";
    public const string MfaDisabled = "auth.mfa_disabled";
    public const string PasswordChanged = "auth.password_changed";
    public const string SessionRevoked = "auth.session_revoked";
    public const string TokenReuseDetected = "auth.refresh_reuse_detected";
    public const string UserCreated = "user.created";
    public const string UserUpdated = "user.updated";
    public const string UserDeactivated = "user.deactivated";
    public const string RoleChanged = "role.changed";
    public const string PermissionsChanged = "role.permissions_changed";
    public const string SettingsChanged = "settings.changed";
    public const string TicketDeleted = "ticket.deleted";
    public const string TicketMerged = "ticket.merged";
    public const string TicketExported = "ticket.exported";
    public const string EntitlementOverridden = "ticket.entitlement_overridden";
    public const string ContactMerged = "contact.merged";
    public const string ContactErased = "contact.erased";
    public const string DataExported = "data.exported";
    public const string LegalHoldChanged = "compliance.legal_hold";
    public const string RetentionRun = "compliance.retention_run";
    public const string ConfigPublished = "config.published";
    public const string ConfigImported = "config.imported";
    public const string ConfigRolledBack = "config.rolled_back";
    public const string ApiKeyCreated = "integration.api_key_created";
    public const string ApiKeyRevoked = "integration.api_key_revoked";
    public const string IntegrationChanged = "integration.changed";
    public const string ChannelChanged = "channel.changed";
    public const string AiConfigChanged = "ai.config_changed";
    public const string AiActionApplied = "ai.action_applied";
    public const string AiActionReverted = "ai.action_reverted";
    public const string SubscriptionChanged = "subscription.changed";
    public const string TenantSuspended = "tenant.suspended";
    public const string TenantResumed = "tenant.resumed";
}
