using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Identity;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Sla;
using SupportDesk.Application.Tenancy;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Configuration;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Organisation;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Products;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Service;
using SupportDesk.Domain.Sla;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Seed;

/// <summary>Extra demo data contributed by a module (knowledge articles, channels, rules, AI agents…), run after the core seed.</summary>
public interface IDemoSeedContributor
{
    int Order { get; }
    Task SeedAsync(DemoSeedContext context, CancellationToken ct);
}

/// <summary>Ids of everything the core seeder created, plus helpers to act as a persona at a pinned point in time.</summary>
public sealed class DemoSeedContext(IServiceScopeFactory scopes, MutableClock clock, Guid tenantId, string slug, DateTime anchor)
{
    public Guid TenantId { get; } = tenantId;
    public string TenantSlug { get; } = slug;
    /// <summary>"Now" for the demo story; historical activity is pinned relative to it.</summary>
    public DateTime Anchor { get; } = anchor;
    public MutableClock Clock { get; } = clock;
    public Dictionary<string, Guid> Users { get; } = new();
    public Dictionary<string, Guid> Departments { get; } = new();
    public Dictionary<string, Guid> Teams { get; } = new();
    public Dictionary<string, Guid> Customers { get; } = new();
    public Dictionary<string, Guid> Contacts { get; } = new();
    public Dictionary<string, Guid> Products { get; } = new();
    public Dictionary<string, Guid> Modules { get; } = new();
    public Dictionary<string, Guid> Versions { get; } = new();
    public Dictionary<string, Guid> Installations { get; } = new();
    public Dictionary<string, Guid> Contracts { get; } = new();
    public Dictionary<string, Guid> Categories { get; } = new();
    public Dictionary<string, Guid> Statuses { get; } = new();
    public Dictionary<string, Guid> Tickets { get; } = new();
    public Guid BrandId { get; set; }

    public async Task AsAgentAsync(string userKey, Func<IServiceProvider, Task> work)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<RequestContext>().CorrelationId = "demo-seed";
        if (!await sp.GetRequiredService<PrincipalLoader>().ApplyAgentAsync(TenantId, Users[userKey]))
            throw new InvalidOperationException($"Demo user {userKey} is not active.");
        await work(sp);
    }

    public async Task AsContactAsync(string contactKey, Func<IServiceProvider, Task> work)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<DeskDbContext>();
        var ctx = sp.GetRequiredService<RequestContext>();
        ctx.SetTenant(TenantId);
        var contact = await db.Contacts.AsNoTracking().FirstAsync(c => c.Id == Contacts[contactKey]);
        ctx.SetContact(TenantId, contact.Id, contact.FullName);
        await work(sp);
    }

    public async Task AsSystemAsync(Func<IServiceProvider, Task> work)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<RequestContext>().SetSystem(TenantId, "Demo seed");
        await work(scope.ServiceProvider);
    }

    /// <summary>Pins the clock to <paramref name="at"/> (UTC) for the following actions in this async flow.</summary>
    public void At(DateTime at) => Clock.Pin(at);
    public DateTime DaysAgo(double days, int hour = 10, int minute = 0) =>
        Anchor.Date.AddDays(-Math.Floor(days)).AddHours(hour - 5.5).AddMinutes(minute); // IST working day → UTC
}

/// <summary>
/// Creates the demonstration environment: platform catalog + operator, the "wings" tenant (an ERP software vendor with
/// five departments, agents, customers, products, installations, contracts and ~200 tickets across every status and
/// channel, created through the real services so SLA, routing, history and metrics are genuine), and a second tenant
/// "northwind" to demonstrate isolation. Idempotent.
/// </summary>
public sealed class DemoSeeder(IServiceScopeFactory scopes, IClock clock, ILogger<DemoSeeder> logger, IEnumerable<IDemoSeedContributor> contributors)
{
    public const string AgentPassword = "Desk@2026";
    public const string PortalPassword = "Portal@2026";
    public const string PlatformPassword = "Platform@2026";

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (clock is not MutableClock mutable) throw new InvalidOperationException("Demo seeding requires the MutableClock.");
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
            await scope.ServiceProvider.GetRequiredService<PlatformCatalogService>().EnsureAsync(ct);
            if (!await db.PlatformOperators.AnyAsync(ct))
            {
                db.PlatformOperators.Add(new PlatformOperator
                {
                    Email = "platform@supportdesk.demo", DisplayName = "Platform Operator",
                    PasswordHash = scope.ServiceProvider.GetRequiredService<PasswordService>().Hash(PlatformPassword)
                });
                await db.SaveChangesAsync(ct);
            }
            if (await db.Tenants.AnyAsync(t => t.Slug == "wings", ct)) return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var anchor = DateTime.UtcNow;
        logger.LogInformation("Seeding demo tenants…");
        var wings = await ProvisionAsync("wings", "Wings Software", "enterprise", "admin@wings.demo", "Asha Menon (Admin)", "Asia/Kolkata", "IN",
            ["Technical Support", "Implementation", "Billing", "Customer Success", "Product Development"], ct);
        var context = new DemoSeedContext(scopes, mutable, wings.TenantId, "wings", anchor);
        try
        {
            mutable.Pin(anchor.AddDays(-120));
            await SeedOrganisationAsync(context, wings, ct);
            await SeedCustomersAndProductsAsync(context, ct);
            await SeedTicketsAsync(context, ct);
            foreach (var c in contributors.OrderBy(c => c.Order))
            {
                logger.LogInformation("Seeding module data: {Contributor}", c.GetType().Name);
                await c.SeedAsync(context, ct);
            }
            mutable.Unpin();
            await context.AsSystemAsync(sp => sp.GetRequiredService<SlaMonitorProcessor>().ProcessAsync(ct));
            await SeedSecondTenantAsync(mutable, ct);
        }
        finally
        {
            mutable.Unpin();
        }
        logger.LogInformation("Demo data ready in {Seconds:0.0}s (tenant 'wings': admin@wings.demo / {Password})", sw.Elapsed.TotalSeconds, AgentPassword);
    }

    private async Task<ProvisionedTenant> ProvisionAsync(string slug, string name, string plan, string adminEmail, string adminName, string tz, string country,
        IReadOnlyList<string> departments, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<TenantProvisioningService>().ProvisionAsync(
            new ProvisionTenantRequest(slug, name, plan, adminEmail, adminName, AgentPassword, tz, country, Trial: false, Seats: 25, departments, DemoMode: true), ct);
        return result;
    }

    // ───────────────────────────── Organisation ─────────────────────────────

    private static readonly (string Key, string Name, string Email, string Role, string[] Depts, bool Supervisor, string Title, bool Light)[] People =
    [
        ("priya", "Priya Raman", "priya@wings.demo", "Supervisor", ["Technical Support"], true, "Support Team Lead", false),
        ("arjun", "Arjun Nair", "arjun@wings.demo", "Agent", ["Technical Support"], false, "Support Engineer L1", false),
        ("meera", "Meera Iyer", "meera@wings.demo", "Agent", ["Technical Support"], false, "Support Engineer L2", false),
        ("karthik", "Karthik Rao", "karthik@wings.demo", "Agent", ["Technical Support", "Implementation"], false, "Senior Support Engineer", false),
        ("rahul", "Rahul Verma", "rahul@wings.demo", "Agent", ["Implementation"], false, "Implementation Consultant", false),
        ("divya", "Divya Shah", "divya@wings.demo", "Supervisor", ["Implementation"], true, "Implementation Manager", false),
        ("sana", "Sana Qureshi", "sana@wings.demo", "Agent", ["Billing"], true, "Billing Specialist", false),
        ("vikram", "Vikram Singh", "vikram@wings.demo", "Agent", ["Customer Success"], true, "Customer Success Manager", false),
        ("neha", "Neha Kapoor", "neha@wings.demo", "Knowledge Manager", ["Technical Support"], false, "Knowledge Manager", false),
        ("dev", "Dev Kumar", "dev@wings.demo", "Light Agent", ["Product Development", "Technical Support"], false, "Software Engineer", true),
        ("lakshmi", "Lakshmi Pillai", "lakshmi@wings.demo", "Supervisor", ["Product Development"], true, "Engineering Manager", false),
        ("anil", "Anil Gupta", "anil@wings.demo", "Analyst", ["Technical Support"], false, "Support Analyst", false)
    ];

    private async Task SeedOrganisationAsync(DemoSeedContext c, ProvisionedTenant wings, CancellationToken ct)
    {
        await c.AsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var passwords = sp.GetRequiredService<PasswordService>();
            var secrets = sp.GetRequiredService<ISecretProtector>();
            foreach (var (k, v) in wings.Departments) c.Departments[Key(k)] = v;
            c.Users["admin"] = wings.AdminUserId;
            c.BrandId = wings.BrandId;

            var admin = await db.Users.FirstAsync(u => u.Id == wings.AdminUserId, ct);
            admin.MfaEnabled = true; // e-mail OTP (demo mode shows the code on the login screen)

            var depts = await db.Departments.ToListAsync(ct);
            foreach (var d in depts)
            {
                d.Description = d.Name switch
                {
                    "Technical Support" => "Product incidents, errors and how-to questions for Wings ERP, Books and POS.",
                    "Implementation" => "New deployments, configuration, data migration and training.",
                    "Billing" => "Invoices, licence renewals, AMC payments and commercial queries.",
                    "Customer Success" => "Account health, upgrades, feedback and escalation management.",
                    _ => "Defects and feature requests escalated from support."
                };
                d.Email = $"{Key(d.Name)}@wings.demo";
                d.TransferRequiresApproval = d.Name == "Product Development";
            }
            var teams = await db.Teams.ToListAsync(ct);
            foreach (var t in teams)
            {
                var dkey = Key(depts.First(d => d.Id == t.DepartmentId).Name);
                c.Teams[dkey] = t.Id;
                t.RoutingStrategy = dkey switch
                {
                    "support" => RoutingStrategy.SkillBased,
                    "billing" => RoutingStrategy.RoundRobin,
                    "engineering" => RoutingStrategy.Manual,
                    _ => RoutingStrategy.LoadBalanced
                };
                t.OnlyAssignToOnlineAgents = false;
            }
            var l2 = new Team { DepartmentId = c.Departments["support"], Name = "Technical Support – Level 2", RoutingStrategy = RoutingStrategy.LoadBalanced, OnlyAssignToOnlineAgents = false };
            db.Teams.Add(l2);
            c.Teams["support-l2"] = l2.Id;

            var roles = await db.Roles.ToDictionaryAsync(r => r.Name, r => r.Id, ct);
            var adminAgent = await db.Agents.FirstAsync(a => a.UserId == admin.Id, ct);
            adminAgent.Availability = AgentAvailability.Online;
            foreach (var p in People)
            {
                var u = new User
                {
                    Email = p.Email, EmailNormalized = p.Email, DisplayName = p.Name, FirstName = p.Name.Split(' ')[0], LastName = p.Name.Split(' ')[^1],
                    Kind = p.Light ? UserKind.LightAgent : UserKind.Agent, TimeZone = "Asia/Kolkata", PasswordHash = passwords.Hash(AgentPassword),
                    PasswordChangedAt = c.Anchor.AddDays(-30), Signature = $"Regards,<br>{p.Name}<br>{p.Title}, Wings Software"
                };
                db.Users.Add(u);
                c.Users[p.Key] = u.Id;
                db.UserRoles.Add(new UserRole { UserId = u.Id, RoleId = roles[p.Role] });
                db.Agents.Add(new Agent
                {
                    UserId = u.Id, Title = p.Title, IsLightAgent = p.Light, Availability = p.Key is "anil" ? AgentAvailability.Offline : AgentAvailability.Online,
                    MaxOpenTickets = p.Role == "Supervisor" ? 15 : 30, HourlyCost = p.Role == "Supervisor" ? 1400m : 900m,
                    LanguagesJson = p.Key is "meera" or "priya" ? "[\"en\",\"ta\"]" : p.Key is "arjun" ? "[\"en\",\"ml\"]" : "[\"en\",\"hi\"]"
                });
                foreach (var dn in p.Depts)
                    db.AgentDepartments.Add(new AgentDepartment { UserId = u.Id, DepartmentId = c.Departments[Key(dn)], IsSupervisor = p.Supervisor });
                if (!p.Light && p.Role is "Agent" or "Supervisor")
                    db.TeamMembers.Add(new TeamMember { TeamId = c.Teams[Key(p.Depts[0])], UserId = u.Id, IsLead = p.Supervisor });
            }
            db.TeamMembers.Add(new TeamMember { TeamId = l2.Id, UserId = c.Users["meera"] });
            db.TeamMembers.Add(new TeamMember { TeamId = l2.Id, UserId = c.Users["karthik"] });
            await db.SaveChangesAsync(ct);

            // Skills & routing attributes
            var skills = new[] { "GST & Taxation", "Inventory", "Payroll", "Reporting", "Integrations", "SQL Server", "Data Migration" }
                .Select(n => new Skill { Name = n, Category = "Product" }).ToList();
            db.Skills.AddRange(skills);
            void Skill(string user, string skill, int level) =>
                db.AgentSkills.Add(new AgentSkill { UserId = c.Users[user], SkillId = skills.First(s => s.Name == skill).Id, Level = level });
            Skill("arjun", "Reporting", 3); Skill("arjun", "Inventory", 3); Skill("meera", "GST & Taxation", 5); Skill("meera", "SQL Server", 4);
            Skill("karthik", "Integrations", 5); Skill("karthik", "SQL Server", 4); Skill("rahul", "Data Migration", 5); Skill("priya", "Payroll", 4);

            // Holidays on the business calendar
            var cal = await db.BusinessCalendars.FirstAsync(x => x.IsDefault, ct);
            var year = c.Anchor.Year;
            foreach (var (m, d, n) in new[] { (1, 26, "Republic Day"), (8, 15, "Independence Day"), (10, 2, "Gandhi Jayanti"), (12, 25, "Christmas") })
                db.CalendarHolidays.Add(new CalendarHoliday { CalendarId = cal.Id, Date = new DateOnly(year, m, d), Name = n, Recurring = true });
            db.CalendarHolidays.Add(new CalendarHoliday { CalendarId = cal.Id, Date = new DateOnly(year, 11, 8), Name = "Diwali" });

            // Premium 24x7 SLA for Platinum customers
            var always = await db.BusinessCalendars.FirstAsync(x => x.Is24x7, ct);
            var premium = new SlaPolicy
            {
                Name = "Platinum 24x7", Description = "Premium commitments for Platinum-tier customers", EvaluationOrder = 10, CalendarId = always.Id,
                ConditionsJson = new ConditionGroup { Conditions = [new Condition { Field = "customer.tier", Op = ConditionOps.Equals, Value = Json.ToElement("Platinum") }] }.ToJson(),
                PauseStatusIdsJson = "[]"
            };
            premium.Targets.AddRange(
            [
                new SlaTarget { PolicyId = premium.Id, Priority = TicketPriority.Critical, FirstResponseMinutes = 15, NextResponseMinutes = 30, ResolutionMinutes = 4 * 60, BusinessHours = false, WarningPercent = 70 },
                new SlaTarget { PolicyId = premium.Id, Priority = TicketPriority.High, FirstResponseMinutes = 30, NextResponseMinutes = 120, ResolutionMinutes = 8 * 60, BusinessHours = false, WarningPercent = 75 },
                new SlaTarget { PolicyId = premium.Id, Priority = TicketPriority.Medium, FirstResponseMinutes = 120, NextResponseMinutes = 240, ResolutionMinutes = 24 * 60, BusinessHours = false },
                new SlaTarget { PolicyId = premium.Id, Priority = TicketPriority.Low, FirstResponseMinutes = 240, NextResponseMinutes = 480, ResolutionMinutes = 48 * 60, BusinessHours = false }
            ]);
            premium.Escalations.AddRange(
            [
                new SlaEscalationLevel { PolicyId = premium.Id, Milestone = SlaMilestoneKind.Resolution, Level = 1, MinutesAfterBreach = -60, Action = EscalationAction.Notify,
                    NotifyJson = Json.Serialize(new { notifyAssignee = true, notifySupervisors = true }) },
                new SlaEscalationLevel { PolicyId = premium.Id, Milestone = SlaMilestoneKind.Resolution, Level = 2, MinutesAfterBreach = 0, Action = EscalationAction.NotifyAndReassign,
                    ReassignToTeamId = l2.Id, NotifyJson = Json.Serialize(new { notifySupervisors = true, userIds = new[] { c.Users["vikram"] } }) },
                new SlaEscalationLevel { PolicyId = premium.Id, Milestone = SlaMilestoneKind.FirstResponse, Level = 1, MinutesAfterBreach = 0, Action = EscalationAction.Notify,
                    NotifyJson = Json.Serialize(new { notifySupervisors = true }) }
            ]);
            db.SlaPolicies.Add(premium);

            // Ticket categories, issue types and custom fields
            var catTree = new (string Dept, string Name, string[] Subs)[]
            {
                ("support", "Application Error", ["Crash or error message", "Calculation mismatch", "Performance"]),
                ("support", "Data & Reports", ["Report export", "Data correction", "Dashboards"]),
                ("support", "Access & Login", ["Password reset", "Permissions"]),
                ("support", "Integration", ["API", "GST portal / e-invoice", "Tally import"]),
                ("implementation", "Configuration", ["Masters setup", "Workflow setup"]),
                ("implementation", "Data Migration", []), ("implementation", "Training", []),
                ("billing", "Invoice Query", []), ("billing", "Licence Renewal", []), ("billing", "Payment", []),
                ("success", "Feedback", []), ("success", "Upgrade Request", []),
                ("engineering", "Bug", []), ("engineering", "Feature Request", [])
            };
            var order = 0;
            foreach (var (dept, name, subs) in catTree)
            {
                var cat = new TicketCategory { Name = name, DepartmentId = c.Departments[dept], SortOrder = order++ };
                db.TicketCategories.Add(cat);
                c.Categories[name] = cat.Id;
                foreach (var s in subs)
                {
                    var sub = new TicketCategory { Name = s, ParentId = cat.Id, DepartmentId = c.Departments[dept], SortOrder = order++ };
                    db.TicketCategories.Add(sub);
                    c.Categories[s] = sub.Id;
                }
            }
            var it = 0;
            foreach (var name in new[] { "Incident", "Service Request", "Question", "Problem", "Change Request" })
                db.IssueTypes.Add(new IssueType { Name = name, SortOrder = it++ });
            db.CustomFields.AddRange(
                new CustomFieldDefinition { Entity = "Ticket", Key = "environment", Label = "Environment", FieldType = CustomFieldType.Picklist, SortOrder = 1, VisibleInPortal = true, EditableInPortal = true,
                    OptionsJson = Json.Serialize(new[] { new { value = "Production", label = "Production" }, new { value = "UAT", label = "UAT" }, new { value = "Test", label = "Test" } }) },
                new CustomFieldDefinition { Entity = "Ticket", Key = "business_impact", Label = "Business impact", FieldType = CustomFieldType.Picklist, SortOrder = 2, VisibleInPortal = true, EditableInPortal = true,
                    OptionsJson = Json.Serialize(new[] { "Low", "Medium", "High", "Critical" }.Select(v => new { value = v, label = v })) },
                new CustomFieldDefinition { Entity = "Ticket", Key = "affected_users", Label = "Affected users", FieldType = CustomFieldType.Number, SortOrder = 3, VisibleInPortal = true, EditableInPortal = true, MinValue = 0 },
                new CustomFieldDefinition { Entity = "Ticket", Key = "go_live_date", Label = "Go-live date", FieldType = CustomFieldType.Date, SortOrder = 4,
                    DepartmentIdsJson = Json.Serialize(new[] { c.Departments["implementation"] }) },
                new CustomFieldDefinition { Entity = "Ticket", Key = "invoice_number", Label = "Invoice number", FieldType = CustomFieldType.Text, SortOrder = 5, MaxLength = 40,
                    DepartmentIdsJson = Json.Serialize(new[] { c.Departments["billing"] }), VisibleInPortal = true, EditableInPortal = true },
                new CustomFieldDefinition { Entity = "Customer", Key = "erp_edition", Label = "ERP edition", FieldType = CustomFieldType.Picklist, SortOrder = 1,
                    OptionsJson = Json.Serialize(new[] { "Standard", "Professional", "Enterprise" }.Select(v => new { value = v, label = v })) });
            db.TicketStatuses.Add(new TicketStatus
            {
                Name = "Escalated to Engineering", Category = StatusCategory.Waiting, Color = "#9333ea", SortOrder = 5, CustomerLabel = "With our engineering team",
                PausesSla = false, SubstatusesJson = Json.Serialize(new[] { "Investigation", "Fix in progress", "Awaiting release" })
            });
            foreach (var s in await db.TicketStatuses.ToListAsync(ct)) c.Statuses[s.Name] = s.Id;
            await db.SaveChangesAsync(ct);
            foreach (var s in await db.TicketStatuses.ToListAsync(ct)) c.Statuses[s.Name] = s.Id;

            // Ticket numbering and policies
            var settings = sp.GetRequiredService<TenantSettingsService>();
            await settings.SetAsync(SettingKeys.Tickets, new TicketSettings { NumberPrefix = "#", NumberStart = 1000, AutoCloseResolvedAfterDays = 5, ReopenWindowDays = 30 }, ct);
            await settings.SetAsync(SettingKeys.Email, new EmailSettings { MessageIdDomain = "support.wings.demo" }, ct);
            _ = secrets;
        });
    }

    // ───────────────────────────── Customers, products, contracts ─────────────────────────────

    private static readonly (string Key, string Name, string Code, string Tier, string Industry, string Region, string City, string Domain, string Manager, (string Key, string Name, string Title, PortalAccessScope Scope, bool Vip)[] People)[] CustomerData =
    [
        ("abc", "ABC Retail Pvt Ltd", "C-1001", "Platinum", "Retail", "South", "Chennai", "abcretail.demo", "vikram",
            [("ramesh", "Ramesh Kumar", "Finance Controller", PortalAccessScope.Organisation, true), ("lata", "Lata Menon", "Store Operations Head", PortalAccessScope.Location, false), ("joseph", "Joseph Thomas", "Accounts Executive", PortalAccessScope.Own, false)]),
        ("sunrise", "Sunrise Pharma Ltd", "C-1002", "Gold", "Pharmaceuticals", "West", "Pune", "sunrisepharma.demo", "vikram",
            [("anita", "Anita Desai", "IT Manager", PortalAccessScope.Organisation, false), ("suresh", "Suresh Patil", "Warehouse Supervisor", PortalAccessScope.Own, false)]),
        ("metro", "Metro Logistics", "C-1003", "Silver", "Logistics", "North", "Gurugram", "metrologistics.demo", "vikram",
            [("harpreet", "Harpreet Kaur", "Operations Manager", PortalAccessScope.Organisation, false), ("imran", "Imran Sheikh", "Billing Clerk", PortalAccessScope.Own, false)]),
        ("greenfield", "Greenfield Foods", "C-1004", "Gold", "FMCG", "South", "Bengaluru", "greenfieldfoods.demo", "vikram",
            [("kavya", "Kavya Reddy", "CFO", PortalAccessScope.Organisation, true), ("manoj", "Manoj Shetty", "Plant Accountant", PortalAccessScope.Own, false)]),
        ("coastal", "Coastal Hospitals", "C-1005", "Platinum", "Healthcare", "South", "Kochi", "coastalhospitals.demo", "vikram",
            [("thomas", "Dr. Thomas Varghese", "Director – Administration", PortalAccessScope.Organisation, true), ("rekha", "Rekha Nair", "Pharmacy Billing Lead", PortalAccessScope.Location, false)]),
        ("zenith", "Zenith Motors", "C-1006", "Silver", "Automotive", "West", "Ahmedabad", "zenithmotors.demo", "vikram",
            [("nikhil", "Nikhil Joshi", "Dealer Accounts Head", PortalAccessScope.Organisation, false)]),
        ("pioneer", "Pioneer Textiles", "C-1007", "Bronze", "Manufacturing", "South", "Coimbatore", "pioneertextiles.demo", "vikram",
            [("senthil", "Senthil Murugan", "Owner", PortalAccessScope.Organisation, false)]),
        ("bluesky", "BlueSky Education Trust", "C-1008", "Standard", "Education", "East", "Kolkata", "blueskyedu.demo", "vikram",
            [("ritu", "Ritu Banerjee", "Accounts Officer", PortalAccessScope.Own, false)])
    ];

    private async Task SeedCustomersAndProductsAsync(DemoSeedContext c, CancellationToken ct)
    {
        await c.AsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var passwords = sp.GetRequiredService<PasswordService>();
            var portalHash = passwords.Hash(PortalPassword);

            // Products
            var family = new ProductFamily { Name = "Wings Business Suite", Description = "ERP, accounting and retail products" };
            db.ProductFamilies.Add(family);
            var products = new (string Key, string Name, string Code, string Dept, string[] Modules, string[] Versions)[]
            {
                ("erp", "Wings ERP", "WERP", "support", ["Finance", "Inventory", "Sales", "Purchase", "Payroll", "GST & Compliance", "Manufacturing"], ["7.2", "7.3", "8.0"]),
                ("books", "Wings Books", "WBKS", "support", ["Accounting", "Banking", "GST Returns"], ["4.5", "5.0"]),
                ("pos", "Wings POS", "WPOS", "support", ["Billing Counter", "Loyalty", "Store Sync"], ["3.1", "3.2"])
            };
            foreach (var p in products)
            {
                var product = new Product { FamilyId = family.Id, Name = p.Name, Code = p.Code, DefaultDepartmentId = c.Departments[p.Dept], DefaultTeamId = c.Teams[p.Dept],
                    Description = $"{p.Name} — part of the Wings Business Suite" };
                db.Products.Add(product);
                c.Products[p.Key] = product.Id;
                foreach (var m in p.Modules)
                {
                    var mod = new ProductModule { ProductId = product.Id, Name = m };
                    db.ProductModules.Add(mod);
                    c.Modules[$"{p.Key}:{m}"] = mod.Id;
                }
                var vi = 0;
                foreach (var v in p.Versions)
                {
                    var ver = new ProductVersion
                    {
                        ProductId = product.Id, Version = v, ReleaseDate = DateOnly.FromDateTime(c.Anchor.AddMonths(-24 + vi * 9)),
                        Status = vi == p.Versions.Length - 1 ? VersionStatus.Released : vi == 0 ? VersionStatus.EndOfLife : VersionStatus.Supported,
                        EndOfSupportDate = vi == 0 ? DateOnly.FromDateTime(c.Anchor.AddMonths(3)) : null
                    };
                    db.ProductVersions.Add(ver);
                    c.Versions[$"{p.Key}:{v}"] = ver.Id;
                    db.ProductReleases.Add(new ProductRelease { ProductId = product.Id, VersionId = ver.Id, Name = $"{v}.0", BuildNumber = $"{1100 + vi * 140}", Type = ReleaseType.Minor, ReleaseDate = ver.ReleaseDate });
                    if (vi == p.Versions.Length - 1)
                        db.ProductReleases.Add(new ProductRelease { ProductId = product.Id, VersionId = ver.Id, Name = $"{v}.1", BuildNumber = $"{1100 + vi * 140 + 23}", Type = ReleaseType.Patch,
                            ReleaseDate = DateOnly.FromDateTime(c.Anchor.AddDays(-20)), ReleaseNotes = "Fixes GST rounding on multi-rate invoices and report export timeouts." });
                    vi++;
                }
            }

            // Customers, locations, contacts
            var group = new CustomerGroup { Name = "Key Accounts", Description = "Platinum and Gold customers with named account managers" };
            db.CustomerGroups.Add(group);
            foreach (var cd in CustomerData)
            {
                var cust = new Customer
                {
                    Name = cd.Name, Code = cd.Code, Tier = cd.Tier, Industry = cd.Industry, ServiceRegion = cd.Region, AccountManagerUserId = c.Users[cd.Manager],
                    DomainsJson = Json.Serialize(new[] { cd.Domain }), Email = $"accounts@{cd.Domain}", Phone = $"+91 44 4000 {int.Parse(cd.Code[2..]):0000}",
                    Website = $"https://www.{cd.Domain}", TimeZone = "Asia/Kolkata", CustomerGroupId = cd.Tier is "Platinum" or "Gold" ? group.Id : null,
                    ErpCustomerId = DeterministicGuid.From("erp-customer", cd.Code), ErpCode = cd.Code,
                    CustomFieldsJson = Json.Serialize(new Dictionary<string, string> { ["erp_edition"] = cd.Tier is "Platinum" ? "Enterprise" : cd.Tier is "Gold" ? "Professional" : "Standard" })
                };
                db.Customers.Add(cust);
                c.Customers[cd.Key] = cust.Id;
                var hq = new CustomerLocation { CustomerId = cust.Id, Name = $"{cd.City} – Head Office", Code = "HO", City = cd.City, Country = "India", IsHeadOffice = true, ServiceRegion = cd.Region };
                var branch = new CustomerLocation { CustomerId = cust.Id, Name = $"{cd.City} – Branch 2", Code = "BR2", City = cd.City, Country = "India", ServiceRegion = cd.Region };
                db.CustomerLocations.AddRange(hq, branch);
                var i = 0;
                foreach (var person in cd.People)
                {
                    var email = $"{person.Key}@{cd.Domain}";
                    var phone = $"+91 98{int.Parse(cd.Code[2..]):0000}{i + 1:0000}";
                    var contact = new Contact
                    {
                        CustomerId = cust.Id, LocationId = i % 2 == 0 ? hq.Id : branch.Id, FullName = person.Name, FirstName = person.Name.Split(' ')[0],
                        LastName = person.Name.Split(' ')[^1], Email = email, EmailNormalized = email, Phone = phone, PhoneNormalized = TextNormalization.NormalizePhone(phone),
                        WhatsAppNumber = phone, JobTitle = person.Title, IsVip = person.Vip, IsPrimary = i == 0, PortalAccess = person.Scope, PortalPasswordHash = portalHash,
                        EmailVerifiedAt = c.Anchor.AddDays(-200), PreferredChannel = i % 3 == 1 ? Channel.WhatsApp : Channel.Email
                    };
                    db.Contacts.Add(contact);
                    c.Contacts[person.Key] = contact.Id;
                    db.ContactIdentities.Add(new ContactIdentity { ContactId = contact.Id, Channel = Channel.Email, Identifier = email, IsVerified = true, CreatedAt = c.Anchor.AddDays(-200) });
                    db.ContactIdentities.Add(new ContactIdentity { ContactId = contact.Id, Channel = Channel.Phone, Identifier = contact.PhoneNormalized!, CreatedAt = c.Anchor.AddDays(-200) });
                    db.ContactIdentities.Add(new ContactIdentity { ContactId = contact.Id, Channel = Channel.WhatsApp, Identifier = contact.PhoneNormalized!, CreatedAt = c.Anchor.AddDays(-200) });
                    foreach (var ch in new[] { Channel.Email, Channel.WhatsApp, Channel.Sms })
                        db.ContactConsents.Add(new ContactChannelConsent { ContactId = contact.Id, Channel = ch, Purpose = ConsentPurpose.Transactional,
                            Status = ch == Channel.Sms && i % 2 == 1 ? ConsentStatus.OptedOut : ConsentStatus.OptedIn, Source = "onboarding", RecordedAt = c.Anchor.AddDays(-200) });
                    i++;
                }

                // Installations & contracts
                var erpVersion = cd.Tier is "Bronze" or "Standard" ? "erp:7.2" : cd.Tier == "Silver" ? "erp:7.3" : "erp:8.0";
                var inst = new Installation
                {
                    CustomerId = cust.Id, LocationId = hq.Id, ProductId = c.Products["erp"], VersionId = c.Versions[erpVersion], Name = $"{cd.Name.Split(' ')[0]} ERP – Production",
                    InstallationCode = $"INS-{cd.Code[2..]}-01", Environment = InstallationEnvironment.Production, DeploymentMode = cd.Tier is "Platinum" ? DeploymentMode.Hosted : DeploymentMode.OnPremise,
                    OperatingSystem = cd.Tier is "Platinum" ? "Windows Server 2022" : "Windows Server 2019", DatabaseVersion = cd.Tier is "Platinum" ? "SQL Server 2022 Standard" : "SQL Server 2019 Express",
                    ServerConfiguration = cd.Tier is "Platinum" ? "8 vCPU, 32 GB RAM, Azure VM" : "4 cores, 16 GB RAM, on-premise", UserCount = cd.Tier is "Platinum" ? 120 : 35,
                    IntegrationsJson = Json.Serialize(new[] { new { system = "GST portal (e-invoice)", direction = "outbound" }, new { system = "Bank statement import", direction = "inbound" } }),
                    InstalledOn = DateOnly.FromDateTime(c.Anchor.AddYears(-2)), GoLiveOn = DateOnly.FromDateTime(c.Anchor.AddYears(-2).AddMonths(2)),
                    ErpInstallationId = DeterministicGuid.From("erp-installation", cd.Code)
                };
                db.Installations.Add(inst);
                c.Installations[cd.Key] = inst.Id;
                db.Licences.Add(new Licence { CustomerId = cust.Id, InstallationId = inst.Id, ProductId = c.Products["erp"], KeyMasked = $"WERP-****-****-{cd.Code[2..]}",
                    Edition = cd.Tier is "Platinum" ? "Enterprise" : "Professional", Seats = inst.UserCount ?? 10, IssuedOn = DateOnly.FromDateTime(c.Anchor.AddYears(-2)),
                    ExpiresOn = DateOnly.FromDateTime(c.Anchor.AddMonths(cd.Key == "pioneer" ? -1 : 10)) });
                if (cd.Key is "abc" or "greenfield" or "coastal")
                {
                    var pos = new Installation { CustomerId = cust.Id, LocationId = branch.Id, ProductId = c.Products["pos"], VersionId = c.Versions["pos:3.2"], Name = $"{cd.Name.Split(' ')[0]} POS – Stores",
                        InstallationCode = $"INS-{cd.Code[2..]}-02", DeploymentMode = DeploymentMode.Hybrid, OperatingSystem = "Windows 11", DatabaseVersion = "SQLite 3 + cloud sync", UserCount = 40 };
                    db.Installations.Add(pos);
                    c.Installations[cd.Key + ":pos"] = pos.Id;
                }

                var expired = cd.Key == "pioneer";
                var contract = new Contract
                {
                    CustomerId = cust.Id, Number = $"AMC-{c.Anchor.Year}-{cd.Code[2..]}", Name = cd.Tier is "Platinum" ? "Premium AMC (24x7)" : "Annual Maintenance Contract",
                    Type = ContractType.Amc, Status = expired ? ContractStatus.Expired : ContractStatus.Active,
                    StartDate = DateOnly.FromDateTime(c.Anchor.AddMonths(expired ? -14 : -4)), EndDate = DateOnly.FromDateTime(c.Anchor.AddMonths(expired ? -2 : 8)),
                    IsBillable = false, ContractValue = cd.Tier switch { "Platinum" => 1_800_000m, "Gold" => 950_000m, "Silver" => 480_000m, _ => 240_000m }, Currency = "INR",
                    AutoRenew = cd.Tier is "Platinum" or "Gold", RenewalDate = DateOnly.FromDateTime(c.Anchor.AddMonths(expired ? -2 : 8)),
                    CoverageJson = Json.Serialize(new { channels = new[] { "Email", "Phone", "Portal", "WhatsApp" }, onsite = cd.Tier is "Platinum" }),
                    ErpContractId = DeterministicGuid.From("erp-contract", cd.Code), ErpReference = $"SO/{c.Anchor.Year}/{cd.Code[2..]}"
                };
                db.Contracts.Add(contract);
                c.Contracts[cd.Key] = contract.Id;
                db.Entitlements.Add(new Entitlement { ContractId = contract.Id, CustomerId = cust.Id, Name = "Wings ERP support", Type = EntitlementType.Support,
                    ProductId = c.Products["erp"], StartDate = contract.StartDate, EndDate = contract.EndDate });
                if (cd.Key is "abc" or "coastal")
                    db.Entitlements.Add(new Entitlement { ContractId = contract.Id, CustomerId = cust.Id, Name = "Wings POS support", Type = EntitlementType.Support,
                        ProductId = c.Products["pos"], StartDate = contract.StartDate, EndDate = contract.EndDate });
                if (cd.Key is "metro" or "zenith")
                {
                    var prepaid = new Contract
                    {
                        CustomerId = cust.Id, Number = $"PPH-{c.Anchor.Year}-{cd.Code[2..]}", Name = "Prepaid consulting hours", Type = ContractType.PrepaidHours, Status = ContractStatus.Active,
                        StartDate = DateOnly.FromDateTime(c.Anchor.AddMonths(-3)), EndDate = DateOnly.FromDateTime(c.Anchor.AddMonths(9)), PrepaidHours = 40, IsBillable = true,
                        HourlyRate = 2500m, Currency = "INR"
                    };
                    db.Contracts.Add(prepaid);
                    db.Entitlements.Add(new Entitlement { ContractId = prepaid.Id, CustomerId = cust.Id, Name = "Implementation consulting", Type = EntitlementType.Hours,
                        HoursAllowed = 40, StartDate = prepaid.StartDate, EndDate = prepaid.EndDate });
                }
                db.ServiceActivities.Add(new ServiceActivity
                {
                    CustomerId = cust.Id, LocationId = hq.Id, InstallationId = inst.Id, ContractId = contract.Id, Type = ServiceActivityType.PreventiveMaintenance,
                    Title = "Quarterly health check & database maintenance", ScheduledStart = c.Anchor.Date.AddDays(12).AddHours(5), ScheduledEnd = c.Anchor.Date.AddDays(12).AddHours(8),
                    AssignedUserId = c.Users["karthik"], VisibleToCustomer = true
                });
            }
            db.AgentRoutingAttributes.Add(new AgentRoutingAttribute { UserId = c.Users["meera"], Kind = RoutingAttributeKind.Customer, RefId = c.Customers["coastal"] });
            db.AgentRoutingAttributes.Add(new AgentRoutingAttribute { UserId = c.Users["karthik"], Kind = RoutingAttributeKind.Product, RefId = c.Products["pos"] });
            db.AgentRoutingAttributes.Add(new AgentRoutingAttribute { UserId = c.Users["arjun"], Kind = RoutingAttributeKind.Territory, Value = "South" });
            db.AgentRoutingAttributes.Add(new AgentRoutingAttribute { UserId = c.Users["priya"], Kind = RoutingAttributeKind.Territory, Value = "West" });
            await db.SaveChangesAsync(ct);
        });
    }

    // ───────────────────────────── Tickets ─────────────────────────────

    private sealed record Step(double AfterHours, string Action, string? Who = null, string? Text = null);

    private sealed record Story(string Key, double DaysAgo, int Hour, string Channel, string Contact, string Product, string? Module, string Category,
        string Priority, string Subject, string Body, Step[] Steps, string? Installation = null);

    private static readonly Story[] Stories =
    [
        new("gst", 0.2, 9, "Email", "ramesh", "erp", "GST & Compliance", "Calculation mismatch", "High", "Invoice calculation issue — GST total doesn't match",
            "<p>The GST calculation on invoice SI/2026/04512 doesn't match our invoice total. CGST + SGST shows ₹18,450.36 but the grand total is off by ₹0.64.</p><p>This is blocking month-end closing.</p>",
            [new(0.3, "note", "arjun", "<p>Checking rounding configuration and tax setup. Likely the per-line rounding vs. invoice-level rounding setting.</p>"),
             new(0.5, "reply", "arjun", "<p>Hi Ramesh,</p><p>Thanks for the details. We're reviewing the tax rounding configuration on your instance and will update you shortly.</p>")], "abc"),
        new("login", 0.1, 11, "Portal", "suresh", "erp", "Inventory", "Password reset", "Medium", "Unable to log in after password change",
            "<p>After changing my password yesterday I get 'Invalid credentials' on the warehouse terminal. Other users are fine.</p>", [], "sunrise"),
        new("export", 1.4, 15, "Email", "harpreet", "erp", "Sales", "Report export", "High", "Report export failure — Sales Register to Excel",
            "<p>Exporting the Sales Register for Q2 to Excel fails after ~2 minutes with a timeout. Smaller date ranges work.</p>",
            [new(1, "reply", "meera", "<p>Hi Harpreet, could you share the exact date range and the number of branches selected? Also please confirm the version from Help → About.</p>"),
             new(5, "status", "meera", "Waiting on Customer"),
             new(20, "customer", null, "<p>Date range 1 Apr – 30 Jun, all 14 branches. Version 7.3.4.</p>"),
             new(22, "note", "meera", "<p>Reproduced on 7.3 with 180k rows. Fixed in 8.0.1 (paging export). Will suggest workaround + upgrade.</p>")], "metro"),
        new("payroll", 3, 10, "Phone", "kavya", "erp", "Payroll", "Calculation mismatch", "Critical", "Payroll run computes wrong PF for new joiners",
            "<p>Phone call from Kavya (CFO): payroll for October computes PF on gross instead of basic for 23 new joiners. Salary disbursement due tomorrow.</p>",
            [new(0.2, "assign", "meera"), new(0.3, "reply", "meera", "<p>Hi Kavya, we're on it as a top priority. A remote session is being scheduled with your HR team.</p>"),
             new(2, "note", "meera", "<p>Root cause: new PF ceiling rule not applied to employees created via the import template.</p>"),
             new(3, "resolve", "meera", "configuration|configuration|<p>Updated the PF calculation rule for the imported employee category and re-ran payroll. Verified with HR.</p>")], "greenfield"),
        new("pharmacy", 2, 8, "WhatsApp", "rekha", "pos", "Billing Counter", "Crash or error message", "High", "Pharmacy counter POS freezes during billing",
            "<p>POS at the pharmacy counter freezes when scanning more than 30 items. Patients are waiting.</p>",
            [new(0.1, "reply", "meera", "<p>Hi Rekha, sorry about this. Please restart the POS service and send us the log file from C:\\WingsPOS\\logs.</p>"),
             new(1, "customer", null, "<p>Restarted, still freezing. Log attached.</p>"),
             new(3, "escalate", "meera", "Recurring freeze; needs engineering"),
             new(4, "status", "meera", "Escalated to Engineering")], "coastal:pos"),
        new("eway", 6, 12, "Email", "nikhil", "erp", "GST & Compliance", "GST portal / e-invoice", "Medium", "E-way bill generation error 2172",
            "<p>E-way bill API returns error 2172 (invalid GSTIN of consignee) though the GSTIN is valid.</p>",
            [new(2, "reply", "karthik", "<p>Hi Nikhil, this error occurs when the consignee GSTIN state code doesn't match the place of supply. Please check the ship-to address state.</p>"),
             new(26, "customer", null, "<p>That was it — the ship-to state was wrong. Thanks!</p>"),
             new(27, "resolve", "karthik", "user_training|user_error|<p>Ship-to state corrected on the delivery challan.</p>")], "zenith"),
        new("renewal", 9, 11, "Email", "senthil", "erp", null, "Licence Renewal", "Medium", "AMC renewal quote request",
            "<p>Our AMC has expired. Please send the renewal quote for the next year and confirm whether support continues meanwhile.</p>",
            [new(3, "reply", "sana", "<p>Hi Senthil, the renewal quote is attached. Support continues for 15 days after expiry as a courtesy.</p>"),
             new(50, "resolve", "sana", "fixed|third_party|<p>Quote shared; customer will process the PO.</p>"), new(130, "close", "sana")], "pioneer"),
        new("migration", 15, 10, "Portal", "anita", "erp", "Inventory", "Data correction", "Medium", "Opening stock migration for new warehouse",
            "<p>We are opening a new warehouse in Nashik and need help migrating 4,200 SKUs with batch and expiry data.</p>",
            [new(4, "transfer", "priya", "implementation"), new(5, "reply", "rahul", "<p>Hi Anita, I've shared the migration template. Please fill batch/expiry columns as dd-mm-yyyy.</p>"),
             new(30, "customer", null, "<p>Template filled and uploaded.</p>"), new(48, "note", "rahul", "<p>Validated: 37 SKUs have missing HSN codes — sent back.</p>"),
             new(52, "reply", "rahul", "<p>37 SKUs are missing HSN codes (list attached). Once fixed, we'll run the import in UAT first.</p>"), new(53, "status", "rahul", "Waiting on Customer")], "sunrise"),
        new("perf", 21, 16, "Email", "thomas", "erp", "Finance", "Performance", "High", "Month-end trial balance takes 40 minutes",
            "<p>Trial balance for the hospital group takes 40+ minutes since last week. It used to take 5.</p>",
            [new(0.4, "reply", "karthik", "<p>Dr. Thomas, we're analysing the database. Could we get a 30-minute remote session today?</p>"),
             new(6, "note", "karthik", "<p>Index fragmentation 92% on GL_Transactions; stats out of date since the SQL agent job was disabled.</p>"),
             new(8, "resolve", "karthik", "fixed|environment|<p>Rebuilt indexes, re-enabled the maintenance job. Trial balance now runs in 3 min 40 s.</p>"),
             new(20, "close", "karthik")], "coastal"),
        new("bank", 30, 13, "Email", "manoj", "books", "Banking", "Calculation mismatch", "Low", "Bank reconciliation shows duplicate entries",
            "<p>After importing the HDFC statement, 12 entries appear twice in the reconciliation screen.</p>",
            [new(5, "reply", "arjun", "<p>Hi Manoj, the statement was imported twice (two files with overlapping dates). I've shared steps to undo the second import.</p>"),
             new(30, "resolve", "arjun", "user_training|user_error|<p>Customer removed the duplicate import.</p>"), new(160, "close", "arjun")], null),
        new("feature", 40, 10, "Portal", "lata", "pos", "Loyalty", "Feedback", "Low", "Allow loyalty points redemption on partial payments",
            "<p>Customers want to redeem loyalty points together with UPI for the balance. Currently points can only cover the full bill.</p>",
            [new(24, "transfer", "vikram", "engineering"), new(30, "note", "lakshmi", "<p>Added to the 8.1 roadmap backlog.</p>")], "abc:pos"),
        new("reopen", 12, 9, "Email", "imran", "erp", "Finance", "Calculation mismatch", "Medium", "TDS deduction not reflected in vendor ledger",
            "<p>TDS deducted on vendor payment voucher PV-2231 is not visible in the vendor ledger.</p>",
            [new(2, "reply", "arjun", "<p>Hi Imran, please re-post the voucher after enabling 'Post TDS to ledger' in the TDS section.</p>"),
             new(26, "resolve", "arjun", "configuration|configuration|<p>Configuration change advised.</p>"),
             new(50, "customer", null, "<p>Re-posted, but the ledger still doesn't show the TDS line for 3 vendors.</p>"),
             new(54, "note", "arjun", "<p>Reopened by customer. The 3 vendors have lower-deduction certificates — different path.</p>")], "metro"),
        new("vip", 0.6, 14, "Email", "thomas", "erp", "Finance", "Application Error", "Critical", "Billing module down at main campus — cannot raise invoices",
            "<p>Since 1 PM no one at the main campus can raise patient invoices. Error 'Object reference not set to an instance of an object' on save.</p>",
            [new(0.05, "reply", "meera", "<p>Dr. Thomas, we've started investigating immediately and will call you within 15 minutes.</p>"),
             new(0.2, "note", "meera", "<p>Stack trace points to the new discount scheme master — null scheme for walk-in patients.</p>")], "coastal")
    ];

    private async Task SeedTicketsAsync(DemoSeedContext c, CancellationToken ct)
    {
        foreach (var s in Stories) await PlayStoryAsync(c, s, ct);
        await SeedVolumeAsync(c, ct);
    }

    private async Task PlayStoryAsync(DemoSeedContext c, Story s, CancellationToken ct)
    {
        var created = c.DaysAgo(s.DaysAgo, s.Hour);
        if (created > c.Anchor) created = c.Anchor.AddHours(-3);
        c.At(created);
        var customerKey = CustomerData.First(x => x.People.Any(p => p.Key == s.Contact)).Key;
        var request = new CreateTicketRequest
        {
            Subject = s.Subject, DescriptionHtml = s.Body, Channel = s.Channel, ContactId = c.Contacts[s.Contact], CustomerId = c.Customers[customerKey],
            ProductId = c.Products[s.Product], ModuleId = s.Module is null ? null : c.Modules[$"{s.Product}:{s.Module}"],
            InstallationId = s.Installation is null ? null : c.Installations[s.Installation], CategoryId = c.Categories.GetValueOrDefault(s.Category),
            Priority = s.Priority, CustomFields = new Dictionary<string, System.Text.Json.JsonElement> { ["environment"] = Json.ToElement("Production") }
        };
        if (c.Categories.TryGetValue(s.Category, out var catId))
        {
            await c.AsSystemAsync(async sp =>
            {
                var db = sp.GetRequiredService<DeskDbContext>();
                var cat = await db.TicketCategories.AsNoTracking().FirstAsync(x => x.Id == catId, ct);
                if (cat.ParentId is { } parent) request = request with { CategoryId = parent, SubcategoryId = cat.Id, DepartmentId = cat.DepartmentId };
                else request = request with { DepartmentId = cat.DepartmentId };
            });
        }
        Guid ticketId = default;
        if (s.Channel is "Portal")
            await c.AsContactAsync(s.Contact, async sp => ticketId = (await sp.GetRequiredService<TicketService>().CreateAsync(request,
                new TicketCreationSource(CreateInitialThread: true, PortalSubmission: true, EnforceRequiredFields: false), ct)).Id);
        else
            await c.AsSystemAsync(async sp => ticketId = (await sp.GetRequiredService<TicketService>().CreateAsync(request,
                new TicketCreationSource(CreateInitialThread: s.Channel is "Email" or "WhatsApp", EnforceRequiredFields: false), ct)).Id);
        c.Tickets[s.Key] = ticketId;

        foreach (var step in s.Steps)
        {
            var at = created.AddHours(step.AfterHours);
            if (at > c.Anchor) break;
            c.At(at);
            try
            {
                await RunStepAsync(c, ticketId, s.Contact, step, ct);
            }
            catch (DomainException ex)
            {
                // Expected for steps that need approval (e.g. transfers into Product Development); logged, not fatal.
                logger.LogInformation("Demo story {Story} step {Step}: {Message}", s.Key, step.Action, ex.Message);
            }
        }
    }

    private static async Task RunStepAsync(DemoSeedContext c, Guid ticketId, string contactKey, Step step, CancellationToken ct)
    {
        switch (step.Action)
        {
            case "reply":
                await c.AsAgentAsync(step.Who!, sp => sp.GetRequiredService<ConversationService>().ReplyAsync(ticketId, new ReplyRequest { BodyHtml = step.Text!, Force = true }, ct));
                break;
            case "note":
                await c.AsAgentAsync(step.Who!, sp => sp.GetRequiredService<ConversationService>().AddNoteAsync(ticketId, new NoteRequest { BodyHtml = step.Text! }, ct));
                break;
            case "customer":
                await c.AsContactAsync(contactKey, async sp =>
                {
                    var contact = await sp.GetRequiredService<DeskDbContext>().Contacts.AsNoTracking().FirstAsync(x => x.Id == c.Contacts[contactKey], ct);
                    await sp.GetRequiredService<ConversationService>().AddCustomerMessageAsync(ticketId,
                        new CustomerMessageInput(step.Text, null, Channel.Email, contact.Id, contact.FullName, contact.Email), ct);
                });
                break;
            case "assign":
                await c.AsAgentAsync("priya", sp => sp.GetRequiredService<TicketService>().AssignAsync(ticketId, new AssignTicketRequest(c.Users[step.Who!], null, null), ct));
                break;
            case "status":
                await c.AsAgentAsync(step.Who!, sp => sp.GetRequiredService<TicketService>().ChangeStatusAsync(ticketId, new ChangeStatusRequest(c.Statuses[step.Text!], null, null, null), ct));
                break;
            case "resolve":
            {
                var parts = step.Text!.Split('|', 3);
                await c.AsAgentAsync(step.Who!, sp => sp.GetRequiredService<TicketService>().ResolveAsync(ticketId, new ResolveTicketRequest(parts[0], parts[1], parts[2]), ct));
                break;
            }
            case "close":
                await c.AsAgentAsync(step.Who!, sp => sp.GetRequiredService<TicketService>().CloseAsync(ticketId, ct));
                break;
            case "escalate":
                await c.AsAgentAsync(step.Who!, sp => sp.GetRequiredService<TicketService>().EscalateAsync(ticketId, new EscalateTicketRequest(step.Text!, c.Users["priya"], false), ct));
                break;
            case "transfer":
                await c.AsAgentAsync(step.Who!, sp => sp.GetRequiredService<TicketService>().TransferAsync(ticketId,
                    new TransferTicketRequest(c.Departments[step.Text!], null, null, "Routing to the right team"), ct));
                break;
        }
    }

    /// <summary>Background volume (~180 tickets over 90 days) so dashboards, trends and SLA compliance have realistic data.</summary>
    private async Task SeedVolumeAsync(DemoSeedContext c, CancellationToken ct)
    {
        var rng = new Random(2026);
        var subjects = new (string Subject, string Category, string Product, string? Module)[]
        {
            ("Stock valuation differs between reports", "Calculation mismatch", "erp", "Inventory"), ("Unable to print delivery challan", "Crash or error message", "erp", "Sales"),
            ("GSTR-1 JSON upload rejected", "GST portal / e-invoice", "erp", "GST & Compliance"), ("New user needs access to purchase approvals", "Permissions", "erp", "Purchase"),
            ("Payslip PDF shows wrong month", "Calculation mismatch", "erp", "Payroll"), ("Dashboard tiles not loading", "Dashboards", "erp", "Finance"),
            ("POS not syncing sales to head office", "API", "pos", "Store Sync"), ("Tally import fails for ledger masters", "Tally import", "books", "Accounting"),
            ("Request to correct posted voucher date", "Data correction", "erp", "Finance"), ("Slow login in the mornings", "Performance", "erp", null),
            ("Barcode labels misaligned on new printer", "Crash or error message", "pos", "Billing Counter"), ("Need training for new accounts team", "Training", "erp", "Finance"),
            ("Invoice copy for last quarter", "Invoice Query", "erp", null), ("Payment made but licence still shows expiring", "Payment", "erp", null),
            ("Upgrade from 7.3 to 8.0 — plan required", "Upgrade Request", "erp", null), ("Loyalty points not credited", "Calculation mismatch", "pos", "Loyalty")
        };
        var contacts = CustomerData.SelectMany(cd => cd.People.Select(p => (cd.Key, p.Key))).ToList();
        var agentsByDepartment = new Dictionary<Guid, string[]>
        {
            [c.Departments["support"]] = ["arjun", "meera", "karthik", "priya"],
            [c.Departments["implementation"]] = ["rahul", "divya", "karthik"],
            [c.Departments["billing"]] = ["sana"],
            [c.Departments["success"]] = ["vikram"],
            [c.Departments["engineering"]] = ["lakshmi"]
        };
        var channels = new[] { "Email", "Email", "Email", "Portal", "Portal", "WhatsApp", "Phone", "Chat", "WebForm" };
        var priorities = new[] { "Low", "Medium", "Medium", "Medium", "High", "High", "Critical" };
        for (var i = 0; i < 180; i++)
        {
            var (customerKey, contactKey) = contacts[rng.Next(contacts.Count)];
            var pick = subjects[rng.Next(subjects.Length)];
            var daysAgo = 3 + rng.NextDouble() * 87;
            var created = c.DaysAgo(daysAgo, 9 + rng.Next(9), rng.Next(60));
            c.At(created);
            var channel = channels[rng.Next(channels.Length)];
            var priority = priorities[rng.Next(priorities.Length)];
            var catId = c.Categories[pick.Category];
            Guid ticketId = default;
            Guid departmentId = default;
            await c.AsSystemAsync(async sp =>
            {
                var db = sp.GetRequiredService<DeskDbContext>();
                var cat = await db.TicketCategories.AsNoTracking().FirstAsync(x => x.Id == catId, ct);
                departmentId = cat.DepartmentId!.Value;
                var t = await sp.GetRequiredService<TicketService>().CreateAsync(new CreateTicketRequest
                {
                    Subject = pick.Subject, DescriptionHtml = $"<p>{pick.Subject}. Reported by {contactKey} ({customerKey}).</p>", Channel = channel,
                    ContactId = c.Contacts[contactKey], CustomerId = c.Customers[customerKey], ProductId = c.Products[pick.Product],
                    ModuleId = pick.Module is null ? null : c.Modules[$"{pick.Product}:{pick.Module}"], CategoryId = cat.ParentId ?? cat.Id,
                    SubcategoryId = cat.ParentId is null ? null : cat.Id, DepartmentId = cat.DepartmentId, Priority = priority,
                    InstallationId = pick.Product == "erp" ? c.Installations[customerKey] : null
                }, new TicketCreationSource(CreateInitialThread: channel is not "Phone", EnforceRequiredFields: false), ct);
                ticketId = t.Id;
            });

            // Most tickets get a first response within a few business hours; some are slow (SLA breaches).
            var slow = rng.NextDouble() < 0.12;
            var firstResponseHours = slow ? 6 + rng.NextDouble() * 30 : 0.2 + rng.NextDouble() * 3;
            var deptAgents = agentsByDepartment[departmentId];
            var agent = deptAgents[rng.Next(deptAgents.Length)];
            var replyAt = created.AddHours(firstResponseHours);
            if (replyAt < c.Anchor)
            {
                c.At(replyAt);
                await TryAsync(() => c.AsAgentAsync(agent, sp => sp.GetRequiredService<ConversationService>().ReplyAsync(ticketId,
                    new ReplyRequest { BodyHtml = "<p>Thanks for reaching out — we're looking into this and will update you shortly.</p>", Force = true }, ct)));
            }
            if (daysAgo < 4 && rng.NextDouble() < 0.6) continue; // recent tickets stay open
            var resolveAt = replyAt.AddHours(slow ? 30 + rng.NextDouble() * 60 : 2 + rng.NextDouble() * 22);
            if (resolveAt >= c.Anchor) continue;
            if (rng.NextDouble() < 0.18)
            {
                c.At(replyAt.AddHours(1 + rng.NextDouble() * 8));
                await TryAsync(() => c.AsContactAsync(contactKey, async sp =>
                {
                    var contact = await sp.GetRequiredService<DeskDbContext>().Contacts.AsNoTracking().FirstAsync(x => x.Id == c.Contacts[contactKey], ct);
                    await sp.GetRequiredService<ConversationService>().AddCustomerMessageAsync(ticketId,
                        new CustomerMessageInput("<p>Any update on this please?</p>", null, Channel.Email, contact.Id, contact.FullName, contact.Email), ct);
                }));
            }
            c.At(resolveAt);
            var codes = new[] { "fixed|product_defect", "configuration|configuration", "user_training|user_error", "workaround|environment", "fixed|data_issue", "defect_fixed_in_release|product_defect" };
            var code = codes[rng.Next(codes.Length)].Split('|');
            await TryAsync(() => c.AsAgentAsync(agent, sp => sp.GetRequiredService<TicketService>().ResolveAsync(ticketId,
                new ResolveTicketRequest(code[0], code[1], "<p>Resolved — please let us know if anything else is needed.</p>"), ct)));
            if (rng.NextDouble() < 0.55)
            {
                var score = rng.NextDouble() switch { < 0.06 => 2, < 0.15 => 3, < 0.45 => 4, _ => 5 };
                await c.AsSystemAsync(async sp =>
                {
                    var db = sp.GetRequiredService<DeskDbContext>();
                    var t = await db.Tickets.FirstAsync(x => x.Id == ticketId, ct);
                    t.CsatScore = score;
                    t.CsatComment = score >= 4 ? "Quick and helpful." : score == 3 ? "Resolved, but took a while." : "Had to follow up several times.";
                    db.SatisfactionResponses.Add(new SatisfactionResponse
                    {
                        TicketId = ticketId, ContactId = t.ContactId, AgentUserId = t.AssigneeId, TokenHash = TextNormalization.Sha256Hex(Guid.NewGuid().ToString()),
                        Rating = score, Comment = t.CsatComment, Channel = Channel.Email, SentAt = resolveAt, RespondedAt = resolveAt.AddHours(3), ExpiresAt = resolveAt.AddDays(14)
                    });
                    await db.SaveChangesAsync(ct);
                });
            }
            if (resolveAt.AddDays(5) < c.Anchor && rng.NextDouble() < 0.7)
            {
                c.At(resolveAt.AddDays(5));
                await TryAsync(() => c.AsAgentAsync(agent, sp => sp.GetRequiredService<TicketService>().CloseAsync(ticketId, ct)));
            }
        }
    }

    private static async Task TryAsync(Func<Task> action)
    {
        try { await action(); }
        catch (DomainException) { /* scenario step not applicable (e.g. permission/state) — skip */ }
    }

    // ───────────────────────────── Second tenant ─────────────────────────────

    private async Task SeedSecondTenantAsync(MutableClock clock, CancellationToken ct)
    {
        var nw = await ProvisionAsync("northwind", "Northwind Traders", "starter", "admin@northwind.demo", "Nora Winters (Admin)", "Europe/London", "GB", ["Customer Care"], ct);
        var c = new DemoSeedContext(scopes, clock, nw.TenantId, "northwind", DateTime.UtcNow);
        await c.AsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var cust = new Customer { Name = "Contoso Cafés", Code = "NW-01", Tier = "Gold", DomainsJson = Json.Serialize(new[] { "contosocafes.demo" }) };
            db.Customers.Add(cust);
            var contact = new Contact { CustomerId = cust.Id, FullName = "Olivia Brown", Email = "olivia@contosocafes.demo", EmailNormalized = "olivia@contosocafes.demo",
                PortalAccess = PortalAccessScope.Organisation, PortalPasswordHash = sp.GetRequiredService<PasswordService>().Hash(PortalPassword) };
            db.Contacts.Add(contact);
            await db.SaveChangesAsync(ct);
            c.Contacts["olivia"] = contact.Id;
            c.Customers["contoso"] = cust.Id;
        });
        foreach (var (subject, days) in new[] { ("Order 4471 delivered damaged", 2.0), ("Change delivery address for weekly order", 5.0), ("Invoice shows wrong VAT", 9.0) })
        {
            clock.Pin(DateTime.UtcNow.AddDays(-days));
            await c.AsContactAsync("olivia", sp => sp.GetRequiredService<TicketService>().CreateAsync(new CreateTicketRequest
            {
                Subject = subject, DescriptionHtml = $"<p>{subject}.</p>", Channel = "Portal", CustomerId = c.Customers["contoso"]
            }, new TicketCreationSource(CreateInitialThread: true, PortalSubmission: true, EnforceRequiredFields: false), ct));
        }
        clock.Unpin();
    }

    private static string Key(string departmentName) => departmentName switch
    {
        "Technical Support" => "support",
        "Implementation" => "implementation",
        "Billing" => "billing",
        "Customer Success" => "success",
        "Product Development" => "engineering",
        _ => TextNormalization.Slugify(departmentName)
    };
}
