using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Knowledge;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Portal;
using SupportDesk.Domain;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Service;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Portal;

/// <summary>
/// The signed-in contact's account: profile, notification preferences (per channel and purpose consents), password,
/// and — for contacts with location/organisation access — support contracts with consumption and entitlements,
/// licensed products, active installations and scheduled service/maintenance visits. Location-scope contacts only
/// see installations and visits of their own location.
/// </summary>
public sealed class PortalAccountService(
    DeskDbContext db,
    PortalContext portal,
    PasswordService passwords,
    TenantSettingsService settings,
    IEventPublisher events,
    IAuditWriter audit)
{
    private static readonly Channel[] ConsentChannels = [Channel.Email, Channel.WhatsApp, Channel.Sms];
    private static readonly Channel[] PreferredChannels = [Channel.Email, Channel.WhatsApp, Channel.Sms, Channel.Phone, Channel.Portal];

    public static bool CanViewContracts(Contact c) => c.CustomerId is not null && c.PortalAccess >= PortalAccessScope.Location;

    public async Task<PortalProfileDto> ProfileAsync(CancellationToken ct = default)
    {
        var c = await db.Contacts.AsNoTracking().FirstAsync(x => x.Id == portal.Me.Id, ct);
        return await MapProfileAsync(c, ct);
    }

    private async Task<PortalProfileDto> MapProfileAsync(Contact c, CancellationToken ct)
    {
        var customer = c.CustomerId is { } cid ? await db.Customers.AsNoTracking().Where(x => x.Id == cid).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
        var location = c.LocationId is { } lid ? await db.CustomerLocations.AsNoTracking().Where(x => x.Id == lid).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
        return new PortalProfileDto(c.Id, c.FullName, c.Email, c.Phone, c.Mobile, c.JobTitle, c.PreferredLanguage, c.PreferredChannel?.ToString(), c.TimeZone,
            customer, location, PortalTicketService.MaxScope(c), CanViewContracts(c), c.PortalLastLoginAt);
    }

    public async Task<PortalProfileDto> UpdateProfileAsync(UpdatePortalProfileRequest r, CancellationToken ct = default)
    {
        var c = await db.Contacts.FirstAsync(x => x.Id == portal.Me.Id, ct);
        var errors = new Dictionary<string, string>();
        var name = (r.FullName ?? "").Trim();
        if (name.Length is < 2 or > 120) errors["fullName"] = "Enter your name (2–120 characters).";
        var language = KbRules.NormalizeLocale(r.PreferredLanguage);
        if (language is null) errors["preferredLanguage"] = "Choose a language.";
        Channel? channel = null;
        if (!string.IsNullOrWhiteSpace(r.PreferredChannel))
        {
            if (Enum.TryParse<Channel>(r.PreferredChannel, true, out var ch) && PreferredChannels.Contains(ch)) channel = ch;
            else errors["preferredChannel"] = "Choose e-mail, WhatsApp, SMS, phone or portal.";
        }
        if (r.Phone is { Length: > 0 } phone && TextNormalization.NormalizePhone(phone) is null) errors["phone"] = "Enter a valid phone number.";
        if (r.Mobile is { Length: > 0 } mobile && TextNormalization.NormalizePhone(mobile) is null) errors["mobile"] = "Enter a valid mobile number.";
        if (r.TimeZone is { Length: > 0 } tz && (tz.Length > 64 || !TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _))) errors["timeZone"] = "Unknown time zone.";
        if (errors.Count > 0) throw new ValidationException(errors);
        var changes = new List<FieldChange>();
        void Set(string field, string? from, string? to, Action apply)
        {
            if ((from ?? "") == (to ?? "")) return;
            changes.Add(new FieldChange(field, from, to));
            apply();
        }
        Set("fullName", c.FullName, name, () => c.FullName = name);
        Set("phone", c.Phone, r.Phone?.Trim(), () => { c.Phone = string.IsNullOrWhiteSpace(r.Phone) ? null : r.Phone.Trim(); c.PhoneNormalized = TextNormalization.NormalizePhone(c.Phone); });
        Set("mobile", c.Mobile, r.Mobile?.Trim(), () => c.Mobile = string.IsNullOrWhiteSpace(r.Mobile) ? null : r.Mobile.Trim());
        Set("jobTitle", c.JobTitle, r.JobTitle?.Trim(), () => c.JobTitle = string.IsNullOrWhiteSpace(r.JobTitle) ? null : TextNormalization.Truncate(r.JobTitle.Trim(), 120));
        Set("preferredLanguage", c.PreferredLanguage, language, () => c.PreferredLanguage = language!);
        Set("preferredChannel", c.PreferredChannel?.ToString(), channel?.ToString(), () => c.PreferredChannel = channel);
        Set("timeZone", c.TimeZone, r.TimeZone, () => c.TimeZone = string.IsNullOrWhiteSpace(r.TimeZone) ? null : r.TimeZone.Trim());
        if (changes.Count > 0)
        {
            events.Publish(EventTypes.ContactUpdated, "Contact", c.Id, new EntityEventPayload(c.Id, c.FullName, changes));
            await db.SaveChangesAsync(ct);
        }
        return await MapProfileAsync(c, ct);
    }

    public async Task<PortalPreferencesDto> PreferencesAsync(CancellationToken ct = default)
    {
        var c = await db.Contacts.AsNoTracking().FirstAsync(x => x.Id == portal.Me.Id, ct);
        var consents = await db.ContactConsents.AsNoTracking().Where(x => x.ContactId == c.Id).ToListAsync(ct);
        var list = new List<PortalConsentDto>();
        foreach (var ch in ConsentChannels)
        foreach (var purpose in Enum.GetValues<ConsentPurpose>())
        {
            var row = consents.Where(x => x.Channel == ch && x.Purpose == purpose).OrderByDescending(x => x.RecordedAt).FirstOrDefault();
            var optedIn = row?.Status switch
            {
                ConsentStatus.OptedIn => true,
                ConsentStatus.OptedOut => false,
                _ => purpose == ConsentPurpose.Transactional && ch == Channel.Email
            };
            list.Add(new PortalConsentDto(ch.ToString(), purpose.ToString(), optedIn));
        }
        return new PortalPreferencesDto(c.PreferredChannel?.ToString(), c.PreferredLanguage, list);
    }

    public async Task<PortalPreferencesDto> UpdatePreferencesAsync(UpdatePortalPreferencesRequest r, CancellationToken ct = default)
    {
        var c = await db.Contacts.FirstAsync(x => x.Id == portal.Me.Id, ct);
        if (!string.IsNullOrWhiteSpace(r.PreferredChannel))
        {
            if (!Enum.TryParse<Channel>(r.PreferredChannel, true, out var ch) || !PreferredChannels.Contains(ch))
                throw new ValidationException(new Dictionary<string, string> { ["preferredChannel"] = "Choose e-mail, WhatsApp, SMS, phone or portal." });
            c.PreferredChannel = ch;
        }
        if (KbRules.NormalizeLocale(r.PreferredLanguage) is { } language) c.PreferredLanguage = language;
        var existing = await db.ContactConsents.Where(x => x.ContactId == c.Id).ToListAsync(ct);
        var now = db.Clock.UtcNow;
        foreach (var consent in r.Consents ?? [])
        {
            if (!Enum.TryParse<Channel>(consent.Channel, true, out var ch) || !ConsentChannels.Contains(ch)) continue;
            if (!Enum.TryParse<ConsentPurpose>(consent.Purpose, true, out var purpose)) continue;
            var status = consent.OptedIn ? ConsentStatus.OptedIn : ConsentStatus.OptedOut;
            var row = existing.FirstOrDefault(x => x.Channel == ch && x.Purpose == purpose);
            if (row is null)
                db.ContactConsents.Add(new ContactChannelConsent { ContactId = c.Id, Channel = ch, Purpose = purpose, Status = status, Source = "portal", RecordedAt = now });
            else if (row.Status != status)
            {
                row.Status = status;
                row.Source = "portal";
                row.RecordedAt = now;
            }
        }
        events.Publish(EventTypes.ContactUpdated, "Contact", c.Id, new EntityEventPayload(c.Id, c.FullName, [new FieldChange("preferences", null, "portal")]));
        await db.SaveChangesAsync(ct);
        return await PreferencesAsync(ct);
    }

    public async Task ChangePasswordAsync(ChangePortalPasswordRequest r, CancellationToken ct = default)
    {
        var c = await db.Contacts.FirstAsync(x => x.Id == portal.Me.Id, ct);
        if (c.PortalPasswordHash is not null && !passwords.Verify(c.PortalPasswordHash, r.CurrentPassword ?? ""))
            throw new ValidationException(new Dictionary<string, string> { ["currentPassword"] = "Your current password is not correct." });
        var policy = (await settings.SecurityAsync(ct)).Password;
        var problems = passwords.Validate(r.NewPassword ?? "", policy);
        if (problems.Count > 0) throw new ValidationException(new Dictionary<string, string> { ["newPassword"] = string.Join(" ", problems) });
        c.PortalPasswordHash = passwords.Hash(r.NewPassword!);
        c.PortalFailedLogins = 0;
        c.PortalLockedUntil = null;
        audit.Write(AuditActions.PasswordChanged, "Contact", c.Id, $"{c.FullName} changed their portal password");
        await db.SaveChangesAsync(ct);
    }

    public async Task<PortalAccountOverviewDto> OverviewAsync(CancellationToken ct = default)
    {
        var me = portal.Me;
        if (!CanViewContracts(me)) throw new ForbiddenException("Your portal access does not include contracts and installations.");
        var customerId = me.CustomerId!.Value;
        var locationOnly = PortalTicketService.MaxScope(me) == PortalTicketService.ScopeLocation ? me.LocationId : null;
        var customer = await db.Customers.AsNoTracking().Where(c => c.Id == customerId).Select(c => new { c.Name, c.Tier }).FirstAsync(ct);
        var today = DateOnly.FromDateTime(db.Clock.UtcNow);
        var products = await db.Products.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var locations = await db.CustomerLocations.AsNoTracking().Where(l => l.CustomerId == customerId).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var installations = await db.Installations.AsNoTracking()
            .Where(i => i.CustomerId == customerId && (locationOnly == null || i.LocationId == null || i.LocationId == locationOnly)).OrderBy(i => i.Name).ToListAsync(ct);
        var installationNames = installations.ToDictionary(i => i.Id, i => i.Name);
        var versions = await db.ProductVersions.AsNoTracking().ToDictionaryAsync(v => v.Id, v => v.Version, ct);

        var contracts = await db.Contracts.AsNoTracking()
            .Where(c => c.CustomerId == customerId && c.Status != ContractStatus.Draft && c.Status != ContractStatus.Cancelled)
            .OrderByDescending(c => c.Status == ContractStatus.Active).ThenByDescending(c => c.EndDate).ToListAsync(ct);
        var contractIds = contracts.Select(c => c.Id).ToList();
        var entitlements = (await db.Entitlements.AsNoTracking().Where(e => contractIds.Contains(e.ContractId)).OrderBy(e => e.Name).ToListAsync(ct)).ToLookup(e => e.ContractId);
        var contractDtos = contracts.Select(c =>
        {
            var coverage = Json.Object(c.CoverageJson);
            var channels = coverage.TryGetValue("channels", out var ch) && ch.ValueKind == System.Text.Json.JsonValueKind.Array
                ? ch.EnumerateArray().Select(x => x.ToString()).ToList() : [];
            var onsite = coverage.TryGetValue("onsite", out var os) && os.ValueKind == System.Text.Json.JsonValueKind.True;
            return new PortalContractDto(c.Id, c.Number, c.Name, c.Type.ToString(), c.Status.ToString(), c.StartDate, c.EndDate,
                Math.Max(0, c.EndDate.DayNumber - today.DayNumber), c.PrepaidHours, c.ConsumedHours,
                c.PrepaidHours is > 0 ? Math.Round((double)(c.ConsumedHours / c.PrepaidHours.Value) * 100, 1) : null, c.AutoRenew, c.RenewalDate, channels, onsite,
                entitlements[c.Id].Select(e => new PortalEntitlementDto(e.Id, e.Name, e.Type.ToString(), e.ProductId is { } p ? products.GetValueOrDefault(p) : null,
                    e.InstallationId is { } i ? installationNames.GetValueOrDefault(i) : null, e.IncidentsAllowed, e.IncidentsUsed, e.HoursAllowed, e.HoursUsed,
                    e.StartDate, e.EndDate, e.IsActive && e.EndDate >= today)).ToList());
        }).ToList();

        var installationIds = installations.Select(i => i.Id).ToList();
        var licences = await db.Licences.AsNoTracking().Where(l => l.CustomerId == customerId && installationIds.Contains(l.InstallationId)).ToListAsync(ct);
        var licenceDtos = licences.OrderBy(l => products.GetValueOrDefault(l.ProductId)).Select(l => new PortalLicenceDto(l.Id, products.GetValueOrDefault(l.ProductId) ?? "Product",
            l.Edition, l.Seats, l.KeyMasked, l.IssuedOn, l.ExpiresOn, l.Status.ToString(), installationNames.GetValueOrDefault(l.InstallationId))).ToList();
        var installationDtos = installations.Select(i => new PortalInstallationDto(i.Id, i.Name, i.InstallationCode, products.GetValueOrDefault(i.ProductId) ?? "Product",
            i.VersionId is { } v ? versions.GetValueOrDefault(v) : null, i.Environment.ToString(), i.DeploymentMode.ToString(),
            i.LocationId is { } l ? locations.GetValueOrDefault(l) : null, i.Status.ToString(), i.GoLiveOn, i.UserCount)).ToList();

        var from = db.Clock.UtcNow.AddDays(-90);
        var to = db.Clock.UtcNow.AddDays(365);
        var activities = await db.ServiceActivities.AsNoTracking()
            .Where(a => a.CustomerId == customerId && a.VisibleToCustomer && a.ScheduledStart >= from && a.ScheduledStart <= to
                        && (locationOnly == null || a.LocationId == null || a.LocationId == locationOnly))
            .OrderBy(a => a.ScheduledStart).Take(100).ToListAsync(ct);
        var activityDtos = activities.Select(a => new PortalServiceActivityDto(a.Id, a.Type.ToString(), a.Title, a.Description, a.ScheduledStart, a.ScheduledEnd,
            a.Status.ToString(), a.LocationId is { } l ? locations.GetValueOrDefault(l) : null, a.InstallationId is { } i ? installationNames.GetValueOrDefault(i) : null,
            a.Status == ServiceActivityStatus.Completed ? a.Outcome : null)).ToList();
        return new PortalAccountOverviewDto(customer.Name, customer.Tier, contractDtos, licenceDtos, installationDtos, activityDtos);
    }
}
