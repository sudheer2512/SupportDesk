using Microsoft.EntityFrameworkCore;
using SupportDesk.Domain;
using SupportDesk.Domain.Ai;
using SupportDesk.Domain.Analytics;
using SupportDesk.Domain.Automation;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Configuration;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Integration;
using SupportDesk.Domain.Knowledge;
using SupportDesk.Domain.Organisation;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Products;
using SupportDesk.Domain.Search;
using SupportDesk.Domain.Service;
using SupportDesk.Domain.Sla;
using SupportDesk.Domain.Tickets;

namespace SupportDesk.Persistence;

/// <summary>
/// Keys, relationships, indexes and constraints. Foreign keys use NO ACTION (restrict) except pure child collections,
/// which cascade from their parent. Indexes lead with TenantId for tenant-scoped access paths; filtered indexes keep
/// hot "open" working sets small; keyset pagination uses (TenantId, LastActivityAt, Id).
/// </summary>
internal static class ModelConfiguration
{
    // Open = StatusCategory < Resolved (ordinal enum stored as int).
    private const string OpenTicketFilter = "[StatusCategory] < 5 AND [IsDeleted] = 0";

    public static void Configure(ModelBuilder m)
    {
        Platform(m);
        Identity(m);
        Organisation(m);
        Customers(m);
        Products(m);
        Tickets(m);
        Configuration(m);
        Sla(m);
        Automation(m);
        Knowledge(m);
        Communication(m);
        Ai(m);
        Analytics(m);
        Integration(m);
        Search(m);
    }

    private static void Platform(ModelBuilder m)
    {
        m.Entity<Tenant>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Slug).HasMaxLength(64);
        });
        m.Entity<Plan>(e => e.HasIndex(x => x.Code).IsUnique());
        m.Entity<Subscription>(e =>
        {
            e.HasIndex(x => x.TenantId).IsUnique();
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Plan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<UsageRecord>(e => e.HasIndex(x => new { x.TenantId, x.Metric, x.PeriodStart }).IsUnique());
        m.Entity<BillingInvoice>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            e.HasOne<Subscription>().WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<PlatformOperator>(e => e.HasIndex(x => x.Email).IsUnique());
        m.Entity<Brand>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => x.CustomDomain).IsUnique().HasFilter("[CustomDomain] IS NOT NULL");
            e.Property(x => x.Slug).HasMaxLength(64);
            e.Property(x => x.CustomDomain).HasMaxLength(253);
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<TenantCounter>(e => e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique());
        m.Entity<TenantSetting>(e => e.HasIndex(x => new { x.TenantId, x.Key }).IsUnique());
        m.Entity<AuditLog>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Sequence });
            e.HasIndex(x => new { x.TenantId, x.OccurredAt });
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.HasIndex(x => x.OccurredAt).HasFilter("[Hash] = ''").HasDatabaseName("IX_AuditLogs_Unsealed");
            e.Property(x => x.Action).HasMaxLength(100);
            e.Property(x => x.Hash).HasMaxLength(64);
            e.Property(x => x.PrevHash).HasMaxLength(64);
        });
        m.Entity<OutboxEvent>(e =>
        {
            e.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasFilter("[Status] IN ('Pending','Processing','Failed')")
                .HasDatabaseName("IX_OutboxEvents_Due");
            e.HasIndex(x => new { x.TenantId, x.OccurredAt });
            e.Property(x => x.Type).HasMaxLength(100);
        });
        m.Entity<OutboxDelivery>(e => e.HasIndex(x => new { x.EventId, x.Handler }).IsUnique());
        m.Entity<BackgroundJob>(e =>
        {
            e.HasIndex(x => new { x.Status, x.RunAt, x.Priority }).HasFilter("[Status] IN ('Queued','Running','Failed')")
                .HasDatabaseName("IX_BackgroundJobs_Due");
            e.HasIndex(x => x.IdempotencyKey).IsUnique().HasFilter("[IdempotencyKey] IS NOT NULL");
            e.Property(x => x.Type).HasMaxLength(100);
        });
        m.Entity<ScheduledTaskState>(e => e.HasIndex(x => x.Name).IsUnique());
        m.Entity<ConfigurationSnapshot>(e => e.HasIndex(x => new { x.TenantId, x.Version }).IsUnique());
        m.Entity<RetentionPolicy>(e => e.HasIndex(x => x.TenantId));
        m.Entity<LegalHold>(e => e.HasIndex(x => new { x.TenantId, x.ReleasedAt }));
        m.Entity<DataRequest>(e => e.HasIndex(x => new { x.TenantId, x.Status }));
        m.Entity<StoredFile>(e =>
        {
            e.HasIndex(x => x.StorageKey).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Sha256 });
        });
    }

    private static void Identity(ModelBuilder m)
    {
        m.Entity<User>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.EmailNormalized }).IsUnique().HasFilter("[IsDeleted] = 0");
            e.HasIndex(x => new { x.ExternalIssuer, x.ExternalSubject });
            e.HasMany(x => x.Roles).WithOne().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<Role>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            e.HasMany(x => x.Permissions).WithOne().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<Permission>(e => e.HasIndex(x => x.Code).IsUnique());
        m.Entity<RolePermission>(e => e.HasIndex(x => new { x.RoleId, x.PermissionCode }).IsUnique());
        m.Entity<UserRole>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.RoleId }).IsUnique();
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<UserSession>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.PrincipalKind, x.PrincipalId });
            e.HasIndex(x => x.FamilyId);
            e.Property(x => x.TokenHash).HasMaxLength(64);
        });
        m.Entity<OtpChallenge>(e => e.HasIndex(x => new { x.PrincipalId, x.Purpose }));
    }

    private static void Organisation(ModelBuilder m)
    {
        m.Entity<Department>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            e.HasOne<Department>().WithMany().HasForeignKey(x => x.ParentDepartmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BusinessCalendar>().WithMany().HasForeignKey(x => x.BusinessCalendarId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<Team>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.DepartmentId });
            e.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<TeamMember>(e =>
        {
            e.HasIndex(x => new { x.TeamId, x.UserId }).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<Agent>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<AgentDepartment>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.DepartmentId }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.DepartmentId });
            e.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<Skill>(e => e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique());
        m.Entity<AgentSkill>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.SkillId }).IsUnique();
            e.HasOne<Skill>().WithMany().HasForeignKey(x => x.SkillId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<AgentRoutingAttribute>(e => e.HasIndex(x => new { x.TenantId, x.Kind, x.RefId }));
        m.Entity<Shift>(e => e.HasIndex(x => x.TenantId));
        m.Entity<AgentShiftAssignment>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.EffectiveFrom });
            e.HasOne<Shift>().WithMany().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<AgentLeave>(e => e.HasIndex(x => new { x.TenantId, x.UserId, x.StartsAt }));
        m.Entity<AgentDelegation>(e => e.HasIndex(x => new { x.TenantId, x.UserId, x.StartsAt }));
        m.Entity<BusinessCalendar>(e => e.HasIndex(x => x.TenantId));
        m.Entity<CalendarHoliday>(e =>
        {
            e.HasIndex(x => new { x.CalendarId, x.Date }).IsUnique();
            e.HasOne<BusinessCalendar>().WithMany().HasForeignKey(x => x.CalendarId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void Customers(ModelBuilder m)
    {
        m.Entity<Customer>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Name });
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique().HasFilter("[Code] IS NOT NULL AND [IsDeleted] = 0");
            e.HasIndex(x => new { x.TenantId, x.ErpCustomerId });
            e.HasIndex(x => x.ParentCustomerId);
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.ParentCustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<CustomerGroup>().WithMany().HasForeignKey(x => x.CustomerGroupId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.AccountManagerUserId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<CustomerGroup>(e => e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique());
        m.Entity<CustomerLocation>(e =>
        {
            e.HasIndex(x => x.CustomerId);
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<CustomerDepartment>(e =>
        {
            e.HasIndex(x => x.CustomerId);
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<Contact>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.EmailNormalized });
            e.HasIndex(x => new { x.TenantId, x.PhoneNormalized });
            e.HasIndex(x => new { x.TenantId, x.FullName });
            e.HasIndex(x => x.CustomerId);
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<CustomerLocation>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<CustomerDepartment>().WithMany().HasForeignKey(x => x.CustomerDepartmentId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<ContactIdentity>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Channel, x.Identifier }).IsUnique();
            e.HasIndex(x => x.ContactId);
            e.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<ContactChannelConsent>(e =>
        {
            e.HasIndex(x => new { x.ContactId, x.Channel, x.Purpose }).IsUnique();
            e.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<ContactMergeLog>(e => e.HasIndex(x => new { x.TenantId, x.SurvivorContactId }));
        m.Entity<ServiceActivity>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.CustomerId, x.ScheduledStart });
            e.HasIndex(x => new { x.TenantId, x.AssignedUserId, x.ScheduledStart });
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void Products(ModelBuilder m)
    {
        m.Entity<ProductFamily>(e => e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique());
        m.Entity<Product>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            e.HasOne<ProductFamily>().WithMany().HasForeignKey(x => x.FamilyId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<ProductModule>(e =>
        {
            e.HasIndex(x => x.ProductId);
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<ProductVersion>(e =>
        {
            e.HasIndex(x => new { x.ProductId, x.Version }).IsUnique();
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<ProductRelease>(e =>
        {
            e.HasIndex(x => x.VersionId);
            e.HasOne<ProductVersion>().WithMany().HasForeignKey(x => x.VersionId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<Installation>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.CustomerId });
            e.HasIndex(x => new { x.TenantId, x.InstallationCode });
            e.HasIndex(x => new { x.TenantId, x.ProductId, x.VersionId });
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ProductVersion>().WithMany().HasForeignKey(x => x.VersionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<CustomerLocation>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<Licence>(e =>
        {
            e.HasIndex(x => x.InstallationId);
            e.HasOne<Installation>().WithMany().HasForeignKey(x => x.InstallationId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<CustomerAsset>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.CustomerId });
            e.HasIndex(x => new { x.TenantId, x.SerialNumber });
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<Contract>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.CustomerId, x.Status, x.EndDate });
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<SlaPolicy>().WithMany().HasForeignKey(x => x.SlaPolicyId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BusinessCalendar>().WithMany().HasForeignKey(x => x.CalendarId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<Entitlement>(e =>
        {
            e.HasIndex(x => x.ContractId);
            e.HasIndex(x => new { x.TenantId, x.CustomerId, x.ProductId });
            e.HasOne<Contract>().WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<TimeEntry>(e =>
        {
            e.HasIndex(x => x.TicketId);
            e.HasIndex(x => new { x.TenantId, x.UserId, x.StartedAt });
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void Tickets(ModelBuilder m)
    {
        m.Entity<Ticket>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.LastActivityAt, x.Id }).HasDatabaseName("IX_Tickets_Keyset");
            e.HasIndex(x => new { x.TenantId, x.AssigneeId, x.StatusCategory }).HasFilter(OpenTicketFilter).HasDatabaseName("IX_Tickets_OpenByAssignee");
            e.HasIndex(x => new { x.TenantId, x.DepartmentId, x.StatusCategory, x.Priority }).HasFilter(OpenTicketFilter).HasDatabaseName("IX_Tickets_OpenByDepartment");
            e.HasIndex(x => new { x.TenantId, x.DueAt }).HasFilter(OpenTicketFilter).HasDatabaseName("IX_Tickets_OpenDue");
            e.HasIndex(x => new { x.TenantId, x.CustomerId, x.CreatedAt });
            e.HasIndex(x => new { x.TenantId, x.ContactId, x.CreatedAt });
            e.HasIndex(x => new { x.TenantId, x.ProductId, x.CreatedAt });
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
            e.HasIndex(x => x.ParentTicketId);
            e.HasMany(x => x.Tags).WithOne().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<CustomerLocation>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.AssigneeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ProductModule>().WithMany().HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ProductVersion>().WithMany().HasForeignKey(x => x.VersionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Installation>().WithMany().HasForeignKey(x => x.InstallationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<CustomerAsset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<TicketCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<TicketCategory>().WithMany().HasForeignKey(x => x.SubcategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<IssueType>().WithMany().HasForeignKey(x => x.IssueTypeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<TicketStatus>().WithMany().HasForeignKey(x => x.StatusId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<SlaPolicy>().WithMany().HasForeignKey(x => x.SlaPolicyId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Contract>().WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Brand>().WithMany().HasForeignKey(x => x.BrandId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.ParentTicketId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.MergedIntoTicketId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<TicketStatus>(e => e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique());
        m.Entity<TicketCategory>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.ParentId, x.Name });
            e.HasOne<TicketCategory>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<IssueType>(e => e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique());
        m.Entity<LookupValue>(e => e.HasIndex(x => new { x.TenantId, x.Type, x.Value }).IsUnique());
        m.Entity<TicketThread>(e =>
        {
            e.HasIndex(x => new { x.TicketId, x.Seq }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.ClientRequestId }).IsUnique().HasFilter("[ClientRequestId] IS NOT NULL");
            e.HasIndex(x => new { x.TenantId, x.AuthorUserId, x.CreatedAt });
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<TicketEvent>(e =>
        {
            e.HasIndex(x => new { x.TicketId, x.OccurredAt });
            e.Property(x => x.Type).HasMaxLength(80);
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<TicketAttachment>(e =>
        {
            e.HasIndex(x => x.TicketId);
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<StoredFile>().WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<TicketLink>(e =>
        {
            e.HasIndex(x => new { x.SourceTicketId, x.TargetTicketId, x.LinkType }).IsUnique();
            e.HasIndex(x => x.TargetTicketId);
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.SourceTicketId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.TargetTicketId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<TicketWatcher>(e =>
        {
            e.HasIndex(x => new { x.TicketId, x.UserId }).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<TicketTask>(e =>
        {
            e.HasIndex(x => x.TicketId);
            e.HasIndex(x => new { x.TenantId, x.AssigneeId, x.Status });
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<TicketTask>().WithMany().HasForeignKey(x => x.ParentTaskId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<Tag>(e => e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique());
        m.Entity<TicketTag>(e =>
        {
            e.HasIndex(x => new { x.TicketId, x.TagId }).IsUnique();
            e.HasIndex(x => x.TagId);
            e.HasOne(x => x.Tag).WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<TicketDraft>(e =>
        {
            e.HasIndex(x => new { x.TicketId, x.UserId, x.Kind }).IsUnique();
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<ReplyTemplate>(e => e.HasIndex(x => new { x.TenantId, x.Name }));
        m.Entity<TicketFollowUp>(e =>
        {
            e.HasIndex(x => new { x.Status, x.RemindAt }).HasFilter("[Status] = 'Pending'");
            e.HasIndex(x => x.TicketId);
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<TicketExternalReference>(e =>
        {
            e.HasIndex(x => x.TicketId);
            e.HasIndex(x => new { x.TenantId, x.System, x.RecordType, x.RecordId });
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<EngineeringLink>(e =>
        {
            e.HasIndex(x => x.TicketId);
            e.HasIndex(x => new { x.TenantId, x.Provider, x.ExternalId });
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<ApprovalRequest>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt });
            e.HasIndex(x => x.EntityId);
        });
        m.Entity<SavedView>(e => e.HasIndex(x => new { x.TenantId, x.Scope, x.OwnerUserId }));
        m.Entity<SatisfactionResponse>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.TicketId);
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void Configuration(ModelBuilder m)
    {
        m.Entity<CustomFieldDefinition>(e => e.HasIndex(x => new { x.TenantId, x.Entity, x.Key }).IsUnique());
        m.Entity<FormLayout>(e => e.HasIndex(x => new { x.TenantId, x.Entity, x.Kind }));
    }

    private static void Sla(ModelBuilder m)
    {
        m.Entity<SlaPolicy>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.EvaluationOrder });
            e.HasMany(x => x.Targets).WithOne().HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Escalations).WithOne().HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<BusinessCalendar>().WithMany().HasForeignKey(x => x.CalendarId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<SlaTarget>(e => e.HasIndex(x => new { x.PolicyId, x.Priority }).IsUnique());
        m.Entity<SlaEscalationLevel>(e => e.HasIndex(x => new { x.PolicyId, x.Milestone, x.Level }).IsUnique());
        m.Entity<SlaInstance>(e =>
        {
            e.HasIndex(x => new { x.TicketId, x.Status });
            e.HasMany(x => x.Milestones).WithOne().HasForeignKey(x => x.InstanceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<SlaPolicy>().WithMany().HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<SlaMilestone>(e =>
        {
            e.HasIndex(x => x.TicketId);
            e.HasIndex(x => new { x.Status, x.DueAt }).HasFilter("[Status] = 'Active'").HasDatabaseName("IX_SlaMilestones_ActiveDue");
            e.HasIndex(x => new { x.Status, x.WarningAt }).HasFilter("[Status] = 'Active' AND [WarningRaised] = 0").HasDatabaseName("IX_SlaMilestones_PendingWarning");
            e.HasIndex(x => new { x.Status, x.BreachedAt }).HasFilter("[Status] = 'Breached'").HasDatabaseName("IX_SlaMilestones_Breached");
        });
        m.Entity<SlaEvent>(e =>
        {
            e.HasIndex(x => new { x.TicketId, x.OccurredAt });
            e.HasIndex(x => new { x.TenantId, x.Type, x.OccurredAt });
        });
    }

    private static void Automation(ModelBuilder m)
    {
        m.Entity<AutomationRule>(e => e.HasIndex(x => new { x.TenantId, x.Trigger, x.IsActive, x.ExecutionOrder }));
        m.Entity<RuleVersion>(e =>
        {
            e.HasIndex(x => new { x.RuleId, x.VersionNumber }).IsUnique();
            e.HasOne<AutomationRule>().WithMany().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<RuleExecution>(e =>
        {
            e.HasIndex(x => new { x.RuleVersionId, x.EventId, x.EntityId }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.RuleId, x.StartedAt });
            e.HasIndex(x => new { x.TenantId, x.EntityId });
        });
        m.Entity<Workflow>(e => e.HasIndex(x => new { x.TenantId, x.DepartmentId }));
        m.Entity<WorkflowVersion>(e =>
        {
            e.HasIndex(x => new { x.WorkflowId, x.VersionNumber }).IsUnique();
            e.HasOne<Workflow>().WithMany().HasForeignKey(x => x.WorkflowId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<Webhook>(e => e.HasIndex(x => x.TenantId));
        m.Entity<WebhookDelivery>(e =>
        {
            e.HasIndex(x => new { x.WebhookId, x.EventId }).IsUnique();
            e.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasFilter("[Status] IN ('Pending','Failed')");
            e.HasOne<Webhook>().WithMany().HasForeignKey(x => x.WebhookId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void Knowledge(ModelBuilder m)
    {
        m.Entity<KbCategory>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Slug }).IsUnique();
            e.HasOne<KbCategory>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<Article>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Slug }).IsUnique().HasFilter("[IsDeleted] = 0");
            e.HasIndex(x => new { x.TenantId, x.Status, x.Visibility });
            e.HasIndex(x => new { x.TenantId, x.ReviewDueAt });
            e.HasOne<KbCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<ArticleVersion>(e =>
        {
            e.HasIndex(x => new { x.ArticleId, x.VersionNumber }).IsUnique();
            e.HasOne<Article>().WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<ArticleTranslation>(e =>
        {
            e.HasIndex(x => new { x.ArticleId, x.Locale }).IsUnique();
            e.HasOne<Article>().WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<ArticleAttachment>(e =>
        {
            e.HasIndex(x => x.ArticleId);
            e.HasOne<Article>().WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<ArticleAccessRule>(e =>
        {
            e.HasIndex(x => x.ArticleId);
            e.HasOne<Article>().WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<ArticleFeedback>(e =>
        {
            e.HasIndex(x => new { x.ArticleId, x.CreatedAt });
            e.HasOne<Article>().WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<KnowledgeChunk>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.SourceType, x.SourceId });
            e.HasIndex(x => new { x.TenantId, x.Visibility });
        });
        m.Entity<CommunityTopic>(e => e.HasIndex(x => new { x.TenantId, x.BrandId, x.LastActivityAt }));
        m.Entity<CommunityPost>(e =>
        {
            e.HasIndex(x => x.TopicId);
            e.HasOne<CommunityTopic>().WithMany().HasForeignKey(x => x.TopicId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<CommunityVote>(e => e.HasIndex(x => new { x.TopicId, x.PostId, x.VoterKey }).IsUnique());
        m.Entity<TroubleshootingFlow>(e => e.HasIndex(x => x.TenantId));
    }

    private static void Communication(ModelBuilder m)
    {
        m.Entity<ChannelAccount>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Type });
            e.HasIndex(x => x.WebhookKey).IsUnique().HasFilter("[WebhookKey] IS NOT NULL");
            e.HasIndex(x => new { x.Type, x.Address });
        });
        m.Entity<Conversation>(e =>
        {
            e.HasIndex(x => new { x.ChannelAccountId, x.ExternalKey }).IsUnique().HasFilter("[Status] <> 'Closed'")
                .HasDatabaseName("IX_Conversations_OpenKey");
            e.HasIndex(x => x.TicketId);
            e.HasIndex(x => new { x.TenantId, x.Status, x.LastMessageAt });
            e.HasOne<ChannelAccount>().WithMany().HasForeignKey(x => x.ChannelAccountId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<Message>(e =>
        {
            e.HasIndex(x => new { x.ChannelAccountId, x.Direction, x.ExternalId }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.InternetMessageId });
            e.HasIndex(x => x.TicketId);
            e.HasIndex(x => new { x.ConversationId, x.OccurredAt });
            e.HasOne<ChannelAccount>().WithMany().HasForeignKey(x => x.ChannelAccountId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.ExternalId).HasMaxLength(400);
            e.Property(x => x.InternetMessageId).HasMaxLength(400);
        });
        m.Entity<MessageDelivery>(e =>
        {
            e.HasIndex(x => x.MessageId);
            e.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasFilter("[Status] IN ('Queued','Sending','Failed')");
            e.HasOne<Message>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<InboundEvent>(e =>
        {
            e.HasIndex(x => new { x.Provider, x.DedupKey }).IsUnique();
            e.HasIndex(x => new { x.Status, x.ReceivedAt });
            e.Property(x => x.DedupKey).HasMaxLength(400);
        });
        m.Entity<CallRecord>(e =>
        {
            e.HasIndex(x => new { x.ChannelAccountId, x.ExternalCallId }).IsUnique();
            e.HasIndex(x => x.TicketId);
        });
        m.Entity<Notification>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.DedupKey }).IsUnique();
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasIndex(x => new { x.ContactId, x.CreatedAt });
        });
        m.Entity<NotificationPreference>(e => e.HasIndex(x => new { x.UserId, x.EventType }).IsUnique());
        m.Entity<NotificationSettings>(e => e.HasIndex(x => x.UserId).IsUnique());
        m.Entity<NotificationDelivery>(e =>
        {
            e.HasIndex(x => new { x.Status, x.ScheduledFor }).HasFilter("[Status] IN ('Pending','Failed')");
            e.HasIndex(x => x.NotificationId);
            e.HasOne<Notification>().WithMany().HasForeignKey(x => x.NotificationId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<PushDevice>(e => e.HasIndex(x => new { x.TenantId, x.Token }).IsUnique());
    }

    private static void Ai(ModelBuilder m)
    {
        m.Entity<AiConfiguration>(e => e.HasIndex(x => x.TenantId).IsUnique());
        m.Entity<AiPromptTemplate>(e => e.HasIndex(x => new { x.TenantId, x.Feature, x.Version }).IsUnique());
        m.Entity<AiExecution>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Feature, x.CreatedAt });
            e.HasIndex(x => new { x.TenantId, x.EntityId });
        });
        m.Entity<AiUsage>(e => e.HasIndex(x => new { x.TenantId, x.PeriodStart, x.Feature, x.Model }).IsUnique());
        m.Entity<AiAgent>(e => e.HasIndex(x => x.TenantId));
        m.Entity<AiActionProposal>(e =>
        {
            e.HasIndex(x => x.TicketId);
            e.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt });
        });
        m.Entity<AnomalyAlert>(e => e.HasIndex(x => new { x.TenantId, x.Dimension, x.DimensionId, x.Day }).IsUnique());
    }

    private static void Analytics(ModelBuilder m)
    {
        m.Entity<TicketMetric>(e =>
        {
            e.HasIndex(x => x.TicketId).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.CreatedDay });
            e.HasIndex(x => new { x.TenantId, x.ResolvedDay });
            e.HasIndex(x => new { x.TenantId, x.AssigneeId, x.CreatedDay });
            e.HasIndex(x => new { x.TenantId, x.CustomerId, x.CreatedDay });
            e.HasIndex(x => new { x.TenantId, x.ProductId, x.CreatedDay });
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<AgentMetric>(e => e.HasIndex(x => new { x.TenantId, x.UserId, x.Day }).IsUnique());
        m.Entity<BacklogSnapshot>(e => e.HasIndex(x => new { x.TenantId, x.Day, x.DepartmentId, x.Priority }).IsUnique());
        m.Entity<ScheduledReport>(e => e.HasIndex(x => new { x.IsActive, x.NextRunAt }));
        m.Entity<Dashboard>(e => e.HasIndex(x => new { x.TenantId, x.Audience }));
    }

    private static void Integration(ModelBuilder m)
    {
        m.Entity<Integration>(e => e.HasIndex(x => new { x.TenantId, x.Type }));
        m.Entity<IntegrationLog>(e => e.HasIndex(x => new { x.TenantId, x.CreatedAt }));
        m.Entity<ApiClient>(e => e.HasIndex(x => x.ClientId).IsUnique());
        m.Entity<ApiKey>(e => e.HasIndex(x => x.Prefix).IsUnique());
    }

    private static void Search(ModelBuilder m)
    {
        m.Entity<SearchDocument>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.EntityType, x.EntityId }).IsUnique();
            e.Property(x => x.EntityType).HasMaxLength(40);
        });
        m.Entity<SearchPosting>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Term }).IncludeProperties(x => new { x.DocumentId, x.Weight });
            e.HasIndex(x => x.DocumentId);
            e.Property(x => x.Term).HasMaxLength(64);
            e.HasOne<SearchDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
