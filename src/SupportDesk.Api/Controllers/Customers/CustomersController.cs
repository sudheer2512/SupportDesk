using Microsoft.AspNetCore.Mvc;
using SupportDesk.Application.Customers;
using SupportDesk.Application.Integrations.Erp;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Customers;
using SupportDesk.Contracts.Erp;

namespace SupportDesk.Api.Controllers.Customers;

/// <summary>Customer organisations: search, detail, Customer 360, hierarchy, locations, departments and their ERP records.</summary>
[Route("api/v1/customers")]
public sealed class CustomersController(
    CustomerService customers,
    Customer360Service customer360,
    ContactMergeService merge,
    ErpIntegrationService erp) : DeskControllerBase
{
    [HttpGet]
    public Task<PagedResult<CustomerListItemDto>> List([FromQuery] CustomerQuery query, CancellationToken ct) => customers.ListAsync(query, ct);

    [HttpGet("facets")]
    public Task<CustomerFacetsDto> Facets(CancellationToken ct) => customers.FacetsAsync(ct);

    [HttpGet("{id:guid}")]
    public Task<CustomerDetailDto> Get(Guid id, CancellationToken ct) => customers.GetAsync(id, ct);

    [HttpGet("{id:guid}/360")]
    public Task<Customer360Dto> Get360(Guid id, CancellationToken ct) => customer360.GetAsync(id, ct);

    [HttpGet("{id:guid}/erp")]
    public Task<ErpCustomerRecordsDto> ErpRecords(Guid id, [FromQuery] string? system, CancellationToken ct) => erp.CustomerRecordsAsync(id, system, ct);

    [HttpGet("{id:guid}/contact-duplicates")]
    public Task<List<DuplicatePairDto>> Duplicates(Guid id, [FromQuery] double? minScore, CancellationToken ct) => merge.ScanAsync(id, minScore, 50, ct);

    [HttpPost]
    public async Task<ActionResult<CustomerDetailDto>> Create(SaveCustomerRequest request, CancellationToken ct)
    {
        var c = await customers.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = c.Id }, await customers.GetAsync(c.Id, ct));
    }

    [HttpPut("{id:guid}")]
    public async Task<CustomerDetailDto> Update(Guid id, SaveCustomerRequest request, CancellationToken ct)
    {
        await customers.UpdateAsync(id, request, ct);
        return await customers.GetAsync(id, ct);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await customers.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/restore")]
    public async Task<CustomerDetailDto> Restore(Guid id, CancellationToken ct)
    {
        await customers.RestoreAsync(id, ct);
        return await customers.GetAsync(id, ct);
    }

    // ───── Locations ─────

    [HttpGet("{id:guid}/locations")]
    public Task<List<LocationDto>> Locations(Guid id, CancellationToken ct) => customers.LocationsAsync(id, ct);

    [HttpPost("{id:guid}/locations")]
    public Task<LocationDto> AddLocation(Guid id, SaveLocationRequest request, CancellationToken ct) => customers.SaveLocationAsync(id, null, request, ct);

    [HttpPut("{id:guid}/locations/{locationId:guid}")]
    public Task<LocationDto> UpdateLocation(Guid id, Guid locationId, SaveLocationRequest request, CancellationToken ct) =>
        customers.SaveLocationAsync(id, locationId, request, ct);

    [HttpDelete("{id:guid}/locations/{locationId:guid}")]
    public async Task<IActionResult> DeleteLocation(Guid id, Guid locationId, CancellationToken ct)
    {
        await customers.DeleteLocationAsync(id, locationId, ct);
        return NoContent();
    }

    // ───── Customer departments ─────

    [HttpGet("{id:guid}/departments")]
    public Task<List<CustomerDepartmentDto>> Departments(Guid id, CancellationToken ct) => customers.DepartmentsAsync(id, ct);

    [HttpPost("{id:guid}/departments")]
    public Task<CustomerDepartmentDto> AddDepartment(Guid id, SaveCustomerDepartmentRequest request, CancellationToken ct) =>
        customers.SaveDepartmentAsync(id, null, request, ct);

    [HttpPut("{id:guid}/departments/{departmentId:guid}")]
    public Task<CustomerDepartmentDto> UpdateDepartment(Guid id, Guid departmentId, SaveCustomerDepartmentRequest request, CancellationToken ct) =>
        customers.SaveDepartmentAsync(id, departmentId, request, ct);

    [HttpDelete("{id:guid}/departments/{departmentId:guid}")]
    public async Task<IActionResult> DeleteDepartment(Guid id, Guid departmentId, CancellationToken ct)
    {
        await customers.DeleteDepartmentAsync(id, departmentId, ct);
        return NoContent();
    }
}

/// <summary>Customer groups (segments such as "Key Accounts").</summary>
[Route("api/v1/customer-groups")]
public sealed class CustomerGroupsController(CustomerService customers) : DeskControllerBase
{
    [HttpGet]
    public Task<List<CustomerGroupDto>> List(CancellationToken ct) => customers.GroupsAsync(ct);

    [HttpPost]
    public Task<CustomerGroupDto> Create(SaveCustomerGroupRequest request, CancellationToken ct) => customers.SaveGroupAsync(null, request, ct);

    [HttpPut("{id:guid}")]
    public Task<CustomerGroupDto> Update(Guid id, SaveCustomerGroupRequest request, CancellationToken ct) => customers.SaveGroupAsync(id, request, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await customers.DeleteGroupAsync(id, ct);
        return NoContent();
    }
}

/// <summary>Service visits and scheduled activities (calendar).</summary>
[Route("api/v1/service-activities")]
public sealed class ServiceActivitiesController(ServiceActivityService activities) : DeskControllerBase
{
    [HttpGet]
    public Task<List<ServiceActivityDto>> List([FromQuery] ServiceActivityQuery query, CancellationToken ct) => activities.ListAsync(query, ct);

    [HttpGet("{id:guid}")]
    public Task<ServiceActivityDto> Get(Guid id, CancellationToken ct) => activities.GetAsync(id, ct);

    [HttpPost]
    public Task<ServiceActivityDto> Create(SaveServiceActivityRequest request, CancellationToken ct) => activities.SaveAsync(null, request, ct);

    [HttpPut("{id:guid}")]
    public Task<ServiceActivityDto> Update(Guid id, SaveServiceActivityRequest request, CancellationToken ct) => activities.SaveAsync(id, request, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await activities.DeleteAsync(id, ct);
        return NoContent();
    }
}
