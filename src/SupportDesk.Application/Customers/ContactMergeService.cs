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
/// Duplicate detection (exact normalised e-mail/phone matches across the tenant plus fuzzy name similarity within the
/// same customer, scored by <see cref="DuplicateScoring"/>) and transactional, idempotent contact merge.
/// </summary>
public sealed class ContactMergeService(
    DeskDbContext db,
    RequestContext ctx,
    IEventPublisher events,
    IAuditWriter audit,
    TicketHistory history,
    ContactService contacts)
{
    /// <summary>Fields the merge request may ask to take from the merged contact (overriding the survivor's value).</summary>
    public static readonly string[] MergeableFields =
        ["fullName", "firstName", "lastName", "email", "phone", "mobile", "whatsAppNumber", "jobTitle", "customerId", "locationId", "customerDepartmentId",
         "preferredLanguage", "preferredChannel", "timeZone"];

    // ───────────────────────────── Duplicate detection ─────────────────────────────

    /// <summary>Possible duplicates of one contact, best first.</summary>
    public async Task<List<DuplicateCandidateDto>> DuplicatesOfAsync(Guid contactId, double? minScore = null, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersView);
        var k = await db.Contacts.AsNoTracking().VisibleTo(ctx, db).FirstOrDefaultAsync(x => x.Id == contactId, ct) ?? throw new NotFoundException("Contact", contactId);
        var threshold = minScore ?? DuplicateScoring.DefaultThreshold;
        var probe = await ProbeAsync(k, ct);
        var emails = probe.ExtraEmails!.Append(probe.Email).Where(e => e is not null).Select(e => e!).Distinct().ToList();
        // Normalised numbers plus their national (10-digit) and default-country forms so +91 / 0-prefixed variants match.
        var phones = probe.Phones.Concat(probe.Phones.Select(DuplicateScoring.NationalNumber).Where(p => p is not null).SelectMany(n => new[] { n!, "91" + n }))
            .Distinct().ToList();

        var viaIdentity = await db.ContactIdentities.AsNoTracking()
            .Where(i => i.ContactId != k.Id && ((i.Channel == Channel.Email && emails.Contains(i.Identifier)) ||
                                                ((i.Channel == Channel.Phone || i.Channel == Channel.WhatsApp || i.Channel == Channel.Sms) && phones.Contains(i.Identifier))))
            .Select(i => i.ContactId).Distinct().ToListAsync(ct);
        var candidates = db.Contacts.AsNoTracking().VisibleTo(ctx, db).Where(c => c.Id != k.Id && c.Status == ContactStatus.Active);
        var exact = await candidates.Where(c => viaIdentity.Contains(c.Id) || (c.EmailNormalized != null && emails.Contains(c.EmailNormalized)) ||
                                                (c.PhoneNormalized != null && phones.Contains(c.PhoneNormalized)))
            .Take(200).ToListAsync(ct);
        var sameCustomer = k.CustomerId is { } cid
            ? await candidates.Where(c => c.CustomerId == cid).OrderBy(c => c.FullName).Take(1000).ToListAsync(ct)
            : [];

        var pool = exact.Concat(sameCustomer).DistinctBy(c => c.Id).ToList();
        var probes = await ProbesAsync(pool, ct);
        var scored = pool.Select(c => (Contact: c, Score: DuplicateScoring.Score(probe, probes[c.Id])))
            .Where(x => x.Score.Score >= threshold).OrderByDescending(x => x.Score.Score).Take(25).ToList();
        var mapped = await contacts.MapListAsync(scored.Select(x => x.Contact).ToList(), ct);
        return scored.Select((x, i) => new DuplicateCandidateDto(mapped[i], x.Score.Score, x.Score.Confidence, x.Score.Reasons)).ToList();
    }

    /// <summary>
    /// Tenant-wide (or one customer's) scan: pairs sharing a normalised e-mail or phone, plus fuzzy-name pairs within
    /// the same customer. Returns the best pairs first.
    /// </summary>
    public async Task<List<DuplicatePairDto>> ScanAsync(Guid? customerId, double? minScore = null, int limit = 50, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersView);
        var threshold = minScore ?? DuplicateScoring.DefaultThreshold;
        var active = db.Contacts.AsNoTracking().VisibleTo(ctx, db).Where(c => c.Status == ContactStatus.Active);
        if (customerId is { } cid) active = active.Where(c => c.CustomerId == cid);

        var dupEmails = await active.Where(c => c.EmailNormalized != null).GroupBy(c => c.EmailNormalized!).Where(g => g.Count() > 1).Select(g => g.Key).Take(500).ToListAsync(ct);
        var dupPhones = await active.Where(c => c.PhoneNormalized != null).GroupBy(c => c.PhoneNormalized!).Where(g => g.Count() > 1).Select(g => g.Key).Take(500).ToListAsync(ct);
        var exactRows = await active.Where(c => (c.EmailNormalized != null && dupEmails.Contains(c.EmailNormalized)) || (c.PhoneNormalized != null && dupPhones.Contains(c.PhoneNormalized)))
            .Take(2000).ToListAsync(ct);

        // Fuzzy names within customers (bounded: the customers of the exact matches plus the requested one / the first 200 customers with ≥2 contacts).
        var busyCustomers = await active.Where(c => c.CustomerId != null).GroupBy(c => c.CustomerId!.Value).Where(g => g.Count() > 1)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).Take(200).ToListAsync(ct);
        var customerRows = await active.Where(c => c.CustomerId != null && busyCustomers.Contains(c.CustomerId.Value)).Take(5000).ToListAsync(ct);

        var pool = exactRows.Concat(customerRows).DistinctBy(c => c.Id).ToList();
        var probes = await ProbesAsync(pool, ct);
        var pairs = new List<(Contact A, Contact B, DuplicateScore S)>();
        var seen = new HashSet<(Guid, Guid)>();
        void Consider(Contact a, Contact b)
        {
            if (a.Id == b.Id) return;
            var key = a.Id.CompareTo(b.Id) < 0 ? (a.Id, b.Id) : (b.Id, a.Id);
            if (!seen.Add(key)) return;
            var s = DuplicateScoring.Score(probes[a.Id], probes[b.Id]);
            if (s.Score >= threshold) pairs.Add((a, b, s));
        }
        foreach (var group in exactRows.GroupBy(c => c.EmailNormalized).Where(g => g.Key is not null && g.Count() > 1))
            foreach (var a in group) foreach (var b in group) Consider(a, b);
        foreach (var group in exactRows.GroupBy(c => c.PhoneNormalized).Where(g => g.Key is not null && g.Count() > 1))
            foreach (var a in group) foreach (var b in group) Consider(a, b);
        foreach (var group in customerRows.GroupBy(c => c.CustomerId))
        {
            var list = group.ToList();
            for (var i = 0; i < list.Count; i++)
            for (var j = i + 1; j < list.Count; j++)
                Consider(list[i], list[j]);
        }

        var top = pairs.OrderByDescending(p => p.S.Score).Take(Math.Clamp(limit, 1, 200)).ToList();
        var involved = top.SelectMany(p => new[] { p.A, p.B }).DistinctBy(c => c.Id).ToList();
        var mapped = (await contacts.MapListAsync(involved, ct)).ToDictionary(c => c.Id);
        return top.Select(p => new DuplicatePairDto(mapped[p.A.Id], mapped[p.B.Id], p.S.Score, p.S.Confidence, p.S.Reasons)).ToList();
    }

    private async Task<DuplicateProbe> ProbeAsync(Contact k, CancellationToken ct) => (await ProbesAsync([k], ct))[k.Id];

    private async Task<Dictionary<Guid, DuplicateProbe>> ProbesAsync(IReadOnlyList<Contact> list, CancellationToken ct)
    {
        var ids = list.Select(c => c.Id).ToList();
        var identities = await db.ContactIdentities.AsNoTracking().Where(i => ids.Contains(i.ContactId))
            .Select(i => new { i.ContactId, i.Channel, i.Identifier }).ToListAsync(ct);
        var byContact = identities.ToLookup(i => i.ContactId);
        return list.ToDictionary(c => c.Id, c =>
        {
            var phones = new[] { c.PhoneNormalized, TextNormalization.NormalizePhone(c.Mobile), TextNormalization.NormalizePhone(c.WhatsAppNumber) }
                .Concat(byContact[c.Id].Where(i => i.Channel is Channel.Phone or Channel.WhatsApp or Channel.Sms).Select(i => i.Identifier))
                .Where(p => p is not null).Select(p => p!).Distinct().ToList();
            var emails = byContact[c.Id].Where(i => i.Channel == Channel.Email).Select(i => i.Identifier).ToList();
            return new DuplicateProbe(c.Id, c.FullName, c.EmailNormalized, phones, c.CustomerId, emails);
        });
    }

    // ───────────────────────────── Merge ─────────────────────────────

    /// <summary>
    /// Merges one contact into another inside a transaction. Tickets, conversation entries, channel conversations and
    /// messages, calls, identities, consents, CSAT responses, notifications, devices and community/KB authorship move
    /// to the survivor; the survivor keeps its own values and fills empty fields from the merged contact (richest data
    /// wins, VIP/verification/portal access are combined). The merged contact is marked Merged with
    /// MergedIntoContactId, a ContactMergeLog keeps a snapshot, and the action is audited. Repeating the same merge
    /// returns the original result (idempotent).
    /// </summary>
    public async Task<ContactMergeResultDto> MergeAsync(MergeContactsRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ContactsMerge);
        if (r.SurvivorId == r.MergedId) throw new ValidationException(new Dictionary<string, string> { ["mergedId"] = "Choose two different contacts." });
        var unknown = (r.PreferMergedFields ?? []).Where(f => !MergeableFields.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unknown.Count > 0) throw new ValidationException(new Dictionary<string, string> { ["preferMergedFields"] = $"Unknown fields: {string.Join(", ", unknown)}" });

        var survivor = await db.Contacts.VisibleTo(ctx, db).FirstOrDefaultAsync(c => c.Id == r.SurvivorId, ct) ?? throw new NotFoundException("Contact", r.SurvivorId);
        var merged = await db.Contacts.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).VisibleTo(ctx, db).FirstOrDefaultAsync(c => c.Id == r.MergedId, ct)
                     ?? throw new NotFoundException("Contact", r.MergedId);

        if (merged.Status == ContactStatus.Merged)
        {
            if (merged.MergedIntoContactId == survivor.Id) return await PreviousResultAsync(survivor.Id, merged.Id, ct);
            throw new ConflictException("already_merged", "This contact was already merged into another contact.");
        }
        if (survivor.Status != ContactStatus.Active) throw new ConflictException("survivor_inactive", "The surviving contact must be active.");
        if (merged.Status == ContactStatus.Erased) throw new ConflictException("contact_erased", "Erased contacts cannot be merged.");

        ContactMergeResultDto? result = null;
        await db.InTransactionAsync(async () =>
        {
            // Claim the merged contact first: a concurrent merge of the same contact finds no row to flip.
            var claimed = await db.Contacts.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter])
                .Where(c => c.Id == merged.Id && c.Status != ContactStatus.Merged)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, ContactStatus.Merged).SetProperty(c => c.MergedIntoContactId, survivor.Id)
                    .SetProperty(c => c.PortalPasswordHash, (string?)null).SetProperty(c => c.IsPrimary, false), ct);
            if (claimed == 0) throw new ConflictException("already_merged", "This contact is being merged by someone else. Reload and try again.");
            db.Entry(merged).State = EntityState.Detached;

            var snapshot = new
            {
                merged.Id, merged.FullName, merged.FirstName, merged.LastName, merged.Email, merged.Phone, merged.Mobile, merged.WhatsAppNumber, merged.JobTitle,
                merged.CustomerId, merged.LocationId, merged.CustomerDepartmentId, merged.PreferredLanguage, PreferredChannel = merged.PreferredChannel?.ToString(),
                merged.TimeZone, merged.IsVip, merged.IsPrimary, PortalAccess = merged.PortalAccess.ToString(), merged.ErpContactId, merged.CustomFieldsJson,
                merged.CreatedAt
            };
            var copied = EnrichSurvivor(survivor, merged, r.PreferMergedFields ?? []);

            var mergedId = merged.Id;
            var survivorId = survivor.Id;
            var tickets = await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).Where(t => t.ContactId == mergedId)
                .Select(t => new { t.Id, t.Number }).ToListAsync(ct);
            var ticketCount = await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).Where(t => t.ContactId == mergedId)
                .ExecuteUpdateAsync(u => u.SetProperty(t => t.ContactId, survivorId), ct);
            var threads = await db.TicketThreads.Where(t => t.AuthorContactId == mergedId).ExecuteUpdateAsync(u => u.SetProperty(t => t.AuthorContactId, survivorId), ct);
            var conversations = await db.Conversations.Where(c => c.ContactId == mergedId).ExecuteUpdateAsync(u => u.SetProperty(c => c.ContactId, survivorId), ct);
            var messages = await db.Messages.Where(m => m.ContactId == mergedId).ExecuteUpdateAsync(u => u.SetProperty(m => m.ContactId, survivorId), ct);
            var csat = await db.SatisfactionResponses.Where(s => s.ContactId == mergedId).ExecuteUpdateAsync(u => u.SetProperty(s => s.ContactId, survivorId), ct);
            var identities = await db.ContactIdentities.Where(i => i.ContactId == mergedId).ExecuteUpdateAsync(u => u.SetProperty(i => i.ContactId, survivorId), ct);
            var other = 0;
            other += await db.CallRecords.Where(c => c.ContactId == mergedId).ExecuteUpdateAsync(u => u.SetProperty(c => c.ContactId, survivorId), ct);
            other += await db.TicketAttachments.Where(a => a.UploadedByContactId == mergedId).ExecuteUpdateAsync(u => u.SetProperty(a => a.UploadedByContactId, survivorId), ct);
            other += await db.Notifications.Where(n => n.ContactId == mergedId).ExecuteUpdateAsync(u => u.SetProperty(n => n.ContactId, survivorId), ct);
            other += await db.PushDevices.Where(d => d.ContactId == mergedId).ExecuteUpdateAsync(u => u.SetProperty(d => d.ContactId, survivorId), ct);
            other += await db.ArticleFeedback.Where(f => f.ContactId == mergedId).ExecuteUpdateAsync(u => u.SetProperty(f => f.ContactId, survivorId), ct);
            other += await db.CommunityTopics.Where(t => t.AuthorContactId == mergedId).ExecuteUpdateAsync(u => u.SetProperty(t => t.AuthorContactId, survivorId), ct);
            other += await db.CommunityPosts.Where(p => p.AuthorContactId == mergedId).ExecuteUpdateAsync(u => u.SetProperty(p => p.AuthorContactId, survivorId), ct);

            // Consents: the most recently recorded decision per channel and purpose wins.
            var consents = 0;
            var mergedConsents = await db.ContactConsents.Where(c => c.ContactId == mergedId).ToListAsync(ct);
            var survivorConsents = await db.ContactConsents.Where(c => c.ContactId == survivorId).ToListAsync(ct);
            foreach (var mc in mergedConsents)
            {
                var existing = survivorConsents.FirstOrDefault(s => s.Channel == mc.Channel && s.Purpose == mc.Purpose);
                if (existing is null)
                {
                    mc.ContactId = survivorId;
                    consents++;
                    continue;
                }
                if (mc.RecordedAt > existing.RecordedAt && mc.Status != ConsentStatus.Unknown)
                {
                    existing.Status = mc.Status;
                    existing.Source = $"{mc.Source} (merged contact)";
                    existing.RecordedAt = mc.RecordedAt;
                    consents++;
                }
                db.ContactConsents.Remove(mc);
            }

            var counts = new { tickets = ticketCount, threads, conversations, messages, identities, consents, satisfactionResponses = csat, other };
            var log = new ContactMergeLog
            {
                SurvivorContactId = survivorId, MergedContactId = mergedId, MergedById = ctx.UserId, MergedAt = db.Clock.UtcNow,
                SnapshotJson = Json.Serialize(new { merged = snapshot, counts, fieldsCopied = copied, mergedBy = ctx.ActorName })
            };
            db.ContactMergeLogs.Add(log);

            foreach (var t in tickets)
                history.Record(new Ticket { Id = t.Id, Number = t.Number }, TicketEventTypes.FieldsChanged,
                    $"Contact {snapshot.FullName} merged into {survivor.FullName}", [new FieldChange("contactId", mergedId.ToString(), survivorId.ToString())]);

            audit.Write(AuditActions.ContactMerged, "Contact", survivorId, $"Contact '{snapshot.FullName}' merged into '{survivor.FullName}'",
                new { mergedId, survivorId, counts, fieldsCopied = copied });
            events.Publish(EventTypes.ContactMerged, "Contact", survivorId, new EntityEventPayload(survivorId, survivor.FullName,
                [new FieldChange("mergedContactId", mergedId.ToString(), survivorId.ToString())]));
            if (copied.Count > 0)
                events.Publish(EventTypes.ContactUpdated, "Contact", survivorId, new EntityEventPayload(survivorId, survivor.FullName,
                    copied.Select(f => new FieldChange(f, null, "from merged contact")).ToList()));
            await db.SaveChangesAsync(ct);
            result = new ContactMergeResultDto(survivorId, mergedId, false, ticketCount, threads, conversations, messages, identities, consents, csat, other, copied, log.Id);
        }, ct);
        return result!;
    }

    private static List<string> EnrichSurvivor(Contact s, Contact m, IReadOnlyList<string> preferMerged)
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
        Take("fullName", s.FullName, m.FullName, v => s.FullName = v);
        Take("firstName", s.FirstName, m.FirstName, v => s.FirstName = v);
        Take("lastName", s.LastName, m.LastName, v => s.LastName = v);
        Take("email", s.Email, m.Email, v => { s.Email = v; s.EmailNormalized = m.EmailNormalized; });
        Take("phone", s.Phone, m.Phone, v => { s.Phone = v; s.PhoneNormalized = m.PhoneNormalized; });
        Take("mobile", s.Mobile, m.Mobile, v => s.Mobile = v);
        Take("whatsAppNumber", s.WhatsAppNumber, m.WhatsAppNumber, v => s.WhatsAppNumber = v);
        Take("jobTitle", s.JobTitle, m.JobTitle, v => s.JobTitle = v);
        Take("customerId", s.CustomerId, m.CustomerId, v => s.CustomerId = v);
        if (s.CustomerId == m.CustomerId)
        {
            Take("locationId", s.LocationId, m.LocationId, v => s.LocationId = v);
            Take("customerDepartmentId", s.CustomerDepartmentId, m.CustomerDepartmentId, v => s.CustomerDepartmentId = v);
        }
        if (Prefer("preferredLanguage") && s.PreferredLanguage != m.PreferredLanguage) { s.PreferredLanguage = m.PreferredLanguage; copied.Add("preferredLanguage"); }
        Take("preferredChannel", s.PreferredChannel, m.PreferredChannel, v => s.PreferredChannel = v);
        Take("timeZone", s.TimeZone, m.TimeZone, v => s.TimeZone = v);
        Take("erpContactId", s.ErpContactId, m.ErpContactId, v => s.ErpContactId = v);
        if (m.IsVip && !s.IsVip) { s.IsVip = true; copied.Add("isVip"); }
        if (m.PortalAccess > s.PortalAccess) { s.PortalAccess = m.PortalAccess; copied.Add("portalAccess"); }
        if (s.PortalPasswordHash is null && m.PortalPasswordHash is not null) { s.PortalPasswordHash = m.PortalPasswordHash; copied.Add("portalPassword"); }
        if (m.EmailVerifiedAt is { } ev && (s.EmailVerifiedAt is null || ev < s.EmailVerifiedAt)) s.EmailVerifiedAt = ev;
        if (m.PhoneVerifiedAt is { } pv && (s.PhoneVerifiedAt is null || pv < s.PhoneVerifiedAt)) s.PhoneVerifiedAt = pv;
        if (m.PortalLastLoginAt > s.PortalLastLoginAt || (s.PortalLastLoginAt is null && m.PortalLastLoginAt is not null)) s.PortalLastLoginAt = m.PortalLastLoginAt;
        // Custom fields: survivor values win, gaps are filled.
        var sf = Json.Object(s.CustomFieldsJson);
        var added = false;
        foreach (var (key, value) in Json.Object(m.CustomFieldsJson))
            if (!sf.ContainsKey(key)) { sf[key] = value; added = true; }
        if (added) { s.CustomFieldsJson = Json.Serialize(sf); copied.Add("customFields"); }
        return copied;
    }

    private async Task<ContactMergeResultDto> PreviousResultAsync(Guid survivorId, Guid mergedId, CancellationToken ct)
    {
        var log = await db.ContactMergeLogs.AsNoTracking().Where(l => l.SurvivorContactId == survivorId && l.MergedContactId == mergedId)
            .OrderByDescending(l => l.MergedAt).FirstOrDefaultAsync(ct);
        var snapshot = Json.Object(log?.SnapshotJson);
        int Count(string key) => snapshot.TryGetValue("counts", out var c) && c.TryGetProperty(key, out var v) && v.TryGetInt32(out var n) ? n : 0;
        var fields = snapshot.TryGetValue("fieldsCopied", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.Array
            ? f.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : [];
        return new ContactMergeResultDto(survivorId, mergedId, true, Count("tickets"), Count("threads"), Count("conversations"), Count("messages"),
            Count("identities"), Count("consents"), Count("satisfactionResponses"), Count("other"), fields, log?.Id);
    }

    public async Task<List<ContactMergeLogDto>> MergeHistoryAsync(Guid contactId, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersView);
        _ = await db.Contacts.AsNoTracking().IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).VisibleTo(ctx, db).FirstOrDefaultAsync(c => c.Id == contactId, ct)
            ?? throw new NotFoundException("Contact", contactId);
        var logs = await db.ContactMergeLogs.AsNoTracking().Where(l => l.SurvivorContactId == contactId || l.MergedContactId == contactId)
            .OrderByDescending(l => l.MergedAt).Take(50).ToListAsync(ct);
        var userIds = logs.Where(l => l.MergedById != null).Select(l => l.MergedById!.Value).Distinct().ToList();
        var users = await db.Users.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var mergedIds = logs.Select(l => l.MergedContactId).Distinct().ToList();
        var names = await db.Contacts.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(c => mergedIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.FullName, ct);
        return logs.Select(l => new ContactMergeLogDto(l.Id, l.SurvivorContactId, l.MergedContactId, names.GetValueOrDefault(l.MergedContactId), l.MergedById,
            l.MergedById is { } u ? users.GetValueOrDefault(u) : null, l.MergedAt, Json.Element(l.SnapshotJson))).ToList();
    }
}
