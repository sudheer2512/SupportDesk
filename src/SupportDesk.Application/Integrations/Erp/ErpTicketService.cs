using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Customers;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Erp;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Products;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Integrations.Erp;

/// <summary>
/// Creates tickets from ERP screens (POST /api/v1/integrations/erp/tickets). Resolves the customer by ERP id or code
/// (importing it from the ERP master when allowed), the contact by e-mail/phone (created when new) or the customer's
/// primary contact, the product/installation by product code, and keeps the originating ERP record (GUID, number,
/// screen context) as the ticket's origin reference. Idempotent with a client request id.
/// </summary>
public sealed class ErpTicketService(
    DeskDbContext db,
    RequestContext ctx,
    TicketService tickets,
    ContactResolver contacts,
    ContactService contactService,
    ErpConnectorResolver resolver,
    ErpSyncService sync,
    IErpCallLog callLog,
    TenantSettingsService settings,
    HtmlSanitization html,
    ILogger<ErpTicketService> logger)
{
    public async Task<(ErpTicketCreatedDto Ticket, bool Created)> CreateAsync(CreateErpTicketRequest r, CancellationToken ct = default)
    {
        // API clients need the tickets:write scope (tickets.create); agents need tickets.create.
        ctx.Require(Permissions.TicketsCreate);
        var sw = Stopwatch.StartNew();
        var system = ErpIntegrationService.NormalizeSystem(r.System);
        var errors = new Dictionary<string, string>();
        var recordType = ErpRecordTypes.All.FirstOrDefault(t => t.Equals(r.RecordType, StringComparison.OrdinalIgnoreCase)) ?? Parse.Clean(r.RecordType, 60);
        if (recordType is null) errors["recordType"] = "The originating record type is required (e.g. SalesInvoice).";
        if (r.RecordId is null && string.IsNullOrWhiteSpace(r.RecordNumber)) errors["recordId"] = "Send the record GUID and/or its number.";
        if (string.IsNullOrWhiteSpace(r.Subject)) errors["subject"] = "A subject is required.";
        if (r.CustomerErpId is null && string.IsNullOrWhiteSpace(r.CustomerCode)) errors["customerErpId"] = "Identify the customer by its ERP id or code.";
        if (r.Context is { } context && context.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined))
            errors["context"] = "The context must be a JSON object.";
        if (r.Context is { ValueKind: JsonValueKind.Object } big && big.GetRawText().Length > 16_000) errors["context"] = "The context is too large (max 16 KB).";
        if (errors.Count > 0) throw new ValidationException(errors);

        var clientRequestId = Parse.Clean(r.ClientRequestId, 200) is { } key ? $"erp:{system}:{key}" : null;
        if (clientRequestId is not null)
        {
            var existing = await db.TicketExternalReferences.AsNoTracking().Where(x => x.System == "client-request" && x.RecordNumber == clientRequestId)
                .Select(x => (Guid?)x.TicketId).FirstOrDefaultAsync(ct);
            if (existing is { } existingId) return (await MapAsync(existingId, ct), false);
        }

        var customer = await ResolveCustomerAsync(system, r.CustomerErpId, Parse.Clean(r.CustomerCode, 60), ct);
        Contact? contact = null;
        if (TextNormalization.NormalizeEmail(r.ContactEmail) is not null || TextNormalization.NormalizePhone(r.ContactPhone) is not null)
        {
            contact = await contacts.FindOrCreateAsync(r.ContactEmail, r.ContactPhone, Parse.Clean(r.ContactName, 250) ?? Parse.Clean(r.ErpUser, 250), customer.Id, Channel.Erp, ct);
            if (contact.CustomerId is null) contact.CustomerId = customer.Id;
            await db.SaveChangesAsync(ct);
        }
        contact ??= await db.Contacts.AsNoTracking().Where(k => k.CustomerId == customer.Id && k.Status == ContactStatus.Active)
            .OrderByDescending(k => k.IsPrimary).ThenBy(k => k.CreatedAt).FirstOrDefaultAsync(ct);

        Guid? productId = null, installationId = null;
        var productCode = Parse.Clean(r.ProductCode, 40) ?? (system == ErpSystems.WingsBooks ? "WBKS" : "WERP");
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Code == productCode && p.IsActive, ct);
        if (product is not null)
        {
            productId = product.Id;
            installationId = await db.Installations.AsNoTracking()
                .Where(i => i.CustomerId == customer.Id && i.ProductId == product.Id && i.Status == InstallationStatus.Active)
                .OrderBy(i => i.Environment).Select(i => (Guid?)i.Id).FirstOrDefaultAsync(ct);
        }

        var description = r.DescriptionHtml is { Length: > 0 } dh ? html.Sanitize(dh) : TextNormalization.TextToHtml(r.Description);
        var recordLabel = $"{recordType} {r.RecordNumber ?? r.RecordId?.ToString()}";
        description += $"<p><em>Raised from {System.Net.WebUtility.HtmlEncode(system)} — {System.Net.WebUtility.HtmlEncode(recordLabel)}" +
                       (r.ErpUser is { Length: > 0 } u ? $" by {System.Net.WebUtility.HtmlEncode(u)}" : "") + ".</em></p>";
        var contextElement = r.Context is { ValueKind: JsonValueKind.Object } ctxObj ? ctxObj : (JsonElement?)null;
        var request = new CreateTicketRequest
        {
            Subject = r.Subject.Trim(), DescriptionHtml = description, Channel = nameof(Channel.Erp), CustomerId = customer.Id, ContactId = contact?.Id,
            ProductId = productId, InstallationId = installationId, Priority = r.Priority, ClientRequestId = clientRequestId,
            Origin = new ExternalReferenceInput(system, recordType!, r.RecordId, Parse.Clean(r.RecordNumber, 100), Parse.Clean(r.RecordUrl, 1000), contextElement)
        };
        Ticket ticket;
        try
        {
            ticket = await tickets.CreateAsync(request, new TicketCreationSource(CreateInitialThread: true, AuthorName: contact?.FullName ?? r.ErpUser,
                FromAddress: contact?.Email, EnforceRequiredFields: false), ct);
        }
        catch (DbUpdateException) when (clientRequestId is not null)
        {
            // A concurrent retry with the same key won the race: return its ticket.
            db.ChangeTracker.Clear();
            var winner = await db.TicketExternalReferences.AsNoTracking().Where(x => x.System == "client-request" && x.RecordNumber == clientRequestId)
                .Select(x => x.TicketId).FirstAsync(ct);
            return (await MapAsync(winner, ct), false);
        }

        await callLog.RecordAsync(new ErpCallRecord(ctx.TenantId, (await resolver.IntegrationAsync(system, ct))?.Id, system, MessageDirection.Inbound, "ticket.create",
            "/api/v1/integrations/erp/tickets", 201, true, (int)sw.ElapsedMilliseconds,
            $"{recordLabel}; customer={customer.ErpCode ?? customer.Code}; by={ctx.ActorName}", $"ticket #{ticket.Number}", ctx.CorrelationId), ct);
        logger.LogInformation("Ticket {Number} created from {System} {RecordType}", ticket.Number, system, recordType);
        return (await MapAsync(ticket.Id, ct), true);
    }

    private async Task<Customer> ResolveCustomerAsync(string system, Guid? erpId, string? code, CancellationToken ct)
    {
        Customer? customer = null;
        if (erpId is { } id) customer = await db.Customers.FirstOrDefaultAsync(c => c.ErpCustomerId == id, ct);
        if (customer is null && code is not null)
            customer = await db.Customers.FirstOrDefaultAsync(c => c.ErpCode == code, ct) ?? await db.Customers.FirstOrDefaultAsync(c => c.Code == code, ct);
        if (customer is not null)
        {
            if (customer.ErpCustomerId is null && erpId is not null && !await db.Customers.AnyAsync(c => c.ErpCustomerId == erpId, ct))
            {
                customer.ErpCustomerId = erpId;
                await db.SaveChangesAsync(ct);
            }
            return customer;
        }

        // Unknown in the desk: import the master from the ERP when the integration allows it.
        var integration = await resolver.IntegrationAsync(system, ct);
        var erpSettings = integration is null ? null : Json.DeserializeOr(integration.SettingsJson, new ErpSettings());
        if (integration is { IsActive: true } && erpSettings is { CreateMissingCustomers: true })
        {
            try
            {
                var master = await resolver.Create(integration).GetCustomerAsync(erpId, code, ct);
                if (master is not null)
                {
                    var known = await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter])
                        .Where(c => c.ErpCustomerId == master.Id || c.ErpCode == master.Code || c.Code == master.Code).ToListAsync(ct);
                    var (imported, outcome) = await sync.UpsertCustomerCoreAsync(master, known, true, system, db.Clock.UtcNow, ct);
                    if (imported is not null && outcome != ErpSyncService.UpsertOutcome.Deleted)
                    {
                        await db.SaveChangesAsync(ct);
                        return imported;
                    }
                }
            }
            catch (Exception ex) when (ErpIntegrationService.IsConnectivity(ex))
            {
                logger.LogWarning(ex, "Could not import ERP customer {ErpId}/{Code}", erpId, code);
            }
        }
        throw new DomainException("unknown_customer",
            $"No customer matches ERP customer {(erpId?.ToString() ?? code)}. Sync the customer master from the ERP first.", 422);
    }

    private async Task<ErpTicketCreatedDto> MapAsync(Guid ticketId, CancellationToken ct)
    {
        var t = await db.Tickets.AsNoTracking().FirstAsync(x => x.Id == ticketId, ct);
        var status = await db.TicketStatuses.AsNoTracking().Where(s => s.Id == t.StatusId).Select(s => s.CustomerLabel ?? s.Name).FirstOrDefaultAsync(ct) ?? "";
        var customerName = t.CustomerId is null ? null : await db.Customers.AsNoTracking().Where(c => c.Id == t.CustomerId).Select(c => c.Name).FirstOrDefaultAsync(ct);
        var brand = await db.Brands.AsNoTracking().Where(b => b.Id == t.BrandId).Select(b => new { b.Slug, b.CustomDomain }).FirstOrDefaultAsync(ct)
                    ?? await db.Brands.AsNoTracking().OrderByDescending(b => b.IsDefault).Select(b => new { b.Slug, b.CustomDomain }).FirstOrDefaultAsync(ct);
        var prefix = (await settings.TicketsAsync(ct)).NumberPrefix;
        var portal = brand is null ? "" : contactService.PortalUrl(brand.Slug, brand.CustomDomain, $"tickets/{t.Id}");
        return new ErpTicketCreatedDto(t.Id, t.Number, $"{prefix}{t.Number}", status, t.EntitlementStatus.ToString(), t.CustomerId, customerName, t.ContactId, portal,
            $"/tickets/{t.Id}");
    }
}
