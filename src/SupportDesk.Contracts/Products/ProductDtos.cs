namespace SupportDesk.Contracts.Products;

// ───────────────────────────── Catalog ─────────────────────────────

public sealed record ProductFamilyDto(Guid Id, string Name, string? Description, int ProductCount);

public sealed record SaveProductFamilyRequest(string Name, string? Description);

public sealed record ProductModuleDto(Guid Id, Guid ProductId, string Name, string? Code, bool IsActive);

public sealed record SaveProductModuleRequest(string Name, string? Code, bool IsActive = true);

public sealed record ProductReleaseDto(Guid Id, Guid ProductId, Guid VersionId, string Name, string? BuildNumber, string Type, DateOnly? ReleaseDate,
    string? ReleaseNotes, string? EngineeringReleaseRef, int InstallationCount);

public sealed record SaveProductReleaseRequest(string Name, string? BuildNumber, string Type, DateOnly? ReleaseDate, string? ReleaseNotes,
    string? EngineeringReleaseRef);

public sealed record ProductVersionDto(Guid Id, Guid ProductId, string Version, DateOnly? ReleaseDate, DateOnly? EndOfSupportDate, string Status,
    string? Notes, bool IsEndOfSupport, int InstallationCount, IReadOnlyList<ProductReleaseDto> Releases);

public sealed record SaveProductVersionRequest(string Version, DateOnly? ReleaseDate, DateOnly? EndOfSupportDate, string Status, string? Notes);

public sealed record ProductDto(
    Guid Id,
    Guid? FamilyId,
    string? FamilyName,
    string Name,
    string Code,
    string? Description,
    bool IsActive,
    Guid? DefaultDepartmentId,
    string? DefaultDepartmentName,
    Guid? DefaultTeamId,
    string? DefaultTeamName,
    Guid? ErpProductId,
    IReadOnlyList<ProductModuleDto> Modules,
    IReadOnlyList<ProductVersionDto> Versions,
    int InstallationCount,
    int CustomerCount,
    int OpenTickets);

public sealed record SaveProductRequest
{
    public Guid? FamilyId { get; init; }
    public string Name { get; init; } = "";
    public string Code { get; init; } = "";
    public string? Description { get; init; }
    public bool IsActive { get; init; } = true;
    public Guid? DefaultDepartmentId { get; init; }
    public Guid? DefaultTeamId { get; init; }
    public Guid? ErpProductId { get; init; }
}

// ───────────────────────────── Installations ─────────────────────────────

public sealed record InstallationQuery
{
    public string? Q { get; init; }
    public Guid? CustomerId { get; init; }
    public Guid? ProductId { get; init; }
    public Guid? VersionId { get; init; }
    public string? Environment { get; init; }
    public string? DeploymentMode { get; init; }
    public string? Status { get; init; }
    /// <summary>Only installations on a version past its end-of-support date.</summary>
    public bool? EndOfSupport { get; init; }
    /// <summary>name (default) | customer | goLive | updated</summary>
    public string? Sort { get; init; }
    public string? Dir { get; init; }
    public string? Cursor { get; init; }
    public int? Limit { get; init; }
    public bool? IncludeTotal { get; init; }
}

public sealed record InstallationListItemDto(
    Guid Id,
    string Name,
    string? InstallationCode,
    Guid CustomerId,
    string CustomerName,
    Guid? LocationId,
    string? LocationName,
    Guid ProductId,
    string ProductName,
    Guid? VersionId,
    string? Version,
    Guid? ReleaseId,
    string? Release,
    string Environment,
    string DeploymentMode,
    string Status,
    int? UserCount,
    DateOnly? GoLiveOn,
    bool VersionEndOfSupport,
    int OpenTickets,
    DateTime UpdatedAt);

public sealed record InstallationIntegrationDto(string System, string? Direction, string? Notes);

public sealed record InstallationDetailDto(
    InstallationListItemDto Summary,
    string? OperatingSystem,
    string? DatabaseVersion,
    string? ServerConfiguration,
    IReadOnlyList<InstallationIntegrationDto> Integrations,
    IReadOnlyList<ProductModuleDto> EnabledModules,
    DateOnly? InstalledOn,
    string? Notes,
    Guid? ErpInstallationId,
    IReadOnlyList<LicenceDto> Licences,
    IReadOnlyList<AssetDto> Assets,
    int TicketsLast90Days,
    string RowVersion);

public sealed record SaveInstallationRequest
{
    public Guid CustomerId { get; init; }
    public Guid? LocationId { get; init; }
    public Guid ProductId { get; init; }
    public Guid? VersionId { get; init; }
    public Guid? ReleaseId { get; init; }
    public string Name { get; init; } = "";
    public string? InstallationCode { get; init; }
    public string Environment { get; init; } = "Production";
    public string DeploymentMode { get; init; } = "OnPremise";
    public string? OperatingSystem { get; init; }
    public string? DatabaseVersion { get; init; }
    public string? ServerConfiguration { get; init; }
    public IReadOnlyList<InstallationIntegrationDto>? Integrations { get; init; }
    public IReadOnlyList<Guid>? EnabledModuleIds { get; init; }
    public int? UserCount { get; init; }
    public DateOnly? InstalledOn { get; init; }
    public DateOnly? GoLiveOn { get; init; }
    public string? Status { get; init; }
    public string? Notes { get; init; }
    public string? RowVersion { get; init; }
}

// ───────────────────────────── Licences & assets ─────────────────────────────

public sealed record LicenceDto(
    Guid Id,
    Guid CustomerId,
    string CustomerName,
    Guid InstallationId,
    string InstallationName,
    Guid ProductId,
    string ProductName,
    string KeyMasked,
    string? Edition,
    int Seats,
    DateOnly IssuedOn,
    DateOnly? ExpiresOn,
    string Status,
    int? DaysToExpiry,
    Guid? ErpLicenceId);

/// <summary>The licence key is masked on save (only the last 4 characters are kept); full keys stay in the licensing system.</summary>
public sealed record SaveLicenceRequest(Guid InstallationId, string LicenceKey, string? Edition, int Seats, DateOnly IssuedOn, DateOnly? ExpiresOn,
    string? Status);

public sealed record LicenceQuery
{
    public Guid? CustomerId { get; init; }
    public Guid? ProductId { get; init; }
    public string? Status { get; init; }
    public int? ExpiringWithinDays { get; init; }
    public int? Limit { get; init; }
}

public sealed record AssetDto(
    Guid Id,
    Guid CustomerId,
    string CustomerName,
    Guid? LocationId,
    string? LocationName,
    Guid? ProductId,
    string? ProductName,
    Guid? InstallationId,
    string? InstallationName,
    string Name,
    string? SerialNumber,
    string? AssetTag,
    DateOnly? PurchaseDate,
    DateOnly? WarrantyEndsOn,
    bool UnderWarranty,
    string Status,
    string? Notes);

public sealed record SaveAssetRequest
{
    public Guid CustomerId { get; init; }
    public Guid? LocationId { get; init; }
    public Guid? ProductId { get; init; }
    public Guid? InstallationId { get; init; }
    public string Name { get; init; } = "";
    public string? SerialNumber { get; init; }
    public string? AssetTag { get; init; }
    public DateOnly? PurchaseDate { get; init; }
    public DateOnly? WarrantyEndsOn { get; init; }
    public string? Status { get; init; }
    public string? Notes { get; init; }
}

public sealed record AssetQuery
{
    public string? Q { get; init; }
    public Guid? CustomerId { get; init; }
    public Guid? ProductId { get; init; }
    public string? Status { get; init; }
    public bool? WarrantyExpired { get; init; }
    public int? Limit { get; init; }
}

// ───────────────────────────── Install base ─────────────────────────────

public sealed record InstallBaseVersionDto(
    Guid? VersionId,
    string Version,
    string Status,
    DateOnly? EndOfSupportDate,
    bool IsEndOfSupport,
    int Installations,
    int Customers,
    int Users,
    int Production,
    int NonProduction,
    int OpenTickets,
    int TicketsLast90Days);

public sealed record InstallBaseProductDto(Guid ProductId, string ProductName, string Code, int Installations, int Customers,
    IReadOnlyList<InstallBaseVersionDto> Versions);

public sealed record InstallBaseDto(IReadOnlyList<InstallBaseProductDto> Products, int TotalInstallations, int OnEndOfSupport);
