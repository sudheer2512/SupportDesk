using System.Text.Json;

namespace SupportDesk.Contracts.Erp;

// ═══════════════════════════════ ERP wire contracts ═══════════════════════════════
// JSON shapes exchanged with Wings ERP / Wings Books over HTTP (camelCase, enums as strings, dates ISO-8601,
// money as decimal numbers in the document currency). Documented in docs/integrations/WINGS-ERP.md.

public static class ErpSystems
{
    public const string WingsErp = "WingsErp";
    public const string WingsBooks = "WingsBooks";
    public static readonly string[] All = [WingsErp, WingsBooks];
}

public static class ErpRecordTypes
{
    public const string SalesInvoice = "SalesInvoice";
    public const string SalesOrder = "SalesOrder";
    public const string Receipt = "Receipt";
    public const string Delivery = "Delivery";
    public const string CreditNote = "CreditNote";
    public const string AmcContract = "AmcContract";
    public const string Customer = "Customer";
    public const string Installation = "Installation";
    public static readonly string[] All = [SalesInvoice, SalesOrder, Receipt, Delivery, CreditNote, AmcContract, Customer, Installation];
}

/// <summary>A page of ERP results; <see cref="NextPage"/> is null on the last page.</summary>
public sealed record ErpPage<T>(IReadOnlyList<T> Items, int? NextPage);

public sealed record ErpBranch(Guid Id, string Code, string Name, string? AddressLine1, string? City, string? State, string? PostalCode,
    string? Phone, bool IsHeadOffice);

public sealed record ErpCustomerMaster(
    Guid Id,
    string Code,
    string Name,
    string? LegalName,
    string? Gstin,
    string? Pan,
    string? Email,
    string? Phone,
    string? Website,
    string? Industry,
    string? Region,
    string? City,
    string? State,
    string? Country,
    bool IsActive,
    bool IsBlocked,
    decimal? CreditLimit,
    string Currency,
    IReadOnlyList<ErpBranch> Branches,
    DateTime ModifiedAt);

public sealed record ErpInstallationRecord(
    Guid Id,
    Guid CustomerId,
    Guid? BranchId,
    string ProductCode,
    string? Version,
    string? Build,
    string Name,
    string? InstallationCode,
    string Environment,
    string DeploymentMode,
    string? OperatingSystem,
    string? DatabaseVersion,
    int? UserCount,
    DateOnly? InstalledOn,
    DateOnly? GoLiveOn,
    string Status,
    IReadOnlyList<string> Modules,
    DateTime ModifiedAt);

public sealed record ErpLicenceRecord(
    Guid Id,
    Guid CustomerId,
    Guid InstallationId,
    string ProductCode,
    string KeyMasked,
    string? Edition,
    int Seats,
    DateOnly IssuedOn,
    DateOnly? ExpiresOn,
    string Status,
    DateTime ModifiedAt);

public sealed record ErpAmcContractRecord(
    Guid Id,
    Guid CustomerId,
    string Number,
    string Name,
    string ContractType, // Amc | AnnualSupport | Warranty | Subscription
    DateOnly StartDate,
    DateOnly EndDate,
    decimal Value,
    string Currency,
    bool AutoRenew,
    string Status, // Active | Expired | Cancelled | Draft
    string? SalesOrderNumber,
    IReadOnlyList<string> ProductCodes,
    bool Onsite,
    DateTime ModifiedAt);

public sealed record ErpTaxBreakup(decimal Cgst, decimal Sgst, decimal Igst, decimal Cess);

public sealed record ErpTransactionLine(
    int LineNo,
    string ItemCode,
    string Description,
    string? HsnSac,
    decimal Quantity,
    string Unit,
    decimal Rate,
    decimal Discount,
    decimal TaxableValue,
    decimal TaxRate,
    decimal TaxAmount,
    decimal Amount);

/// <summary>A financial/commercial document (sales invoice, order, receipt, delivery, credit note, AMC contract).</summary>
public sealed record ErpTransaction(
    string System,
    string RecordType,
    Guid Id,
    string Number,
    Guid CustomerId,
    string? CustomerCode,
    string? CustomerName,
    DateOnly Date,
    DateOnly? DueDate,
    string Status,
    decimal TaxableAmount,
    decimal TaxAmount,
    decimal TotalAmount,
    decimal? BalanceAmount,
    string Currency,
    string? PlaceOfSupply,
    ErpTaxBreakup? Tax,
    IReadOnlyList<ErpTransactionLine> Lines,
    string? Reference,
    string? Url);

/// <summary>Body of PATCH /records/{type}/{id}: field changes requested by SupportDesk (e.g. from an automation rule).</summary>
public sealed record ErpRecordUpdate(JsonElement Changes, string Source, string? Reference);

public sealed record ErpUpdateResult(bool Success, string RecordType, Guid? RecordId, string? RecordNumber, string Message,
    IReadOnlyList<string> AppliedFields);

public sealed record ErpPingResult(bool Ok, string System, string? Version, DateTime ServerTime);

// ═══════════════════════════════ SupportDesk API DTOs ═══════════════════════════════

/// <summary>
/// POST /api/v1/integrations/erp/tickets — raises a ticket from an ERP screen. The originating record is kept as the
/// ticket's origin reference (record GUID, number and the screen context).
/// </summary>
public sealed record CreateErpTicketRequest
{
    public string System { get; init; } = ErpSystems.WingsErp;
    /// <summary>SalesInvoice, SalesOrder, Receipt, Delivery, AmcContract, CreditNote…</summary>
    public string RecordType { get; init; } = "";
    public Guid? RecordId { get; init; }
    public string? RecordNumber { get; init; }
    public string? RecordUrl { get; init; }
    /// <summary>Free-form screen context (company, branch, amounts, module, error text…), stored with the reference.</summary>
    public JsonElement? Context { get; init; }
    /// <summary>The customer's id in the ERP customer master (preferred) …</summary>
    public Guid? CustomerErpId { get; init; }
    /// <summary>… or its ERP customer code.</summary>
    public string? CustomerCode { get; init; }
    public string? ContactEmail { get; init; }
    public string? ContactName { get; init; }
    public string? ContactPhone { get; init; }
    /// <summary>ERP user who raised the ticket (shown as the author when no contact matches).</summary>
    public string? ErpUser { get; init; }
    public string Subject { get; init; } = "";
    /// <summary>Plain-text description (converted to HTML) …</summary>
    public string? Description { get; init; }
    /// <summary>… or sanitised HTML.</summary>
    public string? DescriptionHtml { get; init; }
    public string? Priority { get; init; }
    /// <summary>Product code (e.g. WERP) to classify the ticket; the customer's installation is matched automatically.</summary>
    public string? ProductCode { get; init; }
    /// <summary>Idempotency key: retries with the same key return the ticket created first.</summary>
    public string? ClientRequestId { get; init; }
}

public sealed record ErpTicketCreatedDto(
    Guid TicketId,
    long Number,
    string DisplayNumber,
    string Status,
    string EntitlementStatus,
    Guid? CustomerId,
    string? CustomerName,
    Guid? ContactId,
    string PortalUrl,
    string DeskUrl);

public sealed record ErpLinkedTicketDto(Guid TicketId, string DisplayNumber, string Subject, string Status, bool IsOrigin, DateTime CreatedAt);

/// <summary>GET /api/v1/integrations/erp/records/{type}/{id} — an ERP record with its SupportDesk links.</summary>
public sealed record ErpRecordViewDto(ErpTransaction Record, Guid? CustomerId, string? CustomerName, IReadOnlyList<ErpLinkedTicketDto> LinkedTickets);

/// <summary>ERP view of one customer for Customer 360 (transactions, AMC contracts, installations, outstanding balance).</summary>
public sealed record ErpCustomerRecordsDto(
    string System,
    bool Configured,
    string? Mode,
    string? Error,
    Guid? ErpCustomerId,
    string? ErpCode,
    ErpCustomerMaster? Master,
    decimal OutstandingAmount,
    string Currency,
    IReadOnlyList<ErpTransaction> Invoices,
    IReadOnlyList<ErpTransaction> Orders,
    IReadOnlyList<ErpTransaction> Receipts,
    IReadOnlyList<ErpAmcContractRecord> AmcContracts,
    IReadOnlyList<ErpInstallationRecord> Installations);

public sealed record ErpIntegrationDto(
    Guid? Id,
    string System,
    string Name,
    bool Configured,
    bool IsActive,
    string Mode, // simulator | http
    string? BaseUrl,
    string AuthType, // OAuth2 | ApiKey | None
    string? TokenUrl,
    string? ClientId,
    string? Scope,
    bool HasClientSecret,
    bool HasApiKey,
    int TimeoutSeconds,
    int SyncIntervalMinutes,
    bool SyncCustomers,
    bool SyncInstallations,
    bool SyncLicences,
    bool SyncContracts,
    bool CreateMissingCustomers,
    string Status,
    DateTime? LastSyncAt,
    string? LastError,
    string? RowVersion);

/// <summary>Secrets are write-only: send a value to replace it, null to keep it, "" to clear it.</summary>
public sealed record SaveErpIntegrationRequest
{
    public string? Name { get; init; }
    public bool IsActive { get; init; } = true;
    public string Mode { get; init; } = "simulator";
    public string? BaseUrl { get; init; }
    public string AuthType { get; init; } = "OAuth2";
    public string? TokenUrl { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? ApiKey { get; init; }
    public string? Scope { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
    public int SyncIntervalMinutes { get; init; } = 60;
    public bool SyncCustomers { get; init; } = true;
    public bool SyncInstallations { get; init; } = true;
    public bool SyncLicences { get; init; } = true;
    public bool SyncContracts { get; init; } = true;
    public bool CreateMissingCustomers { get; init; } = true;
}

public sealed record ErpConnectionTestDto(bool Success, string Message, int DurationMs, string? ServerVersion);

public sealed record ErpSyncEntityResultDto(string Entity, int Received, int Created, int Updated, int Unchanged, int Skipped,
    IReadOnlyList<string> Warnings);

public sealed record ErpSyncResultDto(
    string System,
    string Trigger,
    DateTime StartedAt,
    DateTime FinishedAt,
    bool Success,
    string? Error,
    IReadOnlyList<ErpSyncEntityResultDto> Entities);

public sealed record IntegrationLogDto(
    Guid Id,
    string System,
    string Direction,
    string Operation,
    string? Url,
    int? StatusCode,
    bool Success,
    int DurationMs,
    string? RequestExcerpt,
    string? ResponseExcerpt,
    string? CorrelationId,
    DateTime CreatedAt);
