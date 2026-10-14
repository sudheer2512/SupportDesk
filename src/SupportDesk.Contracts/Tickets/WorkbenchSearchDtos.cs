namespace SupportDesk.Contracts.Tickets;

/// <summary>Customer picker row (GET /api/v1/search/customers). Status: Active, Inactive, Prospect, OnHold.</summary>
public sealed record CustomerLookupDto(Guid Id, string Name, string? Code, string Tier, string? Industry, string Status, int OpenTickets);

/// <summary>Contact picker row (GET /api/v1/search/contacts).</summary>
public sealed record ContactLookupDto(Guid Id, string FullName, string? Email, string? Phone, string? JobTitle, bool IsVip, Guid? CustomerId,
    string? CustomerName, Guid? LocationId, string? LocationName);

/// <summary>Installation picker row (GET /api/v1/search/installations). Environment/Status are enum names.</summary>
public sealed record InstallationLookupDto(Guid Id, string Name, Guid ProductId, string ProductName, Guid? VersionId, string? Version,
    string Environment, string Status, Guid? LocationId);

/// <summary>Workbench global search (GET /api/v1/search?q=): tickets, customers and contacts the caller may see.</summary>
public sealed record QuickSearchResultDto(IReadOnlyList<TicketListItemDto> Tickets, IReadOnlyList<CustomerLookupDto> Customers,
    IReadOnlyList<ContactLookupDto> Contacts);
