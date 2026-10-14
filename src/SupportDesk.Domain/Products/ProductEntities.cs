namespace SupportDesk.Domain.Products;

public class ProductFamily : AuditedEntity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
}

public class Product : AuditedEntity
{
    public Guid? FamilyId { get; set; }
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid? DefaultDepartmentId { get; set; }
    public Guid? DefaultTeamId { get; set; }
    public Guid? ErpProductId { get; set; }
}

public class ProductModule : AuditedEntity
{
    public Guid ProductId { get; set; }
    public string Name { get; set; } = "";
    public string? Code { get; set; }
    public bool IsActive { get; set; } = true;
}

public enum VersionStatus { Planned, Released, Supported, EndOfLife }

/// <summary>A version line of a product (e.g. "5.2").</summary>
public class ProductVersion : AuditedEntity
{
    public Guid ProductId { get; set; }
    public string Version { get; set; } = "";
    public DateOnly? ReleaseDate { get; set; }
    public DateOnly? EndOfSupportDate { get; set; }
    public VersionStatus Status { get; set; } = VersionStatus.Released;
    public string? Notes { get; set; }
}

public enum ReleaseType { Major, Minor, Patch, Hotfix }

/// <summary>A concrete build/release of a version (e.g. 5.2.3 build 1187), optionally linked to engineering releases.</summary>
public class ProductRelease : AuditedEntity
{
    public Guid ProductId { get; set; }
    public Guid VersionId { get; set; }
    public string Name { get; set; } = "";
    public string? BuildNumber { get; set; }
    public ReleaseType Type { get; set; }
    public DateOnly? ReleaseDate { get; set; }
    public string? ReleaseNotes { get; set; }
    public string? EngineeringReleaseRef { get; set; }
}

public enum InstallationEnvironment { Production, Staging, Test, Training }
public enum DeploymentMode { OnPremise, Cloud, Hybrid, Hosted }
public enum InstallationStatus { Active, Suspended, Decommissioned }

/// <summary>A product installed at a customer site, with its environment details. Tickets may reference one.</summary>
public class Installation : AuditedEntity, IConcurrencyVersioned
{
    public Guid CustomerId { get; set; }
    public Guid? LocationId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? VersionId { get; set; }
    public Guid? ReleaseId { get; set; }
    public string Name { get; set; } = "";
    public string? InstallationCode { get; set; }
    public InstallationEnvironment Environment { get; set; } = InstallationEnvironment.Production;
    public DeploymentMode DeploymentMode { get; set; } = DeploymentMode.OnPremise;
    public string? OperatingSystem { get; set; }
    public string? DatabaseVersion { get; set; }
    public string? ServerConfiguration { get; set; }
    /// <summary>[{"system":"Tally","direction":"outbound","notes":"…"}]</summary>
    public string IntegrationsJson { get; set; } = "[]";
    /// <summary>Enabled product module ids.</summary>
    public string ModulesJson { get; set; } = "[]";
    public int? UserCount { get; set; }
    public DateOnly? InstalledOn { get; set; }
    public DateOnly? GoLiveOn { get; set; }
    public InstallationStatus Status { get; set; } = InstallationStatus.Active;
    public Guid? ErpInstallationId { get; set; }
    public string? Notes { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public enum LicenceStatus { Active, Expired, Suspended, Revoked }

public class Licence : AuditedEntity
{
    public Guid CustomerId { get; set; }
    public Guid InstallationId { get; set; }
    public Guid ProductId { get; set; }
    /// <summary>Masked licence key (last 4 characters visible). Full keys stay in the licensing system.</summary>
    public string KeyMasked { get; set; } = "";
    public string? Edition { get; set; }
    public int Seats { get; set; }
    public DateOnly IssuedOn { get; set; }
    public DateOnly? ExpiresOn { get; set; }
    public LicenceStatus Status { get; set; } = LicenceStatus.Active;
    public Guid? ErpLicenceId { get; set; }
}

public enum AssetStatus { InService, InRepair, Retired }

public class CustomerAsset : AuditedEntity
{
    public Guid CustomerId { get; set; }
    public Guid? LocationId { get; set; }
    public Guid? ProductId { get; set; }
    public Guid? InstallationId { get; set; }
    public string Name { get; set; } = "";
    public string? SerialNumber { get; set; }
    public string? AssetTag { get; set; }
    public DateOnly? PurchaseDate { get; set; }
    public DateOnly? WarrantyEndsOn { get; set; }
    public AssetStatus Status { get; set; } = AssetStatus.InService;
    public string? Notes { get; set; }
}
