using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Customers;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Products;
using SupportDesk.Domain;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Products;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Products;

/// <summary>
/// Installed base: installations (customer site, product/version/release, environment, deployment, OS, database, server,
/// integrations, enabled modules, users, go-live), licences (masked keys, seats, expiry), customer assets (serials,
/// warranty) and install-base statistics per version (used for product-quality analytics). Scoped by customer scope.
/// </summary>
public sealed class InstallationService(DeskDbContext db, RequestContext ctx, IEventPublisher events)
{
    public const int MaxLimit = 200;

    private IQueryable<Installation> Visible()
    {
        var q = db.Installations.AsNoTracking();
        if (!CustomerAccess.IsRestricted(ctx)) return q;
        var ids = CustomerAccess.VisibleCustomerIds(db, ctx);
        return q.Where(i => ids.Contains(i.CustomerId));
    }

    // ───────────────────────────── Installations ─────────────────────────────

    public async Task<PagedResult<InstallationListItemDto>> ListAsync(InstallationQuery q, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ProductsView);
        var query = Visible();
        var today = DateOnly.FromDateTime(db.Clock.UtcNow);
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = q.Q.Trim();
            var customerIds = db.Customers.Where(c => c.Name.Contains(term) || (c.Code != null && c.Code.Contains(term))).Select(c => c.Id);
            query = query.Where(i => i.Name.Contains(term) || (i.InstallationCode != null && i.InstallationCode.Contains(term)) || customerIds.Contains(i.CustomerId) ||
                                     (i.OperatingSystem != null && i.OperatingSystem.Contains(term)) || (i.DatabaseVersion != null && i.DatabaseVersion.Contains(term)));
        }
        if (q.CustomerId is { } cid) query = query.Where(i => i.CustomerId == cid);
        if (q.ProductId is { } pid) query = query.Where(i => i.ProductId == pid);
        if (q.VersionId is { } vid) query = query.Where(i => i.VersionId == vid);
        var envs = Parse.Enums<InstallationEnvironment>(q.Environment);
        if (envs.Count > 0) query = query.Where(i => envs.Contains(i.Environment));
        var modes = Parse.Enums<DeploymentMode>(q.DeploymentMode);
        if (modes.Count > 0) query = query.Where(i => modes.Contains(i.DeploymentMode));
        var statuses = Parse.Enums<InstallationStatus>(q.Status);
        if (statuses.Count > 0) query = query.Where(i => statuses.Contains(i.Status));
        if (q.EndOfSupport == true)
        {
            var eos = db.ProductVersions.Where(v => v.Status == VersionStatus.EndOfLife || (v.EndOfSupportDate != null && v.EndOfSupportDate < today)).Select(v => v.Id);
            query = query.Where(i => i.VersionId != null && eos.Contains(i.VersionId.Value));
        }

        var limit = Math.Clamp(q.Limit ?? 50, 1, MaxLimit);
        int? total = q.IncludeTotal == true ? await query.CountAsync(ct) : null;
        var cursor = DbExtensions.DecodeCursor(q.Cursor);
        IQueryable<Installation> ordered;
        Func<Installation, string> keyOf;
        if ((q.Sort ?? "name").Equals("updated", StringComparison.OrdinalIgnoreCase))
        {
            if (cursor is not null)
            {
                var v = Parse.Iso(cursor.V);
                query = query.Where(i => i.UpdatedAt < v || (i.UpdatedAt == v && i.Id.CompareTo(cursor.Id) < 0));
            }
            ordered = query.OrderByDescending(i => i.UpdatedAt).ThenByDescending(i => i.Id);
            keyOf = i => Parse.Iso(i.UpdatedAt);
        }
        else
        {
            var asc = Parse.Asc(q.Dir, true);
            if (cursor is not null)
            {
                var v = cursor.V;
                query = asc ? query.Where(i => string.Compare(i.Name, v) > 0 || (i.Name == v && i.Id.CompareTo(cursor.Id) > 0))
                            : query.Where(i => string.Compare(i.Name, v) < 0 || (i.Name == v && i.Id.CompareTo(cursor.Id) < 0));
            }
            ordered = asc ? query.OrderBy(i => i.Name).ThenBy(i => i.Id) : query.OrderByDescending(i => i.Name).ThenByDescending(i => i.Id);
            keyOf = i => i.Name;
        }
        var page = await ordered.Take(limit + 1).ToListAsync(ct);
        var hasMore = page.Count > limit;
        if (hasMore) page.RemoveAt(page.Count - 1);
        return new PagedResult<InstallationListItemDto>(await MapListAsync(page, ct), hasMore ? DbExtensions.EncodeCursor(keyOf(page[^1]), page[^1].Id) : null, total);
    }

    public async Task<List<InstallationListItemDto>> ForCustomerAsync(Guid customerId, CancellationToken ct = default)
    {
        var rows = await Visible().Where(i => i.CustomerId == customerId).OrderBy(i => i.Status).ThenBy(i => i.Name).Take(200).ToListAsync(ct);
        return await MapListAsync(rows, ct);
    }

    public async Task<List<InstallationListItemDto>> MapListAsync(IReadOnlyList<Installation> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var today = DateOnly.FromDateTime(db.Clock.UtcNow);
        var ids = rows.Select(i => i.Id).ToList();
        var customerIds = rows.Select(i => i.CustomerId).Distinct().ToList();
        var customers = await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var locationIds = rows.Where(i => i.LocationId != null).Select(i => i.LocationId!.Value).Distinct().ToList();
        var locations = await db.CustomerLocations.AsNoTracking().Where(l => locationIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var productIds = rows.Select(i => i.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var versions = await db.ProductVersions.AsNoTracking().Where(v => productIds.Contains(v.ProductId)).ToDictionaryAsync(v => v.Id, ct);
        var releaseIds = rows.Where(i => i.ReleaseId != null).Select(i => i.ReleaseId!.Value).Distinct().ToList();
        var releases = await db.ProductReleases.AsNoTracking().Where(r => releaseIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        var open = await db.Tickets.AsNoTracking().Where(t => t.InstallationId != null && ids.Contains(t.InstallationId.Value) && t.StatusCategory < StatusCategory.Resolved)
            .GroupBy(t => t.InstallationId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return rows.Select(i =>
        {
            var v = i.VersionId is { } vid ? versions.GetValueOrDefault(vid) : null;
            return new InstallationListItemDto(i.Id, i.Name, i.InstallationCode, i.CustomerId, customers.GetValueOrDefault(i.CustomerId) ?? "", i.LocationId,
                i.LocationId is { } l ? locations.GetValueOrDefault(l) : null, i.ProductId, products.GetValueOrDefault(i.ProductId) ?? "", i.VersionId, v?.Version,
                i.ReleaseId, i.ReleaseId is { } r ? releases.GetValueOrDefault(r) : null, i.Environment.ToString(), i.DeploymentMode.ToString(), i.Status.ToString(),
                i.UserCount, i.GoLiveOn, v is not null && ProductCatalogService.IsEndOfSupport(v, today), open.GetValueOrDefault(i.Id), i.UpdatedAt);
        }).ToList();
    }

    public async Task<InstallationDetailDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ProductsView);
        var i = await Visible().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Installation", id);
        var summary = (await MapListAsync([i], ct))[0];
        var moduleIds = Json.GuidList(i.ModulesJson);
        var modules = await db.ProductModules.AsNoTracking().Where(m => moduleIds.Contains(m.Id)).OrderBy(m => m.Name)
            .Select(m => new ProductModuleDto(m.Id, m.ProductId, m.Name, m.Code, m.IsActive)).ToListAsync(ct);
        var integrations = Json.DeserializeOr(i.IntegrationsJson, new List<InstallationIntegrationDto>());
        var licences = await LicencesAsync(new LicenceQuery { Limit = 50 }, installationId: id, ct: ct);
        var assets = await AssetsAsync(new AssetQuery { Limit = 50 }, installationId: id, ct: ct);
        var since = db.Clock.UtcNow.AddDays(-90);
        var recent = await db.Tickets.CountAsync(t => t.InstallationId == id && t.CreatedAt >= since, ct);
        return new InstallationDetailDto(summary, i.OperatingSystem, i.DatabaseVersion, i.ServerConfiguration, integrations, modules, i.InstalledOn, i.Notes,
            i.ErpInstallationId, licences, assets, recent, Parse.RowVersion(i.RowVersion));
    }

    public async Task<Installation> SaveAsync(Guid? id, SaveInstallationRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ProductsManage);
        await CustomerAccess.EnsureVisibleAsync(db, ctx, r.CustomerId, ct);
        var errors = new Dictionary<string, string>();
        var name = Parse.Clean(r.Name, 250);
        if (name is null) errors["name"] = "A name is required.";
        if (!await db.Products.AnyAsync(p => p.Id == r.ProductId, ct)) errors["productId"] = "Unknown product.";
        if (r.VersionId is { } vid && !await db.ProductVersions.AnyAsync(v => v.Id == vid && v.ProductId == r.ProductId, ct)) errors["versionId"] = "The version does not belong to the product.";
        if (r.ReleaseId is { } rid && !await db.ProductReleases.AnyAsync(x => x.Id == rid && x.VersionId == r.VersionId, ct)) errors["releaseId"] = "The release does not belong to the version.";
        if (r.LocationId is { } lid && !await db.CustomerLocations.AnyAsync(l => l.Id == lid && l.CustomerId == r.CustomerId, ct)) errors["locationId"] = "The location does not belong to the customer.";
        var moduleIds = (r.EnabledModuleIds ?? []).Distinct().ToList();
        if (moduleIds.Count > 0 && await db.ProductModules.CountAsync(m => moduleIds.Contains(m.Id) && m.ProductId == r.ProductId, ct) != moduleIds.Count)
            errors["enabledModuleIds"] = "Some modules do not belong to the product.";
        var code = Parse.Clean(r.InstallationCode, 60);
        if (code is not null && await db.Installations.AnyAsync(x => x.InstallationCode == code && x.Id != id, ct)) errors["installationCode"] = "This installation code is already used.";
        if (r.UserCount is < 0) errors["userCount"] = "The user count cannot be negative.";
        if (r.InstalledOn is { } io && r.GoLiveOn is { } gl && gl < io) errors["goLiveOn"] = "Go-live cannot be before installation.";
        if (errors.Count > 0) throw new ValidationException(errors);

        Installation i;
        var isNew = id is null;
        if (id is { } iid)
        {
            i = await db.Installations.FirstOrDefaultAsync(x => x.Id == iid, ct) ?? throw new NotFoundException("Installation", iid);
            await CustomerAccess.EnsureVisibleAsync(db, ctx, i.CustomerId, ct);
            db.Entry(i).Property(x => x.RowVersion).OriginalValue = Parse.RowVersion(r.RowVersion, "installation");
        }
        else db.Installations.Add(i = new Installation());
        i.CustomerId = r.CustomerId;
        i.LocationId = r.LocationId;
        i.ProductId = r.ProductId;
        i.VersionId = r.VersionId;
        i.ReleaseId = r.ReleaseId;
        i.Name = name!;
        i.InstallationCode = code;
        i.Environment = Parse.Enum(r.Environment, InstallationEnvironment.Production, "environment");
        i.DeploymentMode = Parse.Enum(r.DeploymentMode, DeploymentMode.OnPremise, "deploymentMode");
        i.OperatingSystem = Parse.Clean(r.OperatingSystem, 200);
        i.DatabaseVersion = Parse.Clean(r.DatabaseVersion, 200);
        i.ServerConfiguration = Parse.Clean(r.ServerConfiguration, 4000);
        i.IntegrationsJson = Json.Serialize((r.Integrations ?? []).Where(x => !string.IsNullOrWhiteSpace(x.System))
            .Select(x => new InstallationIntegrationDto(x.System.Trim(), Parse.Clean(x.Direction, 40), Parse.Clean(x.Notes, 500))).ToList());
        i.ModulesJson = Json.Serialize(moduleIds);
        i.UserCount = r.UserCount;
        i.InstalledOn = r.InstalledOn;
        i.GoLiveOn = r.GoLiveOn;
        i.Status = Parse.Enum(r.Status, isNew ? InstallationStatus.Active : i.Status, "status");
        i.Notes = Parse.Clean(r.Notes, 8000);
        events.Publish(EventTypes.CustomerUpdated, "Customer", i.CustomerId, new EntityEventPayload(i.CustomerId, null,
            [new FieldChange("installation", null, $"{i.Name} ({(isNew ? "added" : "updated")})")]));
        await db.SaveChangesAsync(ct);
        return i;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ProductsManage);
        var i = await db.Installations.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Installation", id);
        await CustomerAccess.EnsureVisibleAsync(db, ctx, i.CustomerId, ct);
        if (await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AnyAsync(t => t.InstallationId == id, ct) || await db.Entitlements.AnyAsync(e => e.InstallationId == id, ct))
        {
            i.Status = InstallationStatus.Decommissioned; // referenced by tickets/entitlements: keep for history
            await db.SaveChangesAsync(ct);
            return;
        }
        await db.CustomerAssets.Where(a => a.InstallationId == id).ExecuteUpdateAsync(u => u.SetProperty(a => a.InstallationId, (Guid?)null), ct);
        db.Installations.Remove(i);
        await db.SaveChangesAsync(ct);
    }

    // ───────────────────────────── Licences ─────────────────────────────

    public async Task<List<LicenceDto>> LicencesAsync(LicenceQuery q, Guid? installationId = null, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ProductsView);
        var today = DateOnly.FromDateTime(db.Clock.UtcNow);
        var query = db.Licences.AsNoTracking();
        if (CustomerAccess.IsRestricted(ctx))
        {
            var ids = CustomerAccess.VisibleCustomerIds(db, ctx);
            query = query.Where(l => ids.Contains(l.CustomerId));
        }
        if (installationId is { } iid) query = query.Where(l => l.InstallationId == iid);
        if (q.CustomerId is { } cid) query = query.Where(l => l.CustomerId == cid);
        if (q.ProductId is { } pid) query = query.Where(l => l.ProductId == pid);
        var statuses = Parse.Enums<LicenceStatus>(q.Status);
        if (statuses.Count > 0) query = query.Where(l => statuses.Contains(l.Status));
        if (q.ExpiringWithinDays is { } days)
        {
            var until = today.AddDays(days);
            query = query.Where(l => l.ExpiresOn != null && l.ExpiresOn <= until && l.Status == LicenceStatus.Active);
        }
        var rows = await query.OrderBy(l => l.ExpiresOn ?? DateOnly.MaxValue).Take(Math.Clamp(q.Limit ?? 200, 1, 500)).ToListAsync(ct);
        var customerIds = rows.Select(l => l.CustomerId).Distinct().ToList();
        var customers = await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var instIds = rows.Select(l => l.InstallationId).Distinct().ToList();
        var installs = await db.Installations.AsNoTracking().Where(i => instIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Name, ct);
        var products = await db.Products.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        return rows.Select(l => new LicenceDto(l.Id, l.CustomerId, customers.GetValueOrDefault(l.CustomerId) ?? "", l.InstallationId, installs.GetValueOrDefault(l.InstallationId) ?? "",
            l.ProductId, products.GetValueOrDefault(l.ProductId) ?? "", l.KeyMasked, l.Edition, l.Seats, l.IssuedOn, l.ExpiresOn,
            (l.Status == LicenceStatus.Active && l.ExpiresOn < today ? LicenceStatus.Expired : l.Status).ToString(),
            l.ExpiresOn is { } e ? e.DayNumber - today.DayNumber : null, l.ErpLicenceId)).ToList();
    }

    public async Task<LicenceDto> SaveLicenceAsync(Guid? id, SaveLicenceRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ProductsManage);
        var installation = await db.Installations.AsNoTracking().FirstOrDefaultAsync(i => i.Id == r.InstallationId, ct)
                           ?? throw new ValidationException(new Dictionary<string, string> { ["installationId"] = "Unknown installation." });
        await CustomerAccess.EnsureVisibleAsync(db, ctx, installation.CustomerId, ct);
        var errors = new Dictionary<string, string>();
        var key = (r.LicenceKey ?? "").Trim();
        if (key.Length < 4) errors["licenceKey"] = "Enter the licence key (at least 4 characters).";
        if (r.Seats < 0) errors["seats"] = "Seats cannot be negative.";
        if (r.ExpiresOn is { } e && e < r.IssuedOn) errors["expiresOn"] = "Expiry cannot be before the issue date.";
        if (errors.Count > 0) throw new ValidationException(errors);
        Licence l;
        if (id is { } lid) l = await db.Licences.FirstOrDefaultAsync(x => x.Id == lid, ct) ?? throw new NotFoundException("Licence", lid);
        else db.Licences.Add(l = new Licence());
        l.CustomerId = installation.CustomerId;
        l.InstallationId = installation.Id;
        l.ProductId = installation.ProductId;
        // Only a masked form is stored; full keys stay in the licensing system.
        l.KeyMasked = key.Contains('*') ? key : MaskKey(key);
        l.Edition = Parse.Clean(r.Edition, 100);
        l.Seats = r.Seats;
        l.IssuedOn = r.IssuedOn;
        l.ExpiresOn = r.ExpiresOn;
        l.Status = Parse.Enum(r.Status, LicenceStatus.Active, "status");
        await db.SaveChangesAsync(ct);
        return (await LicencesAsync(new LicenceQuery(), installationId: installation.Id, ct: ct)).First(x => x.Id == l.Id);
    }

    public static string MaskKey(string key)
    {
        var compact = key.Trim();
        if (compact.Length <= 4) return new string('*', compact.Length);
        var groups = compact.Split('-');
        if (groups.Length > 1)
            return string.Join('-', groups.Select((g, i) => i == 0 && groups.Length > 2 ? g : i == groups.Length - 1 ? g : new string('*', g.Length)));
        return new string('*', compact.Length - 4) + compact[^4..];
    }

    public async Task DeleteLicenceAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ProductsManage);
        var l = await db.Licences.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Licence", id);
        await CustomerAccess.EnsureVisibleAsync(db, ctx, l.CustomerId, ct);
        db.Licences.Remove(l);
        await db.SaveChangesAsync(ct);
    }

    // ───────────────────────────── Assets ─────────────────────────────

    public async Task<List<AssetDto>> AssetsAsync(AssetQuery q, Guid? installationId = null, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ProductsView);
        var today = DateOnly.FromDateTime(db.Clock.UtcNow);
        var query = db.CustomerAssets.AsNoTracking();
        if (CustomerAccess.IsRestricted(ctx))
        {
            var ids = CustomerAccess.VisibleCustomerIds(db, ctx);
            query = query.Where(a => ids.Contains(a.CustomerId));
        }
        if (installationId is { } iid) query = query.Where(a => a.InstallationId == iid);
        if (q.CustomerId is { } cid) query = query.Where(a => a.CustomerId == cid);
        if (q.ProductId is { } pid) query = query.Where(a => a.ProductId == pid);
        var statuses = Parse.Enums<AssetStatus>(q.Status);
        if (statuses.Count > 0) query = query.Where(a => statuses.Contains(a.Status));
        if (q.WarrantyExpired is { } expired)
            query = expired ? query.Where(a => a.WarrantyEndsOn != null && a.WarrantyEndsOn < today) : query.Where(a => a.WarrantyEndsOn == null || a.WarrantyEndsOn >= today);
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = q.Q.Trim();
            query = query.Where(a => a.Name.Contains(term) || (a.SerialNumber != null && a.SerialNumber.Contains(term)) || (a.AssetTag != null && a.AssetTag.Contains(term)));
        }
        var rows = await query.OrderBy(a => a.Name).Take(Math.Clamp(q.Limit ?? 200, 1, 500)).ToListAsync(ct);
        return await MapAssetsAsync(rows, ct);
    }

    private async Task<List<AssetDto>> MapAssetsAsync(List<CustomerAsset> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var today = DateOnly.FromDateTime(db.Clock.UtcNow);
        var customerIds = rows.Select(a => a.CustomerId).Distinct().ToList();
        var customers = await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var locationIds = rows.Where(a => a.LocationId != null).Select(a => a.LocationId!.Value).Distinct().ToList();
        var locations = await db.CustomerLocations.AsNoTracking().Where(l => locationIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var instIds = rows.Where(a => a.InstallationId != null).Select(a => a.InstallationId!.Value).Distinct().ToList();
        var installs = await db.Installations.AsNoTracking().Where(i => instIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Name, ct);
        var products = await db.Products.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        return rows.Select(a => new AssetDto(a.Id, a.CustomerId, customers.GetValueOrDefault(a.CustomerId) ?? "", a.LocationId, a.LocationId is { } l ? locations.GetValueOrDefault(l) : null,
            a.ProductId, a.ProductId is { } p ? products.GetValueOrDefault(p) : null, a.InstallationId, a.InstallationId is { } i ? installs.GetValueOrDefault(i) : null,
            a.Name, a.SerialNumber, a.AssetTag, a.PurchaseDate, a.WarrantyEndsOn, a.WarrantyEndsOn is { } w && w >= today, a.Status.ToString(), a.Notes)).ToList();
    }

    public async Task<AssetDto> SaveAssetAsync(Guid? id, SaveAssetRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ProductsManage);
        await CustomerAccess.EnsureVisibleAsync(db, ctx, r.CustomerId, ct);
        var errors = new Dictionary<string, string>();
        var name = Parse.Clean(r.Name, 250);
        if (name is null) errors["name"] = "A name is required.";
        if (r.ProductId is { } pid && !await db.Products.AnyAsync(p => p.Id == pid, ct)) errors["productId"] = "Unknown product.";
        if (r.InstallationId is { } iid && !await db.Installations.AnyAsync(i => i.Id == iid && i.CustomerId == r.CustomerId, ct)) errors["installationId"] = "The installation does not belong to the customer.";
        if (r.LocationId is { } lid && !await db.CustomerLocations.AnyAsync(l => l.Id == lid && l.CustomerId == r.CustomerId, ct)) errors["locationId"] = "The location does not belong to the customer.";
        var serial = Parse.Clean(r.SerialNumber, 120);
        if (serial is not null && await db.CustomerAssets.AnyAsync(a => a.SerialNumber == serial && a.ProductId == r.ProductId && a.Id != id, ct))
            errors["serialNumber"] = "Another asset of this product has the same serial number.";
        if (r.PurchaseDate is { } pd && r.WarrantyEndsOn is { } we && we < pd) errors["warrantyEndsOn"] = "Warranty cannot end before the purchase date.";
        if (errors.Count > 0) throw new ValidationException(errors);
        CustomerAsset a;
        if (id is { } aid)
        {
            a = await db.CustomerAssets.FirstOrDefaultAsync(x => x.Id == aid, ct) ?? throw new NotFoundException("Asset", aid);
            await CustomerAccess.EnsureVisibleAsync(db, ctx, a.CustomerId, ct);
        }
        else db.CustomerAssets.Add(a = new CustomerAsset());
        a.CustomerId = r.CustomerId;
        a.LocationId = r.LocationId;
        a.ProductId = r.ProductId;
        a.InstallationId = r.InstallationId;
        a.Name = name!;
        a.SerialNumber = serial;
        a.AssetTag = Parse.Clean(r.AssetTag, 120);
        a.PurchaseDate = r.PurchaseDate;
        a.WarrantyEndsOn = r.WarrantyEndsOn;
        a.Status = Parse.Enum(r.Status, AssetStatus.InService, "status");
        a.Notes = Parse.Clean(r.Notes, 4000);
        await db.SaveChangesAsync(ct);
        return (await MapAssetsAsync([a], ct))[0];
    }

    public async Task DeleteAssetAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ProductsManage);
        var a = await db.CustomerAssets.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Asset", id);
        await CustomerAccess.EnsureVisibleAsync(db, ctx, a.CustomerId, ct);
        if (await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AnyAsync(t => t.AssetId == id, ct) || await db.Entitlements.AnyAsync(e => e.AssetId == id, ct))
        {
            a.Status = AssetStatus.Retired;
            await db.SaveChangesAsync(ct);
            return;
        }
        db.CustomerAssets.Remove(a);
        await db.SaveChangesAsync(ct);
    }

    // ───────────────────────────── Install base ─────────────────────────────

    /// <summary>Installations per product and version (customers, users, environments, open tickets and 90-day ticket volume).</summary>
    public async Task<InstallBaseDto> InstallBaseAsync(Guid? productId, CancellationToken ct = default)
    {
        ctx.Require(Permissions.ProductsView);
        var today = DateOnly.FromDateTime(db.Clock.UtcNow);
        var since = db.Clock.UtcNow.AddDays(-90);
        var installs = Visible().Where(i => i.Status != InstallationStatus.Decommissioned);
        if (productId is { } pid) installs = installs.Where(i => i.ProductId == pid);
        var rows = await installs.Select(i => new { i.Id, i.ProductId, i.VersionId, i.CustomerId, i.UserCount, i.Environment }).ToListAsync(ct);
        var productIds = rows.Select(r => r.ProductId).Distinct().ToList();
        if (productId is { } only && !productIds.Contains(only)) productIds.Add(only);
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).OrderBy(p => p.Name).ToListAsync(ct);
        var versions = await db.ProductVersions.AsNoTracking().Where(v => productIds.Contains(v.ProductId)).ToListAsync(ct);
        var ticketStats = await db.Tickets.AsNoTracking().Where(t => t.ProductId != null && productIds.Contains(t.ProductId.Value) && t.InstallationId != null)
            .Where(t => t.StatusCategory < StatusCategory.Resolved || t.CreatedAt >= since)
            .Select(t => new { t.InstallationId, Open = t.StatusCategory < StatusCategory.Resolved, Recent = t.CreatedAt >= since }).ToListAsync(ct);
        var byInstallation = ticketStats.GroupBy(t => t.InstallationId!.Value).ToDictionary(g => g.Key, g => (Open: g.Count(x => x.Open), Recent: g.Count(x => x.Recent)));

        var result = products.Select(p =>
        {
            var mine = rows.Where(r => r.ProductId == p.Id).ToList();
            InstallBaseVersionDto Build(Guid? id, string label, string status, DateOnly? eos, bool isEos, Func<Guid?, bool> match)
            {
                var set = mine.Where(r => match(r.VersionId)).ToList();
                return new InstallBaseVersionDto(id, label, status, eos, isEos, set.Count, set.Select(r => r.CustomerId).Distinct().Count(),
                    set.Sum(r => r.UserCount ?? 0), set.Count(r => r.Environment == InstallationEnvironment.Production),
                    set.Count(r => r.Environment != InstallationEnvironment.Production),
                    set.Sum(r => byInstallation.TryGetValue(r.Id, out var s) ? s.Open : 0), set.Sum(r => byInstallation.TryGetValue(r.Id, out var s) ? s.Recent : 0));
            }
            var versionDtos = versions.Where(v => v.ProductId == p.Id).OrderByDescending(v => v.ReleaseDate ?? DateOnly.MinValue).ThenByDescending(v => v.Version)
                .Select(v => Build(v.Id, v.Version, v.Status.ToString(), v.EndOfSupportDate, ProductCatalogService.IsEndOfSupport(v, today), x => x == v.Id)).ToList();
            if (mine.Any(r => r.VersionId is null)) versionDtos.Add(Build(null, "Unknown", "Unknown", null, false, x => x is null));
            return new InstallBaseProductDto(p.Id, p.Name, p.Code, mine.Count, mine.Select(r => r.CustomerId).Distinct().Count(), versionDtos);
        }).ToList();
        var eosCount = result.SelectMany(p => p.Versions).Where(v => v.IsEndOfSupport).Sum(v => v.Installations);
        return new InstallBaseDto(result, rows.Count, eosCount);
    }
}
