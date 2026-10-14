namespace SupportDesk.Domain.Integration;

public static class IntegrationTypes
{
    public const string WingsErp = "WingsErp";
    public const string WingsBooks = "WingsBooks";
    public const string AzureDevOps = "AzureDevOps";
    public const string GitHub = "GitHub";
    public const string MicrosoftTeams = "MicrosoftTeams";
    public const string Crm = "Crm";
    public const string Telephony = "Telephony";
    public const string Sms = "Sms";
    public const string LocalEngineering = "LocalEngineering";
}

public enum IntegrationStatus { Connected, Error, Disabled }

/// <summary>A configured external system connection. Secrets are encrypted; SettingsJson holds non-secret options.</summary>
public class Integration : AuditedEntity, IConcurrencyVersioned
{
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string? BaseUrl { get; set; }
    public string SettingsJson { get; set; } = "{}";
    public string? SecretsProtected { get; set; }
    public bool IsActive { get; set; } = true;
    public IntegrationStatus Status { get; set; } = IntegrationStatus.Connected;
    public DateTime? LastSyncAt { get; set; }
    public string? LastError { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Request/response log for integration calls (troubleshooting, SLA evidence). Excerpts are truncated and redacted.</summary>
public class IntegrationLog : TenantEntity
{
    public Guid? IntegrationId { get; set; }
    public string System { get; set; } = "";
    public MessageDirection Direction { get; set; }
    public string Operation { get; set; } = "";
    public string? Url { get; set; }
    public int? StatusCode { get; set; }
    public bool Success { get; set; }
    public int DurationMs { get; set; }
    public string? RequestExcerpt { get; set; }
    public string? ResponseExcerpt { get; set; }
    public string? CorrelationId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>OAuth 2.0 client (client-credentials grant) for system-to-system API access.</summary>
public class ApiClient : AuditedEntity
{
    public string Name { get; set; } = "";
    /// <summary>Globally unique public identifier.</summary>
    public string ClientId { get; set; } = "";
    public string SecretHash { get; set; } = "";
    public string ScopesJson { get; set; } = "[]";
    public bool IsActive { get; set; } = true;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public int RateLimitPerMinute { get; set; } = 600;
}

/// <summary>Static API key (header X-Api-Key). Only the hash is stored; Prefix identifies the key in listings.</summary>
public class ApiKey : AuditedEntity
{
    public string Name { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string KeyHash { get; set; } = "";
    public string ScopesJson { get; set; } = "[]";
    /// <summary>User the key acts as (permissions are the intersection of scopes and that user's permissions).</summary>
    public Guid? ActAsUserId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public int RateLimitPerMinute { get; set; } = 300;
}
