using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Contracts.Auth;
using SupportDesk.Contracts.Common;
using SupportDesk.Domain;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Organisation;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Identity;

public sealed record ClientInfo(string? IpAddress, string? UserAgent, string ClientKind);

public sealed record SessionTokens(string AccessToken, DateTime AccessExpiresAt, string RefreshToken, DateTime RefreshExpiresAt, Guid SessionId);

public sealed record AuthResult(string Status, SessionTokens? Tokens = null, MeDto? Me = null, Guid? ChallengeId = null,
    IReadOnlyList<string>? Methods = null, string? DemoCode = null, string? Message = null);

/// <summary>
/// Agent/admin authentication: password login with lockout, MFA (TOTP or e-mail OTP), rotating refresh tokens with
/// reuse detection, idle/absolute session lifetimes, password policy and history.
/// </summary>
public sealed class AuthService(
    DeskDbContext db,
    RequestContext ctx,
    PasswordService passwords,
    TokenService tokens,
    TenantSettingsService settings,
    ISecretProtector secrets,
    ISystemMailer mailer,
    IAuditWriter audit,
    PrincipalLoader principals,
    Administration.IpAccessPolicy ipPolicy)
{
    private const int OtpMinutes = 10;

    public async Task<AuthResult> LoginAsync(LoginRequest request, ClientInfo client, CancellationToken ct = default)
    {
        var tenant = await ResolveTenantAsync(request.Tenant, ct);
        if (tenant is null) return Invalid();
        ctx.SetTenant(tenant.Id, tenant.Slug);
        var security = await settings.SecurityAsync(ct);
        var email = TextNormalization.NormalizeEmail(request.Email) ?? "";
        var user = await db.Users.FirstOrDefaultAsync(u => u.EmailNormalized == email, ct);

        if (user is null || !user.IsActive)
        {
            passwords.VerifyDummy(request.Password);
            audit.Write(AuditActions.LoginFailed, "User", user?.Id, $"Failed login for {email}", new { reason = user is null ? "unknown_user" : "inactive", client.IpAddress });
            await db.SaveChangesAsync(ct);
            return Invalid();
        }
        if (user.LockedUntil > db.Clock.UtcNow)
            return new AuthResult("locked", Message: $"Too many failed attempts. Try again after {user.LockedUntil:HH:mm} UTC.");

        if (!passwords.Verify(user.PasswordHash, request.Password))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= security.Password.MaxFailedAttempts)
            {
                user.LockedUntil = db.Clock.UtcNow.AddMinutes(security.Password.LockoutMinutes);
                user.FailedLoginCount = 0;
            }
            audit.Write(AuditActions.LoginFailed, "User", user.Id, $"Failed login for {user.Email}", new { reason = "bad_password", client.IpAddress });
            await db.SaveChangesAsync(ct);
            return Invalid();
        }

        user.FailedLoginCount = 0;
        // Agent IP allow-list (administration → security).
        if (!await ipPolicy.IsAllowedAsync(client.IpAddress, ct))
        {
            audit.Write(AuditActions.LoginFailed, "User", user.Id, $"Sign-in blocked for {user.Email}: network not allowed", new { reason = "ip_not_allowed", client.IpAddress });
            await db.SaveChangesAsync(ct);
            return new AuthResult(LoginStatus.IpNotAllowed, Message: "Sign-in from your network is not allowed by your organisation.");
        }
        var isAdmin = await IsAdministratorAsync(user, ct);
        var sso = await settings.GetAsync<SsoSettings>(SettingKeys.Sso, ct);
        if (sso.Enabled && sso.EnforceSso && !isAdmin)
        {
            await db.SaveChangesAsync(ct);
            return new AuthResult(LoginStatus.SsoRequired, Message: "Your organisation requires single sign-on. Use “Sign in with SSO”.");
        }
        if (security.Password.ExpiryDays > 0 && user.PasswordChangedAt is { } changed && changed.AddDays(security.Password.ExpiryDays) < db.Clock.UtcNow)
        {
            await db.SaveChangesAsync(ct);
            return new AuthResult(LoginStatus.PasswordExpired, Message: "Your password has expired. Choose a new one to continue.");
        }

        // MFA policy: enrolled users always; users the policy covers get an e-mailed code even without an authenticator app.
        if (user.MfaEnabled || security.RequireMfaForAll || (security.RequireMfaForAdmins && isAdmin))
        {
            var (challenge, demoCode) = await CreateOtpAsync(PrincipalKind.User, user.Id, OtpPurpose.LoginMfa, user.Email, sendEmail: true, security.DemoMode, ct);
            await db.SaveChangesAsync(ct);
            var methods = new List<string>();
            if (!string.IsNullOrEmpty(user.MfaSecretProtected)) methods.Add("totp");
            methods.Add("email");
            return new AuthResult(LoginStatus.MfaRequired, ChallengeId: challenge.Id, Methods: methods, DemoCode: demoCode,
                Message: "Enter the code from your authenticator app or the code we e-mailed you.");
        }

        return await CompleteLoginAsync(user, tenant, client, mfa: false, ct);
    }

    public async Task<AuthResult> VerifyMfaAsync(MfaVerifyRequest request, ClientInfo client, CancellationToken ct = default)
    {
        var challenge = await db.OtpChallenges.FirstOrDefaultAsync(c => c.Id == request.ChallengeId && c.Purpose == OtpPurpose.LoginMfa, ct);
        if (challenge is null || challenge.ConsumedAt is not null || challenge.ExpiresAt < db.Clock.UtcNow || challenge.Attempts >= 5)
            return new AuthResult("invalid", Message: "The verification code has expired. Sign in again.");
        challenge.Attempts++;
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == challenge.TenantId, ct);
        ctx.SetTenant(tenant.Id, tenant.Slug);
        var user = await db.Users.FirstAsync(u => u.Id == challenge.PrincipalId, ct);

        var ok = request.Method == "totp" && user.MfaSecretProtected is not null
            ? Totp.Verify(secrets.Unprotect(user.MfaSecretProtected), request.Code, db.Clock.UtcNow)
            : TokenHasher.FixedEquals(TokenHasher.Hash(Normalize(request.Code)), challenge.CodeHash);
        if (!ok)
        {
            await db.SaveChangesAsync(ct);
            return new AuthResult("invalid", Message: "That code is not correct.");
        }
        challenge.ConsumedAt = db.Clock.UtcNow;
        return await CompleteLoginAsync(user, tenant, client, mfa: true, ct);
    }

    private async Task<AuthResult> CompleteLoginAsync(User user, Tenant tenant, ClientInfo client, bool mfa, CancellationToken ct)
    {
        user.LastLoginAt = db.Clock.UtcNow;
        var session = await CreateSessionAsync(PrincipalKind.User, user.Id, tenant.Id, client, mfa, ct);
        audit.Write(AuditActions.LoginSucceeded, "User", user.Id, $"{user.DisplayName} signed in", new { client.IpAddress, client.ClientKind, mfa });
        await db.SaveChangesAsync(ct);
        var access = await IssueAccessAsync(user, tenant, session.Session.Id, mfa, ct);
        var me = await principals.BuildMeAsync(user.Id, ct);
        return new AuthResult(LoginStatus.Ok, new SessionTokens(access.Token, access.ExpiresAt, session.RefreshToken, session.Session.ExpiresAt, session.Session.Id), me);
    }

    private async Task<IssuedToken> IssueAccessAsync(User user, Tenant tenant, Guid sessionId, bool mfa, CancellationToken ct)
    {
        var policy = (await settings.SecurityAsync(ct)).Session;
        return tokens.Issue(DeskClaims.TypeAgent, user.Id, tenant.Id, tenant.Slug, user.DisplayName,
            TimeSpan.FromMinutes(policy.AccessTokenMinutes), sessionId, user.Email, mfa: mfa);
    }

    /// <summary>Creates a session with a new refresh-token family, enforcing the concurrent-session limit.</summary>
    public async Task<(UserSession Session, string RefreshToken)> CreateSessionAsync(PrincipalKind kind, Guid principalId, Guid? tenantId, ClientInfo client,
        bool mfa, CancellationToken ct)
    {
        var policy = (await settings.SecurityAsync(ct)).Session;
        var now = db.Clock.UtcNow;
        var active = await db.UserSessions
            .Where(s => s.PrincipalKind == kind && s.PrincipalId == principalId && s.RevokedAt == null && s.ExpiresAt > now && s.ReplacedByHash == null)
            .OrderBy(s => s.CreatedAt).ToListAsync(ct);
        foreach (var old in active.Take(Math.Max(0, active.Count - policy.MaxConcurrentSessions + 1)))
        {
            old.RevokedAt = now;
            old.RevokedReason = "session_limit";
        }
        var refresh = TextNormalization.RandomToken(32);
        var session = new UserSession
        {
            TenantId = tenantId,
            PrincipalKind = kind,
            PrincipalId = principalId,
            TokenHash = TokenHasher.Hash(refresh),
            FamilyId = Guid.NewGuid(),
            CreatedAt = now,
            ExpiresAt = now.AddHours(policy.AbsoluteLifetimeHours),
            IdleExpiresAt = now.AddMinutes(policy.IdleTimeoutMinutes),
            IpAddress = client.IpAddress,
            UserAgent = client.UserAgent is { Length: > 300 } ua ? ua[..300] : client.UserAgent,
            ClientKind = client.ClientKind,
            MfaSatisfied = mfa,
            LastSeenAt = now
        };
        db.UserSessions.Add(session);
        return (session, refresh);
    }

    /// <summary>
    /// Rotates a refresh token. Presenting a token that was already rotated means it was stolen or replayed:
    /// the whole token family is revoked and the event is audited.
    /// </summary>
    public async Task<(UserSession Session, string RefreshToken)?> RotateAsync(string refreshToken, PrincipalKind expectedKind, CancellationToken ct)
    {
        var hash = TokenHasher.Hash(refreshToken);
        var session = await db.UserSessions.FirstOrDefaultAsync(s => s.TokenHash == hash, ct);
        var now = db.Clock.UtcNow;
        if (session is null || session.PrincipalKind != expectedKind) return null;
        if (session.ReplacedByHash is not null)
        {
            var family = await db.UserSessions.Where(s => s.FamilyId == session.FamilyId && s.RevokedAt == null).ToListAsync(ct);
            foreach (var s in family)
            {
                s.RevokedAt = now;
                s.RevokedReason = "refresh_token_reuse";
            }
            if (session.TenantId.HasValue) ctx.SetTenant(session.TenantId.Value);
            audit.Write(AuditActions.TokenReuseDetected, session.PrincipalKind.ToString(), session.PrincipalId,
                "A rotated refresh token was presented again; the session family was revoked.", new { session.FamilyId, session.IpAddress });
            await db.SaveChangesAsync(ct);
            return null;
        }
        if (session.RevokedAt is not null || session.ExpiresAt <= now || session.IdleExpiresAt <= now) return null;

        if (session.TenantId.HasValue) ctx.SetTenant(session.TenantId.Value);
        var policy = (await settings.SecurityAsync(ct)).Session;
        var next = TextNormalization.RandomToken(32);
        var nextHash = TokenHasher.Hash(next);
        session.ReplacedByHash = nextHash;
        var successor = new UserSession
        {
            TenantId = session.TenantId,
            PrincipalKind = session.PrincipalKind,
            PrincipalId = session.PrincipalId,
            TokenHash = nextHash,
            FamilyId = session.FamilyId,
            CreatedAt = session.CreatedAt,
            ExpiresAt = session.ExpiresAt,
            IdleExpiresAt = Min(now.AddMinutes(policy.IdleTimeoutMinutes), session.ExpiresAt),
            IpAddress = session.IpAddress,
            UserAgent = session.UserAgent,
            ClientKind = session.ClientKind,
            MfaSatisfied = session.MfaSatisfied,
            LastSeenAt = now
        };
        db.UserSessions.Add(successor);
        await db.SaveChangesAsync(ct);
        return (successor, next);
    }

    public async Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var rotated = await RotateAsync(refreshToken, PrincipalKind.User, ct);
        if (rotated is null) return new AuthResult("invalid", Message: "Session expired. Sign in again.");
        var (session, next) = rotated.Value;
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == session.PrincipalId, ct);
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == session.TenantId, ct);
        if (user is null || !user.IsActive || tenant is null) return new AuthResult("invalid", Message: "Account unavailable.");
        var access = await IssueAccessAsync(user, tenant, session.Id, session.MfaSatisfied, ct);
        var me = await principals.BuildMeAsync(user.Id, ct);
        return new AuthResult(LoginStatus.Ok, new SessionTokens(access.Token, access.ExpiresAt, next, session.ExpiresAt, session.Id), me);
    }

    public async Task LogoutAsync(Guid? sessionId, string? refreshToken, CancellationToken ct = default)
    {
        UserSession? session = null;
        if (refreshToken is not null)
        {
            var hash = TokenHasher.Hash(refreshToken);
            session = await db.UserSessions.FirstOrDefaultAsync(s => s.TokenHash == hash, ct);
        }
        session ??= sessionId is null ? null : await db.UserSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return;
        var family = await db.UserSessions.Where(s => s.FamilyId == session.FamilyId && s.RevokedAt == null).ToListAsync(ct);
        foreach (var s in family)
        {
            s.RevokedAt = db.Clock.UtcNow;
            s.RevokedReason = "logout";
        }
        if (ctx.HasTenant) audit.Write(AuditActions.Logout, session.PrincipalKind.ToString(), session.PrincipalId, "Signed out");
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> IsSessionActiveAsync(Guid sessionId, CancellationToken ct = default)
    {
        var now = db.Clock.UtcNow;
        return await db.UserSessions.AnyAsync(s => s.FamilyId == db.UserSessions.Where(x => x.Id == sessionId).Select(x => x.FamilyId).FirstOrDefault()
                                                   && s.RevokedAt == null && s.ExpiresAt > now, ct);
    }

    public async Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new NotFoundException("User");
        if (!passwords.Verify(user.PasswordHash, request.CurrentPassword))
            throw new DomainException(ErrorCodes.Validation, "The current password is not correct.");
        await SetPasswordAsync(user, request.NewPassword, ct);
        audit.Write(AuditActions.PasswordChanged, "User", user.Id, "Password changed");
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Password change for an expired password (no session yet).</summary>
    public async Task<AuthResult> ChangeExpiredPasswordAsync(string tenantSlug, string email, string currentPassword, string newPassword, ClientInfo client, CancellationToken ct)
    {
        var tenant = await ResolveTenantAsync(tenantSlug, ct);
        if (tenant is null) return Invalid();
        ctx.SetTenant(tenant.Id, tenant.Slug);
        var normalized = TextNormalization.NormalizeEmail(email) ?? "";
        var user = await db.Users.FirstOrDefaultAsync(u => u.EmailNormalized == normalized && u.IsActive, ct);
        if (user is null || !passwords.Verify(user.PasswordHash, currentPassword)) return Invalid();
        await SetPasswordAsync(user, newPassword, ct);
        audit.Write(AuditActions.PasswordChanged, "User", user.Id, "Expired password replaced");
        await db.SaveChangesAsync(ct);
        return await LoginAsync(new LoginRequest(tenantSlug, email, newPassword), client, ct);
    }

    public async Task SetPasswordAsync(User user, string newPassword, CancellationToken ct)
    {
        var policy = (await settings.SecurityAsync(ct)).Password;
        var history = Json.StringList(user.PasswordHistoryJson);
        var errors = passwords.Validate(newPassword, policy, user.PasswordHash is null ? history : history.Prepend(user.PasswordHash));
        if (errors.Count > 0) throw new DomainException(ErrorCodes.Validation, string.Join(" ", errors));
        if (user.PasswordHash is not null) history.Insert(0, user.PasswordHash);
        user.PasswordHistoryJson = Json.Serialize(history.Take(Math.Max(1, policy.HistoryCount)).ToList());
        user.PasswordHash = passwords.Hash(newPassword);
        user.PasswordChangedAt = db.Clock.UtcNow;
    }

    public async Task<string?> RequestPasswordResetAsync(PasswordResetRequest request, CancellationToken ct)
    {
        var tenant = await ResolveTenantAsync(request.Tenant, ct);
        if (tenant is null) return null;
        ctx.SetTenant(tenant.Id, tenant.Slug);
        var email = TextNormalization.NormalizeEmail(request.Email) ?? "";
        var user = await db.Users.FirstOrDefaultAsync(u => u.EmailNormalized == email && u.IsActive, ct);
        if (user is null) return null;
        var demo = (await settings.SecurityAsync(ct)).DemoMode;
        var (_, demoCode) = await CreateOtpAsync(PrincipalKind.User, user.Id, OtpPurpose.PasswordReset, user.Email, true, demo, ct);
        await db.SaveChangesAsync(ct);
        return demoCode;
    }

    public async Task ConfirmPasswordResetAsync(PasswordResetConfirmRequest request, CancellationToken ct)
    {
        var tenant = await ResolveTenantAsync(request.Tenant, ct) ?? throw new DomainException(ErrorCodes.Validation, "Invalid reset request.");
        ctx.SetTenant(tenant.Id, tenant.Slug);
        var email = TextNormalization.NormalizeEmail(request.Email) ?? "";
        var user = await db.Users.FirstOrDefaultAsync(u => u.EmailNormalized == email && u.IsActive, ct)
                   ?? throw new DomainException(ErrorCodes.Validation, "Invalid reset request.");
        var challenge = await db.OtpChallenges
            .Where(c => c.PrincipalId == user.Id && c.Purpose == OtpPurpose.PasswordReset && c.ConsumedAt == null)
            .OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct);
        if (challenge is null || challenge.ExpiresAt < db.Clock.UtcNow || challenge.Attempts >= 5)
            throw new DomainException(ErrorCodes.Validation, "The reset code has expired.");
        challenge.Attempts++;
        if (!TokenHasher.FixedEquals(TokenHasher.Hash(Normalize(request.Code)), challenge.CodeHash))
        {
            await db.SaveChangesAsync(ct);
            throw new DomainException(ErrorCodes.Validation, "The reset code is not correct.");
        }
        challenge.ConsumedAt = db.Clock.UtcNow;
        await SetPasswordAsync(user, request.NewPassword, ct);
        user.LockedUntil = null;
        user.FailedLoginCount = 0;
        audit.Write(AuditActions.PasswordChanged, "User", user.Id, "Password reset with e-mailed code");
        await db.SaveChangesAsync(ct);
    }

    public async Task<MfaSetupDto> BeginMfaSetupAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new NotFoundException("User");
        var secret = Totp.NewSecret();
        await db.OtpChallenges.Where(c => c.PrincipalId == userId && c.Purpose == OtpPurpose.VerifyEmail && c.Destination == "totp-setup")
            .ExecuteDeleteAsync(ct);
        // The pending secret is held (encrypted) in an OTP challenge until confirmed with a valid code.
        db.OtpChallenges.Add(new OtpChallenge
        {
            TenantId = ctx.TenantId, PrincipalKind = PrincipalKind.User, PrincipalId = userId, Purpose = OtpPurpose.VerifyEmail,
            CodeHash = secrets.Protect(secret), Destination = "totp-setup", CreatedAt = db.Clock.UtcNow, ExpiresAt = db.Clock.UtcNow.AddMinutes(15)
        });
        await db.SaveChangesAsync(ct);
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == ctx.TenantId, ct);
        return new MfaSetupDto(secret, Totp.OtpAuthUri($"SupportDesk ({tenant.Name})", user.Email, secret));
    }

    public async Task EnableMfaAsync(Guid userId, string code, CancellationToken ct)
    {
        var pending = await db.OtpChallenges
            .Where(c => c.PrincipalId == userId && c.Destination == "totp-setup" && c.ConsumedAt == null && c.ExpiresAt > db.Clock.UtcNow)
            .OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct) ?? throw new DomainException(ErrorCodes.Validation, "Start MFA setup again.");
        var secret = secrets.Unprotect(pending.CodeHash);
        if (!Totp.Verify(secret, code, db.Clock.UtcNow)) throw new DomainException(ErrorCodes.Validation, "That code is not correct.");
        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        user.MfaEnabled = true;
        user.MfaSecretProtected = secrets.Protect(secret);
        pending.ConsumedAt = db.Clock.UtcNow;
        audit.Write(AuditActions.MfaEnabled, "User", userId, "Authenticator app MFA enabled");
        await db.SaveChangesAsync(ct);
    }

    public async Task DisableMfaAsync(Guid userId, CancellationToken ct)
    {
        var security = await settings.SecurityAsync(ct);
        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        var isAdmin = await IsAdministratorAsync(user, ct);
        if (security.RequireMfaForAll || (security.RequireMfaForAdmins && isAdmin))
            throw new DomainException("mfa_required_by_policy", "Your organisation requires multi-factor authentication.");
        user.MfaEnabled = false;
        user.MfaSecretProtected = null;
        audit.Write(AuditActions.MfaDisabled, "User", userId, "MFA disabled");
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Creates a one-time code (hash stored) and optionally e-mails it. Returns the code only in demo mode.</summary>
    public async Task<(OtpChallenge Challenge, string? DemoCode)> CreateOtpAsync(PrincipalKind kind, Guid principalId, OtpPurpose purpose, string? destination,
        bool sendEmail, bool demoMode, CancellationToken ct)
    {
        var code = System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var challenge = new OtpChallenge
        {
            TenantId = ctx.HasTenant ? ctx.TenantId : null,
            PrincipalKind = kind,
            PrincipalId = principalId,
            Purpose = purpose,
            CodeHash = TokenHasher.Hash(code),
            Destination = destination,
            CreatedAt = db.Clock.UtcNow,
            ExpiresAt = db.Clock.UtcNow.AddMinutes(OtpMinutes)
        };
        db.OtpChallenges.Add(challenge);
        if (sendEmail && destination is not null)
        {
            var subject = purpose switch
            {
                OtpPurpose.PasswordReset => "Your password reset code",
                OtpPurpose.PortalLogin => "Your sign-in code",
                _ => "Your verification code"
            };
            await mailer.SendAsync(ctx.HasTenant ? ctx.TenantId : null, destination, subject,
                $"<p>Your code is <strong style=\"font-size:20px;letter-spacing:3px\">{code}</strong>.</p><p>It expires in {OtpMinutes} minutes. If you did not request it, ignore this e-mail.</p>",
                $"Your code is {code}. It expires in {OtpMinutes} minutes.", null, ct);
        }
        return (challenge, demoMode ? code : null);
    }

    public async Task<Tenant?> ResolveTenantAsync(string? slug, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        var s = slug.Trim().ToLowerInvariant();
        return await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == s && t.Status != TenantStatus.PendingDeletion, ct);
    }

    /// <summary>Administrators for the MFA and SSO policies: the Administrator kind or a role that can manage roles or security.</summary>
    private async Task<bool> IsAdministratorAsync(User user, CancellationToken ct) =>
        user.Kind == UserKind.Administrator ||
        await db.RolePermissions.AnyAsync(p => (p.PermissionCode == Permissions.AdminRoles || p.PermissionCode == Permissions.AdminSecurity)
                                               && db.UserRoles.Any(ur => ur.UserId == user.Id && ur.RoleId == p.RoleId), ct);

    private static string Normalize(string code) => new(code.Where(char.IsLetterOrDigit).ToArray());

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    private static AuthResult Invalid() => new("invalid", Message: "The organisation, e-mail or password is not correct.");
}
