using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Platform;
using SupportDesk.Contracts.Auth;
using SupportDesk.Contracts.Common;
using SupportDesk.Domain;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Identity;

public sealed record PortalAuthResult(string Status, SessionTokens? Tokens = null, PortalMeDto? Contact = null, string? DemoCode = null, string? Message = null);

/// <summary>
/// Customer portal authentication (contacts). The brand slug is resolved server-side to its tenant; contacts sign in
/// with a password or an e-mailed one-time code. Tokens carry type "contact" and can never reach agent endpoints.
/// </summary>
public sealed class PortalAuthService(
    DeskDbContext db,
    RequestContext ctx,
    PasswordService passwords,
    TokenService tokens,
    TenantSettingsService settings,
    AuthService auth,
    IAuditWriter audit,
    LimitGuard limits)
{
    public async Task<Brand?> ResolveBrandAsync(string brandSlug, CancellationToken ct = default)
    {
        var slug = brandSlug.Trim().ToLowerInvariant();
        var brand = await db.AllTenants<Brand>().AsNoTracking().FirstOrDefaultAsync(b => b.Slug == slug && b.PortalEnabled, ct);
        if (brand is null) return null;
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == brand.TenantId, ct);
        if (tenant is null || tenant.Status is TenantStatus.Cancelled or TenantStatus.PendingDeletion) return null;
        ctx.SetTenant(tenant.Id, tenant.Slug);
        return brand;
    }

    /// <summary>Resolves a brand by custom domain (Host header) for white-labelled portals.</summary>
    public async Task<Brand?> ResolveBrandByHostAsync(string host, CancellationToken ct = default)
    {
        var h = host.Trim().ToLowerInvariant();
        var brand = await db.AllTenants<Brand>().AsNoTracking().FirstOrDefaultAsync(b => b.CustomDomain == h && b.PortalEnabled, ct);
        if (brand is not null) ctx.SetTenant(brand.TenantId);
        return brand;
    }

    public async Task<PortalAuthResult> LoginAsync(string brandSlug, PortalLoginRequest request, ClientInfo client, CancellationToken ct)
    {
        var brand = await ResolveBrandAsync(brandSlug, ct);
        if (brand is null) return new PortalAuthResult("invalid", Message: "Unknown help center.");
        var contact = await FindContactAsync(request.Email, ct);
        var security = await settings.SecurityAsync(ct);
        if (contact is null || contact.PortalAccess == PortalAccessScope.None || contact.PortalPasswordHash is null)
        {
            passwords.VerifyDummy(request.Password);
            return new PortalAuthResult("invalid", Message: "The e-mail or password is not correct.");
        }
        if (contact.PortalLockedUntil > db.Clock.UtcNow) return new PortalAuthResult("locked", Message: "Too many attempts. Try again later or sign in with an e-mailed code.");
        if (!passwords.Verify(contact.PortalPasswordHash, request.Password))
        {
            contact.PortalFailedLogins++;
            if (contact.PortalFailedLogins >= security.Password.MaxFailedAttempts)
            {
                contact.PortalLockedUntil = db.Clock.UtcNow.AddMinutes(security.Password.LockoutMinutes);
                contact.PortalFailedLogins = 0;
            }
            await db.SaveChangesAsync(ct);
            return new PortalAuthResult("invalid", Message: "The e-mail or password is not correct.");
        }
        return await CompleteAsync(contact, brand, client, ct);
    }

    public async Task<PortalAuthResult> RequestOtpAsync(string brandSlug, PortalOtpRequest request, CancellationToken ct)
    {
        var brand = await ResolveBrandAsync(brandSlug, ct);
        if (brand is null) return new PortalAuthResult("invalid", Message: "Unknown help center.");
        var contact = await FindContactAsync(request.Email, ct);
        // Same response whether or not the contact exists (no account enumeration).
        if (contact is null || contact.PortalAccess == PortalAccessScope.None)
            return new PortalAuthResult("sent", Message: "If the address is registered, a code is on its way.");
        var demo = (await settings.SecurityAsync(ct)).DemoMode;
        var (_, code) = await auth.CreateOtpAsync(PrincipalKind.Contact, contact.Id, OtpPurpose.PortalLogin, contact.Email, true, demo, ct);
        await db.SaveChangesAsync(ct);
        return new PortalAuthResult("sent", DemoCode: code, Message: "If the address is registered, a code is on its way.");
    }

    public async Task<PortalAuthResult> VerifyOtpAsync(string brandSlug, PortalOtpVerifyRequest request, ClientInfo client, CancellationToken ct)
    {
        var brand = await ResolveBrandAsync(brandSlug, ct);
        if (brand is null) return new PortalAuthResult("invalid", Message: "Unknown help center.");
        var contact = await FindContactAsync(request.Email, ct);
        if (contact is null) return new PortalAuthResult("invalid", Message: "That code is not correct.");
        var challenge = await db.OtpChallenges
            .Where(c => c.PrincipalId == contact.Id && c.Purpose == OtpPurpose.PortalLogin && c.ConsumedAt == null)
            .OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct);
        if (challenge is null || challenge.ExpiresAt < db.Clock.UtcNow || challenge.Attempts >= 5)
            return new PortalAuthResult("invalid", Message: "The code has expired. Request a new one.");
        challenge.Attempts++;
        var code = new string(request.Code.Where(char.IsDigit).ToArray());
        if (!TokenHasher.FixedEquals(TokenHasher.Hash(code), challenge.CodeHash))
        {
            await db.SaveChangesAsync(ct);
            return new PortalAuthResult("invalid", Message: "That code is not correct.");
        }
        challenge.ConsumedAt = db.Clock.UtcNow;
        contact.EmailVerifiedAt ??= db.Clock.UtcNow;
        return await CompleteAsync(contact, brand, client, ct);
    }

    public async Task<PortalAuthResult> RegisterAsync(string brandSlug, PortalRegisterRequest request, ClientInfo client, CancellationToken ct)
    {
        var brand = await ResolveBrandAsync(brandSlug, ct);
        if (brand is null || !brand.AllowSelfRegistration) return new PortalAuthResult("invalid", Message: "Self-registration is not enabled for this help center.");
        var email = TextNormalization.NormalizeEmail(request.Email) ?? throw new DomainException(ErrorCodes.Validation, "A valid e-mail is required.");
        if (await db.Contacts.AnyAsync(c => c.EmailNormalized == email, ct))
            return new PortalAuthResult("exists", Message: "An account already exists for this e-mail. Sign in with an e-mailed code.");
        await limits.EnsureWithinAsync(UsageMetrics.PortalUsers, 1, ct);
        var policy = (await settings.SecurityAsync(ct)).Password;
        var errors = passwords.Validate(request.Password, policy);
        if (errors.Count > 0) throw new DomainException(ErrorCodes.Validation, string.Join(" ", errors));
        var domain = TextNormalization.EmailDomain(email);
        var customers = await db.Customers.AsNoTracking().Select(c => new { c.Id, c.DomainsJson }).ToListAsync(ct);
        var match = customers.FirstOrDefault(c => Common.Json.StringList(c.DomainsJson).Contains(domain!, StringComparer.OrdinalIgnoreCase));
        var contact = new Contact
        {
            FullName = request.FullName.Trim(),
            Email = request.Email.Trim(),
            EmailNormalized = email,
            Phone = request.Phone,
            PhoneNormalized = TextNormalization.NormalizePhone(request.Phone),
            CustomerId = match?.Id,
            PortalAccess = PortalAccessScope.Own,
            PortalPasswordHash = passwords.Hash(request.Password)
        };
        db.Contacts.Add(contact);
        db.ContactIdentities.Add(new ContactIdentity { ContactId = contact.Id, Channel = Channel.Email, Identifier = email, CreatedAt = db.Clock.UtcNow });
        await db.SaveChangesAsync(ct);
        return await CompleteAsync(contact, brand, client, ct);
    }

    public async Task<PortalAuthResult> RefreshAsync(string brandSlug, string refreshToken, CancellationToken ct)
    {
        var brand = await ResolveBrandAsync(brandSlug, ct);
        if (brand is null) return new PortalAuthResult("invalid");
        var rotated = await auth.RotateAsync(refreshToken, PrincipalKind.Contact, ct);
        if (rotated is null) return new PortalAuthResult("invalid", Message: "Session expired.");
        var (session, next) = rotated.Value;
        if (session.TenantId != brand.TenantId) return new PortalAuthResult("invalid");
        var contact = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == session.PrincipalId, ct);
        if (contact is null || contact.PortalAccess == PortalAccessScope.None) return new PortalAuthResult("invalid");
        var access = await IssueAccessAsync(contact, brand, session.Id, ct);
        return new PortalAuthResult("ok", new SessionTokens(access.Token, access.ExpiresAt, next, session.ExpiresAt, session.Id), await BuildMeAsync(contact, brand, ct));
    }

    private async Task<PortalAuthResult> CompleteAsync(Contact contact, Brand brand, ClientInfo client, CancellationToken ct)
    {
        contact.PortalFailedLogins = 0;
        contact.PortalLastLoginAt = db.Clock.UtcNow;
        var (session, refresh) = await auth.CreateSessionAsync(PrincipalKind.Contact, contact.Id, brand.TenantId, client, false, ct);
        ctx.SetContact(brand.TenantId, contact.Id, contact.FullName);
        audit.Write(AuditActions.LoginSucceeded, "Contact", contact.Id, $"{contact.FullName} signed in to the portal ({brand.Slug})", new { client.IpAddress });
        await db.SaveChangesAsync(ct);
        var access = await IssueAccessAsync(contact, brand, session.Id, ct);
        return new PortalAuthResult("ok", new SessionTokens(access.Token, access.ExpiresAt, refresh, session.ExpiresAt, session.Id), await BuildMeAsync(contact, brand, ct));
    }

    private async Task<IssuedToken> IssueAccessAsync(Contact contact, Brand brand, Guid sessionId, CancellationToken ct)
    {
        var policy = (await settings.SecurityAsync(ct)).Session;
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == brand.TenantId, ct);
        return tokens.Issue(DeskClaims.TypeContact, contact.Id, brand.TenantId, tenant.Slug, contact.FullName,
            TimeSpan.FromMinutes(Math.Max(policy.AccessTokenMinutes, 15)), sessionId, contact.Email, brand: brand.Slug);
    }

    public async Task<PortalMeDto> BuildMeAsync(Contact contact, Brand brand, CancellationToken ct = default)
    {
        var customer = contact.CustomerId is null ? null : await db.Customers.AsNoTracking().Where(c => c.Id == contact.CustomerId).Select(c => c.Name).FirstOrDefaultAsync(ct);
        var location = contact.LocationId is null ? null : await db.CustomerLocations.AsNoTracking().Where(l => l.Id == contact.LocationId).Select(l => l.Name).FirstOrDefaultAsync(ct);
        return new PortalMeDto(contact.Id, contact.TenantId, brand.Slug, contact.FullName, contact.Email, contact.CustomerId, customer, contact.LocationId,
            location, contact.PortalAccess.ToString(), contact.PreferredLanguage, contact.CustomerId.HasValue && contact.PortalAccess >= PortalAccessScope.Location);
    }

    private async Task<Contact?> FindContactAsync(string email, CancellationToken ct)
    {
        var normalized = TextNormalization.NormalizeEmail(email);
        if (normalized is null) return null;
        return await db.Contacts.FirstOrDefaultAsync(c => c.EmailNormalized == normalized && c.Status == ContactStatus.Active, ct);
    }
}

/// <summary>Platform operator (SaaS administration) authentication. Operators always use TOTP MFA once enrolled.</summary>
public sealed class PlatformAuthService(DeskDbContext db, PasswordService passwords, TokenService tokens, ISecretProtector secrets, IAuditWriter audit)
{
    public async Task<PlatformLoginResponse> LoginAsync(PlatformLoginRequest request, CancellationToken ct)
    {
        var email = TextNormalization.NormalizeEmail(request.Email) ?? "";
        var op = await db.PlatformOperators.FirstOrDefaultAsync(o => o.Email == email && o.IsActive, ct);
        if (op is null || !passwords.Verify(op.PasswordHash, request.Password))
            return new PlatformLoginResponse("invalid", null, null, null, "Invalid credentials.");
        if (op.MfaSecretProtected is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Code)) return new PlatformLoginResponse("mfa_required", null, null, null, "Enter your authenticator code.");
            if (!Totp.Verify(secrets.Unprotect(op.MfaSecretProtected), request.Code, db.Clock.UtcNow))
                return new PlatformLoginResponse("invalid", null, null, null, "That code is not correct.");
        }
        op.LastLoginAt = db.Clock.UtcNow;
        audit.Write(AuditActions.LoginSucceeded, "PlatformOperator", op.Id, $"Platform operator {op.DisplayName} signed in");
        await db.SaveChangesAsync(ct);
        var token = tokens.Issue(DeskClaims.TypePlatform, op.Id, null, null, op.DisplayName, TimeSpan.FromMinutes(30), email: op.Email, mfa: op.MfaSecretProtected is not null);
        return new PlatformLoginResponse("ok", token.Token, token.ExpiresAt, op.DisplayName);
    }
}
