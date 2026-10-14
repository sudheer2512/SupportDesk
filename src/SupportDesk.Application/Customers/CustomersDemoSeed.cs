using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Integrations.Erp;
using SupportDesk.Application.Seed;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Erp;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Integration;
using SupportDesk.Domain.Products;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Service;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Customers;

/// <summary>
/// Demo data for the customers / products / contracts / ERP module, on top of the core seed: an "Account Manager" role
/// with account-managed customer scope for Vikram, customer departments, a subsidiary (hierarchy roll-up), customer
/// assets with warranties, a warranty contract, service activities across the calendar, an obvious duplicate contact
/// pair (with a ticket) to demonstrate merging, a Wings ERP integration in simulator mode with its initial sync (which
/// also imports two ERP-only customers) and a ticket raised "from ERP" for an ABC Retail sales invoice.
/// </summary>
public sealed class CustomersDemoSeedContributor(ILogger<CustomersDemoSeedContributor> logger) : IDemoSeedContributor
{
    public int Order => 20;

    public async Task SeedAsync(DemoSeedContext c, CancellationToken ct)
    {
        c.At(c.Anchor.AddDays(-3));
        await SeedOrganisationDataAsync(c, ct);
        await SeedActivitiesAsync(c, ct);
        await SeedDuplicateAsync(c, ct);
        c.At(c.Anchor.AddDays(-1).AddHours(-2));
        await SeedErpAsync(c, ct);
        c.Clock.Unpin();
    }

    private static async Task SeedOrganisationDataAsync(DemoSeedContext c, CancellationToken ct)
    {
        await c.AsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();

            // Account managers see only their own accounts (customer scope "account-managed").
            var agentRole = await db.Roles.FirstAsync(r => r.Name == "Agent", ct);
            var agentPerms = await db.RolePermissions.Where(p => p.RoleId == agentRole.Id).Select(p => p.PermissionCode).ToListAsync(ct);
            var amRole = new Role
            {
                Name = "Account Manager", Description = "Customer success: works their own accounts (customer scope limited to managed customers)",
                TicketScope = TicketScope.Departments, CustomerScope = CustomerScope.AccountManaged
            };
            db.Roles.Add(amRole);
            foreach (var p in agentPerms.Append(Permissions.ContractsView).Distinct()) db.RolePermissions.Add(new RolePermission { RoleId = amRole.Id, PermissionCode = p });
            var vikram = c.Users["vikram"];
            await db.UserRoles.Where(ur => ur.UserId == vikram).ExecuteDeleteAsync(ct);
            db.UserRoles.Add(new UserRole { UserId = vikram, RoleId = amRole.Id });
            // Priya (supervisor) co-manages the West region accounts, so Vikram's book is visibly smaller.
            await db.Customers.Where(x => x.Id == c.Customers["sunrise"] || x.Id == c.Customers["zenith"])
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.AccountManagerUserId, c.Users["priya"]), ct);

            // Customer departments and contact assignment.
            var departments = new Dictionary<string, string[]>
            {
                ["abc"] = ["Finance & Accounts", "Store Operations", "IT"],
                ["sunrise"] = ["IT", "Warehouse", "Finance"],
                ["coastal"] = ["Administration", "Pharmacy", "Billing"],
                ["greenfield"] = ["Finance", "Plant Operations"],
                ["metro"] = ["Operations", "Billing"]
            };
            var created = new Dictionary<string, Guid>();
            foreach (var (customer, names) in departments)
                foreach (var name in names)
                {
                    var d = new CustomerDepartment { CustomerId = c.Customers[customer], Name = name };
                    db.CustomerDepartments.Add(d);
                    created[$"{customer}:{name}"] = d.Id;
                }
            await db.SaveChangesAsync(ct);
            async Task Assign(string contact, string key) =>
                await db.Contacts.Where(k => k.Id == c.Contacts[contact]).ExecuteUpdateAsync(u => u.SetProperty(k => k.CustomerDepartmentId, created[key]), ct);
            await Assign("ramesh", "abc:Finance & Accounts");
            await Assign("joseph", "abc:Finance & Accounts");
            await Assign("lata", "abc:Store Operations");
            await Assign("anita", "sunrise:IT");
            await Assign("suresh", "sunrise:Warehouse");
            await Assign("thomas", "coastal:Administration");
            await Assign("rekha", "coastal:Pharmacy");
            await Assign("kavya", "greenfield:Finance");
            await Assign("manoj", "greenfield:Plant Operations");
            await Assign("harpreet", "metro:Operations");
            await Assign("imran", "metro:Billing");

            // A subsidiary (hierarchy roll-up in Customer 360).
            var abc = await db.Customers.AsNoTracking().FirstAsync(x => x.Id == c.Customers["abc"], ct);
            var franchise = new Customer
            {
                Name = "ABC Retail Franchise – Kerala", Code = "C-1001-K", ParentCustomerId = abc.Id, CustomerGroupId = abc.CustomerGroupId, Tier = "Gold",
                Industry = "Retail", ServiceRegion = "South", AccountManagerUserId = c.Users["vikram"], TimeZone = "Asia/Kolkata", Category = "Franchise",
                DomainsJson = Json.Serialize(new[] { "abcretail-kerala.demo" }), Email = "accounts@abcretail-kerala.demo", Phone = "+91 484 400 1001",
                CommunicationPrefsJson = Json.Serialize(new { email = true, phone = true, sms = false, whatsApp = true, preferredChannel = "WhatsApp" })
            };
            db.Customers.Add(franchise);
            c.Customers["abc-kerala"] = franchise.Id;
            var kochi = new CustomerLocation { CustomerId = franchise.Id, Name = "Kochi – Lulu Mall store", Code = "KOC1", City = "Kochi", State = "Kerala", Country = "India", IsHeadOffice = true, ServiceRegion = "South" };
            db.CustomerLocations.Add(kochi);
            var franchiseContact = new Contact
            {
                CustomerId = franchise.Id, LocationId = kochi.Id, FullName = "Aisha Rahman", FirstName = "Aisha", LastName = "Rahman", Email = "aisha@abcretail-kerala.demo",
                EmailNormalized = "aisha@abcretail-kerala.demo", Phone = "+91 98470 11001", PhoneNormalized = TextNormalization.NormalizePhone("+91 98470 11001"),
                JobTitle = "Franchise Manager", IsPrimary = true, PortalAccess = PortalAccessScope.Organisation, PreferredChannel = Channel.WhatsApp
            };
            db.Contacts.Add(franchiseContact);
            db.ContactIdentities.Add(new ContactIdentity { ContactId = franchiseContact.Id, Channel = Channel.Email, Identifier = franchiseContact.EmailNormalized!, CreatedAt = c.Anchor.AddDays(-60) });
            c.Contacts["aisha"] = franchiseContact.Id;
            db.Installations.Add(new Installation
            {
                CustomerId = franchise.Id, LocationId = kochi.Id, ProductId = c.Products["pos"], VersionId = c.Versions["pos:3.1"], Name = "Kerala POS – Stores",
                InstallationCode = "INS-1001K-01", DeploymentMode = DeploymentMode.Hybrid, OperatingSystem = "Windows 10", DatabaseVersion = "SQLite 3 + cloud sync",
                UserCount = 12, InstalledOn = DateOnly.FromDateTime(c.Anchor.AddMonths(-7)), GoLiveOn = DateOnly.FromDateTime(c.Anchor.AddMonths(-6))
            });

            // Customer assets (POS hardware with warranties) and a hardware warranty contract.
            var today = DateOnly.FromDateTime(c.Anchor);
            var assets = new List<CustomerAsset>();
            void Asset(string customer, string name, string serial, int purchasedMonthsAgo, int warrantyMonths, string? installation = null)
            {
                var purchased = today.AddMonths(-purchasedMonthsAgo);
                var a = new CustomerAsset
                {
                    CustomerId = c.Customers[customer], ProductId = c.Products["pos"], InstallationId = installation is null ? null : c.Installations[installation],
                    Name = name, SerialNumber = serial, AssetTag = $"AT-{serial[^4..]}", PurchaseDate = purchased, WarrantyEndsOn = purchased.AddMonths(warrantyMonths),
                    Status = AssetStatus.InService
                };
                db.CustomerAssets.Add(a);
                assets.Add(a);
            }
            Asset("abc", "POS terminal – Billing counter 1", "WPT-24-0001", 14, 24, "abc:pos");
            Asset("abc", "POS terminal – Billing counter 2", "WPT-24-0002", 14, 24, "abc:pos");
            Asset("abc", "Thermal receipt printer – Counter 1", "TRP-23-1187", 30, 24, "abc:pos");
            Asset("greenfield", "POS terminal – Factory outlet", "WPT-24-0317", 8, 24, "greenfield:pos");
            Asset("coastal", "POS terminal – Pharmacy counter", "WPT-23-0912", 26, 24, "coastal:pos");
            Asset("coastal", "Barcode scanner – Pharmacy", "BCS-24-4471", 10, 12, "coastal:pos");
            var warranty = new Contract
            {
                CustomerId = c.Customers["abc"], Number = $"WAR-{c.Anchor.Year}-1001", Name = "POS hardware warranty", Type = ContractType.Warranty,
                Status = ContractStatus.Active, StartDate = today.AddMonths(-14), EndDate = today.AddMonths(10), IsBillable = false, Currency = "INR",
                CoverageJson = Json.Serialize(new { channels = new[] { "Email", "Phone", "Portal" }, onsite = true, hardware = true })
            };
            db.Contracts.Add(warranty);
            foreach (var a in assets.Where(a => a.CustomerId == c.Customers["abc"] && a.WarrantyEndsOn >= today))
                db.Entitlements.Add(new Entitlement { ContractId = warranty.Id, CustomerId = warranty.CustomerId, Name = $"Warranty – {a.Name}", Type = EntitlementType.Warranty,
                    AssetId = a.Id, ProductId = c.Products["pos"], StartDate = warranty.StartDate, EndDate = a.WarrantyEndsOn!.Value });
            // Incident-based support pack for Metro Logistics (shows allowance consumption).
            var pack = new Contract
            {
                CustomerId = c.Customers["metro"], Number = $"ASC-{c.Anchor.Year}-1003", Name = "Priority incident pack (25 incidents)", Type = ContractType.AnnualSupport,
                Status = ContractStatus.Active, StartDate = today.AddMonths(-2), EndDate = today.AddMonths(10), IsBillable = true, ContractValue = 125_000m, Currency = "INR",
                CoverageJson = Json.Serialize(new { channels = new[] { "Email", "Phone" }, maxSeverity = "Critical" })
            };
            db.Contracts.Add(pack);
            db.Entitlements.Add(new Entitlement { ContractId = pack.Id, CustomerId = pack.CustomerId, Name = "Priority incidents", Type = EntitlementType.Incidents,
                IncidentsAllowed = 25, IncidentsUsed = 9, StartDate = pack.StartDate, EndDate = pack.EndDate });
            await db.SaveChangesAsync(ct);
        });
    }

    private static async Task SeedActivitiesAsync(DemoSeedContext c, CancellationToken ct)
    {
        await c.AsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var locations = await db.CustomerLocations.AsNoTracking().Where(l => l.IsHeadOffice).ToDictionaryAsync(l => l.CustomerId, l => l.Id, ct);
            var day = c.Anchor.Date;
            void Add(string customer, ServiceActivityType type, string title, double daysFromNow, int hourIst, int hours, string agent, ServiceActivityStatus status,
                string? outcome = null, string? installation = null, string? description = null)
            {
                var start = day.AddDays(daysFromNow).AddHours(hourIst - 5.5);
                db.ServiceActivities.Add(new ServiceActivity
                {
                    CustomerId = c.Customers[customer], LocationId = locations.GetValueOrDefault(c.Customers[customer]), Type = type, Title = title, Description = description,
                    ScheduledStart = start, ScheduledEnd = start.AddHours(hours), AssignedUserId = c.Users[agent], Status = status, Outcome = outcome,
                    InstallationId = installation is null ? null : c.Installations[installation], ContractId = c.Contracts.GetValueOrDefault(customer) is var k && k != Guid.Empty ? k : null
                });
            }
            Add("abc", ServiceActivityType.OnsiteVisit, "Quarterly business review & roadmap walkthrough", -24, 11, 2, "vikram", ServiceActivityStatus.Completed,
                "Reviewed open items; agreed to pilot the 8.0 loyalty features in two stores.");
            Add("abc", ServiceActivityType.Upgrade, "Wings POS 3.2 rollout – stores 5 to 12", 4, 22, 4, "karthik", ServiceActivityStatus.Scheduled, installation: "abc:pos",
                description: "Night rollout after store closing; rollback plan agreed with Lata.");
            Add("abc", ServiceActivityType.Training, "GST e-invoicing refresher for the accounts team", 9, 15, 2, "meera", ServiceActivityStatus.Scheduled);
            Add("coastal", ServiceActivityType.PreventiveMaintenance, "Database index maintenance & backup verification", -6, 20, 3, "karthik",
                ServiceActivityStatus.Completed, "Rebuilt fragmented indexes; backup restore test passed (42 min).", "coastal");
            Add("coastal", ServiceActivityType.RemoteSession, "Pharmacy POS freeze – log collection", 1, 10, 1, "meera", ServiceActivityStatus.Scheduled, installation: "coastal:pos");
            Add("sunrise", ServiceActivityType.OnsiteVisit, "Nashik warehouse go-live support", 6, 9, 8, "rahul", ServiceActivityStatus.Scheduled, installation: "sunrise");
            Add("greenfield", ServiceActivityType.Meeting, "Payroll configuration sign-off with CFO", -2, 16, 1, "priya", ServiceActivityStatus.Completed, "PF rule change signed off.");
            Add("metro", ServiceActivityType.Training, "Report builder training (Sales Register)", 3, 14, 3, "meera", ServiceActivityStatus.Scheduled);
            Add("pioneer", ServiceActivityType.Call, "AMC renewal follow-up call", -1, 12, 1, "sana", ServiceActivityStatus.Missed, "Customer unavailable; rescheduled.");
            Add("pioneer", ServiceActivityType.Call, "AMC renewal follow-up call (rescheduled)", 2, 12, 1, "sana", ServiceActivityStatus.Scheduled);
            Add("zenith", ServiceActivityType.Upgrade, "Upgrade plan: 7.3 → 8.0 (dealer accounts)", 15, 10, 6, "rahul", ServiceActivityStatus.Scheduled, installation: "zenith");
            Add("bluesky", ServiceActivityType.RemoteSession, "Fee module data clean-up", 0, 15, 2, "arjun", ServiceActivityStatus.InProgress);
            await db.SaveChangesAsync(ct);
        });
    }

    /// <summary>"Ramesh Kumaar" at ABC Retail: same mobile number as Ramesh Kumar, a personal e-mail and a ticket of his own.</summary>
    private static async Task SeedDuplicateAsync(DemoSeedContext c, CancellationToken ct)
    {
        Guid duplicateId = default;
        await c.AsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var original = await db.Contacts.AsNoTracking().FirstAsync(k => k.Id == c.Contacts["ramesh"], ct);
            var dup = new Contact
            {
                CustomerId = original.CustomerId, FullName = "Ramesh Kumaar", FirstName = "Ramesh", LastName = "Kumaar", Email = "ramesh.k.finance@gmail.com",
                EmailNormalized = "ramesh.k.finance@gmail.com", Phone = original.Phone, PhoneNormalized = original.PhoneNormalized, JobTitle = null,
                PreferredChannel = Channel.Phone, PortalAccess = PortalAccessScope.Own
            };
            db.Contacts.Add(dup);
            db.ContactIdentities.Add(new ContactIdentity { ContactId = dup.Id, Channel = Channel.Email, Identifier = dup.EmailNormalized!, CreatedAt = c.Anchor.AddDays(-20) });
            db.ContactConsents.Add(new ContactChannelConsent { ContactId = dup.Id, Channel = Channel.Email, Purpose = ConsentPurpose.Marketing, Status = ConsentStatus.OptedOut,
                Source = "web form", RecordedAt = c.Anchor.AddDays(-20) });
            await db.SaveChangesAsync(ct);
            duplicateId = dup.Id;
        });
        c.Contacts["ramesh-dup"] = duplicateId;
        c.At(c.Anchor.AddDays(-4).AddHours(-3));
        await c.AsSystemAsync(sp => sp.GetRequiredService<TicketService>().CreateAsync(new CreateTicketRequest
        {
            Subject = "Need GSTR-2B reconciliation report for September", Channel = "Email", ContactId = duplicateId, CustomerId = c.Customers["abc"],
            ProductId = c.Products["erp"], InstallationId = c.Installations["abc"],
            DescriptionHtml = "<p>Hi, please share how to generate the GSTR-2B vs purchase register reconciliation for September. Sent from my personal mail.</p><p>Ramesh</p>"
        }, new TicketCreationSource(CreateInitialThread: true, EnforceRequiredFields: false), ct));
    }

    private async Task SeedErpAsync(DemoSeedContext c, CancellationToken ct)
    {
        await c.AsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            db.Integrations.Add(new Integration
            {
                Type = IntegrationTypes.WingsErp, Name = "Wings ERP (simulator)", IsActive = true, Status = IntegrationStatus.Connected,
                SettingsJson = Json.Serialize(new ErpSettings { Mode = ErpModes.Simulator, SyncIntervalMinutes = 360, SimulatorAnchor = DateOnly.FromDateTime(c.Anchor) })
            });
            db.Integrations.Add(new Integration
            {
                Type = IntegrationTypes.WingsBooks, Name = "Wings Books (simulator)", IsActive = true, Status = IntegrationStatus.Connected,
                SettingsJson = Json.Serialize(new ErpSettings
                {
                    Mode = ErpModes.Simulator, SyncIntervalMinutes = 720, SyncInstallations = false, SyncLicences = false, SyncContracts = false,
                    CreateMissingCustomers = false, SimulatorAnchor = DateOnly.FromDateTime(c.Anchor)
                })
            });
            await db.SaveChangesAsync(ct);
        });

        try
        {
            await c.AsSystemAsync(async sp =>
            {
                var result = await sp.GetRequiredService<ErpSyncService>().SyncAsync(ErpSystems.WingsErp, "initial", ct);
                logger.LogInformation("Initial Wings ERP sync: {Summary}", string.Join("; ", result.Entities.Select(e => $"{e.Entity} +{e.Created}/~{e.Updated}")));
            });

            // A ticket raised from the Wings ERP sales-invoice screen at ABC Retail.
            await c.AsSystemAsync(async sp =>
            {
                var db = sp.GetRequiredService<DeskDbContext>();
                var resolver = sp.GetRequiredService<ErpConnectorResolver>();
                var erpId = await db.Customers.Where(x => x.Id == c.Customers["abc"]).Select(x => x.ErpCustomerId).FirstAsync(ct);
                var connector = await resolver.GetAsync(ErpSystems.WingsErp, ct);
                var invoice = (await connector.GetInvoicesAsync(erpId!.Value, 10, ct)).FirstOrDefault(i => i.Lines.Count > 0 && i.Status != "Paid")
                              ?? (await connector.GetInvoicesAsync(erpId.Value, 1, ct)).First();
                var context = Json.ToElement(new
                {
                    screen = "Sales › Invoices › View", company = "ABC Retail Pvt Ltd", branch = "Chennai – Head Office", invoiceDate = invoice.Date,
                    placeOfSupply = invoice.PlaceOfSupply, taxableAmount = invoice.TaxableAmount, cgst = invoice.Tax?.Cgst, sgst = invoice.Tax?.Sgst, igst = invoice.Tax?.Igst,
                    totalAmount = invoice.TotalAmount, currency = invoice.Currency, erpVersion = "8.0.1", user = "ramesh"
                });
                await sp.GetRequiredService<ErpTicketService>().CreateAsync(new CreateErpTicketRequest
                {
                    System = ErpSystems.WingsErp, RecordType = ErpRecordTypes.SalesInvoice, RecordId = invoice.Id, RecordNumber = invoice.Number, Context = context,
                    CustomerErpId = erpId, ContactEmail = "ramesh@abcretail.demo", ErpUser = "ramesh (ABC Retail)", Priority = "High", ProductCode = "WERP",
                    Subject = $"Tax split on invoice {invoice.Number} looks wrong",
                    Description = $"Invoice {invoice.Number} dated {invoice.Date:dd MMM yyyy} shows the GST as " +
                                  (invoice.Tax?.Igst > 0 ? "IGST" : "CGST + SGST") + $" for {invoice.TotalAmount:N2} INR. Our auditor expects the other treatment for this place of supply. " +
                                  "Please check before we file GSTR-1 this month.",
                    ClientRequestId = "demo-erp-ticket-1"
                }, ct);
            });
        }
        catch (DomainException ex)
        {
            logger.LogWarning(ex, "ERP demo data skipped: {Message}", ex.Message);
        }
    }
}
