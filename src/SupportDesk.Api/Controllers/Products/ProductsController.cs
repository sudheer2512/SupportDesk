using Microsoft.AspNetCore.Mvc;
using SupportDesk.Application.Products;
using SupportDesk.Application.Service;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Products;

namespace SupportDesk.Api.Controllers.Products;

/// <summary>Product catalog: families, products, modules, versions and releases; install-base statistics.</summary>
[Route("api/v1/products")]
public sealed class ProductsController(ProductCatalogService catalog, InstallationService installations) : DeskControllerBase
{
    [HttpGet]
    public Task<List<ProductDto>> List([FromQuery] bool includeInactive = true, CancellationToken ct = default) => catalog.ListAsync(includeInactive, ct);

    [HttpGet("{id:guid}")]
    public Task<ProductDto> Get(Guid id, CancellationToken ct) => catalog.GetAsync(id, ct);

    [HttpPost]
    public Task<ProductDto> Create(SaveProductRequest request, CancellationToken ct) => catalog.SaveAsync(null, request, ct);

    [HttpPut("{id:guid}")]
    public Task<ProductDto> Update(Guid id, SaveProductRequest request, CancellationToken ct) => catalog.SaveAsync(id, request, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await catalog.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpGet("install-base")]
    public Task<InstallBaseDto> InstallBase([FromQuery] Guid? productId, CancellationToken ct) => installations.InstallBaseAsync(productId, ct);

    // ───── Modules ─────

    [HttpPost("{id:guid}/modules")]
    public Task<ProductModuleDto> AddModule(Guid id, SaveProductModuleRequest request, CancellationToken ct) => catalog.SaveModuleAsync(id, null, request, ct);

    [HttpPut("{id:guid}/modules/{moduleId:guid}")]
    public Task<ProductModuleDto> UpdateModule(Guid id, Guid moduleId, SaveProductModuleRequest request, CancellationToken ct) =>
        catalog.SaveModuleAsync(id, moduleId, request, ct);

    [HttpDelete("{id:guid}/modules/{moduleId:guid}")]
    public async Task<IActionResult> DeleteModule(Guid id, Guid moduleId, CancellationToken ct)
    {
        await catalog.DeleteModuleAsync(id, moduleId, ct);
        return NoContent();
    }

    // ───── Versions & releases ─────

    [HttpPost("{id:guid}/versions")]
    public Task<ProductVersionDto> AddVersion(Guid id, SaveProductVersionRequest request, CancellationToken ct) => catalog.SaveVersionAsync(id, null, request, ct);

    [HttpPut("{id:guid}/versions/{versionId:guid}")]
    public Task<ProductVersionDto> UpdateVersion(Guid id, Guid versionId, SaveProductVersionRequest request, CancellationToken ct) =>
        catalog.SaveVersionAsync(id, versionId, request, ct);

    [HttpDelete("{id:guid}/versions/{versionId:guid}")]
    public async Task<IActionResult> DeleteVersion(Guid id, Guid versionId, CancellationToken ct)
    {
        await catalog.DeleteVersionAsync(id, versionId, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/versions/{versionId:guid}/releases")]
    public Task<ProductReleaseDto> AddRelease(Guid id, Guid versionId, SaveProductReleaseRequest request, CancellationToken ct) =>
        catalog.SaveReleaseAsync(id, versionId, null, request, ct);

    [HttpPut("{id:guid}/versions/{versionId:guid}/releases/{releaseId:guid}")]
    public Task<ProductReleaseDto> UpdateRelease(Guid id, Guid versionId, Guid releaseId, SaveProductReleaseRequest request, CancellationToken ct) =>
        catalog.SaveReleaseAsync(id, versionId, releaseId, request, ct);

    [HttpDelete("{id:guid}/versions/{versionId:guid}/releases/{releaseId:guid}")]
    public async Task<IActionResult> DeleteRelease(Guid id, Guid versionId, Guid releaseId, CancellationToken ct)
    {
        await catalog.DeleteReleaseAsync(id, versionId, releaseId, ct);
        return NoContent();
    }
}

[Route("api/v1/product-families")]
public sealed class ProductFamiliesController(ProductCatalogService catalog) : DeskControllerBase
{
    [HttpGet]
    public Task<List<ProductFamilyDto>> List(CancellationToken ct) => catalog.FamiliesAsync(ct);

    [HttpPost]
    public Task<ProductFamilyDto> Create(SaveProductFamilyRequest request, CancellationToken ct) => catalog.SaveFamilyAsync(null, request, ct);

    [HttpPut("{id:guid}")]
    public Task<ProductFamilyDto> Update(Guid id, SaveProductFamilyRequest request, CancellationToken ct) => catalog.SaveFamilyAsync(id, request, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await catalog.DeleteFamilyAsync(id, ct);
        return NoContent();
    }
}

/// <summary>Installations (customer sites running a product), with environment details, licences and assets.</summary>
[Route("api/v1/installations")]
public sealed class InstallationsController(InstallationService installations) : DeskControllerBase
{
    [HttpGet]
    public Task<PagedResult<InstallationListItemDto>> List([FromQuery] InstallationQuery query, CancellationToken ct) => installations.ListAsync(query, ct);

    [HttpGet("{id:guid}")]
    public Task<InstallationDetailDto> Get(Guid id, CancellationToken ct) => installations.GetAsync(id, ct);

    [HttpPost]
    public async Task<ActionResult<InstallationDetailDto>> Create(SaveInstallationRequest request, CancellationToken ct)
    {
        var i = await installations.SaveAsync(null, request, ct);
        return CreatedAtAction(nameof(Get), new { id = i.Id }, await installations.GetAsync(i.Id, ct));
    }

    [HttpPut("{id:guid}")]
    public async Task<InstallationDetailDto> Update(Guid id, SaveInstallationRequest request, CancellationToken ct)
    {
        await installations.SaveAsync(id, request, ct);
        return await installations.GetAsync(id, ct);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await installations.DeleteAsync(id, ct);
        return NoContent();
    }
}

[Route("api/v1/licences")]
public sealed class LicencesController(InstallationService installations) : DeskControllerBase
{
    [HttpGet]
    public Task<List<LicenceDto>> List([FromQuery] LicenceQuery query, CancellationToken ct) => installations.LicencesAsync(query, ct: ct);

    [HttpPost]
    public Task<LicenceDto> Create(SaveLicenceRequest request, CancellationToken ct) => installations.SaveLicenceAsync(null, request, ct);

    [HttpPut("{id:guid}")]
    public Task<LicenceDto> Update(Guid id, SaveLicenceRequest request, CancellationToken ct) => installations.SaveLicenceAsync(id, request, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await installations.DeleteLicenceAsync(id, ct);
        return NoContent();
    }
}

[Route("api/v1/assets")]
public sealed class AssetsController(InstallationService installations) : DeskControllerBase
{
    [HttpGet]
    public Task<List<AssetDto>> List([FromQuery] AssetQuery query, CancellationToken ct) => installations.AssetsAsync(query, ct: ct);

    [HttpPost]
    public Task<AssetDto> Create(SaveAssetRequest request, CancellationToken ct) => installations.SaveAssetAsync(null, request, ct);

    [HttpPut("{id:guid}")]
    public Task<AssetDto> Update(Guid id, SaveAssetRequest request, CancellationToken ct) => installations.SaveAssetAsync(id, request, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await installations.DeleteAssetAsync(id, ct);
        return NoContent();
    }
}

/// <summary>Support contracts, entitlements, renewals, consumption and eligibility preview.</summary>
[Route("api/v1/contracts")]
public sealed class ContractsController(ContractService contracts) : DeskControllerBase
{
    [HttpGet]
    public Task<PagedResult<ContractListItemDto>> List([FromQuery] ContractQuery query, CancellationToken ct) => contracts.ListAsync(query, ct);

    [HttpGet("form-meta")]
    public Task<ContractFormMetaDto> FormMeta(CancellationToken ct) => contracts.FormMetaAsync(ct);

    [HttpGet("renewals")]
    public Task<List<ContractRenewalDto>> Renewals([FromQuery] int days = 90, CancellationToken ct = default) => contracts.RenewalsAsync(days, ct);

    /// <summary>Support eligibility a new ticket would get for this customer/product/installation/asset.</summary>
    [HttpGet("eligibility")]
    public Task<EligibilityPreviewDto> Eligibility([FromQuery] Guid customerId, [FromQuery] Guid? productId, [FromQuery] Guid? installationId,
        [FromQuery] Guid? assetId, CancellationToken ct) => contracts.PreviewEligibilityAsync(customerId, productId, installationId, assetId, ct);

    [HttpGet("{id:guid}")]
    public Task<ContractDetailDto> Get(Guid id, CancellationToken ct) => contracts.GetAsync(id, ct);

    [HttpGet("{id:guid}/consumption")]
    public Task<ConsumptionReportDto> Consumption(Guid id, CancellationToken ct) => contracts.ConsumptionAsync(id, ct);

    [HttpPost]
    public async Task<ActionResult<ContractDetailDto>> Create(SaveContractRequest request, CancellationToken ct)
    {
        var c = await contracts.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = c.Id }, await contracts.GetAsync(c.Id, ct));
    }

    [HttpPut("{id:guid}")]
    public async Task<ContractDetailDto> Update(Guid id, SaveContractRequest request, CancellationToken ct)
    {
        await contracts.UpdateAsync(id, request, ct);
        return await contracts.GetAsync(id, ct);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await contracts.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/entitlements")]
    public Task<EntitlementDto> AddEntitlement(Guid id, SaveEntitlementRequest request, CancellationToken ct) => contracts.SaveEntitlementAsync(id, null, request, ct);

    [HttpPut("{id:guid}/entitlements/{entitlementId:guid}")]
    public Task<EntitlementDto> UpdateEntitlement(Guid id, Guid entitlementId, SaveEntitlementRequest request, CancellationToken ct) =>
        contracts.SaveEntitlementAsync(id, entitlementId, request, ct);

    [HttpDelete("{id:guid}/entitlements/{entitlementId:guid}")]
    public async Task<IActionResult> DeleteEntitlement(Guid id, Guid entitlementId, CancellationToken ct)
    {
        await contracts.DeleteEntitlementAsync(id, entitlementId, ct);
        return NoContent();
    }
}
