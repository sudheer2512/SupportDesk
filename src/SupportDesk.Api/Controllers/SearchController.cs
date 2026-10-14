using Microsoft.AspNetCore.Mvc;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Tickets;

namespace SupportDesk.Api.Controllers;

/// <summary>Workbench look-ups: global search and the customer / contact / installation pickers of the ticket form.</summary>
[Route("api/v1/search")]
public sealed class SearchController(WorkbenchSearchService search) : DeskControllerBase
{
    /// <summary>Tickets (by number, subject, customer or contact), customers and contacts matching <paramref name="q"/>.</summary>
    [HttpGet]
    public Task<GlobalSearchResultDto> Search([FromQuery] string? q, [FromQuery] int limit = 6, CancellationToken ct = default) =>
        search.SearchAsync(q, limit, ct);

    [HttpGet("customers")]
    public Task<List<CustomerLookupDto>> Customers([FromQuery] string? q, [FromQuery] int limit = 20, CancellationToken ct = default) =>
        search.CustomersAsync(q, limit, ct);

    [HttpGet("contacts")]
    public Task<List<ContactLookupDto>> Contacts([FromQuery] string? q, [FromQuery] Guid? customerId, [FromQuery] int limit = 20, CancellationToken ct = default) =>
        search.ContactsAsync(q, customerId, limit, ct);

    [HttpGet("installations")]
    public Task<List<InstallationLookupDto>> Installations([FromQuery] Guid customerId, [FromQuery] Guid? productId, CancellationToken ct = default) =>
        search.InstallationsAsync(customerId, productId, ct);
}
