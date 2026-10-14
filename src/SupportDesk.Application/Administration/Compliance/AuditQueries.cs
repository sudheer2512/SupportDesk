using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Compliance;
using SupportDesk.Domain;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Administration.Compliance;

public sealed record AuditQuery(
    string? Actor = null,
    string? EntityType = null,
    Guid? EntityId = null,
    string? Action = null,
    DateTime? From = null,
    DateTime? To = null,
    string? Ip = null,
    string? Search = null,
    string? Cursor = null,
    int Limit = 50);

/// <summary>
/// The tenant audit viewer (admin.audit): search and filter, detail with a before/after diff, CSV export (plan feature
/// audit_export) and hash-chain verification that reports the first broken link (anchored after retention purges).
/// </summary>
public sealed class AuditQueryService(DeskDbContext db, RequestContext ctx, IAuditWriter audit, LimitGuard limits, TenantSettingsService settings)
{
    public const int MaxExportRows = 50_000;

    public async Task<PagedResult<AuditEntryDto>> SearchAsync(AuditQuery q, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAudit);
        var limit = Math.Clamp(q.Limit, 1, 200);
        var query = Filter(q);
        if (DbExtensions.DecodeCursor(q.Cursor) is { } c)
        {
            var at = DateTime.Parse(c.V, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            query = query.Where(a => a.OccurredAt < at || (a.OccurredAt == at && a.Id.CompareTo(c.Id) < 0));
        }
        var rows = await query.OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id).Take(limit + 1).ToListAsync(ct);
        var next = rows.Count > limit ? DbExtensions.EncodeCursor(rows[limit - 1].OccurredAt.ToString("O", CultureInfo.InvariantCulture), rows[limit - 1].Id) : null;
        return new PagedResult<AuditEntryDto>(rows.Take(limit).Select(ToDto).ToList(), next);
    }

    public async Task<AuditFacetsDto> FacetsAsync(CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAudit);
        var tenant = ctx.TenantId;
        var actions = await db.AuditLogs.AsNoTracking().Where(a => a.TenantId == tenant).Select(a => a.Action).Distinct().OrderBy(a => a).ToListAsync(ct);
        var types = await db.AuditLogs.AsNoTracking().Where(a => a.TenantId == tenant && a.EntityType != null).Select(a => a.EntityType!).Distinct().OrderBy(a => a).ToListAsync(ct);
        return new AuditFacetsDto(actions, types, Enum.GetNames<ActorType>());
    }

    public async Task<AuditEntryDetailDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAudit);
        var tenant = ctx.TenantId;
        var a = await db.AuditLogs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenant, ct) ?? throw new NotFoundException("Audit entry", id);
        JsonElement? changes = null;
        if (!string.IsNullOrWhiteSpace(a.ChangesJson))
        {
            try { changes = Json.Element(a.ChangesJson); }
            catch (JsonException) { changes = JsonSerializer.SerializeToElement(a.ChangesJson); }
        }
        return new AuditEntryDetailDto(ToDto(a), changes, changes is { } el ? AuditDiff.Compute(el) : [], a.UserAgent, a.CorrelationId, a.Hash, a.PrevHash);
    }

    /// <summary>CSV export of the filtered entries (newest first, up to 50,000 rows). Audited; requires the audit_export plan feature.</summary>
    public async Task<(byte[] Content, string FileName)> ExportCsvAsync(AuditQuery q, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAudit);
        await limits.RequireFeatureAsync(PlanFeatures.AuditExport, ct);
        var rows = await Filter(q).OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id).Take(MaxExportRows).ToListAsync(ct);
        var sb = new StringBuilder();
        sb.AppendLine("Sequence,OccurredAtUtc,ActorType,Actor,Action,EntityType,EntityId,Summary,IpAddress,Hash");
        foreach (var a in rows)
        {
            sb.AppendJoin(',', [
                a.Sequence.ToString(CultureInfo.InvariantCulture), a.OccurredAt.ToString("O", CultureInfo.InvariantCulture), a.ActorType.ToString(),
                Csv(a.ActorName), Csv(a.Action), Csv(a.EntityType), a.EntityId?.ToString() ?? "", Csv(a.Summary), Csv(a.IpAddress), a.Hash
            ]);
            sb.AppendLine();
        }
        audit.Write(AuditActions.DataExported, "AuditLog", null, $"Exported {rows.Count} audit entries to CSV",
            new { q.Actor, q.EntityType, q.EntityId, q.Action, q.From, q.To, q.Ip, rows = rows.Count });
        await db.SaveChangesAsync(ct);
        var bom = Encoding.UTF8.GetPreamble();
        return ([.. bom, .. Encoding.UTF8.GetBytes(sb.ToString())], $"audit-{ctx.TenantSlug ?? "tenant"}-{db.Clock.UtcNow:yyyyMMdd-HHmm}.csv");
    }

    /// <summary>Verifies the tenant's sealed hash chain (from the retention anchor when old entries were purged).</summary>
    public async Task<AuditChainVerificationDto> VerifyAsync(CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAudit);
        var tenant = ctx.TenantId;
        var anchor = await settings.GetAsync<AuditChainAnchor>(RetentionService.AnchorSettingKey, ct);
        var result = await AuditChainVerifier.VerifyAsync(db, tenant, anchor.AfterSequence, anchor.AfterSequence > 0 ? anchor.Hash : "genesis", ct);
        var unsealed = await db.AuditLogs.LongCountAsync(a => a.TenantId == tenant && a.Hash == "", ct);
        audit.Write("audit.chain_verified", "AuditLog", result.BrokenEntryId,
            result.Valid ? $"Audit chain verified: {result.Checked} sealed entries intact" : $"Audit chain verification FAILED at #{result.BrokenAtSequence}: {result.Message}",
            new { result.Valid, result.Checked, result.BrokenAtSequence });
        await db.SaveChangesAsync(ct);
        return result with { Unsealed = unsealed, AnchoredAfterSequence = anchor.AfterSequence > 0 ? anchor.AfterSequence : null };
    }

    private IQueryable<AuditLog> Filter(AuditQuery q)
    {
        var tenant = ctx.TenantId;
        var query = db.AuditLogs.AsNoTracking().Where(a => a.TenantId == tenant);
        if (!string.IsNullOrWhiteSpace(q.Actor))
        {
            var actor = q.Actor.Trim();
            query = Guid.TryParse(actor, out var actorId)
                ? query.Where(a => a.ActorId == actorId)
                : query.Where(a => a.ActorName != null && a.ActorName.Contains(actor));
        }
        if (!string.IsNullOrWhiteSpace(q.EntityType)) query = query.Where(a => a.EntityType == q.EntityType);
        if (q.EntityId is { } eid) query = query.Where(a => a.EntityId == eid);
        if (!string.IsNullOrWhiteSpace(q.Action))
        {
            var action = q.Action.Trim();
            query = action.EndsWith('*') ? query.Where(a => a.Action.StartsWith(action.TrimEnd('*'))) : query.Where(a => a.Action == action);
        }
        if (q.From is { } from) query = query.Where(a => a.OccurredAt >= DateTime.SpecifyKind(from, DateTimeKind.Utc));
        if (q.To is { } to) query = query.Where(a => a.OccurredAt <= DateTime.SpecifyKind(to, DateTimeKind.Utc));
        if (!string.IsNullOrWhiteSpace(q.Ip)) query = query.Where(a => a.IpAddress != null && a.IpAddress.StartsWith(q.Ip.Trim()));
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = q.Search.Trim();
            query = query.Where(a => a.Summary != null && a.Summary.Contains(term));
        }
        return query;
    }

    public static AuditEntryDto ToDto(AuditLog a) =>
        new(a.Id, a.Sequence, a.OccurredAt, a.ActorType.ToString(), a.ActorId, a.ActorName, a.Action, a.EntityType, a.EntityId, a.Summary, a.IpAddress, a.Hash != "");

    /// <summary>CSV cell: quoted, with spreadsheet formula injection neutralised.</summary>
    public static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var v = value;
        if (v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
        return "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}

/// <summary>Hash-chain verification that can start after a retention anchor (purged prefix).</summary>
public static class AuditChainVerifier
{
    public static async Task<AuditChainVerificationDto> VerifyAsync(DeskDbContext db, Guid? tenantId, long afterSequence, string anchorHash, CancellationToken ct)
    {
        long checkedCount = 0;
        var prev = anchorHash;
        var lastSeq = afterSequence;
        const int page = 2000;
        var now = db.Clock.UtcNow;
        while (true)
        {
            var rows = await db.AuditLogs.AsNoTracking()
                .Where(a => a.TenantId == tenantId && a.Hash != "" && a.Sequence > lastSeq)
                .OrderBy(a => a.Sequence).Take(page).ToListAsync(ct);
            if (rows.Count == 0) break;
            foreach (var row in rows)
            {
                string? problem = null;
                if (row.Sequence != lastSeq + 1) problem = $"Sequence gap: entry #{lastSeq + 1} is missing before #{row.Sequence} (deleted).";
                else if (row.PrevHash != prev) problem = $"Entry #{row.Sequence} does not link to the previous entry's hash (chain re-written).";
                else if (AuditChain.ComputeHash(prev, row) != row.Hash) problem = $"Entry #{row.Sequence} was modified after it was sealed (content does not match its hash).";
                if (problem is not null) return new AuditChainVerificationDto(false, checkedCount, row.Sequence, row.Id, problem, now, 0, null);
                prev = row.Hash;
                lastSeq = row.Sequence;
                checkedCount++;
            }
        }
        return new AuditChainVerificationDto(true, checkedCount, null, null,
            checkedCount == 0 ? "No sealed audit entries yet." : $"Chain intact: {checkedCount} sealed entries verified.", now, 0, null);
    }
}

/// <summary>Flattens the before/after state recorded in an audit entry into a list of differences.</summary>
public static class AuditDiff
{
    public static IReadOnlyList<AuditDiffDto> Compute(JsonElement changes)
    {
        var diffs = new List<AuditDiffDto>();
        if (changes.ValueKind == JsonValueKind.Array)
        {
            // [{"field":"priority","from":"Medium","to":"High"}]
            foreach (var item in changes.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var field = Str(item, "field") ?? Str(item, "name") ?? "?";
                var from = Raw(item, "from") ?? Raw(item, "before");
                var to = Raw(item, "to") ?? Raw(item, "after");
                diffs.Add(new AuditDiffDto(field, from, to, Kind(from, to)));
            }
            return diffs;
        }
        if (changes.ValueKind != JsonValueKind.Object) return diffs;
        var before = Prop(changes, "before") ?? Prop(changes, "from");
        var after = Prop(changes, "after") ?? Prop(changes, "to");
        if (before is not null || after is not null)
        {
            var a = Flatten(before);
            var b = Flatten(after);
            foreach (var key in a.Keys.Union(b.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                a.TryGetValue(key, out var x);
                b.TryGetValue(key, out var y);
                if (x == y) continue;
                diffs.Add(new AuditDiffDto(key, x, y, Kind(x, y)));
            }
            return diffs;
        }
        foreach (var p in changes.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.Object && (Prop(p.Value, "from") is not null || Prop(p.Value, "to") is not null))
            {
                var from = Raw(p.Value, "from");
                var to = Raw(p.Value, "to");
                diffs.Add(new AuditDiffDto(p.Name, from, to, Kind(from, to)));
            }
            else
            {
                foreach (var (k, v) in Flatten(p.Value, p.Name)) diffs.Add(new AuditDiffDto(k, null, v, "added"));
            }
        }
        return diffs;
    }

    private static string Kind(string? from, string? to) => from is null ? "added" : to is null ? "removed" : "changed";

    private static JsonElement? Prop(JsonElement e, string name)
    {
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static string? Str(JsonElement e, string name) => Prop(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    private static string? Raw(JsonElement e, string name) => Prop(e, name) is { } v ? Scalar(v) : null;

    private static string? Scalar(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => v.GetString(),
        _ => v.GetRawText()
    };

    public static Dictionary<string, string?> Flatten(JsonElement? element, string prefix = "")
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (element is { } e) Walk(e, prefix, result);
        return result;
    }

    private static void Walk(JsonElement e, string path, Dictionary<string, string?> into)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var any = false;
                foreach (var p in e.EnumerateObject())
                {
                    any = true;
                    Walk(p.Value, string.IsNullOrEmpty(path) ? p.Name : $"{path}.{p.Name}", into);
                }
                if (!any && path.Length > 0) into[path] = "{}";
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in e.EnumerateArray()) Walk(item, $"{path}[{i++}]", into);
                if (i == 0 && path.Length > 0) into[path] = "[]";
                break;
            default:
                into[path.Length == 0 ? "value" : path] = Scalar(e);
                break;
        }
    }
}
