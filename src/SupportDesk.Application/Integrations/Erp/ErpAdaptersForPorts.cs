using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Erp;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Integrations.Erp;

/// <summary>
/// <see cref="IErpRecordUpdater"/> (cross-module port used by automation rules and workflows) implemented through the
/// tenant's ERP connector. Every update is audited and written to the IntegrationLog.
/// </summary>
public sealed class ErpRecordUpdater(ErpConnectorResolver resolver, IErpCallLog callLog, RequestContext ctx, IAuditWriter audit, DeskDbContext db,
    ILogger<ErpRecordUpdater> logger) : IErpRecordUpdater
{
    public async Task<string> UpdateRecordAsync(string system, string recordType, Guid? recordId, string? recordNumber, JsonElement changes, CancellationToken ct)
    {
        var sys = ErpIntegrationService.NormalizeSystem(system);
        if (changes.ValueKind != JsonValueKind.Object) throw new DomainException("validation_failed", "ERP record changes must be a JSON object.");
        if (recordId is null && string.IsNullOrWhiteSpace(recordNumber)) throw new DomainException("validation_failed", "Identify the ERP record by id or number.");
        var integration = await resolver.IntegrationAsync(sys, ct);
        if (integration is not { IsActive: true }) throw new DomainException("integration_unavailable", $"{sys} is not connected.");
        var connector = resolver.Create(integration);
        var sw = Stopwatch.StartNew();
        ErpUpdateResult result;
        try
        {
            result = await connector.UpdateRecordAsync(recordType, recordId, recordNumber, new ErpRecordUpdate(changes, "SupportDesk", ctx.CorrelationId), ct);
        }
        catch (Exception ex) when (ErpIntegrationService.IsConnectivity(ex))
        {
            logger.LogWarning(ex, "ERP record update failed ({System} {Type})", sys, recordType);
            await callLog.RecordAsync(new ErpCallRecord(ctx.TenantId, integration.Id, sys, MessageDirection.Outbound, "record.update", $"{recordType}/{recordId?.ToString() ?? recordNumber}",
                null, false, (int)sw.ElapsedMilliseconds, Redact(changes), ex.Message, ctx.CorrelationId), ct);
            throw new DomainException("integration_error", $"{sys} rejected the update: {ex.Message}", 502);
        }
        await callLog.RecordAsync(new ErpCallRecord(ctx.TenantId, integration.Id, sys, MessageDirection.Outbound, "record.update", $"{recordType}/{recordId?.ToString() ?? recordNumber}",
            result.Success ? 200 : 422, result.Success, (int)sw.ElapsedMilliseconds, Redact(changes), result.Message, ctx.CorrelationId), ct);
        audit.Write(AuditActions.IntegrationChanged, "ErpRecord", recordId, $"{sys} {recordType} {result.RecordNumber ?? recordNumber} updated: {result.Message}",
            new { fields = result.AppliedFields, result.Success });
        await db.SaveChangesAsync(ct);
        if (!result.Success) throw new DomainException("integration_error", result.Message, 422);
        return $"{sys} {recordType} {result.RecordNumber ?? recordNumber ?? recordId.ToString()} updated ({string.Join(", ", result.AppliedFields)}).";
    }

    private static string Redact(JsonElement changes)
    {
        var text = changes.GetRawText();
        return text.Length > 2000 ? text[..2000] + "…" : text;
    }
}

/// <summary>
/// Adds the customer's recent ERP transactions (latest invoices and orders) to the workbench context panel. Never
/// fails the panel: connectivity problems are logged and the section is simply omitted.
/// </summary>
public sealed class ErpTicketContextContributor(DeskDbContext db, RequestContext ctx, ErpConnectorResolver resolver, ILogger<ErpTicketContextContributor> logger)
    : ITicketContextContributor
{
    public async Task ContributeAsync(Ticket ticket, TicketContextBuilder context, CancellationToken ct)
    {
        if (ticket.CustomerId is not { } customerId || !ctx.Has(Permissions.CustomersView)) return;
        try
        {
            var erpId = await db.Customers.AsNoTracking().Where(c => c.Id == customerId).Select(c => c.ErpCustomerId).FirstOrDefaultAsync(ct);
            if (erpId is not { } id) return;
            var connector = await resolver.TryGetAsync(ErpSystems.WingsErp, ct);
            if (connector is null) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var invoices = await connector.GetInvoicesAsync(id, 5, timeout.Token);
            var orders = await connector.GetOrdersAsync(id, 3, timeout.Token);
            var present = context.RelatedTransactions.Where(r => r.RecordId is not null).Select(r => r.RecordId).ToHashSet();
            foreach (var t in invoices.Concat(orders).Where(t => !present.Contains(t.Id)))
                context.RelatedTransactions.Add(new ExternalReferenceDto(t.Id, t.System, t.RecordType, t.Id, t.Number, t.Url, false,
                    Json.ToElement(new
                    {
                        date = t.Date, dueDate = t.DueDate, status = t.Status, total = t.TotalAmount, balance = t.BalanceAmount, currency = t.Currency,
                        tax = t.TaxAmount, lines = t.Lines.Count, source = "erp"
                    })));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "ERP transactions unavailable for ticket {Ticket}", ticket.Id);
        }
    }
}
