using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SupportDesk.Application.Common;
using SupportDesk.Application.Identity;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Customers;
using SupportDesk.Domain;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Customers;

/// <summary>
/// Contacts: keyset-paged search, detail, create/update (identities kept in sync with e-mail/phone/WhatsApp),
/// additional channel identities, channel consent per purpose, portal access scope and portal invitations /
/// password-reset codes (sent with <see cref="ISystemMailer"/>). Respects the caller's customer scope.
/// </summary>
public sealed class ContactService(
    DeskDbContext db,
    RequestContext ctx,
    IEventPublisher events,
    IAuditWriter audit,
    CustomFieldValidator customFields,
    ISystemMailer mailer,
    TenantSettingsService settings,
    IConfiguration configuration,
    ContactResolver resolver)
{
    public const int MaxLimit = 200;

    // ───────────────────────────── List ─────────────────────────────

    public async Task<PagedResult<ContactListItemDto>> ListAsync(ContactQuery q, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersView);
        var query = db.Contacts.AsNoTracking().VisibleTo(ctx, db);
        var status = (q.Status ?? "Active").Trim();
        if (!status.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var statuses = Parse.Enums<ContactStatus>(status);
            query = query.Where(k => statuses.Contains(k.Status));
        }
        else query = query.Where(k => k.Status != ContactStatus.Erased);
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = q.Q.Trim();
            var digits = new string(term.Where(char.IsDigit).ToArray());
            var email = term.ToLowerInvariant();
            query = query.Where(k => k.FullName.Contains(term) || (k.EmailNormalized != null && k.EmailNormalized.Contains(email)) ||
                                     (digits.Length >= 4 && k.PhoneNormalized != null && k.PhoneNormalized.Contains(digits)) ||
                                     (k.JobTitle != null && k.JobTitle.Contains(term)));
        }
        if (q.CustomerId is { } cid) query = query.Where(k => k.CustomerId == cid);
        if (q.LocationId is { } lid) query = query.Where(k => k.LocationId == lid);
        if (q.IsVip is { } vip) query = query.Where(k => k.IsVip == vip);
        if (!string.IsNullOrWhiteSpace(q.PortalAccess))
        {
            var access = Parse.Enums<PortalAccessScope>(q.PortalAccess);
            query = query.Where(k => access.Contains(k.PortalAccess));
        }

        var limit = Math.Clamp(q.Limit ?? 50, 1, MaxLimit);
        int? total = q.IncludeTotal == true ? await query.CountAsync(ct) : null;
        var cursor = DbExtensions.DecodeCursor(q.Cursor);
        IQueryable<Contact> ordered;
        Func<Contact, string> keyOf;
        switch ((q.Sort ?? "name").ToLowerInvariant())
        {
            case "created" or "updated":
            {
                var byCreated = (q.Sort ?? "").Equals("created", StringComparison.OrdinalIgnoreCase);
                if (cursor is not null)
                {
                    var v = Parse.Iso(cursor.V);
                    query = byCreated ? query.Where(k => k.CreatedAt < v || (k.CreatedAt == v && k.Id.CompareTo(cursor.Id) < 0))
                                      : query.Where(k => k.UpdatedAt < v || (k.UpdatedAt == v && k.Id.CompareTo(cursor.Id) < 0));
                }
                ordered = byCreated ? query.OrderByDescending(k => k.CreatedAt).ThenByDescending(k => k.Id) : query.OrderByDescending(k => k.UpdatedAt).ThenByDescending(k => k.Id);
                keyOf = byCreated ? k => Parse.Iso(k.CreatedAt) : k => Parse.Iso(k.UpdatedAt);
                break;
            }
            default:
            {
                var asc = Parse.Asc(q.Dir, true);
                if (cursor is not null)
                {
                    var v = cursor.V;
                    query = asc ? query.Where(k => string.Compare(k.FullName, v) > 0 || (k.FullName == v && k.Id.CompareTo(cursor.Id) > 0))
                                : query.Where(k => string.Compare(k.FullName, v) < 0 || (k.FullName == v && k.Id.CompareTo(cursor.Id) < 0));
                }
                ordered = asc ? query.OrderBy(k => k.FullName).ThenBy(k => k.Id) : query.OrderByDescending(k => k.FullName).ThenByDescending(k => k.Id);
                keyOf = k => k.FullName;
                break;
            }
        }
        var page = await ordered.Take(limit + 1).ToListAsync(ct);
        var hasMore = page.Count > limit;
        if (hasMore) page.RemoveAt(page.Count - 1);
        return new PagedResult<ContactListItemDto>(await MapListAsync(page, ct), hasMore ? DbExtensions.EncodeCursor(keyOf(page[^1]), page[^1].Id) : null, total);
    }

    public async Task<List<ContactListItemDto>> MapListAsync(IReadOnlyList<Contact> page, CancellationToken ct)
    {
        if (page.Count == 0) return [];
        var ids = page.Select(k => k.Id).ToList();
        var customerIds = page.Where(k => k.CustomerId != null).Select(k => k.CustomerId!.Value).Distinct().ToList();
        var locationIds = page.Where(k => k.LocationId != null).Select(k => k.LocationId!.Value).Distinct().ToList();
        var customers = await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(c => customerIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var locations = await db.CustomerLocations.AsNoTracking().Where(l => locationIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var open = await db.Tickets.AsNoTracking().Where(t => t.ContactId != null && ids.Contains(t.ContactId.Value) && t.StatusCategory < StatusCategory.Resolved)
            .GroupBy(t => t.ContactId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return page.Select(k => Map(k, k.CustomerId is { } c ? customers.GetValueOrDefault(c) : null, k.LocationId is { } l ? locations.GetValueOrDefault(l) : null,
            open.GetValueOrDefault(k.Id))).ToList();
    }

    public static ContactListItemDto Map(Contact k, string? customerName, string? locationName, int openTickets) =>
        new(k.Id, k.FullName, k.Email, k.Phone ?? k.Mobile ?? k.WhatsAppNumber, k.JobTitle, k.CustomerId, customerName, k.LocationId, locationName, k.IsVip,
            k.IsPrimary, k.Status.ToString(), k.PortalAccess.ToString(), k.PortalPasswordHash is not null, openTickets, k.CreatedAt, k.UpdatedAt);

    // ───────────────────────────── Detail ─────────────────────────────

    public async Task<ContactDetailDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersView);
        var k = await db.Contacts.AsNoTracking().VisibleTo(ctx, db).FirstOrDefaultAsync(x => x.Id == id && x.Status != ContactStatus.Erased, ct)
                ?? throw new NotFoundException("Contact", id);
        var customer = k.CustomerId is null ? null : await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(c => c.Id == k.CustomerId).Select(c => c.Name).FirstOrDefaultAsync(ct);
        var location = k.LocationId is null ? null : await db.CustomerLocations.AsNoTracking().Where(l => l.Id == k.LocationId).Select(l => l.Name).FirstOrDefaultAsync(ct);
        var department = k.CustomerDepartmentId is null ? null : await db.CustomerDepartments.AsNoTracking().Where(d => d.Id == k.CustomerDepartmentId).Select(d => d.Name).FirstOrDefaultAsync(ct);
        var identities = await db.ContactIdentities.AsNoTracking().Where(i => i.ContactId == id).OrderBy(i => i.Channel).ThenBy(i => i.Identifier)
            .Select(i => new ContactIdentityDto(i.Id, i.Channel.ToString(), i.Identifier, i.IsVerified, i.VerifiedAt, i.CreatedAt)).ToListAsync(ct);
        var consents = await db.ContactConsents.AsNoTracking().Where(c => c.ContactId == id).ToListAsync(ct);
        var consentDtos = ConsentChannels.SelectMany(ch => Enum.GetValues<ConsentPurpose>().Select(p =>
        {
            var row = consents.FirstOrDefault(c => c.Channel == ch && c.Purpose == p);
            return new ContactConsentDto(ch.ToString(), p.ToString(), (row?.Status ?? ConsentStatus.Unknown).ToString(), row?.Source, row?.RecordedAt);
        })).ToList();
        var stats = await db.Tickets.AsNoTracking().Where(t => t.ContactId == id).GroupBy(_ => 1).Select(g => new
        {
            Open = g.Count(t => t.StatusCategory < StatusCategory.Resolved),
            Total = g.Count(),
            Last = g.Max(t => (DateTime?)t.CreatedAt),
            Csat = g.Where(t => t.CsatScore != null).Average(t => (double?)t.CsatScore)
        }).FirstOrDefaultAsync(ct);
        var conversations = await db.Conversations.CountAsync(c => c.ContactId == id, ct);
        var editable = ctx.Has(Permissions.CustomersManage) && k.Status != ContactStatus.Merged;
        return new ContactDetailDto(k.Id, k.FullName, k.FirstName, k.LastName, k.Email, k.Phone, k.Mobile, k.WhatsAppNumber, k.JobTitle, k.CustomerId, customer,
            k.LocationId, location, k.CustomerDepartmentId, department, k.PreferredLanguage, k.PreferredChannel?.ToString(), k.TimeZone, k.IsVip, k.IsPrimary,
            k.Status.ToString(), k.MergedIntoContactId, k.PortalAccess.ToString(), k.PortalPasswordHash is not null, k.PortalLastLoginAt,
            k.PortalLockedUntil > db.Clock.UtcNow, k.EmailVerifiedAt, k.PhoneVerifiedAt, Json.Element(k.CustomFieldsJson), k.ErpContactId, identities, consentDtos,
            new ContactStatsDto(stats?.Open ?? 0, stats?.Total ?? 0, stats?.Last, stats?.Csat, conversations), k.CreatedAt, k.UpdatedAt,
            Parse.RowVersion(k.RowVersion), new ContactPermissionsDto(editable, ctx.Has(Permissions.ContactsMerge) && k.Status == ContactStatus.Active, editable));
    }

    private static readonly Channel[] ConsentChannels = [Channel.Email, Channel.Sms, Channel.WhatsApp, Channel.Phone];

    // ───────────────────────────── Create / update / delete ─────────────────────────────

    public async Task<Contact> CreateAsync(SaveContactRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersManage);
        var k = new Contact();
        var changes = await ApplyAsync(k, r, isNew: true, ct);
        db.Contacts.Add(k);
        SyncIdentities(k);
        events.Publish(EventTypes.ContactCreated, "Contact", k.Id, new EntityEventPayload(k.Id, k.FullName, changes));
        await db.SaveChangesAsync(ct);
        return k;
    }

    public async Task<Contact> UpdateAsync(Guid id, SaveContactRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersManage);
        var k = await CustomerAccess.LoadVisibleContactAsync(db, ctx, id, ct);
        if (k.Status == ContactStatus.Merged) throw new ConflictException("contact_merged", "This contact was merged into another contact and can no longer be edited.");
        db.Entry(k).Property(x => x.RowVersion).OriginalValue = Parse.RowVersion(r.RowVersion, "contact");
        var before = (k.EmailNormalized, k.PhoneNormalized, WhatsApp: TextNormalization.NormalizePhone(k.WhatsAppNumber), Mobile: TextNormalization.NormalizePhone(k.Mobile));
        var changes = await ApplyAsync(k, r, isNew: false, ct);
        await SyncIdentitiesAsync(k, before, ct);
        if (changes.Count > 0) events.Publish(EventTypes.ContactUpdated, "Contact", k.Id, new EntityEventPayload(k.Id, k.FullName, changes));
        await db.SaveChangesAsync(ct);
        return k;
    }

    private async Task<List<FieldChange>> ApplyAsync(Contact k, SaveContactRequest r, bool isNew, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        var first = Parse.Clean(r.FirstName, 120);
        var last = Parse.Clean(r.LastName, 120);
        var full = Parse.Clean(r.FullName, 250) ?? Parse.Clean($"{first} {last}", 250);
        if (full is null) errors["fullName"] = "A name is required.";
        var email = Parse.Clean(r.Email, 300);
        var emailNormalized = TextNormalization.NormalizeEmail(email);
        if (email is not null && emailNormalized is null) errors["email"] = "Enter a valid e-mail address.";
        var phone = Parse.Clean(r.Phone, 50);
        var phoneNormalized = TextNormalization.NormalizePhone(phone);
        if (phone is not null && phoneNormalized is null) errors["phone"] = "Enter a valid phone number.";

        if (emailNormalized is not null && emailNormalized != k.EmailNormalized)
        {
            var owner = await db.ContactIdentities.AsNoTracking().Where(i => i.Channel == Channel.Email && i.Identifier == emailNormalized && i.ContactId != k.Id)
                .Select(i => (Guid?)i.ContactId).FirstOrDefaultAsync(ct);
            if (owner is { } other && await db.Contacts.AnyAsync(c => c.Id == other && c.Status == ContactStatus.Active, ct))
                throw new DomainException("duplicate_contact", $"Another contact already uses {emailNormalized}. Open it or merge the two contacts.", 409);
        }

        Guid? customerId = r.CustomerId;
        if (customerId is { } cid) await CustomerAccess.EnsureVisibleAsync(db, ctx, cid, ct);
        else if (CustomerAccess.IsRestricted(ctx)) errors["customerId"] = "Select one of your customers.";
        if (customerId is null && emailNormalized is not null)
            customerId = await resolver.CustomerForDomainAsync(TextNormalization.EmailDomain(emailNormalized), ct);
        if (r.LocationId is { } lid && !await db.CustomerLocations.AnyAsync(l => l.Id == lid && l.CustomerId == customerId, ct))
            errors["locationId"] = "The location does not belong to the customer.";
        if (r.CustomerDepartmentId is { } did && !await db.CustomerDepartments.AnyAsync(d => d.Id == did && d.CustomerId == customerId, ct))
            errors["customerDepartmentId"] = "The department does not belong to the customer.";
        Channel? preferred = null;
        if (!string.IsNullOrWhiteSpace(r.PreferredChannel))
        {
            if (Enum.TryParse<Channel>(r.PreferredChannel, true, out var ch)) preferred = ch;
            else errors["preferredChannel"] = "Unknown channel.";
        }
        var status = Parse.Enum(r.Status, isNew ? ContactStatus.Active : k.Status, "status");
        if (status is ContactStatus.Merged or ContactStatus.Erased) errors["status"] = "Use merge or erasure for this status.";
        var portal = Parse.Enum(r.PortalAccess, isNew ? PortalAccessScope.Own : k.PortalAccess, "portalAccess");
        string customFieldsJson = k.CustomFieldsJson;
        try
        {
            customFieldsJson = await customFields.MergeAsync("Contact", k.CustomFieldsJson, r.CustomFields, null, enforceRequired: !ctx.IsSystem, portalOnly: false, ct);
        }
        catch (ValidationException v)
        {
            foreach (var (key, m) in v.Errors) errors[$"customFields.{key}"] = m;
        }
        if (errors.Count > 0) throw new ValidationException(errors);

        var set = new ChangeSet();
        set.Set("fullName", k.FullName, full!, v => k.FullName = v);
        set.Set("firstName", k.FirstName, first ?? full!.Split(' ')[0], v => k.FirstName = v);
        set.Set("lastName", k.LastName, last ?? (full!.Contains(' ') ? full[(full.LastIndexOf(' ') + 1)..] : null), v => k.LastName = v);
        set.Set("email", k.Email, email, v => k.Email = v);
        k.EmailNormalized = emailNormalized;
        set.Set("phone", k.Phone, phone, v => k.Phone = v);
        k.PhoneNormalized = phoneNormalized ?? TextNormalization.NormalizePhone(r.Mobile);
        set.Set("mobile", k.Mobile, Parse.Clean(r.Mobile, 50), v => k.Mobile = v);
        set.Set("whatsAppNumber", k.WhatsAppNumber, Parse.Clean(r.WhatsAppNumber, 50), v => k.WhatsAppNumber = v);
        set.Set("jobTitle", k.JobTitle, Parse.Clean(r.JobTitle, 200), v => k.JobTitle = v);
        set.Set("customerId", k.CustomerId, customerId, v => k.CustomerId = v);
        set.Set("locationId", k.LocationId, r.LocationId, v => k.LocationId = v);
        set.Set("customerDepartmentId", k.CustomerDepartmentId, r.CustomerDepartmentId, v => k.CustomerDepartmentId = v);
        set.Set("preferredLanguage", k.PreferredLanguage, Parse.Clean(r.PreferredLanguage, 20) ?? "en", v => k.PreferredLanguage = v);
        set.Set("preferredChannel", k.PreferredChannel, preferred, v => k.PreferredChannel = v);
        set.Set("timeZone", k.TimeZone, Parse.Clean(r.TimeZone, 100), v => k.TimeZone = v);
        set.Set("isVip", k.IsVip, r.IsVip, v => k.IsVip = v);
        set.Set("isPrimary", k.IsPrimary, r.IsPrimary, v => k.IsPrimary = v);
        set.Set("status", k.Status, status, v => k.Status = v);
        set.Set("portalAccess", k.PortalAccess, portal, v => k.PortalAccess = v);
        set.Set("customFields", k.CustomFieldsJson, customFieldsJson, v => k.CustomFieldsJson = v);
        if (r.IsPrimary && customerId is { } primaryCustomer && (isNew || set.Has("isPrimary")))
            await db.Contacts.Where(c => c.CustomerId == primaryCustomer && c.Id != k.Id && c.IsPrimary)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.IsPrimary, false), ct);
        return set.Changes.ToList();
    }

    /// <summary>Registers identities for the primary e-mail/phone/WhatsApp values of a new contact (the caller saves).</summary>
    private void SyncIdentities(Contact k)
    {
        var now = db.Clock.UtcNow;
        void Add(Channel ch, string? id)
        {
            if (id is null || db.ContactIdentities.Local.Any(i => i.Channel == ch && i.Identifier == id)) return;
            db.ContactIdentities.Add(new ContactIdentity { ContactId = k.Id, Channel = ch, Identifier = id, CreatedAt = now });
        }
        Add(Channel.Email, k.EmailNormalized);
        Add(Channel.Phone, k.PhoneNormalized);
        Add(Channel.WhatsApp, TextNormalization.NormalizePhone(k.WhatsAppNumber));
    }

    private async Task SyncIdentitiesAsync(Contact k, (string? Email, string? Phone, string? WhatsApp, string? Mobile) before, CancellationToken ct)
    {
        var wanted = new List<(Channel Channel, string? Old, string? New)>
        {
            (Channel.Email, before.Email, k.EmailNormalized),
            (Channel.Phone, before.Phone, k.PhoneNormalized),
            (Channel.WhatsApp, before.WhatsApp, TextNormalization.NormalizePhone(k.WhatsAppNumber))
        };
        foreach (var (channel, oldValue, newValue) in wanted)
        {
            if (oldValue == newValue) continue;
            if (oldValue is not null)
                await db.ContactIdentities.Where(i => i.ContactId == k.Id && i.Channel == channel && i.Identifier == oldValue).ExecuteDeleteAsync(ct);
            if (newValue is null) continue;
            var taken = await db.ContactIdentities.AnyAsync(i => i.Channel == channel && i.Identifier == newValue, ct);
            if (!taken) db.ContactIdentities.Add(new ContactIdentity { ContactId = k.Id, Channel = channel, Identifier = newValue, CreatedAt = db.Clock.UtcNow });
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersManage);
        var k = await CustomerAccess.LoadVisibleContactAsync(db, ctx, id, ct);
        k.IsDeleted = true;
        k.DeletedAt = db.Clock.UtcNow;
        k.PortalPasswordHash = null;
        // Identities are released so the address can be matched to another contact again.
        await db.ContactIdentities.Where(i => i.ContactId == id).ExecuteDeleteAsync(ct);
        audit.Write(CustomerAuditActions.ContactDeleted, "Contact", id, $"Contact '{k.FullName}' deleted");
        events.Publish(EventTypes.ContactUpdated, "Contact", id, new EntityEventPayload(id, k.FullName, [new FieldChange("isDeleted", "false", "true")]));
        await db.SaveChangesAsync(ct);
    }

    // ───────────────────────────── Identities & consent ─────────────────────────────

    public async Task<ContactIdentityDto> AddIdentityAsync(Guid contactId, AddContactIdentityRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersManage);
        var k = await CustomerAccess.LoadVisibleContactAsync(db, ctx, contactId, ct);
        if (k.Status == ContactStatus.Merged) throw new ConflictException("contact_merged", "This contact was merged into another contact.");
        var channel = Parse.Enum(r.Channel, Channel.Email, "channel");
        var normalized = ContactResolver.Normalize(channel, r.Identifier)
                         ?? throw new ValidationException(new Dictionary<string, string> { ["identifier"] = "Enter a valid identifier for this channel." });
        var existing = await db.ContactIdentities.FirstOrDefaultAsync(i => i.Channel == channel && i.Identifier == normalized, ct);
        if (existing is not null && existing.ContactId != contactId)
            throw new DomainException("duplicate_contact", "This identity already belongs to another contact. Merge the contacts instead.", 409);
        if (existing is null)
        {
            existing = new ContactIdentity { ContactId = contactId, Channel = channel, Identifier = normalized, CreatedAt = db.Clock.UtcNow };
            db.ContactIdentities.Add(existing);
        }
        if (r.Verified && !existing.IsVerified)
        {
            existing.IsVerified = true;
            existing.VerifiedAt = db.Clock.UtcNow;
        }
        events.Publish(EventTypes.ContactUpdated, "Contact", contactId, new EntityEventPayload(contactId, k.FullName, [new FieldChange("identity", null, $"{channel}:{normalized}")]));
        await db.SaveChangesAsync(ct);
        return new ContactIdentityDto(existing.Id, existing.Channel.ToString(), existing.Identifier, existing.IsVerified, existing.VerifiedAt, existing.CreatedAt);
    }

    public async Task RemoveIdentityAsync(Guid contactId, Guid identityId, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersManage);
        var k = await CustomerAccess.LoadVisibleContactAsync(db, ctx, contactId, ct);
        var identity = await db.ContactIdentities.FirstOrDefaultAsync(i => i.Id == identityId && i.ContactId == contactId, ct)
                       ?? throw new NotFoundException("Identity", identityId);
        db.ContactIdentities.Remove(identity);
        events.Publish(EventTypes.ContactUpdated, "Contact", contactId, new EntityEventPayload(contactId, k.FullName,
            [new FieldChange("identity", $"{identity.Channel}:{identity.Identifier}", null)]));
        await db.SaveChangesAsync(ct);
    }

    public async Task<ContactConsentDto> SetConsentAsync(Guid contactId, SetConsentRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersManage);
        var k = await CustomerAccess.LoadVisibleContactAsync(db, ctx, contactId, ct);
        var channel = Parse.Enum(r.Channel, Channel.Email, "channel");
        var purpose = Parse.Enum(r.Purpose, ConsentPurpose.Transactional, "purpose");
        var status = Parse.Enum(r.Status, ConsentStatus.Unknown, "status");
        var row = await db.ContactConsents.FirstOrDefaultAsync(c => c.ContactId == contactId && c.Channel == channel && c.Purpose == purpose, ct);
        if (row is null) db.ContactConsents.Add(row = new ContactChannelConsent { ContactId = contactId, Channel = channel, Purpose = purpose });
        var from = row.Status;
        row.Status = status;
        row.Source = Parse.Clean(r.Source, 200) ?? $"agent:{ctx.ActorName}";
        row.RecordedAt = db.Clock.UtcNow;
        events.Publish(EventTypes.ContactUpdated, "Contact", contactId, new EntityEventPayload(contactId, k.FullName,
            [new FieldChange($"consent.{channel}.{purpose}", from.ToString(), status.ToString())]));
        await db.SaveChangesAsync(ct);
        return new ContactConsentDto(channel.ToString(), purpose.ToString(), status.ToString(), row.Source, row.RecordedAt);
    }

    // ───────────────────────────── Portal ─────────────────────────────

    public async Task SetPortalAccessAsync(Guid contactId, SetPortalAccessRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersManage);
        var k = await CustomerAccess.LoadVisibleContactAsync(db, ctx, contactId, ct);
        var access = Parse.Enum(r.Access, k.PortalAccess, "access");
        if (access is PortalAccessScope.Location && k.LocationId is null)
            throw new ValidationException(new Dictionary<string, string> { ["access"] = "Location access needs the contact's location to be set." });
        if (access is PortalAccessScope.Organisation or PortalAccessScope.Location && k.CustomerId is null)
            throw new ValidationException(new Dictionary<string, string> { ["access"] = "Organisation and location access need the contact's customer to be set." });
        var from = k.PortalAccess;
        k.PortalAccess = access;
        if (access == PortalAccessScope.None) k.PortalPasswordHash = null;
        audit.Write(CustomerAuditActions.PortalAccessChanged, "Contact", k.Id, $"Portal access of '{k.FullName}' changed from {from} to {access}");
        events.Publish(EventTypes.ContactUpdated, "Contact", k.Id, new EntityEventPayload(k.Id, k.FullName, [new FieldChange("portalAccess", from.ToString(), access.ToString())]));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Sends a portal invitation (72 h sign-in code) or a password-reset sign-in code (30 min) to the contact. The code
    /// is a one-time portal login challenge: after signing in with it the contact sets a password under Account.
    /// Unlocks a locked portal account. Codes are shown in the response only in demo mode.
    /// </summary>
    public async Task<PortalInviteResultDto> SendPortalAccessAsync(Guid contactId, PortalInviteRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersManage);
        var k = await CustomerAccess.LoadVisibleContactAsync(db, ctx, contactId, ct);
        if (k.Status != ContactStatus.Active) throw new ConflictException("contact_inactive", "Only active contacts can be invited to the portal.");
        if (k.EmailNormalized is null) throw new ValidationException(new Dictionary<string, string> { ["email"] = "The contact needs an e-mail address." });
        var reset = string.Equals(r.Mode, "reset", StringComparison.OrdinalIgnoreCase);
        if (k.PortalAccess == PortalAccessScope.None)
        {
            if (reset) throw new ConflictException("portal_disabled", "Portal access is disabled for this contact.");
            k.PortalAccess = PortalAccessScope.Own;
        }
        var brand = r.BrandId is { } bid
            ? await db.Brands.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bid, ct) ?? throw new NotFoundException("Brand", bid)
            : await db.Brands.AsNoTracking().OrderByDescending(b => b.IsDefault).FirstOrDefaultAsync(ct)
              ?? throw new DomainException("configuration_error", "No customer portal (brand) is configured.");
        var now = db.Clock.UtcNow;
        var code = Random.Shared.Next(0, 1_000_000).ToString("D6");
        var expires = reset ? now.AddMinutes(30) : now.AddHours(72);
        db.OtpChallenges.Add(new OtpChallenge
        {
            TenantId = ctx.TenantId, PrincipalKind = PrincipalKind.Contact, PrincipalId = k.Id, Purpose = OtpPurpose.PortalLogin,
            CodeHash = TokenHasher.Hash(code), Destination = k.Email, CreatedAt = now, ExpiresAt = expires
        });
        k.PortalFailedLogins = 0;
        k.PortalLockedUntil = null;
        var loginUrl = PortalUrl(brand.Slug, brand.CustomDomain, $"login?email={Uri.EscapeDataString(k.EmailNormalized)}");
        var greeting = System.Net.WebUtility.HtmlEncode(k.FirstName ?? k.FullName);
        var portalName = System.Net.WebUtility.HtmlEncode(brand.PortalTitle);
        var html = reset
            ? $"<p>Hello {greeting},</p><p>We received a request to reset your {portalName} password. Sign in with this one-time code and then choose a new password under <em>Account</em>:</p>" +
              $"<p style=\"font-size:22px;letter-spacing:4px\"><strong>{code}</strong></p><p><a href=\"{loginUrl}\">Open the help center</a> (the code expires in 30 minutes).</p>" +
              "<p>If you did not ask for this, you can ignore this e-mail — your password has not changed.</p>"
            : $"<p>Hello {greeting},</p><p>You have been invited to the {portalName}, where you can raise and follow your support requests.</p>" +
              $"<p>Sign in with this one-time code:</p><p style=\"font-size:22px;letter-spacing:4px\"><strong>{code}</strong></p>" +
              $"<p><a href=\"{loginUrl}\">Open the help center</a> (the code is valid for 72 hours). You can set a password under <em>Account</em> after signing in.</p>";
        audit.Write(reset ? CustomerAuditActions.PortalReset : CustomerAuditActions.PortalInvited, "Contact", k.Id,
            $"Portal {(reset ? "password reset" : "invitation")} sent to {k.EmailNormalized}");
        await db.SaveChangesAsync(ct);
        await mailer.SendAsync(ctx.TenantId, k.Email!, reset ? $"Reset your {brand.PortalTitle} password" : $"Your invitation to the {brand.PortalTitle}", html, null, null, ct);
        var demo = (await settings.SecurityAsync(ct)).DemoMode;
        return new PortalInviteResultDto(reset ? "reset" : "invite", k.EmailNormalized, loginUrl, expires, demo ? code : null);
    }

    public string PortalUrl(string brandSlug, string? customDomain, string path)
    {
        if (!string.IsNullOrWhiteSpace(customDomain)) return $"https://{customDomain}/{path}";
        // Portal:PublicBaseUrl, else the app's public URL (which includes an IIS virtual directory, e.g. https://host/supportdesk).
        var baseUrl = (configuration["Portal:PublicBaseUrl"] is { Length: > 0 } p ? p
            : configuration["App:PublicUrl"] is { Length: > 0 } a ? a
            : configuration["Communication:PublicBaseUrl"] ?? "").TrimEnd('/');
        return $"{baseUrl}/portal/{brandSlug}/{path}";
    }
}
