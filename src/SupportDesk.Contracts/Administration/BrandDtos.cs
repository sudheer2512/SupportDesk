namespace SupportDesk.Contracts.Administration;

public sealed record AdminBrandListItemDto(
    Guid Id,
    string Name,
    string Slug,
    string? CustomDomain,
    string? LogoUrl,
    string PrimaryColor,
    string AccentColor,
    bool IsDefault,
    bool PortalEnabled,
    string PortalUrl);

/// <summary>DNS record the customer creates so their custom domain serves the brand's portal.</summary>
public sealed record BrandDnsDto(string RecordType, string Host, string Target, string Instructions);

/// <summary>A brand and its portal options. Slug and custom domain are globally unique.</summary>
public sealed record AdminBrandDto(
    Guid Id,
    string Name,
    string Slug,
    string? CustomDomain,
    string? LogoUrl,
    string? FaviconUrl,
    string PrimaryColor,
    string AccentColor,
    string? SupportEmail,
    string? SupportPhone,
    string PortalTitle,
    string? PortalWelcome,
    string? FooterHtml,
    string? EmailFooterHtml,
    Guid? DefaultDepartmentId,
    string DefaultLocale,
    IReadOnlyList<string> Locales,
    bool IsDefault,
    bool PortalEnabled,
    bool KnowledgeBaseEnabled,
    bool CommunityEnabled,
    bool ChatEnabled,
    bool AiAssistantEnabled,
    bool AllowAnonymousTickets,
    bool AllowSelfRegistration,
    string PortalAuthMode,
    string PortalUrl,
    BrandDnsDto? Dns,
    DateTime UpdatedAt,
    string RowVersion);

public sealed record SaveBrandRequest(
    string Name,
    string Slug,
    string? CustomDomain,
    string PrimaryColor,
    string AccentColor,
    string? SupportEmail,
    string? SupportPhone,
    string PortalTitle,
    string? PortalWelcome,
    string? FooterHtml,
    string? EmailFooterHtml,
    Guid? DefaultDepartmentId,
    string DefaultLocale,
    IReadOnlyList<string>? Locales,
    bool PortalEnabled,
    bool KnowledgeBaseEnabled,
    bool CommunityEnabled,
    bool ChatEnabled,
    bool AiAssistantEnabled,
    bool AllowAnonymousTickets,
    bool AllowSelfRegistration,
    string PortalAuthMode,
    string? RowVersion = null);

/// <summary>Result of resolving a brand's custom domain and comparing it with the portal host.</summary>
public sealed record DomainCheckResultDto(string Domain, bool Resolves, bool PointsToPortal, IReadOnlyList<string> Addresses, IReadOnlyList<string> PortalAddresses, string Message);
