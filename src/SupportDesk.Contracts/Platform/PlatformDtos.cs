using SupportDesk.Contracts.Subscriptions;

namespace SupportDesk.Contracts.Platform;

/// <summary>Platform console overview: tenant counts by status and the open receivables.</summary>
public sealed record PlatformOverviewDto(
    int Tenants,
    int Trials,
    int Active,
    int Suspended,
    int Cancelled,
    int TrialsEndingSoon,
    decimal MonthlyRecurringRevenue,
    decimal OpenReceivables,
    int OverdueInvoices,
    int PendingSupportRequests,
    string Currency);

public sealed record PlatformTenantDto(
    Guid Id,
    string Slug,
    string Name,
    string Status,
    string PlanCode,
    string PlanName,
    string SubscriptionStatus,
    DateTime CreatedAt,
    DateTime? LastActivityAt,
    DateTime? TrialEndsAt,
    long Agents,
    long StorageBytes,
    string DatabaseMode,
    string? DedicatedConnectionName,
    DateTime? DeletionScheduledAt);

public sealed record PlatformTenantDetailDto(
    PlatformTenantDto Tenant,
    SubscriptionDto? Subscription,
    IReadOnlyList<InvoiceSummaryDto> RecentInvoices,
    IReadOnlyList<SupportAccessDto> SupportAccess,
    IReadOnlyList<PlatformAuditEntryDto> RecentActivity,
    string? SuspensionReason,
    DateTime? SuspendedAt,
    string? Country,
    string DefaultTimeZone);

public sealed record TenantActionRequest(string? Reason);

/// <summary>Cancel a tenant: access stops immediately and data is scheduled for deletion after <c>RetentionDays</c>.</summary>
public sealed record CancelTenantRequest(string? Reason, int RetentionDays = 30);

public sealed record ExtendTrialRequest(int Days);

public sealed record OperatorChangePlanRequest(string PlanCode, int? Seats = null, string? BillingCycle = null);

/// <summary>Move a tenant to a dedicated database. The connection name must be configured (or resolvable from the template).</summary>
public sealed record ProvisionDedicatedRequest(string ConnectionName);

public sealed record DedicatedDatabaseResultDto(string ConnectionName, string Database, int TablesCopied, long RowsCopied, IReadOnlyList<string> Steps);

public sealed record SavePlanRequest(
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
    int SortOrder);

public sealed record MarkInvoicePaidRequest(string? Reference, DateTime? PaidAt = null);

public sealed record VoidInvoiceRequest(string? Reason);

public sealed record PlatformAuditEntryDto(
    Guid Id,
    Guid? TenantId,
    string? TenantName,
    long Sequence,
    DateTime OccurredAt,
    string ActorType,
    string? ActorName,
    string Action,
    string? EntityType,
    Guid? EntityId,
    string? Summary,
    string? IpAddress);

// ───── Support access (no impersonation: tenant-approved, time-boxed, audited) ─────

public sealed record SupportAccessDto(
    Guid Id,
    Guid TenantId,
    string TenantName,
    string OperatorName,
    string Reason,
    int DurationMinutes,
    string Status,
    DateTime RequestedAt,
    string? DecidedByName,
    DateTime? DecidedAt,
    string? DecisionNote,
    DateTime? ExpiresAt,
    bool Active);

public sealed record RequestSupportAccessRequest(string Reason, int DurationMinutes = 60);

public sealed record DecideSupportAccessRequest(bool Approve, string? Note);

/// <summary>Read-only diagnostics an operator may see while an approved support-access grant is active.</summary>
public sealed record SupportDiagnosticsDto(
    Guid TenantId,
    string TenantName,
    DateTime AccessExpiresAt,
    IReadOnlyList<UsageMeterDto> Usage,
    IReadOnlyDictionary<string, long> ConfigurationCounts,
    IReadOnlyList<PlatformAuditEntryDto> RecentAudit,
    int FailedJobs,
    int DeadLetterEvents);
