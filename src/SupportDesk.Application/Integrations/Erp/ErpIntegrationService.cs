using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Customers;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Erp;
using SupportDesk.Domain;
using SupportDesk.Domain.Integration;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Integrations.Erp;

/// <summary>
/// ERP integration administration (admin.integrations): connection settings (simulator or HTTP with OAuth2 client
/// credentials / API key; secrets encrypted and write-only), connection test, integration log / sync history — plus
/// read models used by agents: a customer's ERP records for Customer 360 and single record lookups for the ticket
/// context panel.
/// </summary>
public sealed class ErpIntegrationService(
    DeskDbContext db,
    RequestContext ctx,
    ErpConnectorResolver resolver,
    ISecretProtector secrets,
    IAuditWriter audit,
    IErpCallLog callLog,
    TenantSettingsService settings,
    ILogger<ErpIntegrationService> logger)
{
    public static string NormalizeSystem(string? system) =>
        ErpSystems.All.FirstOrDefault(s => s.Equals(system, StringComparison.OrdinalIgnoreCase)) ??
        (string.IsNullOrWhiteSpace(system) ? ErpSystems.WingsErp : throw new ValidationException(new Dictionary<string, string> { ["system"] = $"Unknown ERP system '{system}'." }));

    public async Task<bool> IsActiveAsync(string system, CancellationToken ct) =>
        await db.Integrations.AnyAsync(i => i.Type == system && i.IsActive, ct);

    // ───────────────────────────── Settings ─────────────────────────────

    public async Task<ErpIntegrationDto> GetSettingsAsync(string? system, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var sys = NormalizeSystem(system);
        var integration = await resolver.IntegrationAsync(sys, ct);
        return Map(sys, integration);
    }

    private ErpIntegrationDto Map(string system, Integration? i)
    {
        if (i is null)
            return new ErpIntegrationDto(null, system, system == ErpSystems.WingsBooks ? "Wings Books" : "Wings ERP", false, false, ErpModes.Simulator, null, "OAuth2",
                null, null, null, false, false, 30, 60, true, true, true, true, true, nameof(IntegrationStatus.Disabled), null, null, null);
        var s = Json.DeserializeOr(i.SettingsJson, new ErpSettings());
        var sec = string.IsNullOrEmpty(i.SecretsProtected) ? new ErpSecrets(null, null) : Json.DeserializeOr(secrets.Unprotect(i.SecretsProtected), new ErpSecrets(null, null));
        return new ErpIntegrationDto(i.Id, system, i.Name, true, i.IsActive, s.Mode, i.BaseUrl, s.AuthType, s.TokenUrl, s.ClientId, s.Scope,
            !string.IsNullOrEmpty(sec.ClientSecret), !string.IsNullOrEmpty(sec.ApiKey), s.TimeoutSeconds, s.SyncIntervalMinutes, s.SyncCustomers, s.SyncInstallations,
            s.SyncLicences, s.SyncContracts, s.CreateMissingCustomers, i.Status.ToString(), i.LastSyncAt, i.LastError, Convert.ToBase64String(i.RowVersion));
    }

    public async Task<ErpIntegrationDto> SaveSettingsAsync(string? system, SaveErpIntegrationRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var sys = NormalizeSystem(system);
        var errors = new Dictionary<string, string>();
        var mode = r.Mode?.Trim().ToLowerInvariant() is ErpModes.Http ? ErpModes.Http : r.Mode?.Trim().ToLowerInvariant() is ErpModes.Simulator or null or ""
            ? ErpModes.Simulator : null;
        if (mode is null) errors["mode"] = "Mode must be 'simulator' or 'http'.";
        var authType = r.AuthType?.Trim() switch
        {
            null or "" => "OAuth2",
            var a when a.Equals("oauth2", StringComparison.OrdinalIgnoreCase) => "OAuth2",
            var a when a.Equals("apikey", StringComparison.OrdinalIgnoreCase) => "ApiKey",
            var a when a.Equals("none", StringComparison.OrdinalIgnoreCase) => "None",
            _ => null
        };
        if (authType is null) errors["authType"] = "Authentication must be OAuth2, ApiKey or None.";
        string? baseUrl = Parse.Clean(r.BaseUrl, 400)?.TrimEnd('/');
        if (mode == ErpModes.Http)
        {
            if (baseUrl is null || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http"))
                errors["baseUrl"] = "Enter the ERP API base URL (https://…).";
            if (authType == "OAuth2" && Parse.Clean(r.ClientId) is null) errors["clientId"] = "OAuth2 needs a client id.";
            if (r.TokenUrl is { Length: > 0 } tu && !Uri.TryCreate(tu, UriKind.Absolute, out _)) errors["tokenUrl"] = "Enter a valid token URL.";
        }
        if (r.SyncIntervalMinutes is < 5 or > 10080) errors["syncIntervalMinutes"] = "Sync every 5 minutes to 7 days.";
        if (r.TimeoutSeconds is < 5 or > 300) errors["timeoutSeconds"] = "Timeout must be between 5 and 300 seconds.";
        if (errors.Count > 0) throw new ValidationException(errors);

        var integration = await resolver.IntegrationAsync(sys, ct);
        var isNew = integration is null;
        if (integration is null) db.Integrations.Add(integration = new Integration { Type = sys });
        var previous = Json.DeserializeOr(integration.SettingsJson, new ErpSettings());
        var settingsValue = new ErpSettings
        {
            Mode = mode!, AuthType = authType!, TokenUrl = Parse.Clean(r.TokenUrl, 400), ClientId = Parse.Clean(r.ClientId, 200), Scope = Parse.Clean(r.Scope, 400),
            TimeoutSeconds = r.TimeoutSeconds, SyncIntervalMinutes = r.SyncIntervalMinutes, SyncCustomers = r.SyncCustomers, SyncInstallations = r.SyncInstallations,
            SyncLicences = r.SyncLicences, SyncContracts = r.SyncContracts, CreateMissingCustomers = r.CreateMissingCustomers,
            SimulatorAnchor = previous.SimulatorAnchor ?? DateOnly.FromDateTime(db.Clock.UtcNow)
        };
        var oldSecrets = string.IsNullOrEmpty(integration.SecretsProtected) ? new ErpSecrets(null, null)
            : Json.DeserializeOr(secrets.Unprotect(integration.SecretsProtected), new ErpSecrets(null, null));
        // Write-only secrets: null keeps the stored value, "" clears it.
        var newSecrets = new ErpSecrets(r.ClientSecret is null ? oldSecrets.ClientSecret : r.ClientSecret.Length == 0 ? null : r.ClientSecret,
            r.ApiKey is null ? oldSecrets.ApiKey : r.ApiKey.Length == 0 ? null : r.ApiKey);
        integration.Name = Parse.Clean(r.Name, 200) ?? (sys == ErpSystems.WingsBooks ? "Wings Books" : "Wings ERP");
        integration.BaseUrl = baseUrl;
        integration.SettingsJson = Json.Serialize(settingsValue);
        integration.SecretsProtected = newSecrets.ClientSecret is null && newSecrets.ApiKey is null ? null : secrets.Protect(Json.Serialize(newSecrets));
        integration.IsActive = r.IsActive;
        integration.Status = r.IsActive ? (integration.Status == IntegrationStatus.Disabled ? IntegrationStatus.Connected : integration.Status) : IntegrationStatus.Disabled;
        if (r.IsActive && integration.LastError is not null && isNew) integration.LastError = null;
        // The audit entry records which settings changed, never secret values.
        audit.Write(AuditActions.IntegrationChanged, "Integration", integration.Id, $"{integration.Name} integration {(isNew ? "configured" : "updated")}",
            new
            {
                mode, authType, baseUrl, settingsValue.ClientId, settingsValue.SyncIntervalMinutes, r.IsActive,
                clientSecretChanged = r.ClientSecret is not null, apiKeyChanged = r.ApiKey is not null
            });
        await db.SaveChangesAsync(ct);
        return Map(sys, integration);
    }

    public async Task<ErpConnectionTestDto> TestAsync(string? system, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var sys = NormalizeSystem(system);
        var integration = await resolver.IntegrationAsync(sys, ct) ?? throw new DomainException("integration_unavailable", $"{sys} is not configured yet.");
        var sw = Stopwatch.StartNew();
        try
        {
            var connector = resolver.Create(integration);
            var ping = await connector.PingAsync(ct);
            integration.Status = integration.IsActive ? IntegrationStatus.Connected : IntegrationStatus.Disabled;
            integration.LastError = null;
            await db.SaveChangesAsync(ct);
            return new ErpConnectionTestDto(ping.Ok, ping.Ok ? $"Connected to {ping.System} ({connector.Mode})." : "The ERP answered but reported a problem.",
                (int)sw.ElapsedMilliseconds, ping.Version);
        }
        catch (Exception ex) when (ex is DomainException or HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "ERP connection test failed for {System}", sys);
            integration.Status = IntegrationStatus.Error;
            integration.LastError = TextNormalization.Truncate(ex.Message, 1000);
            await db.SaveChangesAsync(ct);
            return new ErpConnectionTestDto(false, ex.Message, (int)sw.ElapsedMilliseconds, null);
        }
    }

    public async Task<List<IntegrationLogDto>> LogsAsync(string? system, string? operation, int limit, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var sys = NormalizeSystem(system);
        var query = db.IntegrationLogs.AsNoTracking().Where(l => l.System == sys);
        if (!string.IsNullOrWhiteSpace(operation)) query = query.Where(l => l.Operation.StartsWith(operation));
        return await query.OrderByDescending(l => l.CreatedAt).Take(Math.Clamp(limit, 1, 500))
            .Select(l => new IntegrationLogDto(l.Id, l.System, l.Direction.ToString(), l.Operation, l.Url, l.StatusCode, l.Success, l.DurationMs, l.RequestExcerpt,
                l.ResponseExcerpt, l.CorrelationId, l.CreatedAt)).ToListAsync(ct);
    }

    // ───────────────────────────── Read models for agents ─────────────────────────────

    /// <summary>The customer's ERP picture for Customer 360. Never throws for connectivity problems: the error is returned in the DTO.</summary>
    public async Task<ErpCustomerRecordsDto> CustomerRecordsAsync(Guid customerId, string? system, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersView);
        var sys = NormalizeSystem(system);
        var customer = await CustomerAccess.LoadVisibleAsync(db, ctx, customerId, ct, track: false);
        ErpCustomerRecordsDto Empty(bool configured, string? mode, string? error) =>
            new(sys, configured, mode, error, customer.ErpCustomerId, customer.ErpCode, null, 0, "INR", [], [], [], [], []);
        var integration = await resolver.IntegrationAsync(sys, ct);
        if (integration is not { IsActive: true }) return Empty(false, null, null);
        var connector = resolver.Create(integration);
        if (customer.ErpCustomerId is not { } erpId)
        {
            if (customer.ErpCode is null && customer.Code is null) return Empty(true, connector.Mode, "This customer is not linked to an ERP customer.");
            try
            {
                var found = await connector.GetCustomerAsync(null, customer.ErpCode ?? customer.Code, ct);
                if (found is null) return Empty(true, connector.Mode, "This customer is not linked to an ERP customer.");
                erpId = found.Id;
            }
            catch (Exception ex) when (IsConnectivity(ex)) { return Empty(true, connector.Mode, ex.Message); }
        }
        try
        {
            var master = await connector.GetCustomerAsync(erpId, null, ct);
            var docs = await connector.GetTransactionsAsync(erpId, null, null, 60, ct);
            var amc = await connector.GetAmcContractsAsync(erpId, null, ct);
            var installs = sys == ErpSystems.WingsErp ? await connector.GetInstallationsAsync(erpId, null, ct) : [];
            var invoices = docs.Where(d => d.RecordType == ErpRecordTypes.SalesInvoice).Take(20).ToList();
            var orders = docs.Where(d => d.RecordType is ErpRecordTypes.SalesOrder or ErpRecordTypes.Delivery).Take(20).ToList();
            var receipts = docs.Where(d => d.RecordType is ErpRecordTypes.Receipt or ErpRecordTypes.CreditNote).Take(20).ToList();
            var outstanding = docs.Where(d => d.RecordType == ErpRecordTypes.SalesInvoice).Sum(d => d.BalanceAmount ?? 0);
            return new ErpCustomerRecordsDto(sys, true, connector.Mode, null, erpId, master?.Code ?? customer.ErpCode, master, outstanding,
                docs.FirstOrDefault()?.Currency ?? master?.Currency ?? "INR", invoices, orders, receipts, amc, installs);
        }
        catch (Exception ex) when (IsConnectivity(ex))
        {
            logger.LogWarning(ex, "ERP records unavailable for customer {Customer}", customerId);
            return Empty(true, connector.Mode, ex.Message);
        }
    }

    /// <summary>One ERP record with the SupportDesk customer it belongs to and the tickets that reference it.</summary>
    public async Task<ErpRecordViewDto> RecordAsync(string recordType, Guid recordId, string? system, CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.TicketsView, Permissions.CustomersView);
        var sys = NormalizeSystem(system);
        var type = ErpRecordTypes.All.FirstOrDefault(t => t.Equals(recordType, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ValidationException(new Dictionary<string, string> { ["type"] = $"Unknown record type '{recordType}'." });
        var connector = await resolver.GetAsync(sys, ct);
        ErpTransaction? record;
        try { record = await connector.GetRecordAsync(type, recordId, ct); }
        catch (Exception ex) when (IsConnectivity(ex)) { throw new DomainException("integration_error", $"{sys} did not answer: {ex.Message}", 502); }
        if (record is null) throw new NotFoundException($"{sys} {type}", recordId);

        var customer = await db.Customers.AsNoTracking().VisibleTo(ctx).Where(c => c.ErpCustomerId == record.CustomerId).Select(c => new { c.Id, c.Name }).FirstOrDefaultAsync(ct);
        // Agents limited to their own accounts only see records of those customers.
        if (customer is null && CustomerAccess.IsRestricted(ctx)) throw new NotFoundException($"{sys} {type}", recordId);
        var prefix = (await settings.TicketsAsync(ct)).NumberPrefix;
        var refs = await db.TicketExternalReferences.AsNoTracking().Where(r => r.System == sys && r.RecordId == recordId).Select(r => new { r.TicketId, r.IsOrigin }).ToListAsync(ct);
        var ticketIds = refs.Select(r => r.TicketId).Distinct().ToList();
        var tickets = await db.Tickets.AsNoTracking().VisibleTo(ctx, db).Where(t => ticketIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Number, t.Subject, t.StatusId, t.CreatedAt }).ToListAsync(ct);
        var statuses = await db.TicketStatuses.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var linked = tickets.OrderByDescending(t => t.CreatedAt).Select(t => new ErpLinkedTicketDto(t.Id, $"{prefix}{t.Number}", t.Subject,
            statuses.GetValueOrDefault(t.StatusId) ?? "", refs.Any(r => r.TicketId == t.Id && r.IsOrigin), t.CreatedAt)).ToList();
        return new ErpRecordViewDto(record, customer?.Id, customer?.Name, linked);
    }

    public static bool IsConnectivity(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException ||
        (ex is DomainException d && d.Code is "integration_error" or "integration_unavailable");

    internal IErpCallLog CallLog => callLog;
}
