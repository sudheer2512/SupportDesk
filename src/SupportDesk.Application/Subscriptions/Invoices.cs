using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Subscriptions;
using SupportDesk.Domain;
using SupportDesk.Domain.Platform;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Subscriptions;

/// <summary>
/// Issues invoices in the shared catalog. Subscription invoices are idempotent per (subscription, period): a filtered
/// unique index backs the check, so a retried or concurrent run never bills a period twice.
/// </summary>
public sealed class InvoiceIssuer(BillingOptions options, ILogger<InvoiceIssuer> logger)
{
    public async Task<BillingInvoice?> FindPeriodInvoiceAsync(DeskDbContext cdb, Subscription sub, DateTime periodStart, CancellationToken ct) =>
        await cdb.AllTenants<BillingInvoice>().FirstOrDefaultAsync(i => i.TenantId == sub.TenantId && i.SubscriptionId == sub.Id
            && i.Kind == InvoiceKind.Subscription && i.PeriodStart == periodStart && i.Status != InvoiceStatus.Void, ct);

    /// <summary>Issues the regular invoice for the subscription's current period unless one exists. Returns the (existing or new) invoice.</summary>
    public async Task<BillingInvoice> EnsurePeriodInvoiceAsync(DeskDbContext cdb, Tenant tenant, Subscription sub, Plan plan, long billableAgents, DateTime now,
        CancellationToken ct)
    {
        var existing = await FindPeriodInvoiceAsync(cdb, sub, sub.CurrentPeriodStart, ct);
        if (existing is not null) return existing;
        var seats = Math.Max(sub.Seats, billableAgents);
        var price = BillingMath.SeatPrice(plan, sub.BillingCycle);
        var lines = new List<InvoiceLineDto>
        {
            new($"{plan.Name} plan — {(sub.BillingCycle == BillingCycle.Annual ? "annual" : "monthly")} agent licences " +
                $"({sub.CurrentPeriodStart:dd MMM yyyy} – {sub.CurrentPeriodEnd.AddDays(-1):dd MMM yyyy})", seats, price, BillingMath.Money(price * seats))
        };
        if (billableAgents > sub.Seats)
            lines.Add(new InvoiceLineDto($"Includes {billableAgents - sub.Seats} agent(s) above the {sub.Seats} licensed seats", 0, 0, 0));
        var invoice = await IssueAsync(cdb, tenant, sub, plan.Currency, InvoiceKind.Subscription, sub.CurrentPeriodStart, sub.CurrentPeriodEnd, lines, null, now, ct);
        try
        {
            await cdb.SaveChangesAsync(ct);
            return invoice;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Another instance issued the same period invoice (or the number) concurrently: use theirs.
            cdb.Entry(invoice).State = EntityState.Detached;
            logger.LogInformation("Period invoice for subscription {Subscription} already issued concurrently", sub.Id);
            return await FindPeriodInvoiceAsync(cdb, sub, sub.CurrentPeriodStart, ct)
                   ?? throw new ConflictException("invoice_conflict", "The invoice could not be issued. Try again.");
        }
    }

    /// <summary>Adds an invoice (not saved). Totals and tax are computed from the lines; numbers are sequential per tenant.</summary>
    public async Task<BillingInvoice> IssueAsync(DeskDbContext cdb, Tenant tenant, Subscription sub, string currency, InvoiceKind kind, DateTime periodStart,
        DateTime periodEnd, IReadOnlyList<InvoiceLineDto> lines, string? notes, DateTime now, CancellationToken ct)
    {
        var subtotal = BillingMath.Money(lines.Sum(l => l.Amount));
        var tax = BillingMath.Money(subtotal * options.TaxRate);
        var count = await cdb.AllTenants<BillingInvoice>().CountAsync(i => i.TenantId == tenant.Id, ct);
        var invoice = new BillingInvoice
        {
            TenantId = tenant.Id, SubscriptionId = sub.Id, Kind = kind, Number = $"{options.InvoicePrefix}-{tenant.Slug.ToUpperInvariant()}-{count + 1:D4}",
            PeriodStart = periodStart, PeriodEnd = periodEnd, LinesJson = Json.Serialize(lines), Subtotal = subtotal, Tax = tax, Total = subtotal + tax,
            Currency = currency, Status = subtotal == 0 ? InvoiceStatus.Paid : InvoiceStatus.Open, IssuedAt = now, DueAt = now.AddDays(options.PaymentTermDays),
            PaidAt = subtotal == 0 ? now : null, PaymentProvider = sub.PaymentProvider, Notes = notes
        };
        cdb.Add(invoice);
        CatalogAudit.Write(cdb, tenant.Id, ActorType.System, null, "Billing", AuditActions.SubscriptionChanged, "BillingInvoice", invoice.Id,
            $"Invoice {invoice.Number} issued ({kind}, {invoice.Total.ToString("N2", CultureInfo.InvariantCulture)} {currency})", new { invoice.Number, kind, invoice.Total, periodStart, periodEnd }, now);
        return invoice;
    }
}

/// <summary>Writes tamper-evident audit rows directly into a (catalog) context with an explicit tenant (null = platform trail).</summary>
public static class CatalogAudit
{
    public static AuditLog Write(DeskDbContext db, Guid? tenantId, ActorType actorType, Guid? actorId, string? actorName, string action, string? entityType,
        Guid? entityId, string summary, object? changes, DateTime now, string? ip = null, string? correlationId = null)
    {
        var log = new AuditLog
        {
            TenantId = tenantId, OccurredAt = now, ActorType = actorType, ActorId = actorId, ActorName = actorName, Action = action, EntityType = entityType,
            EntityId = entityId, Summary = summary.Length > 1000 ? summary[..1000] : summary, ChangesJson = changes is null ? null : Json.Serialize(changes),
            IpAddress = ip, CorrelationId = correlationId
        };
        db.AuditLogs.Add(log);
        return log;
    }
}

/// <summary>Maps invoices to DTOs and renders the PDF.</summary>
public sealed class InvoicePresenter(PaymentProviderRegistry providers, BillingOptions options)
{
    public static InvoiceSummaryDto Summary(BillingInvoice i, string tenantName, DateTime now) => new(
        i.Id, i.TenantId, tenantName, i.Number, i.Kind.ToString(), i.Status.ToString(), i.PeriodStart, i.PeriodEnd, i.IssuedAt, i.DueAt, i.PaidAt,
        i.Total, i.Currency, i.Status == InvoiceStatus.Open && i.DueAt < now);

    public InvoiceDto Detail(BillingInvoice i, Tenant tenant, Subscription? sub, DateTime now) => new(
        Summary(i, tenant.Name, now), i.Subtotal, i.Tax, Json.DeserializeOr(i.LinesJson, new List<InvoiceLineDto>()), i.PaymentReference, i.PaymentProvider,
        i.Status == InvoiceStatus.Open ? providers.For(i.PaymentProvider ?? sub?.PaymentProvider).Instructions(i, tenant.Name) : "",
        i.Notes, new BillingContactDto(sub?.BillingContactName ?? tenant.Name, sub?.BillingContactEmail, sub?.BillingAddress, sub?.BillingTaxId));

    /// <summary>Renders an invoice as PDF (PDFsharp-MigraDoc).</summary>
    public byte[] RenderPdf(InvoiceDto invoice)
    {
        PdfFonts.EnsureConfigured();
        var doc = new Document();
        doc.Info.Title = $"Invoice {invoice.Summary.Number}";
        doc.Info.Author = options.IssuerName;
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = PdfFonts.Family;
        normal.Font.Size = 10;

        var section = doc.AddSection();
        section.PageSetup = doc.DefaultPageSetup.Clone();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.LeftMargin = Unit.FromCentimeter(2);
        section.PageSetup.RightMargin = Unit.FromCentimeter(2);

        var title = section.AddParagraph(options.IssuerName);
        title.Format.Font.Size = 16;
        title.Format.Font.Bold = true;
        if (!string.IsNullOrWhiteSpace(options.IssuerAddress)) section.AddParagraph(options.IssuerAddress);
        section.AddParagraph();

        var heading = section.AddParagraph($"INVOICE {invoice.Summary.Number}");
        heading.Format.Font.Size = 13;
        heading.Format.Font.Bold = true;
        heading.Format.SpaceAfter = Unit.FromPoint(6);

        var meta = section.AddTable();
        meta.AddColumn(Unit.FromCentimeter(8.5));
        meta.AddColumn(Unit.FromCentimeter(8.5));
        var row = meta.AddRow();
        var bill = row.Cells[0];
        bill.AddParagraph("Bill to").Format.Font.Bold = true;
        bill.AddParagraph(invoice.BillTo.Name ?? invoice.Summary.TenantName);
        if (!string.IsNullOrWhiteSpace(invoice.BillTo.Address)) bill.AddParagraph(invoice.BillTo.Address);
        if (!string.IsNullOrWhiteSpace(invoice.BillTo.Email)) bill.AddParagraph(invoice.BillTo.Email);
        if (!string.IsNullOrWhiteSpace(invoice.BillTo.TaxId)) bill.AddParagraph($"Tax id: {invoice.BillTo.TaxId}");
        var facts = row.Cells[1];
        facts.AddParagraph($"Issued: {invoice.Summary.IssuedAt:dd MMM yyyy}");
        facts.AddParagraph($"Due: {invoice.Summary.DueAt:dd MMM yyyy}");
        facts.AddParagraph($"Period: {invoice.Summary.PeriodStart:dd MMM yyyy} – {invoice.Summary.PeriodEnd:dd MMM yyyy}");
        facts.AddParagraph($"Status: {invoice.Summary.Status}{(invoice.Summary.PaidAt is { } p ? $" ({p:dd MMM yyyy})" : "")}");
        section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(10);

        var table = section.AddTable();
        table.Borders.Width = 0.5;
        table.Borders.Color = Colors.LightGray;
        table.AddColumn(Unit.FromCentimeter(9.5));
        table.AddColumn(Unit.FromCentimeter(2)).Format.Alignment = ParagraphAlignment.Right;
        table.AddColumn(Unit.FromCentimeter(2.75)).Format.Alignment = ParagraphAlignment.Right;
        table.AddColumn(Unit.FromCentimeter(2.75)).Format.Alignment = ParagraphAlignment.Right;
        var head = table.AddRow();
        head.HeadingFormat = true;
        head.Format.Font.Bold = true;
        head.Shading.Color = Colors.WhiteSmoke;
        head.Cells[0].AddParagraph("Description");
        head.Cells[1].AddParagraph("Qty");
        head.Cells[2].AddParagraph("Unit price");
        head.Cells[3].AddParagraph("Amount");
        foreach (var line in invoice.Lines)
        {
            var r = table.AddRow();
            r.Cells[0].AddParagraph(line.Description);
            r.Cells[1].AddParagraph(line.Quantity == 0 ? "" : line.Quantity.ToString("0.##", CultureInfo.InvariantCulture));
            r.Cells[2].AddParagraph(line.Quantity == 0 ? "" : Money(line.UnitPrice, invoice.Summary.Currency));
            r.Cells[3].AddParagraph(line.Quantity == 0 ? "" : Money(line.Amount, invoice.Summary.Currency));
        }
        void Total(string label, decimal amount, bool bold = false)
        {
            var r = table.AddRow();
            r.Cells[0].MergeRight = 2;
            r.Cells[0].AddParagraph(label).Format.Alignment = ParagraphAlignment.Right;
            var p = r.Cells[3].AddParagraph(Money(amount, invoice.Summary.Currency));
            if (bold) { r.Format.Font.Bold = true; p.Format.Font.Bold = true; }
        }
        Total("Subtotal", invoice.Subtotal);
        Total("Tax", invoice.Tax);
        Total("Total", invoice.Summary.Total, true);

        if (!string.IsNullOrWhiteSpace(invoice.PaymentInstructions))
        {
            section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(10);
            section.AddParagraph("How to pay").Format.Font.Bold = true;
            section.AddParagraph(invoice.PaymentInstructions);
        }
        if (!string.IsNullOrWhiteSpace(invoice.Notes))
        {
            section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(6);
            section.AddParagraph(invoice.Notes);
        }

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, false);
        return ms.ToArray();
    }

    private static string Money(decimal amount, string currency) => $"{amount.ToString("N2", CultureInfo.InvariantCulture)} {currency}";
}

/// <summary>
/// PDFsharp (Core build) needs a font resolver. This fallback resolver serves a sans-serif family from the operating
/// system's font folders (Arial on Windows, DejaVu/Liberation on Linux), so PDFs render without bundling fonts.
/// </summary>
public static class PdfFonts
{
    public const string Family = "Arial";
    private static readonly object Gate = new();
    private static bool _configured;

    public static void EnsureConfigured()
    {
        if (_configured) return;
        lock (Gate)
        {
            if (_configured) return;
            if (GlobalFontSettings.FontResolver is null && GlobalFontSettings.FallbackFontResolver is null)
                GlobalFontSettings.FallbackFontResolver = new SystemFontResolver();
            _configured = true;
        }
    }

    private sealed class SystemFontResolver : IFontResolver
    {
        private static readonly string[] Folders =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts"),
            "/usr/share/fonts/truetype/dejavu", "/usr/share/fonts/dejavu", "/usr/share/fonts/truetype/liberation", "/usr/share/fonts/liberation",
            "/usr/share/fonts/TTF", "/Library/Fonts", "/System/Library/Fonts/Supplemental"
        ];

        private static readonly (string Face, string[] Files)[] Faces =
        [
            ("regular", ["arial.ttf", "Arial.ttf", "DejaVuSans.ttf", "LiberationSans-Regular.ttf"]),
            ("bold", ["arialbd.ttf", "Arial Bold.ttf", "DejaVuSans-Bold.ttf", "LiberationSans-Bold.ttf"]),
            ("italic", ["ariali.ttf", "Arial Italic.ttf", "DejaVuSans-Oblique.ttf", "LiberationSans-Italic.ttf"]),
            ("bolditalic", ["arialbi.ttf", "Arial Bold Italic.ttf", "DejaVuSans-BoldOblique.ttf", "LiberationSans-BoldItalic.ttf"])
        ];

        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
            new(bold && italic ? "bolditalic" : bold ? "bold" : italic ? "italic" : "regular");

        public byte[]? GetFont(string faceName)
        {
            var files = Faces.FirstOrDefault(f => f.Face == faceName).Files ?? Faces[0].Files;
            foreach (var file in files.Concat(Faces[0].Files))
            foreach (var folder in Folders)
            {
                var path = Path.Combine(folder, file);
                if (File.Exists(path)) return File.ReadAllBytes(path);
            }
            throw new InvalidOperationException("No usable TrueType font found for PDF rendering (install Arial, DejaVu Sans or Liberation Sans).");
        }
    }
}

/// <summary>Keyset paging over invoices (newest first).</summary>
public static class InvoicePaging
{
    public static async Task<PagedResult<InvoiceSummaryDto>> PageAsync(IQueryable<BillingInvoice> query, Func<Guid, string> tenantName, string? cursor, int limit,
        DateTime now, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 200);
        if (DbExtensions.DecodeCursor(cursor) is { } c)
        {
            var at = DateTime.Parse(c.V, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            query = query.Where(i => i.IssuedAt < at || (i.IssuedAt == at && i.Id.CompareTo(c.Id) < 0));
        }
        var rows = await query.OrderByDescending(i => i.IssuedAt).ThenByDescending(i => i.Id).Take(limit + 1).AsNoTracking().ToListAsync(ct);
        var next = rows.Count > limit ? DbExtensions.EncodeCursor(rows[limit - 1].IssuedAt.ToString("O", CultureInfo.InvariantCulture), rows[limit - 1].Id) : null;
        return new PagedResult<InvoiceSummaryDto>(rows.Take(limit).Select(i => InvoicePresenter.Summary(i, tenantName(i.TenantId), now)).ToList(), next);
    }
}
