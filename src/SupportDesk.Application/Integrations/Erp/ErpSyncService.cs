using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Customers;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Service;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Erp;
using SupportDesk.Domain;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Integration;
using SupportDesk.Domain.Products;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Service;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Integrations.Erp;

/// <summary>
/// Imports and refreshes customer masters (with branches), installations, licences and AMC contracts from the ERP.
/// Upserts match on ERP ids first and then link existing unlinked records by natural keys (customer code, branch
/// code, installation code, installation+product, contract number), so re-running never creates duplicates.
/// Conflict-safe: ERP-owned fields are refreshed, desk-owned fields (tier, account manager, SLA, coverage, notes…)
/// are never overwritten and contact details are only filled when empty; a database application lock prevents two
/// syncs of the same integration from running at once. Every run is written to the IntegrationLog.
/// </summary>
public sealed class ErpSyncService(
    DeskDbContext db,
    RequestContext ctx,
    ErpConnectorResolver resolver,
    IErpCallLog callLog,
    IEventPublisher events,
    IJobQueue jobs,
    IAuditWriter audit,
    ILogger<ErpSyncService> logger)
{
    public const string ManualTrigger = "manual";
    public const string ScheduleTrigger = "schedule";

    public async Task<ErpSyncResultDto> SyncAsync(string? system, string trigger, CancellationToken ct = default)
    {
        if (!ctx.IsSystem) ctx.Require(Permissions.AdminIntegrations);
        var sys = ErpIntegrationService.NormalizeSystem(system);
        var integration = await resolver.IntegrationAsync(sys, ct) ?? throw new DomainException("integration_unavailable", $"{sys} is not configured.");
        if (!integration.IsActive) throw new DomainException("integration_disabled", $"{integration.Name} is disabled.");
        var settings = Json.DeserializeOr(integration.SettingsJson, new ErpSettings());
        var connector = resolver.Create(integration);
        var started = db.Clock.UtcNow;
        var sw = Stopwatch.StartNew();
        var results = new List<ErpSyncEntityResultDto>();
        string? error = null;

        await db.Database.OpenConnectionAsync(ct);
        try
        {
            if (!await TryLockAsync(integration.Id, ct))
                throw new ConflictException("sync_running", $"A {integration.Name} sync is already running. Try again in a moment.");
            try
            {
                var since = trigger == ScheduleTrigger ? integration.LastSyncAt?.AddMinutes(-15) : null;
                if (settings.SyncCustomers) results.AddRange(await SyncCustomersAsync(connector, since, settings, ct));
                if (sys == ErpSystems.WingsErp && settings.SyncInstallations) results.Add(await SyncInstallationsAsync(connector, since, ct));
                if (sys == ErpSystems.WingsErp && settings.SyncLicences) results.Add(await SyncLicencesAsync(connector, since, ct));
                if (settings.SyncContracts) results.Add(await SyncContractsAsync(connector, since, ct));
            }
            catch (Exception ex) when (ErpIntegrationService.IsConnectivity(ex) || ex is DbUpdateException)
            {
                logger.LogWarning(ex, "{System} sync failed", sys);
                error = ex is DbUpdateException ? "A record could not be saved: " + (ex.InnerException?.Message ?? ex.Message) : ex.Message;
                db.ChangeTracker.Clear();
            }
            finally
            {
                await ReleaseLockAsync(integration.Id, ct);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        var finished = db.Clock.UtcNow;
        var success = error is null;
        DateTime? lastSync = success ? finished : integration.LastSyncAt;
        var newStatus = success ? IntegrationStatus.Connected : IntegrationStatus.Error;
        string? lastError = error is null ? null : TextNormalization.Truncate(error, 1000);
        await db.Integrations.Where(i => i.Id == integration.Id).ExecuteUpdateAsync(u => u
            .SetProperty(i => i.LastSyncAt, lastSync)
            .SetProperty(i => i.Status, newStatus)
            .SetProperty(i => i.LastError, lastError), ct);
        var result = new ErpSyncResultDto(sys, trigger, started, finished, success, error, results);
        if (trigger == ManualTrigger)
        {
            audit.Write(CustomerAuditActions.ErpSync, "Integration", integration.Id, $"{integration.Name} sync run manually ({(success ? "succeeded" : "failed")})",
                results.Select(r => new { r.Entity, r.Created, r.Updated, r.Skipped }));
            await db.SaveChangesAsync(ct);
        }
        await callLog.RecordAsync(new ErpCallRecord(ctx.TenantId, integration.Id, sys, MessageDirection.Inbound, "sync", connector.Mode == ErpModes.Http ? integration.BaseUrl : "simulator",
            null, success, (int)sw.ElapsedMilliseconds, $"trigger={trigger}; by={ctx.ActorName}", Json.Serialize(result), ctx.CorrelationId), ct);
        return result;
    }

    // ───────────────────────────── Lock ─────────────────────────────

    private async Task<bool> TryLockAsync(Guid integrationId, CancellationToken ct)
    {
        await using var cmd = Command("DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;", integrationId);
        var value = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(value) >= 0;
    }

    private async Task ReleaseLockAsync(Guid integrationId, CancellationToken ct)
    {
        try
        {
            await using var cmd = Command("EXEC sp_releaseapplock @Resource = @res, @LockOwner = 'Session';", integrationId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (DbException ex) { logger.LogWarning(ex, "Could not release the ERP sync lock"); }
    }

    private DbCommand Command(string sql, Guid integrationId)
    {
        var conn = db.Database.GetDbConnection();
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var p = cmd.CreateParameter();
        p.ParameterName = "@res";
        p.DbType = DbType.String;
        p.Value = $"erp-sync:{integrationId:N}";
        cmd.Parameters.Add(p);
        return cmd;
    }

    // ───────────────────────────── Customers & branches ─────────────────────────────

    private sealed class Tally(string entity)
    {
        public int Received, Created, Updated, Unchanged, Skipped;
        public readonly List<string> Warnings = [];
        public void Warn(string w) { if (Warnings.Count < 20 && !Warnings.Contains(w)) Warnings.Add(w); }
        public ErpSyncEntityResultDto Result() => new(entity, Received, Created, Updated, Unchanged, Skipped, Warnings);
    }

    private async Task<List<ErpSyncEntityResultDto>> SyncCustomersAsync(IErpConnector connector, DateTime? since, ErpSettings settings, CancellationToken ct)
    {
        var masters = await connector.GetCustomersAsync(since, ct);
        var tally = new Tally("Customers");
        var branchTally = new Tally("Locations");
        tally.Received = masters.Count;
        var now = db.Clock.UtcNow;
        var customers = await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).ToListAsync(ct);
        foreach (var m in masters)
        {
            var (customer, outcome) = await UpsertCustomerCoreAsync(m, customers, settings.CreateMissingCustomers, connector.System, now, ct);
            switch (outcome)
            {
                case UpsertOutcome.Created: tally.Created++; break;
                case UpsertOutcome.Updated: tally.Updated++; break;
                case UpsertOutcome.Unchanged: tally.Unchanged++; break;
                default:
                    tally.Skipped++;
                    if (outcome == UpsertOutcome.Conflict) tally.Warn($"Customer {m.Code}: the ERP id is linked to more than one customer.");
                    if (outcome == UpsertOutcome.Deleted) tally.Warn($"Customer {m.Code} was deleted in SupportDesk; restore it to resume syncing.");
                    continue;
            }
            await UpsertBranchesAsync(customer!, m.Branches, branchTally, ct);
        }
        await db.SaveChangesAsync(ct);
        return [tally.Result(), branchTally.Result()];
    }

    public enum UpsertOutcome { Created, Updated, Unchanged, Skipped, Conflict, Deleted }

    /// <summary>Finds (by ERP id, then unlinked ERP code / customer code) or creates the customer for an ERP master. The caller saves.</summary>
    public async Task<(Customer? Customer, UpsertOutcome Outcome)> UpsertCustomerCoreAsync(ErpCustomerMaster m, List<Customer> customers, bool createMissing,
        string system, DateTime now, CancellationToken ct)
    {
        var byId = customers.Where(c => c.ErpCustomerId == m.Id).ToList();
        if (byId.Count > 1) return (null, UpsertOutcome.Conflict);
        var c = byId.FirstOrDefault()
                ?? customers.FirstOrDefault(x => x.ErpCustomerId == null && x.ErpCode != null && x.ErpCode.Equals(m.Code, StringComparison.OrdinalIgnoreCase))
                ?? customers.FirstOrDefault(x => x.ErpCustomerId == null && !x.IsDeleted && x.Code != null && x.Code.Equals(m.Code, StringComparison.OrdinalIgnoreCase));
        if (c is { IsDeleted: true }) return (null, UpsertOutcome.Deleted);
        var status = m.IsBlocked ? CustomerStatus.OnHold : m.IsActive ? CustomerStatus.Active : CustomerStatus.Inactive;
        var codeFree = !customers.Any(x => !x.IsDeleted && x.Code != null && x.Code.Equals(m.Code, StringComparison.OrdinalIgnoreCase) && x != c) &&
                       !await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AnyAsync(x => x.Code == m.Code && (c == null || x.Id != c.Id) && !x.IsDeleted, ct);
        if (c is null)
        {
            if (!createMissing) return (null, UpsertOutcome.Skipped);
            var domain = TextNormalization.EmailDomain(m.Email);
            var domainFree = domain is not null && !customers.Any(x => x.DomainsJson.Contains($"\"{domain}\""));
            c = new Customer
            {
                Name = m.Name, Code = codeFree ? m.Code : null, ErpCustomerId = m.Id, ErpCode = m.Code, ErpSyncedAt = now, Email = m.Email, Phone = m.Phone,
                Website = m.Website, Industry = m.Industry, ServiceRegion = m.Region, Status = status, Tier = "Standard",
                TimeZone = (m.Country ?? "India").Equals("India", StringComparison.OrdinalIgnoreCase) ? "Asia/Kolkata" : null,
                DomainsJson = Json.Serialize(domainFree && domain is not null ? new[] { domain } : []),
                Notes = $"Imported from {system}{(m.Gstin is null ? "" : $" — GSTIN {m.Gstin}")}{(m.LegalName is null || m.LegalName == m.Name ? "" : $" — legal name {m.LegalName}")}."
            };
            db.Customers.Add(c);
            customers.Add(c);
            events.Publish(EventTypes.CustomerCreated, "Customer", c.Id, new EntityEventPayload(c.Id, c.Name, [new FieldChange("source", null, system)]));
            return (c, UpsertOutcome.Created);
        }
        var set = new ChangeSet();
        set.Set("erpCustomerId", c.ErpCustomerId, (Guid?)m.Id, v => c.ErpCustomerId = v);
        set.Set("erpCode", c.ErpCode, m.Code, v => c.ErpCode = v);
        if (c.Code is null && codeFree) set.Set("code", c.Code, m.Code, v => c.Code = v);
        if (string.IsNullOrWhiteSpace(c.Email) && m.Email is not null) set.Set("email", c.Email, m.Email, v => c.Email = v);
        if (string.IsNullOrWhiteSpace(c.Phone) && m.Phone is not null) set.Set("phone", c.Phone, m.Phone, v => c.Phone = v);
        if (string.IsNullOrWhiteSpace(c.Website) && m.Website is not null) set.Set("website", c.Website, m.Website, v => c.Website = v);
        if (string.IsNullOrWhiteSpace(c.Industry) && m.Industry is not null) set.Set("industry", c.Industry, m.Industry, v => c.Industry = v);
        if (string.IsNullOrWhiteSpace(c.ServiceRegion) && m.Region is not null) set.Set("serviceRegion", c.ServiceRegion, m.Region, v => c.ServiceRegion = v);
        // The ERP can put an account on hold or deactivate it; it never re-activates one the desk has parked.
        if (status != CustomerStatus.Active && c.Status == CustomerStatus.Active) set.Set("status", c.Status, status, v => c.Status = v);
        c.ErpSyncedAt = now;
        if (!set.Any) return (c, UpsertOutcome.Unchanged);
        events.Publish(EventTypes.CustomerUpdated, "Customer", c.Id, new EntityEventPayload(c.Id, c.Name, set.Changes));
        return (c, UpsertOutcome.Updated);
    }

    private async Task UpsertBranchesAsync(Customer c, IReadOnlyList<ErpBranch> branches, Tally tally, CancellationToken ct)
    {
        if (branches.Count == 0) return;
        tally.Received += branches.Count;
        var existing = db.Entry(c).State == EntityState.Added ? [] : await db.CustomerLocations.Where(l => l.CustomerId == c.Id).ToListAsync(ct);
        existing.AddRange(db.CustomerLocations.Local.Where(l => l.CustomerId == c.Id && !existing.Contains(l)));
        foreach (var b in branches)
        {
            var l = existing.FirstOrDefault(x => x.ErpLocationId == b.Id)
                    ?? existing.FirstOrDefault(x => x.ErpLocationId == null && x.Code != null && x.Code.Equals(b.Code, StringComparison.OrdinalIgnoreCase));
            if (l is null)
            {
                l = new CustomerLocation
                {
                    CustomerId = c.Id, Name = b.Name, Code = b.Code, AddressLine1 = b.AddressLine1, City = b.City, State = b.State, PostalCode = b.PostalCode,
                    Phone = b.Phone, Country = "India", IsHeadOffice = b.IsHeadOffice && !existing.Any(x => x.IsHeadOffice), ErpLocationId = b.Id, ServiceRegion = c.ServiceRegion
                };
                db.CustomerLocations.Add(l);
                existing.Add(l);
                tally.Created++;
                continue;
            }
            var set = new ChangeSet();
            set.Set("erpLocationId", l.ErpLocationId, (Guid?)b.Id, v => l.ErpLocationId = v);
            if (l.AddressLine1 is null && b.AddressLine1 is not null) set.Set("addressLine1", l.AddressLine1, b.AddressLine1, v => l.AddressLine1 = v);
            if (l.City is null && b.City is not null) set.Set("city", l.City, b.City, v => l.City = v);
            if (l.State is null && b.State is not null) set.Set("state", l.State, b.State, v => l.State = v);
            if (l.PostalCode is null && b.PostalCode is not null) set.Set("postalCode", l.PostalCode, b.PostalCode, v => l.PostalCode = v);
            if (l.Phone is null && b.Phone is not null) set.Set("phone", l.Phone, b.Phone, v => l.Phone = v);
            if (set.Any) tally.Updated++; else tally.Unchanged++;
        }
    }

    // ───────────────────────────── Installations ─────────────────────────────

    private async Task<ErpSyncEntityResultDto> SyncInstallationsAsync(IErpConnector connector, DateTime? since, CancellationToken ct)
    {
        var records = await connector.GetInstallationsAsync(null, since, ct);
        var tally = new Tally("Installations") { Received = records.Count };
        var customers = await db.Customers.Where(c => c.ErpCustomerId != null).Select(c => new { c.Id, c.ErpCustomerId }).ToListAsync(ct);
        var customerByErp = customers.GroupBy(c => c.ErpCustomerId!.Value).ToDictionary(g => g.Key, g => g.First().Id);
        var products = await db.Products.ToListAsync(ct);
        var versions = await db.ProductVersions.ToListAsync(ct);
        var releases = await db.ProductReleases.ToListAsync(ct);
        var modules = await db.ProductModules.ToListAsync(ct);
        var locations = await db.CustomerLocations.Where(l => l.ErpLocationId != null).Select(l => new { l.Id, l.ErpLocationId, l.CustomerId }).ToListAsync(ct);
        var installations = await db.Installations.ToListAsync(ct);

        foreach (var r in records)
        {
            if (!customerByErp.TryGetValue(r.CustomerId, out var customerId)) { tally.Skipped++; tally.Warn($"Installation {r.InstallationCode ?? r.Name}: customer not imported."); continue; }
            var product = products.FirstOrDefault(p => p.Code.Equals(r.ProductCode, StringComparison.OrdinalIgnoreCase));
            if (product is null) { tally.Skipped++; tally.Warn($"Unknown product code '{r.ProductCode}' — add it to the catalog to import its installations."); continue; }
            var version = r.Version is null ? null : versions.FirstOrDefault(v => v.ProductId == product.Id && v.Version == r.Version)
                                                   ?? versions.FirstOrDefault(v => v.ProductId == product.Id && r.Version.StartsWith(v.Version + ".", StringComparison.Ordinal));
            if (r.Version is not null && version is null) tally.Warn($"Version {r.ProductCode} {r.Version} is not in the catalog.");
            var release = r.Build is null || version is null ? null
                : releases.FirstOrDefault(x => x.VersionId == version.Id && (x.Name == r.Build || x.BuildNumber == r.Build));
            var locationId = r.BranchId is { } bid ? locations.FirstOrDefault(l => l.ErpLocationId == bid && l.CustomerId == customerId)?.Id : null;
            var moduleIds = r.Modules.Select(n => modules.FirstOrDefault(m => m.ProductId == product.Id && m.Name.Equals(n, StringComparison.OrdinalIgnoreCase))?.Id)
                .Where(id => id is not null).Select(id => id!.Value).Distinct().OrderBy(id => id).ToList();
            var environment = Enum.TryParse<InstallationEnvironment>(r.Environment, true, out var env) ? env : InstallationEnvironment.Production;
            var mode = Enum.TryParse<DeploymentMode>(r.DeploymentMode, true, out var dm) ? dm : DeploymentMode.OnPremise;
            var status = Enum.TryParse<InstallationStatus>(r.Status, true, out var st) ? st : InstallationStatus.Active;

            var i = installations.FirstOrDefault(x => x.ErpInstallationId == r.Id)
                    ?? installations.FirstOrDefault(x => x.ErpInstallationId == null && x.CustomerId == customerId && x.InstallationCode != null &&
                                                         x.InstallationCode.Equals(r.InstallationCode, StringComparison.OrdinalIgnoreCase))
                    ?? installations.FirstOrDefault(x => x.ErpInstallationId == null && x.CustomerId == customerId && x.InstallationCode == null &&
                                                         x.ProductId == product.Id && x.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase));
            if (i is null)
            {
                var code = r.InstallationCode;
                if (code is not null && installations.Any(x => x.InstallationCode == code)) code = null;
                i = new Installation
                {
                    CustomerId = customerId, ProductId = product.Id, Name = r.Name, InstallationCode = code, ErpInstallationId = r.Id,
                    IntegrationsJson = "[]", ModulesJson = Json.Serialize(moduleIds)
                };
                ApplyInstallation(i, r, version?.Id, release?.Id, locationId, environment, mode, status, moduleIds, new ChangeSet());
                db.Installations.Add(i);
                installations.Add(i);
                tally.Created++;
                continue;
            }
            var set = new ChangeSet();
            set.Set("erpInstallationId", i.ErpInstallationId, (Guid?)r.Id, v => i.ErpInstallationId = v);
            set.Set("productId", i.ProductId, product.Id, v => i.ProductId = v);
            if (i.InstallationCode is null && r.InstallationCode is { } erpCode && !installations.Any(x => x.InstallationCode == erpCode))
                set.Set("installationCode", i.InstallationCode, erpCode, v => i.InstallationCode = v);
            ApplyInstallation(i, r, version?.Id ?? i.VersionId, release?.Id ?? (version?.Id == i.VersionId ? i.ReleaseId : null), locationId ?? i.LocationId,
                environment, mode, status, moduleIds, set);
            if (set.Any) tally.Updated++; else tally.Unchanged++;
        }
        await db.SaveChangesAsync(ct);
        return tally.Result();
    }

    private static void ApplyInstallation(Installation i, ErpInstallationRecord r, Guid? versionId, Guid? releaseId, Guid? locationId, InstallationEnvironment env,
        DeploymentMode mode, InstallationStatus status, List<Guid> moduleIds, ChangeSet set)
    {
        set.Set("versionId", i.VersionId, versionId, v => i.VersionId = v);
        set.Set("releaseId", i.ReleaseId, releaseId, v => i.ReleaseId = v);
        set.Set("locationId", i.LocationId, locationId, v => i.LocationId = v);
        set.Set("environment", i.Environment, env, v => i.Environment = v);
        set.Set("deploymentMode", i.DeploymentMode, mode, v => i.DeploymentMode = v);
        if (r.OperatingSystem is not null) set.Set("operatingSystem", i.OperatingSystem, r.OperatingSystem, v => i.OperatingSystem = v);
        if (r.DatabaseVersion is not null) set.Set("databaseVersion", i.DatabaseVersion, r.DatabaseVersion, v => i.DatabaseVersion = v);
        if (r.UserCount is not null) set.Set("userCount", i.UserCount, r.UserCount, v => i.UserCount = v);
        if (r.InstalledOn is not null) set.Set("installedOn", i.InstalledOn, r.InstalledOn, v => i.InstalledOn = v);
        if (r.GoLiveOn is not null) set.Set("goLiveOn", i.GoLiveOn, r.GoLiveOn, v => i.GoLiveOn = v);
        set.Set("status", i.Status, status, v => i.Status = v);
        if (moduleIds.Count > 0)
        {
            var current = Json.GuidList(i.ModulesJson).OrderBy(x => x).ToList();
            if (!current.SequenceEqual(moduleIds)) set.Set("modules", i.ModulesJson, Json.Serialize(moduleIds), v => i.ModulesJson = v);
        }
    }

    // ───────────────────────────── Licences ─────────────────────────────

    private async Task<ErpSyncEntityResultDto> SyncLicencesAsync(IErpConnector connector, DateTime? since, CancellationToken ct)
    {
        var records = await connector.GetLicencesAsync(null, since, ct);
        var tally = new Tally("Licences") { Received = records.Count };
        var installations = await db.Installations.Where(i => i.ErpInstallationId != null).Select(i => new { i.Id, i.ErpInstallationId, i.CustomerId, i.ProductId }).ToListAsync(ct);
        var licences = await db.Licences.ToListAsync(ct);
        foreach (var r in records)
        {
            var inst = installations.FirstOrDefault(i => i.ErpInstallationId == r.InstallationId);
            if (inst is null) { tally.Skipped++; tally.Warn($"Licence {r.KeyMasked}: installation not imported."); continue; }
            var status = Enum.TryParse<LicenceStatus>(r.Status, true, out var st) ? st : LicenceStatus.Active;
            var l = licences.FirstOrDefault(x => x.ErpLicenceId == r.Id)
                    ?? licences.FirstOrDefault(x => x.ErpLicenceId == null && x.InstallationId == inst.Id && x.ProductId == inst.ProductId);
            var isNew = l is null;
            if (l is null)
            {
                l = new Licence { ErpLicenceId = r.Id };
                db.Licences.Add(l);
                licences.Add(l);
            }
            var set = new ChangeSet();
            set.Set("erpLicenceId", l.ErpLicenceId, (Guid?)r.Id, v => l.ErpLicenceId = v);
            set.Set("customerId", l.CustomerId, inst.CustomerId, v => l.CustomerId = v);
            set.Set("installationId", l.InstallationId, inst.Id, v => l.InstallationId = v);
            set.Set("productId", l.ProductId, inst.ProductId, v => l.ProductId = v);
            set.Set("keyMasked", l.KeyMasked, r.KeyMasked, v => l.KeyMasked = v);
            set.Set("edition", l.Edition, r.Edition, v => l.Edition = v);
            set.Set("seats", l.Seats, r.Seats, v => l.Seats = v);
            set.Set("issuedOn", l.IssuedOn, r.IssuedOn, v => l.IssuedOn = v);
            set.Set("expiresOn", l.ExpiresOn, r.ExpiresOn, v => l.ExpiresOn = v);
            set.Set("status", l.Status, status, v => l.Status = v);
            if (isNew) tally.Created++; else if (set.Any) tally.Updated++; else tally.Unchanged++;
        }
        await db.SaveChangesAsync(ct);
        return tally.Result();
    }

    // ───────────────────────────── AMC contracts ─────────────────────────────

    private async Task<ErpSyncEntityResultDto> SyncContractsAsync(IErpConnector connector, DateTime? since, CancellationToken ct)
    {
        var records = await connector.GetAmcContractsAsync(null, since, ct);
        var tally = new Tally("Contracts") { Received = records.Count };
        var customers = await db.Customers.Where(c => c.ErpCustomerId != null).Select(c => new { c.Id, c.ErpCustomerId }).ToListAsync(ct);
        var customerByErp = customers.GroupBy(c => c.ErpCustomerId!.Value).ToDictionary(g => g.Key, g => g.First().Id);
        var products = await db.Products.AsNoTracking().ToListAsync(ct);
        var contracts = await db.Contracts.ToListAsync(ct);
        var affectedCustomers = new HashSet<Guid>();
        foreach (var r in records)
        {
            if (!customerByErp.TryGetValue(r.CustomerId, out var customerId)) { tally.Skipped++; tally.Warn($"Contract {r.Number}: customer not imported."); continue; }
            var type = Enum.TryParse<ContractType>(r.ContractType, true, out var t) ? t : ContractType.Amc;
            var status = Enum.TryParse<ContractStatus>(r.Status, true, out var s) ? s : ContractStatus.Active;
            var c = contracts.FirstOrDefault(x => x.ErpContractId == r.Id)
                    ?? contracts.FirstOrDefault(x => x.ErpContractId == null && x.CustomerId == customerId && x.Number.Equals(r.Number, StringComparison.OrdinalIgnoreCase));
            if (c is null)
            {
                var number = contracts.Any(x => x.Number.Equals(r.Number, StringComparison.OrdinalIgnoreCase)) ? $"{r.Number}-ERP" : r.Number;
                c = new Contract
                {
                    CustomerId = customerId, Number = number, Name = r.Name, Type = type, Status = status, StartDate = r.StartDate, EndDate = r.EndDate,
                    ContractValue = r.Value, Currency = r.Currency, AutoRenew = r.AutoRenew, RenewalDate = r.AutoRenew ? r.EndDate.AddDays(1) : null,
                    ErpContractId = r.Id, ErpReference = r.SalesOrderNumber, CoverageJson = Json.Serialize(new { onsite = r.Onsite, source = connector.System })
                };
                db.Contracts.Add(c);
                contracts.Add(c);
                foreach (var code in r.ProductCodes)
                {
                    var product = products.FirstOrDefault(p => p.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
                    if (product is null) { tally.Warn($"Contract {r.Number}: product code '{code}' is not in the catalog."); continue; }
                    db.Entitlements.Add(new Entitlement
                    {
                        ContractId = c.Id, CustomerId = customerId, Name = $"{product.Name} support", Type = EntitlementType.Support, ProductId = product.Id,
                        StartDate = r.StartDate, EndDate = r.EndDate
                    });
                }
                affectedCustomers.Add(customerId);
                tally.Created++;
                continue;
            }
            var (oldStart, oldEnd) = (c.StartDate, c.EndDate);
            var set = new ChangeSet();
            set.Set("erpContractId", c.ErpContractId, (Guid?)r.Id, v => c.ErpContractId = v);
            set.Set("type", c.Type, type, v => c.Type = v);
            // Desk-side suspensions/cancellations are deliberate; the ERP status only drives Draft/Active/Expired.
            if (c.Status is not (ContractStatus.Suspended or ContractStatus.Cancelled) || status == ContractStatus.Cancelled)
                set.Set("status", c.Status, status, v => c.Status = v);
            set.Set("startDate", c.StartDate, r.StartDate, v => c.StartDate = v);
            set.Set("endDate", c.EndDate, r.EndDate, v => c.EndDate = v);
            set.Set("contractValue", c.ContractValue, (decimal?)r.Value, v => c.ContractValue = v);
            set.Set("currency", c.Currency, r.Currency, v => c.Currency = v);
            set.Set("autoRenew", c.AutoRenew, r.AutoRenew, v => c.AutoRenew = v);
            if (r.SalesOrderNumber is not null) set.Set("erpReference", c.ErpReference, r.SalesOrderNumber, v => c.ErpReference = v);
            if (set.Has("status") || set.Has("startDate") || set.Has("endDate"))
            {
                affectedCustomers.Add(customerId);
                // Entitlements that followed the contract period keep following it.
                foreach (var e in await db.Entitlements.Where(e => e.ContractId == c.Id).ToListAsync(ct))
                {
                    if (e.StartDate == oldStart) e.StartDate = r.StartDate;
                    if (e.EndDate == oldEnd || e.EndDate > r.EndDate) e.EndDate = r.EndDate;
                }
            }
            if (set.Any) tally.Updated++; else tally.Unchanged++;
        }
        foreach (var customerId in affectedCustomers)
            jobs.Enqueue(ContractEligibilityJobHandler.JobType, new ContractEligibilityPayload(customerId));
        await db.SaveChangesAsync(ct);
        return tally.Result();
    }
}

/// <summary>Runs scheduled ERP syncs: every active WingsErp/WingsBooks integration whose interval has elapsed.</summary>
public sealed class ErpSyncTask(DeskDbContext db, TenantScopeRunner runner, ILogger<ErpSyncTask> logger) : IRecurringTask
{
    public const string TaskName = "erp-sync";
    public string Name => TaskName;
    public TimeSpan Interval => TimeSpan.FromMinutes(5);

    public async Task RunAsync(CancellationToken ct)
    {
        var now = db.Clock.UtcNow;
        // Discover connected tenants in every database (dedicated tenants' settings are not in the shared one).
        var candidates = new List<(Guid TenantId, string Type, DateTime? LastSyncAt, string SettingsJson)>();
        await runner.ForEachDatabaseAsync("ERP sync", async (sp, partition, token) =>
        {
            var found = await sp.GetRequiredService<DeskDbContext>().AllTenants<Integration>().AsNoTracking()
                .Where(i => i.IsActive && (i.Type == ErpSystems.WingsErp || i.Type == ErpSystems.WingsBooks))
                .Select(i => new { i.TenantId, i.Type, i.LastSyncAt, i.SettingsJson }).ToListAsync(token);
            candidates.AddRange(found.Where(i => partition.Owns(i.TenantId)).Select(i => (i.TenantId, i.Type, i.LastSyncAt, i.SettingsJson)));
        }, ct);
        foreach (var c in candidates)
        {
            var interval = Json.DeserializeOr(c.SettingsJson, new ErpSettings()).SyncIntervalMinutes;
            if (c.LastSyncAt is { } last && last.AddMinutes(Math.Max(5, interval)) > now) continue;
            try
            {
                await runner.RunForTenantAsync(c.TenantId, "ERP sync", sp => sp.GetRequiredService<ErpSyncService>().SyncAsync(c.Type, ErpSyncService.ScheduleTrigger, ct),
                    ct, ActorType.Integration);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Scheduled {System} sync failed for tenant {Tenant}", c.Type, c.TenantId);
            }
        }
    }
}
