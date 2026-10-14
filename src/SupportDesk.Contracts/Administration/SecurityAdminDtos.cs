namespace SupportDesk.Contracts.Administration;

public sealed record PasswordPolicyDto(
    int MinLength,
    bool RequireUpper,
    bool RequireLower,
    bool RequireDigit,
    bool RequireSymbol,
    int HistoryCount,
    int ExpiryDays,
    int MaxFailedAttempts,
    int LockoutMinutes);

public sealed record SessionPolicyDto(int AccessTokenMinutes, int IdleTimeoutMinutes, int AbsoluteLifetimeHours, int MaxConcurrentSessions);

/// <summary>Agent IP allow-list. Ranges are single addresses or CIDR blocks (IPv4/IPv6). Enforced at sign-in and on every agent request.</summary>
public sealed record IpAllowListDto(bool Enabled, IReadOnlyList<string> Ranges);

/// <summary>Security policies. MfaPolicy: Optional, RequiredForAdmins or RequiredForAll. CurrentIp: the caller's address as the API sees it.</summary>
public sealed record SecurityPolicyDto(
    string MfaPolicy,
    PasswordPolicyDto Password,
    SessionPolicyDto Session,
    IpAllowListDto IpAllowList,
    string? CurrentIp,
    int Version);

public sealed record SaveSecurityPolicyRequest(string MfaPolicy, PasswordPolicyDto Password, SessionPolicyDto Session, IpAllowListDto IpAllowList);

/// <summary>OpenID Connect single sign-on. The client secret is write-only (HasClientSecret tells whether one is stored).</summary>
public sealed record SsoConfigDto(
    bool Enabled,
    string? Authority,
    string? ClientId,
    bool HasClientSecret,
    string Scopes,
    string EmailClaim,
    string NameClaim,
    bool AutoProvision,
    Guid? DefaultRoleId,
    bool EnforceSso,
    string CallbackUrl,
    string StartUrl,
    bool PlanIncludesSso);

/// <summary>ClientSecret: null keeps the stored secret, a value replaces it; ClearClientSecret removes it.</summary>
public sealed record SaveSsoConfigRequest(
    bool Enabled,
    string? Authority,
    string? ClientId,
    string? ClientSecret,
    bool ClearClientSecret,
    string Scopes,
    string EmailClaim,
    string NameClaim,
    bool AutoProvision,
    Guid? DefaultRoleId,
    bool EnforceSso);

public sealed record TestSsoRequest(string? Authority = null);

public sealed record SsoTestResultDto(bool Success, string? Issuer, string? AuthorizationEndpoint, string? TokenEndpoint, int SigningKeys, string Message);

/// <summary>An active agent session (refresh-token family head).</summary>
public sealed record AdminSessionDto(
    Guid Id,
    Guid UserId,
    string UserName,
    string? Email,
    string? ClientKind,
    string? IpAddress,
    string? UserAgent,
    DateTime CreatedAt,
    DateTime LastSeenAt,
    DateTime ExpiresAt,
    bool MfaSatisfied,
    bool Current);

public sealed record RevokeSessionsResultDto(int Revoked);
