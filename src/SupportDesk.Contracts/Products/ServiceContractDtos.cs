using System.Text.Json;
using SupportDesk.Contracts.Common;

namespace SupportDesk.Contracts.Products;

/// <summary>Pick-lists for the contract form (GET /api/v1/contracts/form-meta).</summary>
public sealed record ContractFormMetaDto(IReadOnlyList<string> Types, IReadOnlyList<string> Statuses, IReadOnlyList<string> EntitlementTypes,
    IReadOnlyList<IdName> SlaPolicies, IReadOnlyList<IdName> Calendars);

// ───────────────────────────── Support contracts & entitlements ─────────────────────────────

public sealed record ContractQuery
{
    public string? Q { get; init; }
    public Guid? CustomerId { get; init; }
    /// <summary>Comma-separated ContractType values (Warranty, Amc, AnnualSupport, PrepaidHours, TimeAndMaterial, Subscription).</summary>
    public string? Type { get; init; }
    /// <summary>Comma-separated ContractStatus values (Draft, Active, Expired, Suspended, Cancelled).</summary>
    public string? Status { get; init; }
    public int? ExpiringWithinDays { get; init; }
    /// <summary>endDate (default) | number | customer | value | updated</summary>
    public string? Sort { get; init; }
    public string? Dir { get; init; }
    public string? Cursor { get; init; }
    public int? Limit { get; init; }
    public bool? IncludeTotal { get; init; }
}

public sealed record ContractListItemDto(
    Guid Id,
    string Number,
    string Name,
    Guid CustomerId,
    string CustomerName,
    string Type,
    string Status,
    DateOnly StartDate,
    DateOnly EndDate,
    int DaysToExpiry,
    bool IsBillable,
    decimal? ContractValue,
    string Currency,
    decimal? PrepaidHours,
    decimal ConsumedHours,
    bool AutoRenew,
    DateOnly? RenewalDate,
    Guid? SlaPolicyId,
    string? SlaPolicyName,
    int EntitlementCount,
    string? ErpReference,
    Guid? AccountManagerUserId,
    string? AccountManagerName);

public sealed record EntitlementDto(
    Guid Id,
    Guid ContractId,
    string? ContractNumber,
    Guid CustomerId,
    string Name,
    string Type,
    Guid? ProductId,
    string? ProductName,
    Guid? InstallationId,
    string? InstallationName,
    Guid? AssetId,
    string? AssetName,
    int? IncidentsAllowed,
    int IncidentsUsed,
    decimal? HoursAllowed,
    decimal HoursUsed,
    DateOnly StartDate,
    DateOnly EndDate,
    bool IsActive,
    double? UsagePercent,
    bool Exhausted);

public sealed record SaveEntitlementRequest
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "Support";
    public Guid? ProductId { get; init; }
    public Guid? InstallationId { get; init; }
    public Guid? AssetId { get; init; }
    public int? IncidentsAllowed { get; init; }
    public decimal? HoursAllowed { get; init; }
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public bool IsActive { get; init; } = true;
}

public sealed record ContractConsumptionSummaryDto(decimal? PrepaidHours, decimal ConsumedHours, decimal? RemainingHours, decimal BillableHours,
    decimal NonBillableHours, decimal BillableAmount, double? PercentUsed, int TicketsCovered);

public sealed record ContractPermissionsDto(bool Edit, bool ViewCosts);

public sealed record ContractDetailDto(
    ContractListItemDto Summary,
    Guid? CalendarId,
    string? CalendarName,
    decimal? HourlyRate,
    JsonElement Coverage,
    string? Notes,
    Guid? ErpContractId,
    IReadOnlyList<EntitlementDto> Entitlements,
    ContractConsumptionSummaryDto Consumption,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string RowVersion,
    ContractPermissionsDto Permissions);

public sealed record SaveContractRequest
{
    public Guid CustomerId { get; init; }
    /// <summary>Optional; generated as {TYPE}-{yyyy}-{seq} when empty.</summary>
    public string? Number { get; init; }
    public string Name { get; init; } = "";
    public string Type { get; init; } = "Amc";
    public string? Status { get; init; }
    public DateOnly StartDate { get; init; }
    public DateOnly EndDate { get; init; }
    public Guid? SlaPolicyId { get; init; }
    public Guid? CalendarId { get; init; }
    public decimal? PrepaidHours { get; init; }
    public bool IsBillable { get; init; }
    public decimal? HourlyRate { get; init; }
    public decimal? ContractValue { get; init; }
    public string? Currency { get; init; }
    public bool AutoRenew { get; init; }
    public DateOnly? RenewalDate { get; init; }
    public JsonElement? Coverage { get; init; }
    public string? ErpReference { get; init; }
    public string? Notes { get; init; }
    /// <summary>Entitlements created with a new contract (ignored on update; use the entitlement endpoints).</summary>
    public IReadOnlyList<SaveEntitlementRequest>? Entitlements { get; init; }
    public string? RowVersion { get; init; }
}

/// <summary>Effort consumption from logged time entries (prepaid hours, billable value).</summary>
public sealed record ConsumptionMonthDto(string Month, decimal Hours, decimal BillableHours, decimal BillableAmount);

public sealed record ConsumptionAgentDto(Guid UserId, string Name, decimal Hours, decimal BillableHours);

public sealed record ConsumptionEntryDto(Guid Id, Guid TicketId, string TicketNumber, string TicketSubject, string UserName, DateTime StartedAt,
    int Minutes, bool IsBillable, string? ActivityType, decimal BillableAmount);

public sealed record ConsumptionReportDto(
    Guid ContractId,
    string ContractNumber,
    ContractConsumptionSummaryDto Summary,
    IReadOnlyList<ConsumptionMonthDto> ByMonth,
    IReadOnlyList<ConsumptionAgentDto> ByAgent,
    IReadOnlyList<ConsumptionEntryDto> Entries);

public sealed record ContractRenewalDto(ContractListItemDto Contract, int DaysToExpiry, string Urgency, int OpenTickets);

/// <summary>Support eligibility that a ticket for this customer/product/installation/asset would get right now.</summary>
public sealed record EligibilityPreviewDto(
    string Status,
    string Note,
    Guid? ContractId,
    string? ContractNumber,
    string? ContractType,
    Guid? EntitlementId,
    string? EntitlementName,
    string? Allowance,
    DateOnly? CoveredUntil,
    Guid? SlaPolicyId,
    string? SlaPolicyName,
    bool IsBillable,
    IReadOnlyList<ContractListItemDto> ActiveContracts);
