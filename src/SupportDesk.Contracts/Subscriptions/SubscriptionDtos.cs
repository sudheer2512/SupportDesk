namespace SupportDesk.Contracts.Subscriptions;

// ───── Plans ─────

/// <summary>A commercial plan. Null limits are unlimited. Prices are per agent per month/year in <see cref="Currency"/>.</summary>
public sealed record PlanDto(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    decimal MonthlyPricePerAgent,
    decimal AnnualPricePerAgent,
    string Currency,
    bool IsPublic,
    int TrialDays,
    int? MaxAgents,
    int? MaxLightAgents,
    int? MaxPortalUsers,
    long? StorageMb,
    int? MaxChannels,
    long? AiTokensMonthly,
    int? MaxAttachmentMb,
    long? ApiCallsMonthly,
    int? MaxBrands,
    IReadOnlyList<string> Features,
    int SortOrder,
    int TenantCount);

/// <summary>Feature flag a plan may include (key + human label).</summary>
public sealed record PlanFeatureDto(string Key, string Label);

// ───── Subscription (tenant administrator) ─────

/// <summary>One usage meter: <c>State</c> is ok | warning (≥ 80 %) | exceeded | unlimited.</summary>
public sealed record UsageMeterDto(string Metric, string Label, long Used, long? Limit, string Unit, double? Percent, string State);

public sealed record BillingContactDto(string? Name, string? Email, string? Address, string? TaxId);

public sealed record UpdateBillingContactRequest(string? Name, string? Email, string? Address, string? TaxId);

public sealed record SubscriptionDto(
    Guid Id,
    Guid TenantId,
    string TenantName,
    string TenantSlug,
    string TenantStatus,
    PlanDto Plan,
    string Status,
    string BillingCycle,
    int Seats,
    DateTime StartedAt,
    DateTime? TrialEndsAt,
    int? TrialDaysLeft,
    DateTime CurrentPeriodStart,
    DateTime CurrentPeriodEnd,
    bool CancelAtPeriodEnd,
    PlanDto? PendingPlan,
    DateTime? GraceEndsAt,
    DateTime? SuspendedAt,
    string? SuspensionReason,
    string PaymentProvider,
    string PaymentInstructions,
    BillingContactDto BillingContact,
    IReadOnlyList<UsageMeterDto> Usage,
    decimal EstimatedNextInvoice,
    string Currency,
    int OpenInvoices,
    decimal AmountDue,
    string RowVersion);

/// <summary>Result of validating a plan change before it is applied.</summary>
public sealed record PlanChangePreviewDto(
    string FromPlan,
    string ToPlan,
    string Direction,
    bool Immediate,
    DateTime EffectiveAt,
    decimal ProratedAmount,
    decimal NewPeriodAmount,
    string Currency,
    int Seats,
    string BillingCycle,
    IReadOnlyList<PlanConflictDto> Conflicts,
    bool Allowed,
    string Message);

public sealed record PlanConflictDto(string Metric, string Label, long Used, long Limit);

/// <summary>Upgrades apply immediately with a pro-rata invoice; downgrades are scheduled for the end of the period.</summary>
public sealed record ChangePlanRequest(string PlanCode, string? BillingCycle = null, int? Seats = null, string? RowVersion = null);

public sealed record ChangePlanResultDto(SubscriptionDto Subscription, InvoiceSummaryDto? ProrationInvoice, string Message);

public sealed record UsageHistoryDto(string Metric, string Label, string Unit, IReadOnlyList<UsagePointDto> Points);

public sealed record UsagePointDto(DateTime Day, long Quantity);

// ───── Invoices ─────

public sealed record InvoiceLineDto(string Description, decimal Quantity, decimal UnitPrice, decimal Amount);

public sealed record InvoiceSummaryDto(
    Guid Id,
    Guid TenantId,
    string TenantName,
    string Number,
    string Kind,
    string Status,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    DateTime IssuedAt,
    DateTime DueAt,
    DateTime? PaidAt,
    decimal Total,
    string Currency,
    bool Overdue);

public sealed record InvoiceDto(
    InvoiceSummaryDto Summary,
    decimal Subtotal,
    decimal Tax,
    IReadOnlyList<InvoiceLineDto> Lines,
    string? PaymentReference,
    string? PaymentProvider,
    string PaymentInstructions,
    string? Notes,
    BillingContactDto BillTo);

// ───── Trial sign-up (public) ─────

/// <summary>
/// Self-service trial sign-up. <c>Website</c> is a honeypot: humans never fill it (the field is hidden), bots do.
/// </summary>
public sealed record SignupRequest(
    string Company,
    string Slug,
    string AdminName,
    string AdminEmail,
    string Password,
    string? PlanCode = null,
    string? TimeZone = null,
    string? Website = null);

/// <summary><c>DemoCode</c> is only returned in development/test environments (like the MFA demo code).</summary>
public sealed record SignupStartedDto(Guid SignupId, string Email, DateTime ExpiresAt, string? DemoCode);

public sealed record SignupVerifyRequest(string Code);

public sealed record SignupCompletedDto(Guid TenantId, string Workspace, string AdminEmail, string PlanName, DateTime? TrialEndsAt);

public sealed record SlugAvailabilityDto(string Slug, bool Available, string? Reason, string? Suggestion);
