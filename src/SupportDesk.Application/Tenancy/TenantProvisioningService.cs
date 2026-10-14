using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Domain;
using SupportDesk.Domain.Ai;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Organisation;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Sla;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Tenancy;

public sealed record ProvisionTenantRequest(
    string Slug,
    string Name,
    string PlanCode,
    string AdminEmail,
    string AdminName,
    string AdminPassword,
    string TimeZone = "UTC",
    string? Country = null,
    bool Trial = true,
    int Seats = 5,
    IReadOnlyList<string>? Departments = null,
    bool DemoMode = false);

public sealed record ProvisionedTenant(Guid TenantId, Guid AdminUserId, Guid BrandId, IReadOnlyDictionary<string, Guid> Roles,
    IReadOnlyDictionary<string, Guid> Departments, Guid DefaultCalendarId, Guid DefaultSlaPolicyId);

/// <summary>Global catalogs (plans, permission list). Idempotent.</summary>
public sealed class PlatformCatalogService(DeskDbContext db)
{
    public async Task EnsureAsync(CancellationToken ct = default)
    {
        var existing = await db.Permissions.Select(p => p.Code).ToListAsync(ct);
        foreach (var p in Permissions.Catalog.Where(p => !existing.Contains(p.Code)))
            db.Permissions.Add(new Permission { Code = p.Code, Module = p.Module, Description = p.Description, IsSensitive = p.Sensitive });

        var plans = await db.Plans.Select(p => p.Code).ToListAsync(ct);
        void AddPlan(string code, string name, decimal monthly, int? agents, int? portalUsers, long? storageMb, int? channels, long? aiTokens,
            int? attachMb, long? apiCalls, int? brands, int trialDays, int order, params string[] features)
        {
            if (plans.Contains(code)) return;
            db.Plans.Add(new Plan
            {
                Code = code, Name = name, MonthlyPricePerAgent = monthly, AnnualPricePerAgent = monthly * 10, MaxAgents = agents, MaxPortalUsers = portalUsers,
                StorageMb = storageMb, MaxChannels = channels, AiTokensMonthly = aiTokens, MaxAttachmentMb = attachMb, ApiCallsMonthly = apiCalls, MaxBrands = brands,
                TrialDays = trialDays, FeaturesJson = Json.Serialize(features), SortOrder = order,
                Description = $"{name} plan"
            });
        }
        string[] all = [PlanFeatures.Automation, PlanFeatures.Blueprints, PlanFeatures.Ai, PlanFeatures.AiAgents, PlanFeatures.Sso, PlanFeatures.AdvancedAnalytics,
            PlanFeatures.MultiBrand, PlanFeatures.Community, PlanFeatures.Telephony, PlanFeatures.ErpIntegration, PlanFeatures.EngineeringIntegration,
            PlanFeatures.AuditExport, PlanFeatures.CustomRoles];
        AddPlan("trial", "Trial", 0, 10, 500, 2048, 5, 200_000, 20, 50_000, 2, 30, 0, all);
        AddPlan("starter", "Starter", 15, 10, 1000, 10_240, 3, 100_000, 20, 100_000, 1, 0, 1, PlanFeatures.Automation, PlanFeatures.Community);
        AddPlan("professional", "Professional", 35, 100, 20_000, 102_400, 15, 2_000_000, 50, 1_000_000, 3, 0, 2,
            PlanFeatures.Automation, PlanFeatures.Blueprints, PlanFeatures.Ai, PlanFeatures.AdvancedAnalytics, PlanFeatures.MultiBrand, PlanFeatures.Community,
            PlanFeatures.Telephony, PlanFeatures.ErpIntegration, PlanFeatures.EngineeringIntegration, PlanFeatures.CustomRoles);
        AddPlan("enterprise", "Enterprise", 60, null, null, 1_048_576, null, 20_000_000, 100, 10_000_000, null, 0, 3, [.. all, PlanFeatures.DedicatedDatabase]);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Creates a tenant with a complete, working default configuration: subscription, roles and permissions, business
/// calendars, statuses, a department and team, an SLA policy, lookups, a brand/portal, AI settings and the first administrator.
/// </summary>
public sealed class TenantProvisioningService(DeskDbContext db, RequestContext ctx, PasswordService passwords, PlatformCatalogService catalog, TenantDatabaseDirectoryAccessor directory)
{
    public async Task<ProvisionedTenant> ProvisionAsync(ProvisionTenantRequest r, CancellationToken ct = default)
    {
        await catalog.EnsureAsync(ct);
        var slug = TextNormalization.Slugify(r.Slug, 40);
        if (slug.Length < 2) throw new DomainException("validation_failed", "The organisation identifier must have at least 2 characters.");
        if (await db.Tenants.AnyAsync(t => t.Slug == slug, ct) || await db.AllTenants<Brand>().AnyAsync(b => b.Slug == slug, ct))
            throw new ConflictException("slug_taken", $"The identifier '{slug}' is already in use.");
        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Code == r.PlanCode, ct) ?? throw new NotFoundException("Plan", r.PlanCode);
        var now = db.Clock.UtcNow;

        var tenant = new Tenant
        {
            Slug = slug, Name = r.Name, Status = r.Trial ? TenantStatus.Trial : TenantStatus.Active, DefaultTimeZone = r.TimeZone, Country = r.Country
        };
        ctx.EnterPlatformScope();
        ctx.SwitchTenant(tenant.Id);
        db.Tenants.Add(tenant);

        db.Subscriptions.Add(new Subscription
        {
            PlanId = plan.Id, Status = r.Trial ? SubscriptionStatus.Trialing : SubscriptionStatus.Active, Seats = r.Seats, StartedAt = now,
            TrialEndsAt = r.Trial ? now.AddDays(Math.Max(plan.TrialDays, 14)) : null, CurrentPeriodStart = now, CurrentPeriodEnd = now.AddMonths(1)
        });

        // Roles
        var roles = new Dictionary<string, Guid>();
        void AddRole(string name, string description, TicketScope scope, CustomerScope custScope, IEnumerable<string> perms)
        {
            var role = new Role { Name = name, Description = description, IsSystem = true, TicketScope = scope, CustomerScope = custScope };
            db.Roles.Add(role);
            foreach (var p in perms.Distinct()) db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionCode = p });
            roles[name] = role.Id;
        }
        AddRole("Administrator", "Full access to every module and setting", TicketScope.All, CustomerScope.All, Permissions.Templates.Administrator);
        AddRole("Supervisor", "Team leads: monitoring, approvals, bulk actions, team analytics", TicketScope.Departments, CustomerScope.All, Permissions.Templates.Supervisor);
        AddRole("Agent", "Support agents working tickets in their departments", TicketScope.Departments, CustomerScope.All, Permissions.Templates.Agent);
        AddRole("Light Agent", "Collaborators: view tickets and add internal notes only", TicketScope.Departments, CustomerScope.All, Permissions.Templates.LightAgent);
        AddRole("Knowledge Manager", "Owns the knowledge base and community", TicketScope.All, CustomerScope.All, Permissions.Templates.KnowledgeManager);
        AddRole("Analyst", "Read-only reporting across the organisation", TicketScope.All, CustomerScope.All, Permissions.Templates.Analyst);

        // Calendars
        var business = new BusinessCalendar
        {
            Name = "Business hours (Mon–Fri 09:00–18:00)", TimeZone = r.TimeZone, IsDefault = true,
            WeeklyHoursJson = WorkingCalendar.WeeklyToJson(WorkingCalendar.WeekdaysCalendar(r.TimeZone, TimeSpan.FromHours(9), TimeSpan.FromHours(18)).Weekly)
        };
        var always = new BusinessCalendar { Name = "24x7", TimeZone = r.TimeZone, Is24x7 = true, WeeklyHoursJson = "{}" };
        db.BusinessCalendars.AddRange(business, always);

        // Statuses
        var statuses = new List<TicketStatus>
        {
            new() { Name = "Open", Category = StatusCategory.Open, Color = "#2563eb", SortOrder = 1, IsDefault = true, IsSystem = true, CustomerLabel = "Received" },
            new() { Name = "Assigned", Category = StatusCategory.Assigned, Color = "#7c3aed", SortOrder = 2, IsSystem = true, CustomerLabel = "In progress" },
            new() { Name = "In Progress", Category = StatusCategory.InProgress, Color = "#0891b2", SortOrder = 3, IsSystem = true, CustomerLabel = "In progress" },
            new() { Name = "Waiting on Customer", Category = StatusCategory.Waiting, Color = "#d97706", SortOrder = 4, PausesSla = true, IsSystem = true,
                CustomerLabel = "Awaiting your reply", SubstatusesJson = Json.Serialize(new[] { "Information requested", "Confirmation requested", "Scheduling" }) },
            new() { Name = "Waiting on Third Party", Category = StatusCategory.Waiting, Color = "#ca8a04", SortOrder = 5, PausesSla = false, IsSystem = false,
                CustomerLabel = "In progress", SubstatusesJson = Json.Serialize(new[] { "Engineering", "Vendor", "Partner" }) },
            new() { Name = "Resolved", Category = StatusCategory.Resolved, Color = "#16a34a", SortOrder = 6, IsSystem = true, CustomerLabel = "Resolved" },
            new() { Name = "Closed", Category = StatusCategory.Closed, Color = "#64748b", SortOrder = 7, IsSystem = true, CustomerLabel = "Closed" }
        };
        db.TicketStatuses.AddRange(statuses);

        // SLA policy (illustrative defaults from the specification)
        var sla = new SlaPolicy
        {
            Name = "Standard SLA", Description = "Default commitments by priority", IsDefault = true, EvaluationOrder = 1000, CalendarId = business.Id,
            PauseStatusIdsJson = Json.Serialize(statuses.Where(s => s.PausesSla).Select(s => s.Id))
        };
        sla.Targets.AddRange(
        [
            new SlaTarget { PolicyId = sla.Id, Priority = TicketPriority.Critical, FirstResponseMinutes = 15, NextResponseMinutes = 60, ResolutionMinutes = 240, BusinessHours = false },
            new SlaTarget { PolicyId = sla.Id, Priority = TicketPriority.High, FirstResponseMinutes = 60, NextResponseMinutes = 240, ResolutionMinutes = 480, BusinessHours = true },
            new SlaTarget { PolicyId = sla.Id, Priority = TicketPriority.Medium, FirstResponseMinutes = 240, NextResponseMinutes = 480, ResolutionMinutes = 24 * 60, BusinessHours = true },
            new SlaTarget { PolicyId = sla.Id, Priority = TicketPriority.Low, FirstResponseMinutes = 480, NextResponseMinutes = 960, ResolutionMinutes = 3 * 9 * 60, BusinessHours = true }
        ]);
        sla.Escalations.AddRange(
        [
            new SlaEscalationLevel { PolicyId = sla.Id, Milestone = SlaMilestoneKind.Resolution, Level = 1, MinutesAfterBreach = 0, Action = EscalationAction.Notify,
                NotifyJson = Json.Serialize(new { notifyAssignee = true, notifySupervisors = true }) },
            new SlaEscalationLevel { PolicyId = sla.Id, Milestone = SlaMilestoneKind.Resolution, Level = 2, MinutesAfterBreach = 120, Action = EscalationAction.RaisePriority,
                NotifyJson = Json.Serialize(new { notifySupervisors = true }) },
            new SlaEscalationLevel { PolicyId = sla.Id, Milestone = SlaMilestoneKind.FirstResponse, Level = 1, MinutesAfterBreach = 0, Action = EscalationAction.Notify,
                NotifyJson = Json.Serialize(new { notifyAssignee = true, notifySupervisors = true }) }
        ]);
        db.SlaPolicies.Add(sla);

        // Departments + default teams
        var departments = new Dictionary<string, Guid>();
        var deptNames = r.Departments is { Count: > 0 } ? r.Departments : ["Support"];
        var order = 0;
        foreach (var name in deptNames)
        {
            var dept = new Department
            {
                Name = name, Code = TextNormalization.Slugify(name, 20).Replace("-", "").ToUpperInvariant()[..Math.Min(8, TextNormalization.Slugify(name, 20).Replace("-", "").Length)],
                BusinessCalendarId = business.Id, DefaultSlaPolicyId = sla.Id, SortOrder = order++
            };
            var team = new Team { DepartmentId = dept.Id, Name = $"{name} – Level 1", RoutingStrategy = RoutingStrategy.LoadBalanced };
            dept.DefaultTeamId = team.Id;
            db.Departments.Add(dept);
            db.Teams.Add(team);
            departments[name] = dept.Id;
        }

        // Lookups
        void Lookup(string type, params (string Value, string Label)[] items)
        {
            var i = 0;
            foreach (var (v, l) in items) db.LookupValues.Add(new LookupValue { Type = type, Value = v, Label = l, SortOrder = i++ });
        }
        Lookup("resolution_code", ("fixed", "Fixed"), ("configuration", "Configuration change"), ("workaround", "Workaround provided"), ("user_training", "User guidance / training"),
            ("duplicate", "Duplicate"), ("not_reproducible", "Could not reproduce"), ("defect_fixed_in_release", "Defect fixed in release"), ("no_response", "No customer response"));
        Lookup("root_cause", ("product_defect", "Product defect"), ("configuration", "Configuration"), ("data_issue", "Data issue"), ("user_error", "User error"),
            ("environment", "Environment / infrastructure"), ("integration", "Integration"), ("documentation_gap", "Documentation gap"), ("third_party", "Third-party"));
        Lookup("time_activity", ("investigation", "Investigation"), ("remote_session", "Remote session"), ("onsite", "On-site visit"), ("configuration", "Configuration"),
            ("training", "Training"), ("communication", "Customer communication"));
        Lookup("close_reason", ("resolved", "Resolved"), ("customer_confirmed", "Confirmed by customer"), ("auto_closed", "Auto-closed after inactivity"), ("spam", "Spam"));

        // Brand / portal
        var brand = new Brand
        {
            Name = r.Name, Slug = slug, IsDefault = true, PortalTitle = $"{r.Name} Help Center", DefaultDepartmentId = departments.Values.First(),
            PortalWelcome = "Search our knowledge base or raise a ticket — we're here to help.", SupportEmail = r.AdminEmail
        };
        db.Brands.Add(brand);

        // AI configuration (built-in local provider works offline; switch to OpenAI/Azure/Anthropic in settings)
        db.AiConfigurations.Add(new AiConfiguration
        {
            FeaturesJson = Json.Serialize(AiFeatures.All.ToDictionary(f => f, f => new { enabled = true, autoApply = false, minConfidence = 0.75 }))
        });

        if (r.DemoMode) db.TenantSettings.Add(new TenantSetting { Key = SettingKeys.Security, ValueJson = Json.Serialize(new SecuritySettings { DemoMode = true }) });

        // First administrator
        var admin = new User
        {
            Email = r.AdminEmail, EmailNormalized = TextNormalization.NormalizeEmail(r.AdminEmail)!, DisplayName = r.AdminName, Kind = UserKind.Administrator,
            TimeZone = r.TimeZone, PasswordHash = passwords.Hash(r.AdminPassword), PasswordChangedAt = now
        };
        db.Users.Add(admin);
        db.UserRoles.Add(new UserRole { UserId = admin.Id, RoleId = roles["Administrator"] });
        db.Agents.Add(new Agent { UserId = admin.Id, Title = "Administrator" });
        foreach (var d in departments.Values) db.AgentDepartments.Add(new AgentDepartment { UserId = admin.Id, DepartmentId = d, IsSupervisor = true });

        await db.SaveChangesAsync(ct);
        directory.Invalidate();
        return new ProvisionedTenant(tenant.Id, admin.Id, brand.Id, roles, departments, business.Id, sla.Id);
    }
}

/// <summary>Thin indirection so provisioning can invalidate the tenant database directory cache.</summary>
public sealed class TenantDatabaseDirectoryAccessor(Persistence.Infrastructure.TenantDatabaseDirectory directory)
{
    public void Invalidate() => directory.Invalidate();
}
