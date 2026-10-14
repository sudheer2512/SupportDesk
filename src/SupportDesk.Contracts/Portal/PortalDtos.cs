namespace SupportDesk.Contracts.Portal;

/// <summary>Public branding/feature configuration of a customer portal (GET /api/v1/portal/{brand}/config).</summary>
public sealed record PortalConfigDto(
    string BrandSlug,
    string Name,
    string? LogoUrl,
    string PrimaryColor,
    string AccentColor,
    string PortalTitle,
    string? PortalWelcome,
    string? FooterHtml,
    string? SupportEmail,
    string? SupportPhone,
    IReadOnlyList<string> Locales,
    PortalFeaturesDto Features,
    string AuthMode);

public sealed record PortalFeaturesDto(bool Kb, bool Community, bool Chat, bool AiAssistant, bool AnonymousTickets, bool SelfRegistration);
