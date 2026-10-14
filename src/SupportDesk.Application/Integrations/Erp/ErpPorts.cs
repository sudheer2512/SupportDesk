using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Common;
using SupportDesk.Contracts.Erp;
using SupportDesk.Domain;
using SupportDesk.Domain.Integration;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Integrations.Erp;

/// <summary>
/// Port to an ERP back office (Wings ERP, Wings Books): customer master, installations, licences, AMC contracts,
/// transactions (invoices, orders, receipts…) and record updates. Implementations talk to the ERP over its API
/// (HTTP/JSON) or simulate it — never by reading the ERP database.
/// </summary>
public interface IErpConnector
{
    string System { get; }
    /// <summary>simulator | http</summary>
    string Mode { get; }
    Task<ErpPingResult> PingAsync(CancellationToken ct);
    Task<IReadOnlyList<ErpCustomerMaster>> GetCustomersAsync(DateTime? modifiedSince, CancellationToken ct);
    Task<ErpCustomerMaster?> GetCustomerAsync(Guid? id, string? code, CancellationToken ct);
    Task<IReadOnlyList<ErpInstallationRecord>> GetInstallationsAsync(Guid? customerId, DateTime? modifiedSince, CancellationToken ct);
    Task<IReadOnlyList<ErpLicenceRecord>> GetLicencesAsync(Guid? customerId, DateTime? modifiedSince, CancellationToken ct);
    Task<IReadOnlyList<ErpAmcContractRecord>> GetAmcContractsAsync(Guid? customerId, DateTime? modifiedSince, CancellationToken ct);
    /// <summary>A customer's documents, newest first; <paramref name="recordType"/> null = all types.</summary>
    Task<IReadOnlyList<ErpTransaction>> GetTransactionsAsync(Guid customerId, string? recordType, DateOnly? from, int take, CancellationToken ct);
    Task<ErpTransaction?> GetRecordAsync(string recordType, Guid id, CancellationToken ct);
    Task<ErpUpdateResult> UpdateRecordAsync(string recordType, Guid? id, string? number, ErpRecordUpdate update, CancellationToken ct);
}

public static class ErpConnectorExtensions
{
    public static Task<IReadOnlyList<ErpTransaction>> GetInvoicesAsync(this IErpConnector c, Guid customerId, int take, CancellationToken ct) =>
        c.GetTransactionsAsync(customerId, ErpRecordTypes.SalesInvoice, null, take, ct);

    public static Task<IReadOnlyList<ErpTransaction>> GetOrdersAsync(this IErpConnector c, Guid customerId, int take, CancellationToken ct) =>
        c.GetTransactionsAsync(customerId, ErpRecordTypes.SalesOrder, null, take, ct);
}

/// <summary>Non-secret connection options stored in Integration.SettingsJson.</summary>
public sealed record ErpSettings
{
    public string Mode { get; init; } = ErpModes.Simulator;
    public string AuthType { get; init; } = "OAuth2";
    public string? TokenUrl { get; init; }
    public string? ClientId { get; init; }
    public string? Scope { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
    public int SyncIntervalMinutes { get; init; } = 60;
    public bool SyncCustomers { get; init; } = true;
    public bool SyncInstallations { get; init; } = true;
    public bool SyncLicences { get; init; } = true;
    public bool SyncContracts { get; init; } = true;
    public bool CreateMissingCustomers { get; init; } = true;
    /// <summary>Simulator only: the "today" its generated history is anchored to (stable across restarts).</summary>
    public DateOnly? SimulatorAnchor { get; init; }
}

public static class ErpModes
{
    public const string Simulator = "simulator";
    public const string Http = "http";
}

/// <summary>Secrets stored encrypted in Integration.SecretsProtected.</summary>
public sealed record ErpSecrets(string? ClientSecret, string? ApiKey);

/// <summary>Everything an adapter needs to talk to one configured ERP for one tenant.</summary>
public sealed record ErpConnection(Guid TenantId, Guid IntegrationId, string System, string? BaseUrl, ErpSettings Settings, ErpSecrets Secrets);

/// <summary>Creates connectors for a configured integration (implemented by the Integrations project: HTTP client and simulator).</summary>
public interface IErpConnectorFactory
{
    IErpConnector Create(ErpConnection connection);
}

public sealed class NullErpConnectorFactory : IErpConnectorFactory
{
    public IErpConnector Create(ErpConnection connection) =>
        throw new DomainException("integration_unavailable", $"No {connection.System} adapter is installed.");
}

/// <summary>One logged ERP call (request/response excerpts must already be redacted and short).</summary>
public sealed record ErpCallRecord(
    Guid TenantId,
    Guid? IntegrationId,
    string System,
    MessageDirection Direction,
    string Operation,
    string? Url,
    int? StatusCode,
    bool Success,
    int DurationMs,
    string? RequestExcerpt,
    string? ResponseExcerpt,
    string? CorrelationId);

/// <summary>Writes IntegrationLog rows in their own scope so logs survive a rolled-back business transaction.</summary>
public interface IErpCallLog
{
    Task RecordAsync(ErpCallRecord record, CancellationToken ct = default);
}

public sealed class ErpCallLog(IServiceScopeFactory scopes) : IErpCallLog
{
    public const int MaxExcerpt = 4000;

    public async Task RecordAsync(ErpCallRecord r, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<RequestContext>().SetSystem(r.TenantId, "ERP integration", ActorType.Integration);
        var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
        db.IntegrationLogs.Add(new IntegrationLog
        {
            IntegrationId = r.IntegrationId, System = r.System, Direction = r.Direction, Operation = Trim(r.Operation, 200)!, Url = Trim(r.Url, 450),
            StatusCode = r.StatusCode, Success = r.Success, DurationMs = r.DurationMs, RequestExcerpt = Trim(r.RequestExcerpt, MaxExcerpt),
            ResponseExcerpt = Trim(r.ResponseExcerpt, MaxExcerpt), CorrelationId = Trim(r.CorrelationId, 100), CreatedAt = db.Clock.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    private static string? Trim(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..(max - 1)] + "…";
}

/// <summary>Loads the tenant's configured ERP integration and builds a connector for it.</summary>
public sealed class ErpConnectorResolver(DeskDbContext db, RequestContext ctx, IErpConnectorFactory factory, Platform.ISecretProtector secrets)
{
    public Task<Integration?> IntegrationAsync(string system, CancellationToken ct) =>
        db.Integrations.Where(i => i.Type == system).OrderByDescending(i => i.IsActive).ThenBy(i => i.CreatedAt).FirstOrDefaultAsync(ct);

    /// <summary>The connector for an active integration of <paramref name="system"/>, or null when none is configured/active.</summary>
    public async Task<IErpConnector?> TryGetAsync(string system, CancellationToken ct)
    {
        var integration = await IntegrationAsync(system, ct);
        return integration is { IsActive: true } ? Create(integration) : null;
    }

    public async Task<IErpConnector> GetAsync(string system, CancellationToken ct) =>
        await TryGetAsync(system, ct) ?? throw new DomainException("integration_unavailable", $"{system} is not connected. Configure it under Products & Assets → ERP integration.");

    public IErpConnector Create(Integration integration)
    {
        var settings = Json.DeserializeOr(integration.SettingsJson, new ErpSettings());
        if (settings.SimulatorAnchor is null) settings = settings with { SimulatorAnchor = DateOnly.FromDateTime(integration.CreatedAt == default ? db.Clock.UtcNow : integration.CreatedAt) };
        var secret = string.IsNullOrEmpty(integration.SecretsProtected)
            ? new ErpSecrets(null, null)
            : Json.DeserializeOr(secrets.Unprotect(integration.SecretsProtected), new ErpSecrets(null, null));
        return factory.Create(new ErpConnection(ctx.TenantId, integration.Id, integration.Type, integration.BaseUrl, settings, secret));
    }
}
