using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Customers;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Products;
using SupportDesk.Domain;
using SupportDesk.Domain.Products;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Products;

/// <summary>
/// Product catalog: families, products (default department/team used to route tickets), modules, versions (status,
/// end of support) and releases (build, notes). Reads need products.view, changes products.manage.
/// </summary>
public sealed class ProductCatalogService(DeskDbContext db, RequestContext ctx)
{
    // ───────────────────────────── Families ─────────────────────────────

    public async Task<List<ProductFamilyDto>> FamiliesAsync(CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.ProductsView, Permissions.TicketsView);
        return await db.ProductFamilies.AsNoTracking().OrderBy(f => f.Name)
            .Select(f => new ProductFamilyDto(f.Id, f.Name, f.Description, db.Products.Count(p => p.FamilyId == f.Id))).ToListAsync(ct);
    }

    public async Task<ProductFamilyDto> SaveFamilyAsync(Guid? id, SaveProductFamilyRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ProductsManage);
        var name = Parse.Clean(r.Name, 200) ?? throw Invalid("name", "A name is required.");
        if (await db.ProductFamilies.AnyAsync(f => f.Name == name && f.Id != id, ct)) throw Invalid("name", "A family with this name already exists.");
        ProductFamily family;
        if (id is { } fid) family = await db.ProductFamilies.FirstOrDefaultAsync(f => f.Id == fid, ct) ?? throw new NotFoundException("Product family", fid);
        else db.ProductFamilies.Add(family = new ProductFamily());
        family.Name = name;
        family.Description = Parse.Clean(r.Description, 2000);
        await db.SaveChangesAsync(ct);
        return new ProductFamilyDto(family.Id, family.Name, family.Description, await db.Products.CountAsync(p => p.FamilyId == family.Id, ct));
    }

    public async Task DeleteFamilyAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ProductsManage);
        var family = await db.ProductFamilies.FirstOrDefaultAsync(f => f.Id == id, ct) ?? throw new NotFoundException("Product family", id);
        if (await db.Products.AnyAsync(p => p.FamilyId == id, ct)) throw new ConflictException("in_use", "Move the family's products to another family first.");
        db.ProductFamilies.Remove(family);
        await db.SaveChangesAsync(ct);
    }

    // ───────────────────────────── Products ─────────────────────────────

    public async Task<List<ProductDto>> ListAsync(bool includeInactive = true, CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.ProductsView, Permissions.TicketsView);
        var products = await db.Products.AsNoTracking().Where(p => includeInactive || p.IsActive).OrderBy(p => p.Name).ToListAsync(ct);
        return await MapAsync(products, ct);
    }

    public async Task<ProductDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.ProductsView, Permissions.TicketsView);
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Product", id);
        return (await MapAsync([product], ct))[0];
    }

    private async Task<List<ProductDto>> MapAsync(IReadOnlyList<Product> products, CancellationToken ct)
    {
        var ids = products.Select(p => p.Id).ToList();
        var today = DateOnly.FromDateTime(db.Clock.UtcNow);
        var families = await db.ProductFamilies.AsNoTracking().ToDictionaryAsync(f => f.Id, f => f.Name, ct);
        var departments = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var teams = await db.Teams.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var modules = (await db.ProductModules.AsNoTracking().Where(m => ids.Contains(m.ProductId)).OrderBy(m => m.Name).ToListAsync(ct)).ToLookup(m => m.ProductId);
        var versions = (await db.ProductVersions.AsNoTracking().Where(v => ids.Contains(v.ProductId)).ToListAsync(ct)).ToLookup(v => v.ProductId);
        var releases = (await db.ProductReleases.AsNoTracking().Where(r => ids.Contains(r.ProductId)).ToListAsync(ct)).ToLookup(r => r.VersionId);
        var installs = await db.Installations.AsNoTracking().Where(i => ids.Contains(i.ProductId) && i.Status != InstallationStatus.Decommissioned)
            .Select(i => new { i.ProductId, i.VersionId, i.ReleaseId, i.CustomerId }).ToListAsync(ct);
        var open = await db.Tickets.AsNoTracking().Where(t => t.ProductId != null && ids.Contains(t.ProductId.Value) && t.StatusCategory < StatusCategory.Resolved)
            .GroupBy(t => t.ProductId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return products.Select(p => new ProductDto(
            p.Id, p.FamilyId, p.FamilyId is { } f ? families.GetValueOrDefault(f) : null, p.Name, p.Code, p.Description, p.IsActive,
            p.DefaultDepartmentId, p.DefaultDepartmentId is { } d ? departments.GetValueOrDefault(d) : null,
            p.DefaultTeamId, p.DefaultTeamId is { } t ? teams.GetValueOrDefault(t) : null, p.ErpProductId,
            modules[p.Id].Select(m => new ProductModuleDto(m.Id, m.ProductId, m.Name, m.Code, m.IsActive)).ToList(),
            versions[p.Id].OrderByDescending(v => v.ReleaseDate ?? DateOnly.MinValue).ThenByDescending(v => v.Version).Select(v => new ProductVersionDto(
                v.Id, v.ProductId, v.Version, v.ReleaseDate, v.EndOfSupportDate, v.Status.ToString(), v.Notes, IsEndOfSupport(v, today),
                installs.Count(i => i.VersionId == v.Id),
                releases[v.Id].OrderByDescending(r => r.ReleaseDate ?? DateOnly.MinValue).ThenByDescending(r => r.Name).Select(r => new ProductReleaseDto(
                    r.Id, r.ProductId, r.VersionId, r.Name, r.BuildNumber, r.Type.ToString(), r.ReleaseDate, r.ReleaseNotes, r.EngineeringReleaseRef,
                    installs.Count(i => i.ReleaseId == r.Id))).ToList())).ToList(),
            installs.Count(i => i.ProductId == p.Id), installs.Where(i => i.ProductId == p.Id).Select(i => i.CustomerId).Distinct().Count(),
            open.GetValueOrDefault(p.Id))).ToList();
    }

    public static bool IsEndOfSupport(ProductVersion v, DateOnly today) =>
        v.Status == VersionStatus.EndOfLife || (v.EndOfSupportDate is { } eos && eos < today);

    public async Task<ProductDto> SaveAsync(Guid? id, SaveProductRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ProductsManage);
        var errors = new Dictionary<string, string>();
        var name = Parse.Clean(r.Name, 200);
        var code = Parse.Clean(r.Code, 40)?.ToUpperInvariant();
        if (name is null) errors["name"] = "A name is required.";
        if (code is null) errors["code"] = "A code is required.";
        else if (await db.Products.AnyAsync(p => p.Code == code && p.Id != id, ct)) errors["code"] = $"Code '{code}' is already used.";
        if (r.FamilyId is { } fid && !await db.ProductFamilies.AnyAsync(f => f.Id == fid, ct)) errors["familyId"] = "Unknown product family.";
        if (r.DefaultDepartmentId is { } did && !await db.Departments.AnyAsync(d => d.Id == did, ct)) errors["defaultDepartmentId"] = "Unknown department.";
        if (r.DefaultTeamId is { } tid && !await db.Teams.AnyAsync(t => t.Id == tid && (r.DefaultDepartmentId == null || t.DepartmentId == r.DefaultDepartmentId), ct))
            errors["defaultTeamId"] = "The team does not belong to the default department.";
        if (errors.Count > 0) throw new ValidationException(errors);
        Product p;
        if (id is { } pid) p = await db.Products.FirstOrDefaultAsync(x => x.Id == pid, ct) ?? throw new NotFoundException("Product", pid);
        else db.Products.Add(p = new Product());
        p.Name = name!;
        p.Code = code!;
        p.FamilyId = r.FamilyId;
        p.Description = Parse.Clean(r.Description, 4000);
        p.IsActive = r.IsActive;
        p.DefaultDepartmentId = r.DefaultDepartmentId;
        p.DefaultTeamId = r.DefaultTeamId;
        p.ErpProductId = r.ErpProductId;
        await db.SaveChangesAsync(ct);
        return await GetAsync(p.Id, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ProductsManage);
        var p = await db.Products.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Product", id);
        var used = await db.Installations.AnyAsync(i => i.ProductId == id, ct) ||
                   await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AnyAsync(t => t.ProductId == id, ct) ||
                   await db.Entitlements.AnyAsync(e => e.ProductId == id, ct);
        if (used)
        {
            p.IsActive = false; // referenced by installations or history: retire instead of deleting
            await db.SaveChangesAsync(ct);
            return;
        }
        db.Products.Remove(p);
        await db.SaveChangesAsync(ct);
    }

    // ───────────────────────────── Modules ─────────────────────────────

    public async Task<ProductModuleDto> SaveModuleAsync(Guid productId, Guid? id, SaveProductModuleRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ProductsManage);
        if (!await db.Products.AnyAsync(p => p.Id == productId, ct)) throw new NotFoundException("Product", productId);
        var name = Parse.Clean(r.Name, 200) ?? throw Invalid("name", "A name is required.");
        if (await db.ProductModules.AnyAsync(m => m.ProductId == productId && m.Name == name && m.Id != id, ct)) throw Invalid("name", "This module already exists.");
        ProductModule m;
        if (id is { } mid) m = await db.ProductModules.FirstOrDefaultAsync(x => x.Id == mid && x.ProductId == productId, ct) ?? throw new NotFoundException("Module", mid);
        else db.ProductModules.Add(m = new ProductModule { ProductId = productId });
        m.Name = name;
        m.Code = Parse.Clean(r.Code, 40);
        m.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return new ProductModuleDto(m.Id, m.ProductId, m.Name, m.Code, m.IsActive);
    }

    public async Task DeleteModuleAsync(Guid productId, Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ProductsManage);
        var m = await db.ProductModules.FirstOrDefaultAsync(x => x.Id == id && x.ProductId == productId, ct) ?? throw new NotFoundException("Module", id);
        if (await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AnyAsync(t => t.ModuleId == id, ct)) m.IsActive = false;
        else db.ProductModules.Remove(m);
        await db.SaveChangesAsync(ct);
    }

    // ───────────────────────────── Versions & releases ─────────────────────────────

    public async Task<ProductVersionDto> SaveVersionAsync(Guid productId, Guid? id, SaveProductVersionRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ProductsManage);
        if (!await db.Products.AnyAsync(p => p.Id == productId, ct)) throw new NotFoundException("Product", productId);
        var label = Parse.Clean(r.Version, 60) ?? throw Invalid("version", "A version is required.");
        if (await db.ProductVersions.AnyAsync(v => v.ProductId == productId && v.Version == label && v.Id != id, ct)) throw Invalid("version", "This version already exists.");
        if (r.ReleaseDate is { } rd && r.EndOfSupportDate is { } eos && eos < rd) throw Invalid("endOfSupportDate", "End of support must be after the release date.");
        ProductVersion v;
        if (id is { } vid) v = await db.ProductVersions.FirstOrDefaultAsync(x => x.Id == vid && x.ProductId == productId, ct) ?? throw new NotFoundException("Version", vid);
        else db.ProductVersions.Add(v = new ProductVersion { ProductId = productId });
        v.Version = label;
        v.ReleaseDate = r.ReleaseDate;
        v.EndOfSupportDate = r.EndOfSupportDate;
        v.Status = Parse.Enum(r.Status, VersionStatus.Released, "status");
        v.Notes = Parse.Clean(r.Notes, 4000);
        await db.SaveChangesAsync(ct);
        return (await GetAsync(productId, ct)).Versions.First(x => x.Id == v.Id);
    }

    public async Task DeleteVersionAsync(Guid productId, Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ProductsManage);
        var v = await db.ProductVersions.FirstOrDefaultAsync(x => x.Id == id && x.ProductId == productId, ct) ?? throw new NotFoundException("Version", id);
        if (await db.Installations.AnyAsync(i => i.VersionId == id, ct) || await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AnyAsync(t => t.VersionId == id, ct))
            throw new ConflictException("in_use", "Installations or tickets reference this version; mark it End of life instead.");
        db.ProductVersions.Remove(v);
        await db.SaveChangesAsync(ct);
    }

    public async Task<ProductReleaseDto> SaveReleaseAsync(Guid productId, Guid versionId, Guid? id, SaveProductReleaseRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ProductsManage);
        if (!await db.ProductVersions.AnyAsync(v => v.Id == versionId && v.ProductId == productId, ct)) throw new NotFoundException("Version", versionId);
        var name = Parse.Clean(r.Name, 100) ?? throw Invalid("name", "A release name is required.");
        if (await db.ProductReleases.AnyAsync(x => x.VersionId == versionId && x.Name == name && x.Id != id, ct)) throw Invalid("name", "This release already exists.");
        ProductRelease rel;
        if (id is { } rid) rel = await db.ProductReleases.FirstOrDefaultAsync(x => x.Id == rid && x.VersionId == versionId, ct) ?? throw new NotFoundException("Release", rid);
        else db.ProductReleases.Add(rel = new ProductRelease { ProductId = productId, VersionId = versionId });
        rel.Name = name;
        rel.BuildNumber = Parse.Clean(r.BuildNumber, 60);
        rel.Type = Parse.Enum(r.Type, ReleaseType.Minor, "type");
        rel.ReleaseDate = r.ReleaseDate;
        rel.ReleaseNotes = Parse.Clean(r.ReleaseNotes, 20000);
        rel.EngineeringReleaseRef = Parse.Clean(r.EngineeringReleaseRef, 200);
        await db.SaveChangesAsync(ct);
        return (await GetAsync(productId, ct)).Versions.SelectMany(v => v.Releases).First(x => x.Id == rel.Id);
    }

    public async Task DeleteReleaseAsync(Guid productId, Guid versionId, Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ProductsManage);
        var rel = await db.ProductReleases.FirstOrDefaultAsync(x => x.Id == id && x.VersionId == versionId && x.ProductId == productId, ct)
                  ?? throw new NotFoundException("Release", id);
        if (await db.Installations.AnyAsync(i => i.ReleaseId == id, ct)) throw new ConflictException("in_use", "Installations run this release.");
        db.ProductReleases.Remove(rel);
        await db.SaveChangesAsync(ct);
    }

    private static ValidationException Invalid(string field, string message) => new(new Dictionary<string, string> { [field] = message });
}
