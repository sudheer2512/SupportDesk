namespace SupportDesk.Domain.Platform;

// ───────────── SaaS operation: usage history, trial sign-up, support access ─────────────

/// <summary>
/// Daily usage history (one row per tenant, metric and UTC day) written by the usage-metering task. Lives in the shared
/// catalog database so the platform console and history charts work for shared and dedicated tenants alike.
/// </summary>
public class UsageSnapshot : TenantEntity
{
    public DateTime Day { get; set; }
    public string Metric { get; set; } = "";
    public long Quantity { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum SignupStatus { Pending, Completed, Expired, Locked }

/// <summary>
/// A self-service trial sign-up awaiting e-mail verification. Global (no tenant yet); the password is stored only as a
/// hash and the verification code only as a SHA-256 hash. Completed sign-ups keep the provisioned tenant id.
/// </summary>
public class TrialSignup : Entity, IHasTimestamps
{
    public string CompanyName { get; set; } = "";
    public string Slug { get; set; } = "";
    public string AdminName { get; set; } = "";
    public string AdminEmail { get; set; } = "";
    public string AdminEmailNormalized { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string PlanCode { get; set; } = "trial";
    public string CodeHash { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public int Attempts { get; set; }
    public SignupStatus Status { get; set; } = SignupStatus.Pending;
    public Guid? TenantId { get; set; }
    public string? IpAddress { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum SupportAccessStatus { Pending, Approved, Rejected, Revoked, Expired }

/// <summary>
/// Time-boxed, tenant-approved support access for a platform operator (there is no impersonation). The operator
/// requests access with a reason; a tenant administrator approves or rejects; access ends at <see cref="ExpiresAt"/>
/// or when revoked. Every step is audited in both the tenant and the platform audit trail.
/// </summary>
public class SupportAccessGrant : AuditedEntity
{
    public Guid OperatorId { get; set; }
    public string OperatorName { get; set; } = "";
    public string Reason { get; set; } = "";
    public int DurationMinutes { get; set; } = 60;
    public SupportAccessStatus Status { get; set; } = SupportAccessStatus.Pending;
    public Guid? DecidedById { get; set; }
    public string? DecidedByName { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

public enum ConfigDraftStatus { Draft, Published, Discarded }

/// <summary>
/// An imported configuration bundle waiting to be reviewed: the diff is recomputed against the live configuration and
/// per-item resolutions (skip / overwrite / rename) are stored until the draft is published (which creates a snapshot).
/// </summary>
public class ConfigurationDraft : AuditedEntity
{
    public string Label { get; set; } = "";
    public string? SourceTenant { get; set; }
    public int FormatVersion { get; set; } = 1;
    public string BundleJson { get; set; } = "{}";
    /// <summary>{"statuses/Open":{"action":"overwrite"},"categories/Billing":{"action":"rename","newName":"Billing (imported)"}}</summary>
    public string ResolutionsJson { get; set; } = "{}";
    public ConfigDraftStatus Status { get; set; } = ConfigDraftStatus.Draft;
    public Guid? PublishedSnapshotId { get; set; }
    public DateTime? PublishedAt { get; set; }
    public string? ResultJson { get; set; }
}
