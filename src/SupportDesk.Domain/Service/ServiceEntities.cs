namespace SupportDesk.Domain.Service;

public enum ContractType { Warranty, Amc, AnnualSupport, PrepaidHours, TimeAndMaterial, Subscription }
public enum ContractStatus { Draft, Active, Expired, Suspended, Cancelled }

/// <summary>
/// Support contract (warranty, AMC, annual support renewal, prepaid hours…). Determines support eligibility,
/// the contracted SLA policy and support hours calendar, and billable/non-billable classification.
/// </summary>
public class Contract : AuditedEntity, IConcurrencyVersioned
{
    public Guid CustomerId { get; set; }
    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
    public ContractType Type { get; set; }
    public ContractStatus Status { get; set; } = ContractStatus.Active;
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public Guid? SlaPolicyId { get; set; }
    /// <summary>Support hours (e.g. 9x5 or 24x7 calendar) used for SLA business time.</summary>
    public Guid? CalendarId { get; set; }
    public decimal? PrepaidHours { get; set; }
    public decimal ConsumedHours { get; set; }
    public bool IsBillable { get; set; }
    public decimal? HourlyRate { get; set; }
    public decimal? ContractValue { get; set; }
    public string Currency { get; set; } = "INR";
    public bool AutoRenew { get; set; }
    public DateOnly? RenewalDate { get; set; }
    /// <summary>Channels and severities covered, e.g. {"channels":["Email","Phone"],"maxSeverity":"Critical","onsite":true}.</summary>
    public string CoverageJson { get; set; } = "{}";
    public Guid? ErpContractId { get; set; }
    public string? ErpReference { get; set; }
    public string? Notes { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public enum EntitlementType { Support, Warranty, Hours, Incidents, Onsite }

/// <summary>What a contract covers: a product, an installation, an asset, a number of incidents or hours.</summary>
public class Entitlement : AuditedEntity
{
    public Guid ContractId { get; set; }
    public Guid CustomerId { get; set; }
    public string Name { get; set; } = "";
    public EntitlementType Type { get; set; }
    public Guid? ProductId { get; set; }
    public Guid? InstallationId { get; set; }
    public Guid? AssetId { get; set; }
    public int? IncidentsAllowed { get; set; }
    public int IncidentsUsed { get; set; }
    public decimal? HoursAllowed { get; set; }
    public decimal HoursUsed { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public bool IsActive { get; set; } = true;
}

public enum TimeEntrySource { Manual, Timer, Imported }

/// <summary>Effort logged on a ticket. Billable entries consume prepaid hours on the covering contract.</summary>
public class TimeEntry : AuditedEntity
{
    public Guid TicketId { get; set; }
    public Guid UserId { get; set; }
    public DateTime StartedAt { get; set; }
    public int Minutes { get; set; }
    public bool IsBillable { get; set; }
    public string? ActivityType { get; set; }
    public string? Notes { get; set; }
    public decimal HourlyRate { get; set; }
    public decimal CostAmount { get; set; }
    public decimal BillableAmount { get; set; }
    public Guid? ContractId { get; set; }
    public Guid? EntitlementId { get; set; }
    public TimeEntrySource Source { get; set; } = TimeEntrySource.Manual;
}
