namespace SupportDesk.Contracts.Administration;

/// <summary>Tenant default delivery channels for one notification event (used when a user has not set their own preference).</summary>
public sealed record NotificationEventDefaultDto(string EventType, string Label, string Description, bool InApp, bool Email, bool Push, bool Digest);

/// <summary>Tenant notification defaults: event × channel matrix, quiet hours and digest frequency (Hourly or Daily).</summary>
public sealed record NotificationPolicyDto(
    IReadOnlyList<NotificationEventDefaultDto> Events,
    string? QuietHoursStart,
    string? QuietHoursEnd,
    string TimeZone,
    string DigestFrequency,
    string DailyDigestAt,
    int Version);

public sealed record SaveNotificationPolicyRequest(
    IReadOnlyList<NotificationEventDefaultDto> Events,
    string? QuietHoursStart,
    string? QuietHoursEnd,
    string TimeZone,
    string DigestFrequency,
    string DailyDigestAt);

public sealed record TemplatePlaceholderDto(string Name, string Description, string Sample);

/// <summary>
/// An e-mail template (Audience: Customer or Agent). Subject/BodyHtml are the saved override (or the default when not
/// customised); the override is used only while IsActive. Defaults are included for reset/compare.
/// </summary>
public sealed record NotificationTemplateDto(
    string Key,
    string Name,
    string Audience,
    string Description,
    string Subject,
    string BodyHtml,
    bool IsCustomized,
    bool IsActive,
    IReadOnlyList<TemplatePlaceholderDto> Placeholders,
    string DefaultSubject,
    string DefaultBodyHtml,
    DateTime? UpdatedAt,
    string? RowVersion);

public sealed record SaveNotificationTemplateRequest(string Subject, string BodyHtml, bool IsActive, string? RowVersion = null);

/// <summary>Renders a template with sample values. Subject/BodyHtml override the saved template (unsaved edits).</summary>
public sealed record PreviewTemplateRequest(string? Subject = null, string? BodyHtml = null, Guid? BrandId = null);

public sealed record TemplatePreviewDto(string Subject, string BodyHtml, IReadOnlyList<string> UnknownPlaceholders);
