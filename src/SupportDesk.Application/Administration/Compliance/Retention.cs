using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Compliance;
using SupportDesk.Domain;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Administration.Compliance;

/// <summary>Retention data types, their allowed actions and minimum ages.</summary>
public static class RetentionDataTypes
{
    public const string ClosedTickets = "ClosedTickets";
    public const string Messages = "Messages";
    public const string Attachments = "Attachments";
    public const string AuditLogs = "AuditLogs";
    public const string AiLogs = "AiLogs";

    public static readonly IReadOnlyList<RetentionDataTypeDto> All =
    [
        new(ClosedTickets, "Closed tickets", "Tickets closed more than N days ago. Archive hides them from queues; anonymise redacts text but keeps metrics; delete removes them with their history.",
            ["Archive", "Anonymize", "Delete"], 30),
        new(Messages, "Channel messages", "Raw e-mail / chat / social messages received or sent more than N days ago (the ticket conversation is kept).",
            ["Anonymize", "Delete"], 30),
        new(Attachments, "Ticket attachments", "Files attached to tickets more than N days ago.", ["Delete"], 30),
        new(AuditLogs, "Audit log", "Sealed audit records older than N days (at least one year). The hash chain stays verifiable from a recorded anchor.",
            ["Delete"], 365),
        new(AiLogs, "AI execution log", "AI model calls (inputs by hash, outputs, cost) older than N days.", ["Anonymize", "Delete"], 7),
    ];

    public static RetentionDataTypeDto? Find(string? key) => All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Anchor that keeps a purged audit chain verifiable: the last purged sequence and its hash.</summary>
public sealed record AuditChainAnchor
{
    public long AfterSequence { get; init; }
    public string Hash { get; init; } = "genesis";
    public DateTime PurgedAt { get; init; }
}

/// <summary>
/// Retention policies (admin.compliance): CRUD, dry-run counts and execution. Execution honours legal holds and runs in
/// batches; every run is audited. The recurring <see cref="RetentionTask"/> runs all active policies every six hours per tenant.
/// </summary>
public sealed class RetentionService(
    DeskDbContext db,
    RequestContext ctx,
    IAuditWriter audit,
    LegalHoldMatcher holds,
    DependentRowPurger purger,
    IFileStorage storage,
    TenantSettingsService settings,
    ILogger<RetentionService> logger)
{
    public const string AnchorSettingKey = "compliance.audit_anchor";
    public const int BatchSize = 1000;
    private const string Redacted = "[removed by retention policy]";

    public static RetentionPolicyDto ToDto(RetentionPolicy p) =>
        new(p.Id, p.Name, p.EntityType, p.Action.ToString(), p.AfterDays, p.IsActive, p.LastRunAt, p.LastRunAffected, p.CreatedAt);

    public async Task<List<RetentionPolicyDto>> ListAsync(CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminCompliance);
        return (await db.RetentionPolicies.AsNoTracking().OrderBy(p => p.EntityType).ThenBy(p => p.Name).ToListAsync(ct)).Select(ToDto).ToList();
    }

    public async Task<RetentionPolicyDto> CreateAsync(SaveRetentionPolicyRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminCompliance);
        var (type, action) = Validate(r);
        var policy = new RetentionPolicy { Name = r.Name.Trim(), EntityType = type, Action = action, AfterDays = r.AfterDays, IsActive = r.IsActive };
        db.RetentionPolicies.Add(policy);
        audit.Write(AuditActions.SettingsChanged, "RetentionPolicy", policy.Id, $"Retention policy '{policy.Name}' created", new { after = ToDto(policy) });
        await db.SaveChangesAsync(ct);
        return ToDto(policy);
    }

    public async Task<RetentionPolicyDto> UpdateAsync(Guid id, SaveRetentionPolicyRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminCompliance);
        var (type, action) = Validate(r);
        var policy = await db.RetentionPolicies.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Retention policy", id);
        var before = ToDto(policy);
        policy.Name = r.Name.Trim();
        policy.EntityType = type;
        policy.Action = action;
        policy.AfterDays = r.AfterDays;
        policy.IsActive = r.IsActive;
        audit.Write(AuditActions.SettingsChanged, "RetentionPolicy", policy.Id, $"Retention policy '{policy.Name}' updated", new { before, after = ToDto(policy) });
        await db.SaveChangesAsync(ct);
        return ToDto(policy);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminCompliance);
        var policy = await db.RetentionPolicies.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Retention policy", id);
        db.RetentionPolicies.Remove(policy);
        audit.Write(AuditActions.SettingsChanged, "RetentionPolicy", policy.Id, $"Retention policy '{policy.Name}' deleted", new { before = ToDto(policy) });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Counts what a policy (saved or proposed) would affect now, and what legal holds keep back.</summary>
    public async Task<RetentionDryRunDto> DryRunAsync(SaveRetentionPolicyRequest r, Guid? policyId = null, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminCompliance);
        var (type, action) = Validate(r);
        var now = db.Clock.UtcNow;
        var cutoff = now.AddDays(-r.AfterDays);
        var held = await holds.HeldTicketIdsAsync(ct);
        var (eligible, heldBack) = type switch
        {
            RetentionDataTypes.ClosedTickets => await CountAsync(TicketCandidates(cutoff, action), held, (q, ids) => q.Where(t => ids.Contains(t.Id)), ct),
            RetentionDataTypes.Messages => await CountAsync(MessageCandidates(cutoff, action), held,
                (q, ids) => q.Where(m => m.TicketId != null && ids.Contains(m.TicketId.Value)), ct),
            RetentionDataTypes.Attachments => await CountAsync(db.TicketAttachments.Where(a => a.CreatedAt < cutoff), held, (q, ids) => q.Where(a => ids.Contains(a.TicketId)), ct),
            RetentionDataTypes.AuditLogs => (await AuditCandidates(cutoff).LongCountAsync(ct), 0L),
            RetentionDataTypes.AiLogs => await CountAsync(AiCandidates(cutoff, action), held,
                (q, ids) => q.Where(a => a.EntityType == "Ticket" && a.EntityId != null && ids.Contains(a.EntityId.Value)), ct),
            _ => (0L, 0L)
        };
        return new RetentionDryRunDto(policyId, type, action.ToString(), r.AfterDays, cutoff, eligible, heldBack, now);
    }

    public async Task<RetentionDryRunDto> DryRunAsync(Guid policyId, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminCompliance);
        var p = await db.RetentionPolicies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == policyId, ct) ?? throw new NotFoundException("Retention policy", policyId);
        return await DryRunAsync(new SaveRetentionPolicyRequest(p.Name, p.EntityType, p.Action.ToString(), p.AfterDays, p.IsActive), p.Id, ct);
    }

    public async Task<RetentionRunResultDto> RunNowAsync(Guid policyId, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminCompliance);
        var policy = await db.RetentionPolicies.FirstOrDefaultAsync(p => p.Id == policyId, ct) ?? throw new NotFoundException("Retention policy", policyId);
        return await ExecuteAsync(policy, ct);
    }

    /// <summary>Applies one policy (one batch). Used by "run now" and the recurring task (system context).</summary>
    public async Task<RetentionRunResultDto> ExecuteAsync(RetentionPolicy policy, CancellationToken ct = default)
    {
        var now = db.Clock.UtcNow;
        var cutoff = now.AddDays(-policy.AfterDays);
        var type = RetentionDataTypes.Find(policy.EntityType) ?? throw new DomainException("validation_failed", $"Unknown retention data type '{policy.EntityType}'.");
        if (policy.AfterDays < type.MinimumDays) throw new DomainException("validation_failed", $"{type.Label} must be kept at least {type.MinimumDays} days.");
        var held = await holds.HeldTicketIdsAsync(ct);
        long affected = 0, heldBack = 0;
        var filesToDelete = new List<string>();
        await db.InTransactionAsync(async () =>
        {
            switch (type.Key)
            {
                case RetentionDataTypes.ClosedTickets:
                {
                    var ids = await TicketCandidates(cutoff, policy.Action).OrderBy(t => t.ClosedAt).Select(t => t.Id).Take(BatchSize + held.Count).ToListAsync(ct);
                    heldBack = ids.Count(held.Contains);
                    var targets = ids.Where(id => !held.Contains(id)).Take(BatchSize).ToList();
                    affected = await ApplyTicketsAsync(targets, policy.Action, filesToDelete, now, ct);
                    break;
                }
                case RetentionDataTypes.Messages:
                {
                    var rows = await MessageCandidates(cutoff, policy.Action).OrderBy(m => m.OccurredAt).Select(m => new Pair(m.Id, m.TicketId))
                        .Take(BatchSize + held.Count).ToListAsync(ct);
                    heldBack = rows.Count(r => r.TicketId is { } t && held.Contains(t));
                    var ids = rows.Where(r => r.TicketId is not { } t || !held.Contains(t)).Select(r => r.Id).Take(BatchSize).ToList();
                    if (policy.Action == RetentionAction.Delete) affected = await purger.DeleteAsync("Messages", ids, ct);
                    else
                        affected = await db.Messages.Where(m => ids.Contains(m.Id)).ExecuteUpdateAsync(u => u
                            .SetProperty(m => m.BodyHtml, (string?)null).SetProperty(m => m.BodyText, Redacted).SetProperty(m => m.Subject, (string?)null)
                            .SetProperty(m => m.FromName, (string?)null).SetProperty(m => m.FromAddress, (string?)null).SetProperty(m => m.ToJson, (string?)null)
                            .SetProperty(m => m.CcJson, (string?)null).SetProperty(m => m.MetadataJson, (string?)null), ct);
                    break;
                }
                case RetentionDataTypes.Attachments:
                {
                    var rows = await db.TicketAttachments.Where(a => a.CreatedAt < cutoff).OrderBy(a => a.CreatedAt).Select(a => new { a.Id, a.TicketId, a.FileId })
                        .Take(BatchSize + held.Count).ToListAsync(ct);
                    heldBack = rows.Count(r => held.Contains(r.TicketId));
                    var targets = rows.Where(r => !held.Contains(r.TicketId)).Take(BatchSize).ToList();
                    affected = await purger.DeleteAsync("TicketAttachments", targets.Select(t => t.Id).ToList(), ct);
                    filesToDelete.AddRange(await DeleteUnreferencedFilesAsync(targets.Select(t => t.FileId).Distinct().ToList(), ct));
                    break;
                }
                case RetentionDataTypes.AuditLogs:
                    affected = await PurgeAuditAsync(cutoff, now, ct);
                    break;
                case RetentionDataTypes.AiLogs:
                {
                    var rows = await AiCandidates(cutoff, policy.Action).OrderBy(a => a.CreatedAt)
                        .Select(a => new Pair(a.Id, a.EntityType == "Ticket" ? a.EntityId : null)).Take(BatchSize + held.Count).ToListAsync(ct);
                    heldBack = rows.Count(r => r.TicketId is { } t && held.Contains(t));
                    var ids = rows.Where(r => r.TicketId is not { } t || !held.Contains(t)).Select(r => r.Id).Take(BatchSize).ToList();
                    affected = policy.Action == RetentionAction.Delete
                        ? await purger.DeleteAsync("AiExecutions", ids, ct)
                        : await db.AiExecutions.Where(a => ids.Contains(a.Id)).ExecuteUpdateAsync(u => u
                            .SetProperty(a => a.OutputJson, (string?)null).SetProperty(a => a.CitationsJson, (string?)null).SetProperty(a => a.Error, (string?)null), ct);
                    break;
                }
            }
            policy.LastRunAt = now;
            policy.LastRunAffected = (int)Math.Min(int.MaxValue, affected);
            audit.Write(AuditActions.RetentionRun, "RetentionPolicy", policy.Id,
                $"Retention '{policy.Name}' ({type.Label}, {policy.Action}, older than {policy.AfterDays} days): {affected} affected, {heldBack} kept by legal holds",
                new { policy.EntityType, action = policy.Action.ToString(), policy.AfterDays, cutoff, affected, heldBack });
            await db.SaveChangesAsync(ct);
        }, ct);
        foreach (var key in filesToDelete)
        {
            try { await storage.DeleteAsync(key, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "Could not delete stored file {Key}", key); }
        }
        return new RetentionRunResultDto(policy.Id, affected, heldBack, now);
    }

    private sealed record Pair(Guid Id, Guid? TicketId);

    private IQueryable<Ticket> TicketCandidates(DateTime cutoff, RetentionAction action)
    {
        var q = db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter])
            .Where(t => t.StatusCategory == StatusCategory.Closed && (t.ClosedAt ?? t.UpdatedAt) < cutoff);
        return action switch
        {
            RetentionAction.Archive => q.Where(t => !t.IsArchived),
            RetentionAction.Anonymize => q.Where(t => t.DescriptionText != Redacted),
            _ => q
        };
    }

    private IQueryable<Domain.Communication.Message> MessageCandidates(DateTime cutoff, RetentionAction action)
    {
        var q = db.Messages.Where(m => m.OccurredAt < cutoff);
        return action == RetentionAction.Anonymize ? q.Where(m => m.BodyText != Redacted) : q;
    }

    private IQueryable<Domain.Ai.AiExecution> AiCandidates(DateTime cutoff, RetentionAction action)
    {
        var q = db.AiExecutions.Where(a => a.CreatedAt < cutoff);
        return action == RetentionAction.Anonymize ? q.Where(a => a.OutputJson != null || a.CitationsJson != null) : q;
    }

    private IQueryable<AuditLog> AuditCandidates(DateTime cutoff)
    {
        var tenant = ctx.TenantId;
        return db.AuditLogs.Where(a => a.TenantId == tenant && a.Hash != "" && a.OccurredAt < cutoff);
    }

    /// <summary>
    /// Counts candidates and the part legal holds keep back. The held filter goes on the entity query itself: a filter on a
    /// projected record (or a conditional ticket id) cannot be translated to SQL.
    /// </summary>
    private static async Task<(long, long)> CountAsync<T>(IQueryable<T> rows, HashSet<Guid> held, Func<IQueryable<T>, List<Guid>, IQueryable<T>> heldRows,
        CancellationToken ct)
    {
        var total = await rows.LongCountAsync(ct);
        if (held.Count == 0) return (total, 0);
        var heldCount = await heldRows(rows, held.ToList()).LongCountAsync(ct);
        return (total - heldCount, heldCount);
    }

    private async Task<long> ApplyTicketsAsync(List<Guid> ids, RetentionAction action, List<string> files, DateTime now, CancellationToken ct)
    {
        if (ids.Count == 0) return 0;
        switch (action)
        {
            case RetentionAction.Archive:
                return await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).Where(t => ids.Contains(t.Id))
                    .ExecuteUpdateAsync(u => u.SetProperty(t => t.IsArchived, true).SetProperty(t => t.ArchivedAt, now), ct);
            case RetentionAction.Anonymize:
                await db.TicketThreads.Where(t => ids.Contains(t.TicketId)).ExecuteUpdateAsync(u => u
                    .SetProperty(t => t.BodyHtml, (string?)null).SetProperty(t => t.BodyText, Redacted).SetProperty(t => t.FromAddress, (string?)null)
                    .SetProperty(t => t.ToJson, (string?)null).SetProperty(t => t.CcJson, (string?)null).SetProperty(t => t.BccJson, (string?)null)
                    .SetProperty(t => t.IsRedacted, true), ct);
                return await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).Where(t => ids.Contains(t.Id)).ExecuteUpdateAsync(u => u
                    .SetProperty(t => t.Subject, "[redacted]").SetProperty(t => t.DescriptionHtml, (string?)null).SetProperty(t => t.DescriptionText, Redacted)
                    .SetProperty(t => t.ResolutionHtml, (string?)null).SetProperty(t => t.CsatComment, (string?)null).SetProperty(t => t.AiSummary, (string?)null)
                    .SetProperty(t => t.CustomFieldsJson, "{}"), ct);
            default:
            {
                var fileIds = await db.TicketAttachments.Where(a => ids.Contains(a.TicketId)).Select(a => a.FileId).Distinct().ToListAsync(ct);
                // Purged tickets leave the search index too (no events: handlers would look for rows that no longer exist).
                var docs = await db.SearchDocuments.Where(d => d.EntityType == "Ticket" && ids.Contains(d.EntityId)).Select(d => d.Id).ToListAsync(ct);
                await purger.DeleteAsync("SearchDocuments", docs, ct);
                var n = await purger.DeleteAsync("Tickets", ids, ct);
                files.AddRange(await DeleteUnreferencedFilesAsync(fileIds, ct));
                return n;
            }
        }
    }

    /// <summary>Deletes StoredFile rows no longer referenced anywhere; returns their storage keys (blobs are removed after commit).</summary>
    private async Task<List<string>> DeleteUnreferencedFilesAsync(List<Guid> fileIds, CancellationToken ct)
    {
        var keys = new List<string>();
        foreach (var id in fileIds)
        {
            var key = await db.StoredFiles.Where(f => f.Id == id).Select(f => f.StorageKey).FirstOrDefaultAsync(ct);
            if (key is null) continue;
            try
            {
                var n = await db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM dbo.StoredFiles WHERE Id = @id AND NOT EXISTS (SELECT 1 FROM dbo.TicketAttachments a WHERE a.FileId = @id)",
                    [new SqlParameter("@id", id)], ct);
                if (n > 0) keys.Add(key);
            }
            catch (SqlException ex) when (ex.Number == 547)
            {
                // Still referenced elsewhere (knowledge article, message…): keep the file. The statement failed atomically.
            }
        }
        return keys;
    }

    /// <summary>Deletes the oldest contiguous run of sealed audit rows before the cutoff and records the chain anchor.</summary>
    private async Task<long> PurgeAuditAsync(DateTime cutoff, DateTime now, CancellationToken ct)
    {
        var tenant = ctx.TenantId;
        var anchor = await settings.GetAsync<AuditChainAnchor>(AnchorSettingKey, ct);
        // Contiguous prefix only: stop at the first row at/after the cutoff so the remaining chain stays unbroken.
        var firstKept = await db.AuditLogs.Where(a => a.TenantId == tenant && a.Hash != "" && a.OccurredAt >= cutoff)
            .OrderBy(a => a.Sequence).Select(a => (long?)a.Sequence).FirstOrDefaultAsync(ct);
        var last = await db.AuditLogs.Where(a => a.TenantId == tenant && a.Hash != "" && a.OccurredAt < cutoff && a.Sequence > anchor.AfterSequence
                                                 && (firstKept == null || a.Sequence < firstKept))
            .OrderByDescending(a => a.Sequence).Select(a => new { a.Sequence, a.Hash }).FirstOrDefaultAsync(ct);
        if (last is null) return 0;
        var upTo = Math.Min(last.Sequence, anchor.AfterSequence + BatchSize * 10L);
        var hash = upTo == last.Sequence ? last.Hash : await db.AuditLogs.Where(a => a.TenantId == tenant && a.Sequence == upTo).Select(a => a.Hash).FirstAsync(ct);
        await db.Database.ExecuteSqlRawAsync("EXEC sp_set_session_context N'audit_purge', 1", ct);
        int deleted;
        try
        {
            deleted = await db.Database.ExecuteSqlRawAsync("DELETE FROM dbo.AuditLogs WHERE TenantId = @t AND [Hash] <> '' AND [Sequence] <= @s",
                [new SqlParameter("@t", tenant), new SqlParameter("@s", upTo)], ct);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("EXEC sp_set_session_context N'audit_purge', 0", ct);
        }
        await settings.SetAsync(AnchorSettingKey, new AuditChainAnchor { AfterSequence = upTo, Hash = hash, PurgedAt = now }, ct);
        return deleted;
    }

    private static (string Type, RetentionAction Action) Validate(SaveRetentionPolicyRequest r)
    {
        var errors = new Dictionary<string, string>();
        var type = RetentionDataTypes.Find(r.DataType);
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 200) errors["name"] = "Required (at most 200 characters).";
        if (type is null) errors["dataType"] = "Choose a data type.";
        if (!Enum.TryParse<RetentionAction>(r.Action, true, out var action) || (type is not null && !type.Actions.Contains(action.ToString())))
            errors["action"] = type is null ? "Choose an action." : $"Allowed for {type.Label}: {string.Join(", ", type.Actions)}.";
        if (type is not null && (r.AfterDays < type.MinimumDays || r.AfterDays > 36500))
            errors["afterDays"] = $"{type.Label} must be kept at least {type.MinimumDays} days (and at most 100 years).";
        if (errors.Count > 0) throw new ValidationException(errors);
        return (type!.Key, action);
    }
}

/// <summary>
/// Retention run every six hours: every active policy of every active tenant (one batch per policy and run), then housekeeping of
/// data-subject export files that were downloaded or have expired.
/// </summary>
public sealed class RetentionTask(TenantScopeRunner runner, ILogger<RetentionTask> logger) : IRecurringTask
{
    public const string TaskName = "compliance-retention";
    public string Name => TaskName;
    public TimeSpan Interval => TimeSpan.FromHours(6);

    public Task RunAsync(CancellationToken ct) => runner.ForEachActiveTenantAsync("Retention", async (sp, tenant, token) =>
    {
        var db = sp.GetRequiredService<DeskDbContext>();
        var service = sp.GetRequiredService<RetentionService>();
        foreach (var policy in await db.RetentionPolicies.Where(p => p.IsActive).ToListAsync(token))
        {
            try { await service.ExecuteAsync(policy, token); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Retention policy {Policy} failed for tenant {Tenant}", policy.Id, tenant.Id);
                db.ChangeTracker.Clear();
            }
        }
        await sp.GetRequiredService<DataRequestService>().CleanupExportsAsync(token);
    }, ct);
}
