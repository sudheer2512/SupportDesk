using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Integrations.Erp;
using SupportDesk.Contracts.Erp;
using SupportDesk.Domain;
using SupportDesk.Persistence;

namespace SupportDesk.Integrations.Erp;

/// <summary>A customer the simulated ERP knows (from the desk's ERP-linked customers plus the simulator's own ERP-only accounts).</summary>
public sealed record SimCustomer(Guid Id, string Code, string Name, string Tier, string? Industry, string? Region, string City, string Domain, bool ErpOnly);

/// <summary>
/// The simulated ERP's customer master. It stands in for the ERP database: customers already linked to the ERP in the
/// desk (ErpCustomerId) plus two accounts that exist only in the ERP (so a first sync imports them).
/// </summary>
public sealed class SimulatorUniverse(DeskDbContext db)
{
    public static readonly (string Code, string Name, string Tier, string Industry, string Region, string City, string Domain)[] ErpOnlyCustomers =
    [
        ("C-1009", "Horizon Agro Exports Pvt Ltd", "Gold", "Agriculture", "West", "Nashik", "horizonagro.demo"),
        ("C-1010", "Vista Builders & Developers", "Silver", "Construction", "North", "Jaipur", "vistabuilders.demo")
    ];

    private IReadOnlyList<SimCustomer>? _customers;

    public async Task<IReadOnlyList<SimCustomer>> CustomersAsync(CancellationToken ct)
    {
        if (_customers is not null) return _customers;
        var rows = await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(c => c.ErpCustomerId != null)
            .Select(c => new { c.ErpCustomerId, c.ErpCode, c.Code, c.Name, c.Tier, c.Industry, c.ServiceRegion, c.DomainsJson, c.Id }).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var cities = await db.CustomerLocations.AsNoTracking().Where(l => ids.Contains(l.CustomerId) && l.City != null)
            .OrderByDescending(l => l.IsHeadOffice).Select(l => new { l.CustomerId, l.City }).ToListAsync(ct);
        var list = rows.Select(r =>
        {
            var code = r.ErpCode ?? r.Code ?? $"C-{Math.Abs(r.ErpCustomerId!.Value.GetHashCode()) % 9000 + 1000}";
            var domain = Json.StringList(r.DomainsJson).FirstOrDefault() ?? $"{Slug(r.Name)}.demo";
            return new SimCustomer(r.ErpCustomerId!.Value, code, r.Name, r.Tier, r.Industry, r.ServiceRegion, cities.FirstOrDefault(c => c.CustomerId == r.Id)?.City ?? "Bengaluru",
                domain, false);
        }).ToList();
        // The simulator's own accounts always come from its definition (their desk copies may have been edited).
        foreach (var e in ErpOnlyCustomers)
        {
            var id = DeterministicGuid.From("erp-customer", e.Code);
            list.RemoveAll(c => c.Id == id);
            list.Add(new SimCustomer(id, e.Code, e.Name, e.Tier, e.Industry, e.Region, e.City, e.Domain, true));
        }
        _customers = list.GroupBy(c => c.Id).Select(g => g.First()).OrderBy(c => c.Code, StringComparer.Ordinal).ToList();
        return _customers;
    }

    private static string Slug(string name) => new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).Take(20).ToArray());
}

/// <summary>
/// Deterministic Wings ERP / Wings Books simulator for development and demos. Everything is derived from the customer
/// master (ERP id, code, tier, city) and the integration's anchor date, so ids and numbers are stable across restarts
/// and syncs: branches, installations (production, POS, UAT), licences, current and previous AMC contracts, and a year
/// of sales orders, GST invoices (CGST+SGST within Karnataka, IGST otherwise; INR), receipts and deliveries. Record
/// updates are accepted for a small set of fields and kept in memory.
/// </summary>
public sealed class WingsErpSimulator(ErpConnection connection, SimulatorUniverse universe, IClock clock) : IErpConnector
{
    private const string SupplierState = "29"; // Karnataka (Wings Software, Bengaluru)
    private static readonly ConcurrentDictionary<(Guid Tenant, Guid Record), Dictionary<string, string>> Overlay = new();
    private readonly ConcurrentDictionary<Guid, List<ErpTransaction>> _documents = new();

    public string System => connection.System;
    public string Mode => ErpModes.Simulator;
    private bool IsBooks => connection.System == ErpSystems.WingsBooks;
    private DateOnly Anchor => connection.Settings.SimulatorAnchor ?? DateOnly.FromDateTime(clock.UtcNow);
    private DateOnly Today => DateOnly.FromDateTime(clock.UtcNow);
    private DateTime ModifiedAt => Anchor.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    public Task<ErpPingResult> PingAsync(CancellationToken ct) =>
        Task.FromResult(new ErpPingResult(true, System, IsBooks ? "Wings Books 5.0 (simulator)" : "Wings ERP 8.0.1 (simulator)", clock.UtcNow));

    // ───────────────────────────── Masters ─────────────────────────────

    public async Task<IReadOnlyList<ErpCustomerMaster>> GetCustomersAsync(DateTime? modifiedSince, CancellationToken ct)
    {
        if (modifiedSince > ModifiedAt) return [];
        return (await universe.CustomersAsync(ct)).Select(Master).ToList();
    }

    public async Task<ErpCustomerMaster?> GetCustomerAsync(Guid? id, string? code, CancellationToken ct)
    {
        var all = await universe.CustomersAsync(ct);
        var c = id is { } g ? all.FirstOrDefault(x => x.Id == g) : all.FirstOrDefault(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        return c is null ? null : Master(c);
    }

    private ErpCustomerMaster Master(SimCustomer c)
    {
        var n = Number(c);
        var state = StateCode(c.City);
        var pan = $"AABC{char.ToUpperInvariant(c.Name.FirstOrDefault(char.IsLetter, 'W'))}{n % 10000:0000}{(char)('A' + n % 26)}";
        return new ErpCustomerMaster(c.Id, c.Code, c.Name, c.Name.Replace("Pvt Ltd", "Private Limited", StringComparison.Ordinal), $"{state}{pan}1Z{n % 10}", pan,
            $"accounts@{c.Domain}", $"+91 44 4000 {n % 10000:0000}", $"https://www.{c.Domain}", c.Industry, c.Region, c.City, StateName(state), "India", true, false,
            c.Tier switch { "Platinum" => 5_000_000m, "Gold" => 2_500_000m, "Silver" => 1_000_000m, _ => 500_000m }, "INR",
            [
                new ErpBranch(DeterministicGuid.From("erp-branch", c.Code, "HO"), "HO", $"{c.City} – Head Office", $"{10 + n % 80}, Main Road", c.City, StateName(state),
                    $"{state}00{n % 100:00}", $"+91 44 4000 {n % 10000:0000}", true),
                new ErpBranch(DeterministicGuid.From("erp-branch", c.Code, "BR2"), "BR2", $"{c.City} – Branch 2", $"{100 + n % 200}, Industrial Estate", c.City,
                    StateName(state), $"{state}01{n % 100:00}", null, false)
            ],
            ModifiedAt);
    }

    // ───────────────────────────── Installed base ─────────────────────────────

    public async Task<IReadOnlyList<ErpInstallationRecord>> GetInstallationsAsync(Guid? customerId, DateTime? modifiedSince, CancellationToken ct)
    {
        if (IsBooks || modifiedSince > ModifiedAt) return [];
        return (await universe.CustomersAsync(ct)).Where(c => customerId is null || c.Id == customerId).SelectMany(Installations).ToList();
    }

    private IEnumerable<ErpInstallationRecord> Installations(SimCustomer c)
    {
        var n = Number(c);
        var first = c.Name.Split(' ')[0];
        var version = c.Tier is "Bronze" or "Standard" ? "7.2" : c.Tier == "Silver" ? "7.3" : "8.0";
        var build = c.Tier == "Platinum" ? "8.0.1" : $"{version}.0";
        var platinum = c.Tier == "Platinum";
        var installed = Anchor.AddYears(-2);
        string[] modules = c.Tier switch
        {
            "Platinum" => ["Finance", "Inventory", "Sales", "Purchase", "Payroll", "GST & Compliance", "Manufacturing"],
            "Gold" => ["Finance", "Inventory", "Sales", "Purchase", "Payroll", "GST & Compliance"],
            _ => ["Finance", "Inventory", "Sales", "GST & Compliance"]
        };
        var ho = DeterministicGuid.From("erp-branch", c.Code, "HO");
        var br2 = DeterministicGuid.From("erp-branch", c.Code, "BR2");
        yield return new ErpInstallationRecord(DeterministicGuid.From("erp-installation", c.Code), c.Id, ho, "WERP", version, build, $"{first} ERP – Production",
            $"INS-{c.Code[2..]}-01", "Production", platinum ? "Hosted" : "OnPremise", platinum ? "Windows Server 2022" : "Windows Server 2019",
            platinum ? "SQL Server 2022 Standard" : "SQL Server 2019 Express", platinum ? 120 : 35, installed, installed.AddMonths(2), "Active", modules, ModifiedAt);
        if (HasPos(c))
            yield return new ErpInstallationRecord(DeterministicGuid.From("erp-installation", c.Code, "pos"), c.Id, br2, "WPOS", "3.2", "3.2.0", $"{first} POS – Stores",
                $"INS-{c.Code[2..]}-02", "Production", "Hybrid", "Windows 11", "SQLite 3 + cloud sync", 40, installed.AddMonths(3), installed.AddMonths(4), "Active",
                ["Billing Counter", "Loyalty", "Store Sync"], ModifiedAt);
        if (platinum)
            yield return new ErpInstallationRecord(DeterministicGuid.From("erp-installation", c.Code, "uat"), c.Id, ho, "WERP", version, build, $"{first} ERP – UAT",
                $"INS-{c.Code[2..]}-03", "Staging", "Hosted", "Windows Server 2022", "SQL Server 2022 Developer", 10, Anchor.AddMonths(-9), null, "Active", modules,
                ModifiedAt);
        _ = n;
    }

    private static bool HasPos(SimCustomer c) => c.Industry is "Retail" or "FMCG" or "Healthcare";

    public async Task<IReadOnlyList<ErpLicenceRecord>> GetLicencesAsync(Guid? customerId, DateTime? modifiedSince, CancellationToken ct)
    {
        if (IsBooks || modifiedSince > ModifiedAt) return [];
        var today = Today;
        return (await universe.CustomersAsync(ct)).Where(c => customerId is null || c.Id == customerId).SelectMany(c => Installations(c).Select(i =>
        {
            var expires = c.Tier == "Bronze" ? Anchor.AddMonths(-1) : Anchor.AddMonths(10);
            var key = i.ProductCode == "WPOS" ? $"WPOS-****-****-{c.Code[2..]}" : i.Environment == "Staging" ? $"WERP-****-UAT-{c.Code[2..]}" : $"WERP-****-****-{c.Code[2..]}";
            return new ErpLicenceRecord(DeterministicGuid.From("erp-licence", i.Id), c.Id, i.Id, i.ProductCode, key,
                i.ProductCode == "WPOS" ? "Retail" : c.Tier == "Platinum" ? "Enterprise" : "Professional", i.UserCount ?? 10, Anchor.AddYears(-2), expires,
                expires < today ? "Expired" : "Active", ModifiedAt);
        })).ToList();
    }

    public async Task<IReadOnlyList<ErpAmcContractRecord>> GetAmcContractsAsync(Guid? customerId, DateTime? modifiedSince, CancellationToken ct)
    {
        if (IsBooks || modifiedSince > ModifiedAt) return [];
        return (await universe.CustomersAsync(ct)).Where(c => customerId is null || c.Id == customerId).SelectMany(Amc).ToList();
    }

    private IEnumerable<ErpAmcContractRecord> Amc(SimCustomer c)
    {
        var expired = c.Tier == "Bronze";
        var start = Anchor.AddMonths(expired ? -14 : -4);
        var end = Anchor.AddMonths(expired ? -2 : 8);
        var value = c.Tier switch { "Platinum" => 1_800_000m, "Gold" => 950_000m, "Silver" => 480_000m, _ => 240_000m };
        var products = HasPos(c) ? new[] { "WERP", "WPOS" } : ["WERP"];
        var today = Today;
        yield return new ErpAmcContractRecord(DeterministicGuid.From("erp-contract", c.Code), c.Id, $"AMC-{Anchor.Year}-{c.Code[2..]}",
            c.Tier == "Platinum" ? "Premium AMC (24x7)" : "Annual Maintenance Contract", "Amc", start, end, value, "INR", c.Tier is "Platinum" or "Gold",
            end < today ? "Expired" : "Active", $"SO/{Anchor.Year}/{c.Code[2..]}", products, c.Tier == "Platinum", ModifiedAt);
        // The previous year's contract (history).
        var prevEnd = start.AddDays(-1);
        yield return new ErpAmcContractRecord(DeterministicGuid.From("erp-contract", c.Code, "prev"), c.Id, $"AMC-{Anchor.Year - 1}-{c.Code[2..]}",
            "Annual Maintenance Contract (previous term)", "Amc", prevEnd.AddYears(-1).AddDays(1), prevEnd, Math.Round(value * 0.92m, 0), "INR", false, "Expired",
            $"SO/{Anchor.Year - 1}/{c.Code[2..]}", products, c.Tier == "Platinum", ModifiedAt);
    }

    // ───────────────────────────── Transactions ─────────────────────────────

    public async Task<IReadOnlyList<ErpTransaction>> GetTransactionsAsync(Guid customerId, string? recordType, DateOnly? from, int take, CancellationToken ct)
    {
        var c = (await universe.CustomersAsync(ct)).FirstOrDefault(x => x.Id == customerId);
        if (c is null) return [];
        return Documents(c).Where(d => (recordType is null || d.RecordType == recordType) && (from is null || d.Date >= from))
            .OrderByDescending(d => d.Date).ThenByDescending(d => d.Number, StringComparer.Ordinal).Take(Math.Clamp(take, 1, 500)).Select(ApplyOverlay).ToList();
    }

    public async Task<ErpTransaction?> GetRecordAsync(string recordType, Guid id, CancellationToken ct)
    {
        var customers = await universe.CustomersAsync(ct);
        if (recordType == ErpRecordTypes.AmcContract)
        {
            foreach (var c in customers)
                if (Amc(c).FirstOrDefault(a => a.Id == id) is { } a) return ApplyOverlay(AmcAsTransaction(c, a));
            return null;
        }
        foreach (var c in customers)
            if (Documents(c).FirstOrDefault(d => d.Id == id && d.RecordType == recordType) is { } doc) return ApplyOverlay(doc);
        return null;
    }

    private static readonly HashSet<string> UpdatableFields = new(StringComparer.OrdinalIgnoreCase) { "status", "remarks", "supportTicket", "followUpDate", "deliveryDate" };

    public async Task<ErpUpdateResult> UpdateRecordAsync(string recordType, Guid? id, string? number, ErpRecordUpdate update, CancellationToken ct)
    {
        var customers = await universe.CustomersAsync(ct);
        ErpTransaction? doc = null;
        foreach (var c in customers)
        {
            doc = Documents(c).FirstOrDefault(d => d.RecordType.Equals(recordType, StringComparison.OrdinalIgnoreCase) &&
                                                   ((id is not null && d.Id == id) || (number is not null && d.Number == number)));
            if (doc is not null) break;
        }
        if (doc is null) return new ErpUpdateResult(false, recordType, id, number, $"{recordType} {id?.ToString() ?? number} was not found.", []);
        if (update.Changes.ValueKind != JsonValueKind.Object) return new ErpUpdateResult(false, recordType, doc.Id, doc.Number, "Changes must be an object.", []);
        var applied = new List<string>();
        var rejected = new List<string>();
        var overlay = Overlay.GetOrAdd((connection.TenantId, doc.Id), _ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        lock (overlay)
        {
            foreach (var p in update.Changes.EnumerateObject())
            {
                if (!UpdatableFields.Contains(p.Name)) { rejected.Add(p.Name); continue; }
                var value = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
                overlay[p.Name] = value.Length > 200 ? value[..200] : value;
                applied.Add(p.Name);
            }
        }
        if (rejected.Count > 0 && applied.Count == 0)
            return new ErpUpdateResult(false, recordType, doc.Id, doc.Number, $"Field(s) {string.Join(", ", rejected)} cannot be updated through the API.", []);
        var message = rejected.Count == 0 ? "Updated." : $"Updated; ignored read-only field(s) {string.Join(", ", rejected)}.";
        return new ErpUpdateResult(true, recordType, doc.Id, doc.Number, message, applied);
    }

    private ErpTransaction ApplyOverlay(ErpTransaction t)
    {
        if (!Overlay.TryGetValue((connection.TenantId, t.Id), out var o)) return t;
        lock (o)
        {
            return t with
            {
                Status = o.TryGetValue("status", out var s) ? s : t.Status,
                Reference = o.TryGetValue("remarks", out var r) ? $"{t.Reference} — {r}".Trim(' ', '—') : o.TryGetValue("supportTicket", out var st) ? $"{t.Reference} (ticket {st})".Trim() : t.Reference
            };
        }
    }

    private ErpTransaction AmcAsTransaction(SimCustomer c, ErpAmcContractRecord a)
    {
        var line = Line(1, "WERP-AMC", $"{a.Name} ({a.StartDate:MMM yyyy} – {a.EndDate:MMM yyyy})", "998316", 1, "Year", a.Value, 0, StateCode(c.City));
        var (taxable, tax, total, breakup) = Totals([line], StateCode(c.City));
        return new ErpTransaction(System, ErpRecordTypes.AmcContract, a.Id, a.Number, c.Id, c.Code, c.Name, a.StartDate, a.EndDate, a.Status, taxable, tax, total, null,
            "INR", StateName(StateCode(c.City)), breakup, [line], a.SalesOrderNumber, null);
    }

    private List<ErpTransaction> Documents(SimCustomer c) => _documents.GetOrAdd(c.Id, _ => BuildDocuments(c));

    private List<ErpTransaction> BuildDocuments(SimCustomer c)
    {
        var docs = new List<ErpTransaction>();
        var n = Number(c);
        var state = StateCode(c.City);
        var today = Today;
        var series = IsBooks ? "WB/" : "";
        var discount = c.Tier == "Platinum" ? 0.05m : 0m;

        // AMC billing: one invoice at the start of each AMC term (Wings ERP only; Books mirrors it as its own invoice).
        foreach (var amc in Amc(c).Where(a => a.StartDate <= today && a.StartDate >= Anchor.AddDays(-420)))
        {
            var lines = new[] { Line(1, "WERP-AMC", $"Annual maintenance – Wings ERP ({amc.StartDate:MMM yyyy} – {amc.EndDate:MMM yyyy})", "998316", 1, "Year", amc.Value, 0, state) };
            AddInvoice(docs, c, state, $"AMC-{amc.Number}", amc.StartDate, lines, $"{amc.SalesOrderNumber}", today, series);
        }

        // Services and licences: an order and invoice every few weeks, most paid within a month.
        var interval = 24 + n % 11;
        var date = Anchor.AddDays(-400 + n % 17);
        var k = 0;
        while (date <= today && k < 40)
        {
            k++;
            var template = Templates[(n + k) % Templates.Length];
            var qty = template.Qty + (template.Unit == "Day" ? k % 4 : 0);
            var lines = new List<ErpTransactionLine> { Line(1, template.Item, template.Description, template.Sac, qty, template.Unit, template.Rate, discount, state) };
            if (k % 3 == 0) lines.Add(Line(2, "SRV-TRAVEL", "Travel & accommodation (at actuals)", "996311", 1, "Lot", 4500 + n % 5 * 500, 0, state));
            var orderDate = date.AddDays(-(3 + k % 5));
            var soNumber = $"{series}SO/{Fy(orderDate)}/{n % 1000:000}{k:00}";
            if (!IsBooks)
            {
                docs.Add(Document(ErpRecordTypes.SalesOrder, DeterministicGuid.From("erp-doc", c.Id, "SO", k), soNumber, c, state, orderDate, null, "Invoiced", lines, null, null));
                if (template.Item.StartsWith("WPOS", StringComparison.Ordinal) || template.Item == "WERP-ADDUSR")
                    docs.Add(Document(ErpRecordTypes.Delivery, DeterministicGuid.From("erp-doc", c.Id, "DC", k), $"DC/{Fy(orderDate)}/{n % 1000:000}{k:00}", c, state,
                        orderDate.AddDays(2), null, "Delivered", lines, soNumber, null));
            }
            AddInvoice(docs, c, state, $"{n % 1000:000}{k:00}", date, lines, soNumber, today, series);
            date = date.AddDays(interval + (k * 7 % 9) - 4);
        }

        // One recent open order awaiting delivery.
        if (!IsBooks)
        {
            var open = Templates[(n + k + 1) % Templates.Length];
            var openDate = today.AddDays(-(2 + n % 6));
            docs.Add(Document(ErpRecordTypes.SalesOrder, DeterministicGuid.From("erp-doc", c.Id, "SO", "open", openDate.DayNumber / 30), $"SO/{Fy(openDate)}/{n % 1000:000}9{openDate.DayNumber % 100:00}",
                c, state, openDate, openDate.AddDays(14), "Open", [Line(1, open.Item, open.Description, open.Sac, open.Qty, open.Unit, open.Rate, discount, state)], null, null));
        }
        return docs;
    }

    private void AddInvoice(List<ErpTransaction> docs, SimCustomer c, string state, string key, DateOnly date, IReadOnlyList<ErpTransactionLine> lines, string? reference,
        DateOnly today, string series)
    {
        var n = Number(c);
        var id = DeterministicGuid.From(IsBooks ? "books-doc" : "erp-doc", c.Id, "SI", key);
        var number = $"{series}{(IsBooks ? "INV" : "SI")}/{Fy(date)}/{Math.Abs(id.GetHashCode()) % 90000 + 10000:00000}";
        var due = date.AddDays(30);
        var payDays = 18 + (int)((uint)id.GetHashCode() % 14);
        var chronicLate = n % 3 == 0 && date.DayNumber % 5 == 0;
        var paid = !chronicLate && date.AddDays(payDays) <= today;
        var invoice = Document(ErpRecordTypes.SalesInvoice, id, number, c, state, date, due, "Posted", lines, reference, null);
        var status = paid ? "Paid" : due < today ? "Overdue" : "Posted";
        docs.Add(invoice with { Status = status, BalanceAmount = paid ? 0 : invoice.TotalAmount });
        if (paid)
        {
            var paidOn = date.AddDays(payDays);
            var receiptLine = new ErpTransactionLine(1, "RECEIPT", $"Payment against {number} (NEFT)", null, 1, "Nos", invoice.TotalAmount, 0, invoice.TotalAmount, 0, 0,
                invoice.TotalAmount);
            docs.Add(new ErpTransaction(System, ErpRecordTypes.Receipt, DeterministicGuid.From(IsBooks ? "books-doc" : "erp-doc", c.Id, "RC", key),
                $"{series}RCPT/{Fy(paidOn)}/{Math.Abs(id.GetHashCode()) % 90000 + 10000:00000}", c.Id, c.Code, c.Name, paidOn, null, "Cleared", invoice.TotalAmount, 0,
                invoice.TotalAmount, null, "INR", null, null, [receiptLine], number, null));
        }
    }

    private ErpTransaction Document(string type, Guid id, string number, SimCustomer c, string state, DateOnly date, DateOnly? due, string status,
        IReadOnlyList<ErpTransactionLine> lines, string? reference, decimal? balance)
    {
        var (taxable, tax, total, breakup) = Totals(lines, state);
        return new ErpTransaction(System, type, id, number, c.Id, c.Code, c.Name, date, due, status, taxable, tax, total, balance, "INR", StateName(state), breakup, lines,
            reference, null);
    }

    private static ErpTransactionLine Line(int no, string item, string description, string? sac, decimal qty, string unit, decimal rate, decimal discountRate, string state)
    {
        var gross = qty * rate;
        var discount = Math.Round(gross * discountRate, 2);
        var taxable = gross - discount;
        var tax = Math.Round(taxable * 0.18m, 2);
        return new ErpTransactionLine(no, item, description, sac, qty, unit, rate, discount, taxable, 18m, tax, taxable + tax);
    }

    private static (decimal Taxable, decimal Tax, decimal Total, ErpTaxBreakup Breakup) Totals(IReadOnlyList<ErpTransactionLine> lines, string state)
    {
        var taxable = lines.Sum(l => l.TaxableValue);
        var tax = lines.Sum(l => l.TaxAmount);
        var intra = state == SupplierState;
        var half = Math.Round(tax / 2, 2);
        var breakup = intra ? new ErpTaxBreakup(half, tax - half, 0, 0) : new ErpTaxBreakup(0, 0, tax, 0);
        return (taxable, tax, taxable + tax, breakup);
    }

    private static readonly (string Item, string Description, string Sac, decimal Qty, string Unit, decimal Rate)[] Templates =
    [
        ("WERP-ADDUSR", "Additional user licences – Wings ERP", "997331", 5, "Nos", 12_000),
        ("SRV-IMPL", "Implementation services", "998313", 2, "Day", 18_000),
        ("SRV-TRN", "On-site training session", "999293", 1, "Session", 25_000),
        ("SRV-CUST", "Customisation – report pack", "998314", 1, "Nos", 45_000),
        ("WPOS-TERM", "Wings POS terminal licence", "997331", 2, "Nos", 15_000),
        ("SRV-HC", "Database health check & performance tuning", "998313", 1, "Nos", 30_000)
    ];

    // ───────────────────────────── Helpers ─────────────────────────────

    private static int Number(SimCustomer c)
    {
        var digits = new string(c.Code.Where(char.IsDigit).ToArray());
        return digits.Length > 0 && int.TryParse(digits.Length > 6 ? digits[^6..] : digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : Math.Abs(BitConverter.ToInt32(c.Id.ToByteArray(), 0) % 10000);
    }

    /// <summary>Indian financial year label (April–March), e.g. "26-27".</summary>
    private static string Fy(DateOnly d)
    {
        var start = d.Month >= 4 ? d.Year : d.Year - 1;
        return $"{start % 100:00}-{(start + 1) % 100:00}";
    }

    private static string StateCode(string? city) => city switch
    {
        "Chennai" or "Coimbatore" => "33",
        "Pune" or "Nashik" or "Mumbai" => "27",
        "Gurugram" => "06",
        "Bengaluru" or "Mysuru" => "29",
        "Kochi" => "32",
        "Ahmedabad" => "24",
        "Kolkata" => "19",
        "Jaipur" => "08",
        "Hyderabad" => "36",
        "Delhi" or "New Delhi" => "07",
        _ => "29"
    };

    private static string StateName(string code) => code switch
    {
        "33" => "Tamil Nadu", "27" => "Maharashtra", "06" => "Haryana", "29" => "Karnataka", "32" => "Kerala", "24" => "Gujarat", "19" => "West Bengal",
        "08" => "Rajasthan", "36" => "Telangana", "07" => "Delhi", _ => "Karnataka"
    };
}
