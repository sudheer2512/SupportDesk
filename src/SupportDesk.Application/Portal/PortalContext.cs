using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Identity;
using SupportDesk.Application.Knowledge;
using SupportDesk.Contracts.Common;
using SupportDesk.Domain;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Platform;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Portal;

/// <summary>
/// Request-scoped portal state: the brand (resolved server-side from the slug — for signed-in contacts it must belong
/// to the token's tenant) and the signed-in contact (active, portal access not None). Never trusts client-supplied
/// tenant or customer ids.
/// </summary>
public sealed class PortalContext(DeskDbContext db, RequestContext ctx, PortalAuthService auth, KbAccess access)
{
    private Brand? _brand;
    private KbReader? _reader;

    public Brand Brand => _brand ?? throw new InvalidOperationException("Portal brand not resolved.");
    public Contact? Contact { get; private set; }
    public bool SignedIn => Contact is not null;
    public Contact Me => Contact ?? throw new DomainException(ErrorCodes.Unauthorized, "Sign in to continue.", 401);

    public async Task<Brand> ResolveAsync(string brandSlug, bool requireContact = false, CancellationToken ct = default)
    {
        if (_brand is not null && string.Equals(_brand.Slug, brandSlug, StringComparison.OrdinalIgnoreCase))
        {
            if (requireContact) _ = Me;
            return _brand;
        }
        var slug = (brandSlug ?? "").Trim().ToLowerInvariant();
        Brand? brand;
        if (ctx.HasTenant)
            brand = await db.Brands.AsNoTracking().FirstOrDefaultAsync(b => b.Slug == slug && b.PortalEnabled, ct);
        else
            brand = await auth.ResolveBrandAsync(slug, ct);
        if (brand is null) throw new NotFoundException("Help center", brandSlug);
        _brand = brand;

        if (ctx.IsContact)
        {
            Contact = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == ctx.ContactId && c.Status == ContactStatus.Active, ct);
            if (Contact is null || Contact.PortalAccess == PortalAccessScope.None)
                throw new DomainException(ErrorCodes.Unauthorized, "Your portal access is no longer active. Contact support.", 401);
        }
        if (requireContact) _ = Me;
        return brand;
    }

    /// <summary>Knowledge reader for this request (anonymous = public content only).</summary>
    public async Task<KbReader> ReaderAsync(CancellationToken ct = default)
    {
        if (_reader is not null) return _reader;
        _reader = Contact is null ? KbReader.Anonymous(Brand.Id) : await access.ForContactAsync(Contact.Id, Brand.Id, ct);
        return _reader;
    }

    /// <summary>Path of the brand's portal API (attachment URLs).</summary>
    public string ApiBase => $"/api/v1/portal/{Brand.Slug}";
}
