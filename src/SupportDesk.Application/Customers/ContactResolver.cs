using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Domain;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Customers;

/// <summary>
/// Automatic contact matching for inbound conversations and ticket creation: by channel identity (e-mail, phone,
/// WhatsApp, social id), then by normalised e-mail/phone, otherwise a new contact is created and linked to the
/// customer whose e-mail domain matches. Identities are unique per tenant and channel.
/// </summary>
public sealed class ContactResolver(DeskDbContext db, IEventPublisher events)
{
    public async Task<Contact?> FindAsync(Channel channel, string identifier, CancellationToken ct = default)
    {
        var normalized = Normalize(channel, identifier);
        if (normalized is null) return null;
        var contactId = await db.ContactIdentities.Where(i => i.Channel == channel && i.Identifier == normalized).Select(i => (Guid?)i.ContactId).FirstOrDefaultAsync(ct);
        if (contactId is { } id)
        {
            var c = await db.Contacts.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (c is { Status: ContactStatus.Merged, MergedIntoContactId: { } survivor }) c = await db.Contacts.FirstOrDefaultAsync(x => x.Id == survivor, ct);
            if (c is not null) return c;
        }
        return channel switch
        {
            Channel.Email or Channel.Portal or Channel.WebForm => await db.Contacts.FirstOrDefaultAsync(x => x.EmailNormalized == normalized && x.Status == ContactStatus.Active, ct),
            Channel.Phone or Channel.Sms or Channel.WhatsApp => await db.Contacts.FirstOrDefaultAsync(x => x.PhoneNormalized == normalized && x.Status == ContactStatus.Active, ct),
            _ => null
        };
    }

    /// <summary>Finds a contact by e-mail or phone, or creates one. The caller saves.</summary>
    public async Task<Contact> FindOrCreateAsync(string? email, string? phone, string? fullName, Guid? customerId, Channel channel, CancellationToken ct = default)
    {
        var normalizedEmail = TextNormalization.NormalizeEmail(email);
        var normalizedPhone = TextNormalization.NormalizePhone(phone);
        Contact? contact = null;
        if (normalizedEmail is not null) contact = await FindAsync(Channel.Email, normalizedEmail, ct);
        if (contact is null && normalizedPhone is not null) contact = await FindAsync(channel is Channel.WhatsApp ? Channel.WhatsApp : Channel.Phone, normalizedPhone, ct)
                                                             ?? await FindAsync(Channel.Phone, normalizedPhone, ct);
        contact ??= db.Contacts.Local.FirstOrDefault(c => (normalizedEmail != null && c.EmailNormalized == normalizedEmail) ||
                                                          (normalizedPhone != null && c.PhoneNormalized == normalizedPhone));
        if (contact is not null)
        {
            if (contact.CustomerId is null && customerId is not null) contact.CustomerId = customerId;
            return contact;
        }

        customerId ??= await CustomerForDomainAsync(TextNormalization.EmailDomain(normalizedEmail), ct);
        contact = new Contact
        {
            FullName = string.IsNullOrWhiteSpace(fullName) ? (email ?? phone ?? "Unknown contact").Split('@')[0] : fullName.Trim(),
            Email = email?.Trim(),
            EmailNormalized = normalizedEmail,
            Phone = phone?.Trim(),
            PhoneNormalized = normalizedPhone,
            WhatsAppNumber = channel == Channel.WhatsApp ? phone : null,
            CustomerId = customerId,
            PreferredChannel = channel is Channel.Email or Channel.WhatsApp or Channel.Phone or Channel.Sms ? channel : null
        };
        db.Contacts.Add(contact);
        if (normalizedEmail is not null) AddIdentity(contact.Id, Channel.Email, normalizedEmail);
        if (normalizedPhone is not null)
        {
            AddIdentity(contact.Id, Channel.Phone, normalizedPhone);
            if (channel == Channel.WhatsApp) AddIdentity(contact.Id, Channel.WhatsApp, normalizedPhone);
        }
        events.Publish(EventTypes.ContactCreated, "Contact", contact.Id, new EntityEventPayload(contact.Id, contact.FullName));
        return contact;
    }

    /// <summary>Registers an additional channel identity (e.g. Messenger PSID) for a contact. The caller saves.</summary>
    public async Task AttachIdentityAsync(Guid contactId, Channel channel, string identifier, bool verified, CancellationToken ct = default)
    {
        var normalized = Normalize(channel, identifier);
        if (normalized is null) return;
        var existing = await db.ContactIdentities.FirstOrDefaultAsync(i => i.Channel == channel && i.Identifier == normalized, ct);
        if (existing is not null) return;
        AddIdentity(contactId, channel, normalized, verified);
    }

    private void AddIdentity(Guid contactId, Channel channel, string identifier, bool verified = false)
    {
        if (db.ContactIdentities.Local.Any(i => i.Channel == channel && i.Identifier == identifier)) return;
        db.ContactIdentities.Add(new ContactIdentity
        {
            ContactId = contactId, Channel = channel, Identifier = identifier, IsVerified = verified,
            VerifiedAt = verified ? db.Clock.UtcNow : null, CreatedAt = db.Clock.UtcNow
        });
    }

    public async Task<Guid?> CustomerForDomainAsync(string? domain, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(domain)) return null;
        var quoted = $"\"{domain}\"";
        var candidates = await db.Customers.AsNoTracking().Where(c => c.DomainsJson.Contains(quoted)).Select(c => new { c.Id, c.DomainsJson }).ToListAsync(ct);
        return candidates.FirstOrDefault(c => Json.StringList(c.DomainsJson).Contains(domain, StringComparer.OrdinalIgnoreCase))?.Id;
    }

    public static string? Normalize(Channel channel, string identifier) => channel switch
    {
        Channel.Email or Channel.Portal or Channel.WebForm => TextNormalization.NormalizeEmail(identifier),
        Channel.Phone or Channel.Sms or Channel.WhatsApp => TextNormalization.NormalizePhone(identifier),
        _ => string.IsNullOrWhiteSpace(identifier) ? null : identifier.Trim()
    };
}
