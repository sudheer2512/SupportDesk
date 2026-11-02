using SupportDesk.Contracts.Common;

namespace SupportDesk.Contracts.Auth;

public sealed record LoginRequest(string Tenant, string Email, string Password);

public static class LoginStatus
{
    public const string Ok = "ok";
    public const string MfaRequired = "mfa_required";
    public const string PasswordExpired = "password_expired";
    /// <summary>The organisation's agent IP allow-list does not include the caller's address (HTTP 403).</summary>
    public const string IpNotAllowed = "ip_not_allowed";
    /// <summary>The organisation requires single sign-on for this user (HTTP 403).</summary>
    public const string SsoRequired = "sso_required";
}

/// <summary>
/// Login result. For web clients the refresh token is set as an httpOnly SameSite=Strict cookie and
/// <see cref="RefreshToken"/> is null; mobile clients (header X-Client: mobile) receive it in the body.
/// </summary>
public sealed record LoginResponse(
    string Status,
    string? AccessToken = null,
    DateTime? ExpiresAt = null,
    string? RefreshToken = null,
    MeDto? User = null,
    Guid? ChallengeId = null,
    IReadOnlyList<string>? Methods = null,
    string? DemoCode = null,
    string? Message = null);

public sealed record MfaVerifyRequest(Guid ChallengeId, string Code, string? Method = "totp");

public sealed record RefreshRequest(string? RefreshToken);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record MfaSetupDto(string Secret, string OtpAuthUri);

public sealed record MfaEnableRequest(string Code);

public sealed record PasswordResetRequest(string Tenant, string Email);

public sealed record PasswordResetConfirmRequest(string Tenant, string Email, string Code, string NewPassword);

/// <summary>The signed-in agent/admin with effective permissions (the UI hides actions; the API enforces them).</summary>
public sealed record MeDto(
    Guid Id,
    Guid TenantId,
    string TenantSlug,
    string TenantName,
    string DisplayName,
    string Email,
    string? AvatarUrl,
    string TimeZone,
    string Locale,
    string Kind,
    bool IsLightAgent,
    IReadOnlyList<string> Permissions,
    string TicketScope,
    IReadOnlyList<IdName> Departments,
    IReadOnlyList<IdName> Teams,
    string Availability,
    IReadOnlyList<string> PlanFeatures,
    string SubscriptionStatus,
    bool MfaEnabled,
    string? Signature,
    /// <summary>Names of the user's roles ("Support supervisor"), for display; permissions drive behaviour.</summary>
    IReadOnlyList<string> Roles);

// ───── Customer portal ─────

public sealed record PortalLoginRequest(string Email, string Password);

public sealed record PortalOtpRequest(string Email);

public sealed record PortalOtpVerifyRequest(string Email, string Code);

public sealed record PortalRegisterRequest(string FullName, string Email, string Password, string? Company, string? Phone);

public sealed record PortalLoginResponse(string Status, string? AccessToken, DateTime? ExpiresAt, string? RefreshToken, PortalMeDto? Contact,
    string? DemoCode = null, string? Message = null);

public sealed record PortalMeDto(
    Guid Id,
    Guid TenantId,
    string BrandSlug,
    string FullName,
    string? Email,
    Guid? CustomerId,
    string? CustomerName,
    Guid? LocationId,
    string? LocationName,
    string AccessScope,
    string PreferredLanguage,
    bool CanViewContracts);

// ───── Platform operators ─────

public sealed record PlatformLoginRequest(string Email, string Password, string? Code);

public sealed record PlatformLoginResponse(string Status, string? AccessToken, DateTime? ExpiresAt, string? DisplayName, string? Message = null);

// ───── OAuth 2.0 client credentials ─────

/// <summary>RFC 6749 token response (snake_case on the wire).</summary>
public sealed record OAuthTokenResponse(string access_token, string token_type, int expires_in, string scope);
