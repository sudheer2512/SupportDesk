namespace SupportDesk.Domain.Rules;

public sealed record PermissionInfo(string Code, string Module, string Description, bool Sensitive = false);

/// <summary>
/// Permission catalog. Checks are always enforced in the backend (services and API filters); the UI only hides
/// actions. Codes are stable strings stored in RolePermission.
/// </summary>
public static class Permissions
{
    // Tickets
    public const string TicketsView = "tickets.view";
    public const string TicketsCreate = "tickets.create";
    public const string TicketsEdit = "tickets.edit";
    public const string TicketsReply = "tickets.reply";
    public const string TicketsNote = "tickets.note";
    public const string TicketsAssign = "tickets.assign";
    public const string TicketsTransfer = "tickets.transfer";
    public const string TicketsEscalate = "tickets.escalate";
    public const string TicketsResolve = "tickets.resolve";
    public const string TicketsReopen = "tickets.reopen";
    public const string TicketsMerge = "tickets.merge";
    public const string TicketsSplit = "tickets.split";
    public const string TicketsDelete = "tickets.delete";
    public const string TicketsBulk = "tickets.bulk";
    public const string TicketsTime = "tickets.time";
    public const string TicketsViewCosts = "tickets.view_costs";
    public const string TicketsOverrideEntitlement = "tickets.override_entitlement";
    public const string TicketsExport = "tickets.export";

    // Customers & products
    public const string CustomersView = "customers.view";
    public const string CustomersManage = "customers.manage";
    public const string ContactsMerge = "contacts.merge";
    public const string CustomersMerge = "customers.merge";
    public const string ProductsView = "products.view";
    public const string ProductsManage = "products.manage";
    public const string ContractsView = "contracts.view";
    public const string ContractsManage = "contracts.manage";

    // Knowledge
    public const string KbViewInternal = "kb.view_internal";
    public const string KbAuthor = "kb.author";
    public const string KbReview = "kb.review";
    public const string KbPublish = "kb.publish";
    public const string CommunityModerate = "community.moderate";

    // Collaboration
    public const string ApprovalsDecide = "approvals.decide";
    public const string SupervisorMonitor = "supervisor.monitor";
    public const string AgentsManage = "agents.manage";
    public const string EngineeringLink = "engineering.link";
    public const string EngineeringView = "engineering.view";
    public const string EngineeringManage = "engineering.manage";

    // Analytics
    public const string AnalyticsView = "analytics.view";
    public const string AnalyticsTeam = "analytics.team";
    public const string AnalyticsExport = "analytics.export";
    public const string AnalyticsSchedule = "analytics.schedule";

    // AI
    public const string AiUse = "ai.use";
    public const string AiApprove = "ai.approve_actions";

    // Administration
    public const string AdminUsers = "admin.users";
    public const string AdminRoles = "admin.roles";
    public const string AdminOrganisation = "admin.organisation";
    public const string AdminTicketConfig = "admin.ticket_config";
    public const string AdminSla = "admin.sla";
    public const string AdminAutomation = "admin.automation";
    public const string AdminChannels = "admin.channels";
    public const string AdminKnowledge = "admin.knowledge";
    public const string AdminAi = "admin.ai";
    public const string AdminIntegrations = "admin.integrations";
    public const string AdminSecurity = "admin.security";
    public const string AdminAudit = "admin.audit";
    public const string AdminCompliance = "admin.compliance";
    public const string AdminBilling = "admin.billing";
    public const string AdminConfigTransfer = "admin.config_transfer";

    public static readonly IReadOnlyList<PermissionInfo> Catalog =
    [
        new(TicketsView, "Tickets", "View tickets within the role's data scope"),
        new(TicketsCreate, "Tickets", "Create and clone tickets"),
        new(TicketsEdit, "Tickets", "Edit ticket fields"),
        new(TicketsReply, "Tickets", "Send public replies to customers"),
        new(TicketsNote, "Tickets", "Add internal notes"),
        new(TicketsAssign, "Tickets", "Assign and reassign tickets"),
        new(TicketsTransfer, "Tickets", "Transfer tickets between departments"),
        new(TicketsEscalate, "Tickets", "Escalate tickets"),
        new(TicketsResolve, "Tickets", "Resolve and close tickets"),
        new(TicketsReopen, "Tickets", "Reopen resolved/closed tickets"),
        new(TicketsMerge, "Tickets", "Merge tickets"),
        new(TicketsSplit, "Tickets", "Split tickets"),
        new(TicketsDelete, "Tickets", "Delete tickets and mark spam", true),
        new(TicketsBulk, "Tickets", "Bulk-update tickets"),
        new(TicketsTime, "Tickets", "Log time on tickets"),
        new(TicketsViewCosts, "Tickets", "See support costs and billable amounts", true),
        new(TicketsOverrideEntitlement, "Tickets", "Override support eligibility", true),
        new(TicketsExport, "Tickets", "Export ticket lists"),
        new(CustomersView, "Customers", "View customers and contacts"),
        new(CustomersManage, "Customers", "Create and edit customers, contacts and locations"),
        new(ContactsMerge, "Customers", "Merge duplicate contacts", true),
        new(CustomersMerge, "Customers", "Merge duplicate customer organisations", true),
        new(ProductsView, "Products", "View products, installations and assets"),
        new(ProductsManage, "Products", "Manage products, versions, installations and assets"),
        new(ContractsView, "Products", "View contracts and entitlements"),
        new(ContractsManage, "Products", "Manage contracts and entitlements", true),
        new(KbViewInternal, "Knowledge", "Read internal knowledge articles"),
        new(KbAuthor, "Knowledge", "Write and edit article drafts"),
        new(KbReview, "Knowledge", "Review and approve articles"),
        new(KbPublish, "Knowledge", "Publish, archive and expire articles"),
        new(CommunityModerate, "Knowledge", "Moderate community forums"),
        new(ApprovalsDecide, "Collaboration", "Approve or reject approval requests"),
        new(SupervisorMonitor, "Collaboration", "Supervisor monitoring (live queues, agent status)"),
        new(AgentsManage, "Collaboration", "Manage agent availability, shifts, leave and delegation"),
        new(EngineeringLink, "Collaboration", "Create and link engineering work items"),
        new(EngineeringView, "Collaboration", "View the engineering board, work items and releases"),
        new(EngineeringManage, "Collaboration", "Create, update and move engineering work items; publish releases"),
        new(AnalyticsView, "Analytics", "View personal dashboards and reports"),
        new(AnalyticsTeam, "Analytics", "View team, department and organisation analytics"),
        new(AnalyticsExport, "Analytics", "Export reports (CSV/Excel/PDF)"),
        new(AnalyticsSchedule, "Analytics", "Schedule report delivery"),
        new(AiUse, "AI", "Use AI assistance (summaries, suggestions, classification)"),
        new(AiApprove, "AI", "Approve, apply and revert AI-proposed actions", true),
        new(AdminUsers, "Administration", "Manage agents and users", true),
        new(AdminRoles, "Administration", "Manage roles and permissions", true),
        new(AdminOrganisation, "Administration", "Manage departments, teams, brands and calendars"),
        new(AdminTicketConfig, "Administration", "Manage ticket fields, statuses, categories and layouts"),
        new(AdminSla, "Administration", "Manage SLA policies and calendars"),
        new(AdminAutomation, "Administration", "Manage automation rules and blueprints"),
        new(AdminChannels, "Administration", "Manage email accounts and messaging channels", true),
        new(AdminKnowledge, "Administration", "Manage knowledge base settings and portals"),
        new(AdminAi, "Administration", "Manage AI models, prompts, budgets and agents", true),
        new(AdminIntegrations, "Administration", "Manage integrations, API keys, OAuth clients and webhooks", true),
        new(AdminSecurity, "Administration", "Manage password, session, MFA and SSO policies", true),
        new(AdminAudit, "Administration", "View audit logs and verify integrity"),
        new(AdminCompliance, "Administration", "Retention, legal holds, data export and erasure", true),
        new(AdminBilling, "Administration", "Manage subscription, plan and billing", true),
        new(AdminConfigTransfer, "Administration", "Export, import, publish and roll back configuration", true),
    ];

    public static readonly IReadOnlySet<string> AllCodes = Catalog.Select(p => p.Code).ToHashSet();

    /// <summary>Default role templates created for every new tenant.</summary>
    public static class Templates
    {
        public static readonly string[] LightAgent = [TicketsView, TicketsNote, CustomersView, ProductsView, KbViewInternal, AnalyticsView, EngineeringView, EngineeringManage];

        public static readonly string[] Agent =
        [
            TicketsView, TicketsCreate, TicketsEdit, TicketsReply, TicketsNote, TicketsAssign, TicketsEscalate, TicketsResolve,
            TicketsReopen, TicketsMerge, TicketsSplit, TicketsTime, CustomersView, CustomersManage, ProductsView, ContractsView,
            KbViewInternal, KbAuthor, AnalyticsView, AiUse, EngineeringLink, EngineeringView
        ];

        public static readonly string[] Supervisor =
        [
            .. Agent, TicketsTransfer, TicketsBulk, TicketsDelete, TicketsViewCosts, TicketsOverrideEntitlement, TicketsExport,
            ContactsMerge, CustomersMerge, ContractsManage, KbReview, KbPublish, CommunityModerate, ApprovalsDecide, SupervisorMonitor,
            AgentsManage, AnalyticsTeam, AnalyticsExport, AnalyticsSchedule, AiApprove, EngineeringManage
        ];

        public static readonly string[] KnowledgeManager =
        [
            TicketsView, TicketsNote, CustomersView, ProductsView, KbViewInternal, KbAuthor, KbReview, KbPublish,
            CommunityModerate, AdminKnowledge, AnalyticsView, AiUse
        ];

        public static readonly string[] Analyst =
        [
            TicketsView, CustomersView, ProductsView, ContractsView, AnalyticsView, AnalyticsTeam, AnalyticsExport, AnalyticsSchedule, TicketsViewCosts
        ];

        public static readonly string[] Administrator = [.. AllCodes];
    }
}
