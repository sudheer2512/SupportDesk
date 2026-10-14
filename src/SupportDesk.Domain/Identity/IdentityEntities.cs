namespace SupportDesk.Domain.Identity;

public enum UserKind { Agent, LightAgent, Administrator, System }

/// <summary>An internal user (agent, supervisor, administrator). Customer portal users are Contacts.</summary>
public class User : AuditedEntity, ISoftDeletable, IConcurrencyVersioned
{
    public string Email { get; set; } = "";
    public string EmailNormalized { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Phone { get; set; }
    public UserKind Kind { get; set; } = UserKind.Agent;
    public bool IsActive { get; set; } = true;
    public string? PasswordHash { get; set; }
    public DateTime? PasswordChangedAt { get; set; }
    /// <summary>Hashes of recent passwords (password history policy).</summary>
    public string PasswordHistoryJson { get; set; } = "[]";
    public bool MfaEnabled { get; set; }
    /// <summary>TOTP secret encrypted with ISecretProtector.</summary>
    public string? MfaSecretProtected { get; set; }
    public string? MfaRecoveryCodesJson { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTime? LockedUntil { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public string TimeZone { get; set; } = "UTC";
    public string Locale { get; set; } = "en";
    public string? AvatarUrl { get; set; }
    public string? Signature { get; set; }
    /// <summary>Subject/issuer from an external OpenID Connect provider (SSO).</summary>
    public string? ExternalSubject { get; set; }
    public string? ExternalIssuer { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public List<UserRole> Roles { get; set; } = [];
}

/// <summary>Breadth of ticket data a role can see. The broadest scope across a user's roles wins.</summary>
public enum TicketScope { Own = 1, Team = 2, Departments = 3, All = 4 }

public enum CustomerScope { AccountManaged = 1, All = 2 }

public class Role : AuditedEntity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public TicketScope TicketScope { get; set; } = TicketScope.Departments;
    public CustomerScope CustomerScope { get; set; } = CustomerScope.All;
    public List<RolePermission> Permissions { get; set; } = [];
}

/// <summary>Global permission catalog row (seeded from <see cref="SupportDesk.Domain.Rules.Permissions"/>).</summary>
public class Permission : Entity
{
    public string Code { get; set; } = "";
    public string Module { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsSensitive { get; set; }
}

public class RolePermission : TenantEntity
{
    public Guid RoleId { get; set; }
    public string PermissionCode { get; set; } = "";
}

public class UserRole : TenantEntity
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
}

public enum PrincipalKind { User, Contact, Platform }

/// <summary>
/// A login session backed by a rotating refresh token. Reusing an already-rotated token revokes the whole family
/// (token theft detection). Idle and absolute lifetimes come from the tenant session policy.
/// </summary>
public class UserSession : Entity
{
    public Guid? TenantId { get; set; }
    public PrincipalKind PrincipalKind { get; set; }
    public Guid PrincipalId { get; set; }
    public string TokenHash { get; set; } = "";
    public Guid FamilyId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime IdleExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
    public string? ReplacedByHash { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? ClientKind { get; set; }
    public bool MfaSatisfied { get; set; }
    public DateTime LastSeenAt { get; set; }
}

public enum OtpPurpose { LoginMfa, PortalLogin, VerifyEmail, VerifyPhone, PasswordReset, ChatIdentity }

/// <summary>One-time code (email/SMS OTP or MFA challenge). Only the hash is stored.</summary>
public class OtpChallenge : Entity
{
    public Guid? TenantId { get; set; }
    public PrincipalKind PrincipalKind { get; set; }
    public Guid PrincipalId { get; set; }
    public OtpPurpose Purpose { get; set; }
    public string CodeHash { get; set; } = "";
    public string? Destination { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public int Attempts { get; set; }
    public DateTime? ConsumedAt { get; set; }
}
