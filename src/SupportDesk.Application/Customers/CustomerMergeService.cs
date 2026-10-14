using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Customers;
using SupportDesk.Domain;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Customers;

/// <summary>
/// Organisation merge (customers.merge): folds a duplicate customer into the one that is kept, inside one transaction.
/// Contacts, tickets (including deleted ones), locations, customer departments, contracts and entitlements, installations,
/// licences, assets, service activities, subsidiaries, legal holds, KB access rules and analytics/search references move
/// to the survivor; ERP links and e-mail domains are carried over. The survivor keeps its own values and fills blanks
/// from the merged customer (or takes the fields listed in PreferMergedFields). The merged customer is marked Merged and
/// hidden (soft-deleted, read-only, cannot be restored); a CustomerMergeLog keeps a snapshot and the moved counts, the
/// merge is audited, and repeating it returns the original result (idempotent).
/// </summary>
public sealed class CustomerMergeService(
    DeskDbContext db,
    RequestContext ctx,
    IEventPublisher events,
    IAuditWriter audit,
    TicketHistory history)
{
    public const string AuditAction = "customer.merged";

    /// <summary>Fields whose value may be taken from the merged customer.</summary>
    public static readonly string[] MergeableFields =
        ["name", "code", "tier", "category", "industry", "serviceRegion", "accountManagerUserId", "customerGroupId", "preferredLanguage", "timeZone",
         "website", "phone", "email", "notes", "erpCustomerId"];

    public async Task<CustomerMergeResultDto> MergeAsync(MergeCustomersRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersMerge);
        if (r.KeepId == r.MergeId) throw new ValidationException(new Dictionary<string, string> { ["mergeId"] = "Choose two different customers." });
        var prefer = (r.PreferMergedFields ?? []).Select(f => f.Trim()).Where(f => f.Length > 0).ToList();
        var unknown = prefer.Where(f => !MergeableFields.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unknown.Count > 0) throw new ValidationException(new Dictionary<string, string> { ["preferMergedFields"] = $"Unknown fields: {string.Join(", ", unknown)}" });

        var survivor = await db.Customers.VisibleTo(ctx).FirstOrDefaultAsync(c => c.Id == r.KeepId, ct) ?? throw new NotFoundException("Customer", r.KeepId);
        var merged = await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).VisibleTo(ctx).FirstOrDefaultAsync(c => c.Id == r.MergeId, ct)
                     ?? throw new NotFoundException("Customer", r.MergeId);
        if (merged.Status == CustomerStatus.Merged)
        {
            var previous = await db.Set<CustomerMergeLog>().AsNoTracking().Where(l => l.MergedCustomerId == merged.Id).OrderByDescending(l => l.MergedAt).FirstOrDefaultAsync(ct);
            if (previous?.SurvivorCustomerId == survivor.Id) return PreviousResult(previous);
            throw new ConflictException("already_merged", "This customer was already merged into another customer.");
        }
        if (merged.IsDeleted) throw new ConflictException("customer_deleted", "Restore the deleted customer before merging it.");
        if (survivor.Status == CustomerStatus.Merged) throw new ConflictException("survivor_merged", "The customer to keep was itself merged.");

        // Hierarchy: the survivor may be a direct subsidiary of the merged customer (it takes its place); deeper would loop.
        var mergedSubtree = await SubtreeAsync(merged.Id, ct);
        if (mergedSubtree.Contains(survivor.Id) && survivor.ParentCustomerId != merged.Id)
            throw new ValidationException(new Dictionary<string, string>
            {
                ["keepId"] = "The customer to keep is a sub-subsidiary of the one being merged. Merge into a higher level of the hierarchy instead."
            });

        CustomerMergeResultDto? result = null;
        await db.InTransactionAsync(async () =>
        {
            // Claim the merged customer first: a concurrent merge of the same customer finds nothing to flip.
            var now = db.Clock.UtcNow;
            var mergedId = merged.Id;
            var survivorId = survivor.Id;
            var claimed = await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter])
                .Where(c => c.Id == mergedId && c.Status != CustomerStatus.Merged)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, CustomerStatus.Merged).SetProperty(c => c.IsDeleted, true).SetProperty(c => c.DeletedAt, now), ct);
            if (claimed == 0) throw new ConflictException("already_merged", "This customer is being merged by someone else. Reload and try again.");

            var snapshot = new
            {
                merged.Id, merged.Name, merged.Code, Status = merged.Status.ToString(), merged.Tier, merged.Category, merged.Industry, merged.ServiceRegion,
                merged.AccountManagerUserId, merged.CustomerGroupId, merged.ParentCustomerId, merged.PreferredLanguage, merged.TimeZone, merged.Website,
                merged.Phone, merged.Email, merged.DomainsJson, merged.Notes, merged.CustomFieldsJson, merged.ErpCustomerId, merged.ErpCode, merged.CreatedAt
            };
            var copied = EnrichSurvivor(survivor, merged, prefer);
            // Unique keys that move to the survivor are cleared on the (now hidden) merged row first.
            if (copied.Contains("code"))
                await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).Where(c => c.Id == mergedId)
                    .ExecuteUpdateAsync(u => u.SetProperty(c => c.Code, (string?)null), ct);
            if (copied.Contains("erpCustomerId"))
                await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).Where(c => c.Id == mergedId)
                    .ExecuteUpdateAsync(u => u.SetProperty(c => c.ErpCustomerId, (Guid?)null).SetProperty(c => c.ErpCode, (string?)null), ct);
            // Domains identify the customer's contacts: the survivor takes them all.
            await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).Where(c => c.Id == mergedId)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.DomainsJson, "[]"), ct);
            db.Entry(merged).State = EntityState.Detached;

            // The survivor takes the merged customer's place in the hierarchy when it was its subsidiary.
            if (survivor.ParentCustomerId == mergedId) survivor.ParentCustomerId = merged.ParentCustomerId == survivorId ? null : merged.ParentCustomerId;

            var tickets = await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(t => t.CustomerId == mergedId)
                .Select(t => new { t.Id, t.Number }).ToListAsync(ct);
            var moved = new Dictionary<string, int>
            {
                ["contacts"] = await db.Contacts.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).Where(k => k.CustomerId == mergedId)
                    .ExecuteUpdateAsync(u => u.SetProperty(k => k.CustomerId, survivorId), ct),
                ["tickets"] = await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).Where(t => t.CustomerId == mergedId)
                    .ExecuteUpdateAsync(u => u.SetProperty(t => t.CustomerId, survivorId), ct),
                ["locations"] = await db.CustomerLocations.Where(l => l.CustomerId == mergedId).ExecuteUpdateAsync(u => u.SetProperty(l => l.CustomerId, survivorId), ct),
                ["customerDepartments"] = await db.CustomerDepartments.Where(d => d.CustomerId == mergedId).ExecuteUpdateAsync(u => u.SetProperty(d => d.CustomerId, survivorId), ct),
                ["contracts"] = await db.Contracts.Where(c => c.CustomerId == mergedId).ExecuteUpdateAsync(u => u.SetProperty(c => c.CustomerId, survivorId), ct),
                ["entitlements"] = await db.Entitlements.Where(e => e.CustomerId == mergedId).ExecuteUpdateAsync(u => u.SetProperty(e => e.CustomerId, survivorId), ct),
                ["installations"] = await db.Installations.Where(i => i.CustomerId == mergedId).ExecuteUpdateAsync(u => u.SetProperty(i => i.CustomerId, survivorId), ct),
                ["licences"] = await db.Licences.Where(l => l.CustomerId == mergedId).ExecuteUpdateAsync(u => u.SetProperty(l => l.CustomerId, survivorId), ct),
                ["assets"] = await db.CustomerAssets.Where(a => a.CustomerId == mergedId).ExecuteUpdateAsync(u => u.SetProperty(a => a.CustomerId, survivorId), ct),
                ["serviceActivities"] = await db.ServiceActivities.Where(a => a.CustomerId == mergedId).ExecuteUpdateAsync(u => u.SetProperty(a => a.CustomerId, survivorId), ct),
                ["subsidiaries"] = await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).Where(c => c.ParentCustomerId == mergedId && c.Id != survivorId)
                    .ExecuteUpdateAsync(u => u.SetProperty(c => c.ParentCustomerId, survivorId), ct),
                // A hold on the merged customer must keep covering its records after they move.
                ["legalHolds"] = await db.LegalHolds.Where(h => h.CustomerId == mergedId).ExecuteUpdateAsync(u => u.SetProperty(h => h.CustomerId, survivorId), ct),
                ["kbAccessRules"] = await db.ArticleAccessRules.Where(a => a.CustomerId == mergedId).ExecuteUpdateAsync(u => u.SetProperty(a => a.CustomerId, survivorId), ct),
            };
            // Derived references (no history value of their own).
            await db.TicketMetrics.Where(m => m.CustomerId == mergedId).ExecuteUpdateAsync(u => u.SetProperty(m => m.CustomerId, survivorId), ct);
            await db.SearchDocuments.Where(d => d.CustomerId == mergedId && d.EntityType != "Customer").ExecuteUpdateAsync(u => u.SetProperty(d => d.CustomerId, survivorId), ct);

            var counts = moved.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
            var log = new CustomerMergeLog
            {
                SurvivorCustomerId = survivorId, MergedCustomerId = mergedId, MergedById = ctx.UserId, MergedAt = now,
                SnapshotJson = Json.Serialize(new { merged = snapshot, counts = moved, fieldsCopied = copied, mergedBy = ctx.ActorName, survivorName = survivor.Name })
            };
            db.Set<CustomerMergeLog>().Add(log);

            foreach (var t in tickets)
                history.Record(new Ticket { Id = t.Id, Number = t.Number }, TicketEventTypes.FieldsChanged, $"Customer {snapshot.Name} merged into {survivor.Name}",
                    [new FieldChange("customerId", mergedId.ToString(), survivorId.ToString())]);

            audit.Write(AuditAction, "Customer", survivorId, $"Customer '{snapshot.Name}' merged into '{survivor.Name}'",
                new { mergedId, survivorId, counts = moved, fieldsCopied = copied });
            events.Publish(EventTypes.CustomerUpdated, "Customer", survivorId, new EntityEventPayload(survivorId, survivor.Name,
                [new FieldChange("mergedCustomerId", mergedId.ToString(), survivorId.ToString()), .. copied.Select(f => new FieldChange(f, null, "from merged customer"))]));
            events.Publish(EventTypes.CustomerUpdated, "Customer", mergedId, new EntityEventPayload(mergedId, snapshot.Name,
                [new FieldChange("status", snapshot.Status, CustomerStatus.Merged.ToString()), new FieldChange("isDeleted", "false", "true")]));
            await db.SaveChangesAsync(ct);
            result = new CustomerMergeResultDto(survivorId, mergedId, false, moved, copied, log.Id);
        }, ct);
        return result!;
    }

    /// <summary>Merges involving the customer (as survivor or merged), newest first. The survivor must be visible to the caller.</summary>
    public async Task<List<CustomerMergeLogDto>> HistoryAsync(Guid customerId, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersView);
        var logs = await db.Set<CustomerMergeLog>().AsNoTracking().Where(l => l.SurvivorCustomerId == customerId || l.MergedCustomerId == customerId)
            .OrderByDescending(l => l.MergedAt).Take(50).ToListAsync(ct);
        var survivorIds = logs.Select(l => l.SurvivorCustomerId).Distinct().ToList();
        var visible = await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().VisibleTo(ctx)
            .Where(c => survivorIds.Contains(c.Id) || c.Id == customerId).Select(c => c.Id).ToListAsync(ct);
        if (!visible.Contains(customerId) && !logs.Any(l => visible.Contains(l.SurvivorCustomerId))) throw new NotFoundException("Customer", customerId);
        logs = logs.Where(l => visible.Contains(l.SurvivorCustomerId)).ToList();

        var customerIds = logs.SelectMany(l => new[] { l.SurvivorCustomerId, l.MergedCustomerId }).Distinct().ToList();
        var names = await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(c => customerIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var userIds = logs.Where(l => l.MergedById != null).Select(l => l.MergedById!.Value).Distinct().ToList();
        var users = await db.Users.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return logs.Select(l => new CustomerMergeLogDto(l.Id, l.SurvivorCustomerId, names.GetValueOrDefault(l.SurvivorCustomerId), l.MergedCustomerId,
            names.GetValueOrDefault(l.MergedCustomerId), l.MergedById, l.MergedById is { } u ? users.GetValueOrDefault(u) : null, l.MergedAt,
            Json.Element(l.SnapshotJson))).ToList();
    }

    private static List<string> EnrichSurvivor(Customer s, Customer m, IReadOnlyList<string> preferMerged)
    {
        var copied = new List<string>();
        bool Prefer(string field) => preferMerged.Contains(field, StringComparer.OrdinalIgnoreCase);
        void Take<T>(string field, T current, T incoming, Action<T> apply)
        {
            var empty = current is null || current is string { Length: 0 };
            var hasIncoming = incoming is not null && incoming is not string { Length: 0 };
            if (!hasIncoming || EqualityComparer<T>.Default.Equals(current, incoming)) return;
            if (!empty && !Prefer(field)) return;
            apply(incoming);
            copied.Add(field);
        }
        Take("name", s.Name, m.Name, v => s.Name = v);
        Take("code", s.Code, m.Code, v => s.Code = v);
        if (Prefer("tier") && s.Tier != m.Tier) { s.Tier = m.Tier; copied.Add("tier"); }
        Take("category", s.Category, m.Category, v => s.Category = v);
        Take("industry", s.Industry, m.Industry, v => s.Industry = v);
        Take("serviceRegion", s.ServiceRegion, m.ServiceRegion, v => s.ServiceRegion = v);
        Take("accountManagerUserId", s.AccountManagerUserId, m.AccountManagerUserId, v => s.AccountManagerUserId = v);
        Take("customerGroupId", s.CustomerGroupId, m.CustomerGroupId, v => s.CustomerGroupId = v);
        if (Prefer("preferredLanguage") && s.PreferredLanguage != m.PreferredLanguage) { s.PreferredLanguage = m.PreferredLanguage; copied.Add("preferredLanguage"); }
        Take("timeZone", s.TimeZone, m.TimeZone, v => s.TimeZone = v);
        Take("website", s.Website, m.Website, v => s.Website = v);
        Take("phone", s.Phone, m.Phone, v => s.Phone = v);
        Take("email", s.Email, m.Email, v => s.Email = v);
        if (!string.IsNullOrWhiteSpace(m.Notes) && s.Notes != m.Notes)
        {
            s.Notes = Prefer("notes") || string.IsNullOrWhiteSpace(s.Notes) ? m.Notes : $"{s.Notes}\n\n— From merged customer {m.Name}:\n{m.Notes}";
            if (s.Notes.Length > 8000) s.Notes = s.Notes[..8000];
            copied.Add("notes");
        }
        if (m.ErpCustomerId is not null && (s.ErpCustomerId is null || Prefer("erpCustomerId")) && s.ErpCustomerId != m.ErpCustomerId)
        {
            s.ErpCustomerId = m.ErpCustomerId;
            s.ErpCode = m.ErpCode;
            s.ErpSyncedAt = m.ErpSyncedAt;
            copied.Add("erpCustomerId");
        }
        var domains = Json.StringList(s.DomainsJson);
        var added = Json.StringList(m.DomainsJson).Where(d => !domains.Contains(d, StringComparer.OrdinalIgnoreCase)).ToList();
        if (added.Count > 0) { s.DomainsJson = Json.Serialize(domains.Concat(added).ToList()); copied.Add("domains"); }
        // Custom fields: survivor values win, gaps are filled.
        var sf = Json.Object(s.CustomFieldsJson);
        var gaps = false;
        foreach (var (key, value) in Json.Object(m.CustomFieldsJson))
            if (!sf.ContainsKey(key)) { sf[key] = value; gaps = true; }
        if (gaps) { s.CustomFieldsJson = Json.Serialize(sf); copied.Add("customFields"); }
        return copied;
    }

    private static CustomerMergeResultDto PreviousResult(CustomerMergeLog log)
    {
        var snapshot = Json.Object(log.SnapshotJson);
        var counts = new Dictionary<string, int>();
        if (snapshot.TryGetValue("counts", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Object)
            foreach (var p in c.EnumerateObject())
                if (p.Value.TryGetInt32(out var n)) counts[p.Name] = n;
        var fields = snapshot.TryGetValue("fieldsCopied", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.Array
            ? f.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : [];
        return new CustomerMergeResultDto(log.SurvivorCustomerId, log.MergedCustomerId, true, counts, fields, log.Id);
    }

    private async Task<HashSet<Guid>> SubtreeAsync(Guid rootId, CancellationToken ct)
    {
        var seen = new HashSet<Guid> { rootId };
        var frontier = new List<Guid> { rootId };
        while (frontier.Count > 0 && seen.Count < 2000)
        {
            var next = await db.Customers.AsNoTracking().Where(x => x.ParentCustomerId != null && frontier.Contains(x.ParentCustomerId.Value)).Select(x => x.Id).ToListAsync(ct);
            frontier = next.Where(seen.Add).ToList();
        }
        return seen;
    }
}
