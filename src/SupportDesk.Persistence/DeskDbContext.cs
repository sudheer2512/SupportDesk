using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
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
using SupportDesk.Persistence.Context;

namespace SupportDesk.Persistence;

/// <summary>Signals the outbox dispatcher that new events were committed (avoids waiting for the next poll).</summary>
public interface IOutboxSignal
{
    void Notify();
}

/// <summary>
/// The SupportDesk unit of work. Enforces tenant isolation on every query (named filter "tenant") and on every write
/// (tenant stamping and cross-tenant guard), stamps timestamps/authors, protects append-only tables, and stores
/// enums as strings (ordered enums as ints) and DateTimes as UTC.
/// </summary>
public class DeskDbContext(DbContextOptions<DeskDbContext> options, RequestContext requestContext, IClock clock, IOutboxSignal? outboxSignal = null)
    : DbContext(options)
{
    public const string TenantFilter = "tenant";
    public const string SoftDeleteFilter = "soft_delete";

    public RequestContext Request => requestContext;
    public IClock Clock => clock;

    /// <summary>Referenced by the tenant query filter; evaluated per query.</summary>
    protected Guid CurrentTenantId => requestContext.TenantId;

    // Platform
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();
    public DbSet<BillingInvoice> BillingInvoices => Set<BillingInvoice>();
    public DbSet<PlatformOperator> PlatformOperators => Set<PlatformOperator>();
    public DbSet<Brand> Brands => Set<Brand>();
    public DbSet<TenantCounter> TenantCounters => Set<TenantCounter>();
    public DbSet<TenantSetting> TenantSettings => Set<TenantSetting>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();
    public DbSet<OutboxDelivery> OutboxDeliveries => Set<OutboxDelivery>();
    public DbSet<BackgroundJob> BackgroundJobs => Set<BackgroundJob>();
    public DbSet<ScheduledTaskState> ScheduledTasks => Set<ScheduledTaskState>();
    public DbSet<ConfigurationSnapshot> ConfigurationSnapshots => Set<ConfigurationSnapshot>();
    public DbSet<RetentionPolicy> RetentionPolicies => Set<RetentionPolicy>();
    public DbSet<LegalHold> LegalHolds => Set<LegalHold>();
    public DbSet<DataRequest> DataRequests => Set<DataRequest>();
    public DbSet<StoredFile> StoredFiles => Set<StoredFile>();

    // Identity
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<OtpChallenge> OtpChallenges => Set<OtpChallenge>();

    // Organisation
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AgentDepartment> AgentDepartments => Set<AgentDepartment>();
    public DbSet<Skill> Skills => Set<Skill>();
    public DbSet<AgentSkill> AgentSkills => Set<AgentSkill>();
    public DbSet<AgentRoutingAttribute> AgentRoutingAttributes => Set<AgentRoutingAttribute>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<AgentShiftAssignment> AgentShiftAssignments => Set<AgentShiftAssignment>();
    public DbSet<AgentLeave> AgentLeaves => Set<AgentLeave>();
    public DbSet<AgentDelegation> AgentDelegations => Set<AgentDelegation>();
    public DbSet<BusinessCalendar> BusinessCalendars => Set<BusinessCalendar>();
    public DbSet<CalendarHoliday> CalendarHolidays => Set<CalendarHoliday>();

    // Customers
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CustomerGroup> CustomerGroups => Set<CustomerGroup>();
    public DbSet<CustomerLocation> CustomerLocations => Set<CustomerLocation>();
    public DbSet<CustomerDepartment> CustomerDepartments => Set<CustomerDepartment>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<ContactIdentity> ContactIdentities => Set<ContactIdentity>();
    public DbSet<ContactChannelConsent> ContactConsents => Set<ContactChannelConsent>();
    public DbSet<ContactMergeLog> ContactMergeLogs => Set<ContactMergeLog>();
    public DbSet<ServiceActivity> ServiceActivities => Set<ServiceActivity>();

    // Products & service
    public DbSet<ProductFamily> ProductFamilies => Set<ProductFamily>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductModule> ProductModules => Set<ProductModule>();
    public DbSet<ProductVersion> ProductVersions => Set<ProductVersion>();
    public DbSet<ProductRelease> ProductReleases => Set<ProductRelease>();
    public DbSet<Installation> Installations => Set<Installation>();
    public DbSet<Licence> Licences => Set<Licence>();
    public DbSet<CustomerAsset> CustomerAssets => Set<CustomerAsset>();
    public DbSet<Contract> Contracts => Set<Contract>();
    public DbSet<Entitlement> Entitlements => Set<Entitlement>();
    public DbSet<TimeEntry> TimeEntries => Set<TimeEntry>();

    // Tickets
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketStatus> TicketStatuses => Set<TicketStatus>();
    public DbSet<TicketCategory> TicketCategories => Set<TicketCategory>();
    public DbSet<IssueType> IssueTypes => Set<IssueType>();
    public DbSet<LookupValue> LookupValues => Set<LookupValue>();
    public DbSet<TicketThread> TicketThreads => Set<TicketThread>();
    public DbSet<TicketEvent> TicketEvents => Set<TicketEvent>();
    public DbSet<TicketAttachment> TicketAttachments => Set<TicketAttachment>();
    public DbSet<TicketLink> TicketLinks => Set<TicketLink>();
    public DbSet<TicketWatcher> TicketWatchers => Set<TicketWatcher>();
    public DbSet<TicketTask> TicketTasks => Set<TicketTask>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<TicketTag> TicketTags => Set<TicketTag>();
    public DbSet<TicketDraft> TicketDrafts => Set<TicketDraft>();
    public DbSet<ReplyTemplate> ReplyTemplates => Set<ReplyTemplate>();
    public DbSet<TicketFollowUp> TicketFollowUps => Set<TicketFollowUp>();
    public DbSet<TicketExternalReference> TicketExternalReferences => Set<TicketExternalReference>();
    public DbSet<EngineeringLink> EngineeringLinks => Set<EngineeringLink>();
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();
    public DbSet<SavedView> SavedViews => Set<SavedView>();
    public DbSet<SatisfactionResponse> SatisfactionResponses => Set<SatisfactionResponse>();

    // Configuration
    public DbSet<CustomFieldDefinition> CustomFields => Set<CustomFieldDefinition>();
    public DbSet<FormLayout> FormLayouts => Set<FormLayout>();

    // SLA
    public DbSet<SlaPolicy> SlaPolicies => Set<SlaPolicy>();
    public DbSet<SlaTarget> SlaTargets => Set<SlaTarget>();
    public DbSet<SlaEscalationLevel> SlaEscalationLevels => Set<SlaEscalationLevel>();
    public DbSet<SlaInstance> SlaInstances => Set<SlaInstance>();
    public DbSet<SlaMilestone> SlaMilestones => Set<SlaMilestone>();
    public DbSet<SlaEvent> SlaEvents => Set<SlaEvent>();

    // Automation
    public DbSet<AutomationRule> AutomationRules => Set<AutomationRule>();
    public DbSet<RuleVersion> RuleVersions => Set<RuleVersion>();
    public DbSet<RuleExecution> RuleExecutions => Set<RuleExecution>();
    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<WorkflowVersion> WorkflowVersions => Set<WorkflowVersion>();
    public DbSet<Webhook> Webhooks => Set<Webhook>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

    // Knowledge
    public DbSet<KbCategory> KbCategories => Set<KbCategory>();
    public DbSet<Article> Articles => Set<Article>();
    public DbSet<ArticleVersion> ArticleVersions => Set<ArticleVersion>();
    public DbSet<ArticleTranslation> ArticleTranslations => Set<ArticleTranslation>();
    public DbSet<ArticleAttachment> ArticleAttachments => Set<ArticleAttachment>();
    public DbSet<ArticleAccessRule> ArticleAccessRules => Set<ArticleAccessRule>();
    public DbSet<ArticleFeedback> ArticleFeedback => Set<ArticleFeedback>();
    public DbSet<KnowledgeChunk> KnowledgeChunks => Set<KnowledgeChunk>();
    public DbSet<CommunityTopic> CommunityTopics => Set<CommunityTopic>();
    public DbSet<CommunityPost> CommunityPosts => Set<CommunityPost>();
    public DbSet<CommunityVote> CommunityVotes => Set<CommunityVote>();
    public DbSet<TroubleshootingFlow> TroubleshootingFlows => Set<TroubleshootingFlow>();

    // Communication
    public DbSet<ChannelAccount> ChannelAccounts => Set<ChannelAccount>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageDelivery> MessageDeliveries => Set<MessageDelivery>();
    public DbSet<InboundEvent> InboundEvents => Set<InboundEvent>();
    public DbSet<CallRecord> CallRecords => Set<CallRecord>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    public DbSet<NotificationSettings> NotificationSettings => Set<NotificationSettings>();
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();
    public DbSet<PushDevice> PushDevices => Set<PushDevice>();

    // AI
    public DbSet<AiConfiguration> AiConfigurations => Set<AiConfiguration>();
    public DbSet<AiPromptTemplate> AiPromptTemplates => Set<AiPromptTemplate>();
    public DbSet<AiExecution> AiExecutions => Set<AiExecution>();
    public DbSet<AiUsage> AiUsage => Set<AiUsage>();
    public DbSet<AiAgent> AiAgents => Set<AiAgent>();
    public DbSet<AiActionProposal> AiActionProposals => Set<AiActionProposal>();
    public DbSet<AnomalyAlert> AnomalyAlerts => Set<AnomalyAlert>();

    // Analytics
    public DbSet<TicketMetric> TicketMetrics => Set<TicketMetric>();
    public DbSet<AgentMetric> AgentMetrics => Set<AgentMetric>();
    public DbSet<BacklogSnapshot> BacklogSnapshots => Set<BacklogSnapshot>();
    public DbSet<ScheduledReport> ScheduledReports => Set<ScheduledReport>();
    public DbSet<Dashboard> Dashboards => Set<Dashboard>();

    // Integration
    public DbSet<Integration> Integrations => Set<Integration>();
    public DbSet<IntegrationLog> IntegrationLogs => Set<IntegrationLog>();
    public DbSet<ApiClient> ApiClients => Set<ApiClient>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    // Search
    public DbSet<SearchDocument> SearchDocuments => Set<SearchDocument>();
    public DbSet<SearchPosting> SearchPostings => Set<SearchPosting>();

    /// <summary>Enums whose order matters in SQL (range filters, sorting) are stored as ints; others as strings.</summary>
    private static readonly HashSet<Type> OrdinalEnums =
    [
        typeof(TicketPriority), typeof(TicketSeverity), typeof(StatusCategory), typeof(TicketScope), typeof(CustomerScope),
        typeof(PortalAccessScope), typeof(DayOfWeek)
    ];

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        b.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
        b.Properties<decimal>().HavePrecision(18, 4);
        b.Properties<decimal?>().HavePrecision(18, 4);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        ModelConfiguration.Configure(modelBuilder);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList())
        {
            var clr = entityType.ClrType;
            var builder = modelBuilder.Entity(clr);

            // Ids are always assigned in code (sequential GUIDs), never generated by the database.
            if (typeof(Entity).IsAssignableFrom(clr)) builder.Property(nameof(Entity.Id)).ValueGeneratedNever();

            if (typeof(IConcurrencyVersioned).IsAssignableFrom(clr))
                builder.Property(nameof(IConcurrencyVersioned.RowVersion)).IsRowVersion();

            foreach (var prop in entityType.GetProperties())
            {
                var type = Nullable.GetUnderlyingType(prop.ClrType) ?? prop.ClrType;
                if (type.IsEnum)
                {
                    if (OrdinalEnums.Contains(type)) continue;
                    var converter = (ValueConverter)Activator.CreateInstance(typeof(EnumToStringConverter<>).MakeGenericType(type), [null])!;
                    prop.SetValueConverter(converter);
                    prop.SetMaxLength(40);
                    prop.SetIsUnicode(false);
                }
                else if (type == typeof(string) && prop.GetMaxLength() is null)
                {
                    prop.SetMaxLength(IsLongText(prop.Name) ? null : prop.Name is "Subject" or "Title" ? 500 : 450);
                }
            }

            if (typeof(ITenantOwned).IsAssignableFrom(clr))
                typeof(DeskDbContext).GetMethod(nameof(ApplyTenantFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .MakeGenericMethod(clr).Invoke(this, [modelBuilder]);
            if (typeof(ISoftDeletable).IsAssignableFrom(clr))
                typeof(DeskDbContext).GetMethod(nameof(ApplySoftDeleteFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                    .MakeGenericMethod(clr).Invoke(null, [modelBuilder]);
        }
    }

    private static bool IsLongText(string name) =>
        name.EndsWith("Json", StringComparison.Ordinal) || name.EndsWith("Html", StringComparison.Ordinal) ||
        name.EndsWith("Text", StringComparison.Ordinal) || name.EndsWith("Protected", StringComparison.Ordinal) ||
        name is "Body" or "Description" or "Notes" or "Snippet" or "Summary" or "Content" or "SystemPrompt" or "UserTemplate"
            or "RequestExcerpt" or "ResponseExcerpt" or "Error" or "LastError" or "Instructions" or "Comment" or "Reason"
            or "Outcome" or "ProcessingError" or "Rationale" or "ReleaseNotes" or "ServerConfiguration" or "ChangeNote"
            or "ReviewNote" or "DecisionNote" or "CsatComment" or "AiSummary" or "Keywords" or "Signature" or "PortalWelcome"
            or "ScanDetail" or "EntitlementNote" or "HelpText" or "RejectionReason" or "SuspensionReason" or "Note";

    private void ApplyTenantFilter<T>(ModelBuilder mb) where T : class, ITenantOwned
    {
        Expression<Func<T, bool>> filter = e => e.TenantId == CurrentTenantId;
        mb.Entity<T>().HasQueryFilter(TenantFilter, filter);
    }

    private static void ApplySoftDeleteFilter<T>(ModelBuilder mb) where T : class, ISoftDeletable
    {
        Expression<Func<T, bool>> filter = e => !e.IsDeleted;
        mb.Entity<T>().HasQueryFilter(SoftDeleteFilter, filter);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var hasOutbox = BeforeSave();
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        if (hasOutbox) outboxSignal?.Notify();
        return result;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var hasOutbox = BeforeSave();
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (hasOutbox) outboxSignal?.Notify();
        return result;
    }

    /// <summary>Tenant stamping/guarding, timestamps, append-only protection. Returns true if outbox events were added.</summary>
    private bool BeforeSave()
    {
        var now = clock.UtcNow;
        var tenant = requestContext.TenantId;
        var actor = requestContext.UserId;
        var hasOutbox = false;

        foreach (var entry in ChangeTracker.Entries().ToList())
        {
            if (entry.State is EntityState.Unchanged or EntityState.Detached) continue;

            if (entry.Entity is IAppendOnly && entry.State is EntityState.Modified or EntityState.Deleted && !IsAllowedAppendOnlyChange(entry))
                throw new InvalidOperationException($"{entry.Metadata.ClrType.Name} rows are append-only.");

            if (entry.Entity is ITenantOwned owned)
            {
                if (entry.State == EntityState.Added)
                {
                    if (owned.TenantId == Guid.Empty)
                    {
                        if (tenant == Guid.Empty)
                            throw new InvalidOperationException($"Cannot insert {entry.Metadata.ClrType.Name}: no tenant in the request context.");
                        owned.TenantId = tenant;
                    }
                    else if (owned.TenantId != tenant && !requestContext.AllowCrossTenant)
                        throw new CrossTenantWriteException(entry.Metadata.ClrType.Name);
                }
                else if (owned.TenantId != tenant && !requestContext.AllowCrossTenant)
                {
                    throw new CrossTenantWriteException(entry.Metadata.ClrType.Name);
                }
                else if (entry.State == EntityState.Modified && entry.Property(nameof(ITenantOwned.TenantId)).IsModified)
                {
                    throw new CrossTenantWriteException(entry.Metadata.ClrType.Name);
                }
            }

            switch (entry.Entity)
            {
                case OutboxEvent ev when entry.State == EntityState.Added:
                    hasOutbox = true;
                    ev.TenantId ??= tenant == Guid.Empty ? null : tenant;
                    if (ev.OccurredAt == default) ev.OccurredAt = now;
                    if (ev.NextAttemptAt == default) ev.NextAttemptAt = now;
                    break;
                case BackgroundJob job when entry.State == EntityState.Added:
                    job.TenantId ??= tenant == Guid.Empty ? null : tenant;
                    if (job.CreatedAt == default) job.CreatedAt = now;
                    if (job.RunAt == default) job.RunAt = now;
                    break;
                case AuditLog log when entry.State == EntityState.Added:
                    log.TenantId ??= tenant == Guid.Empty ? null : tenant;
                    if (log.OccurredAt == default) log.OccurredAt = now;
                    break;
            }

            if (entry.Entity is IHasTimestamps ts)
            {
                if (entry.State == EntityState.Added)
                {
                    if (ts.CreatedAt == default) ts.CreatedAt = now;
                    ts.UpdatedAt = now;
                }
                else if (entry.State == EntityState.Modified)
                {
                    ts.UpdatedAt = now;
                    entry.Property(nameof(IHasTimestamps.CreatedAt)).IsModified = false;
                }
            }

            if (entry.Entity is IHasAuthors au)
            {
                if (entry.State == EntityState.Added) au.CreatedById ??= actor;
                if (entry.State is EntityState.Added or EntityState.Modified) au.UpdatedById = actor ?? au.UpdatedById;
            }
        }
        return hasOutbox;
    }

    /// <summary>Audit rows may only be sealed once (Sequence/PrevHash/Hash written by the sealer while Hash is empty).</summary>
    private static bool IsAllowedAppendOnlyChange(EntityEntry entry)
    {
        if (entry.Entity is not AuditLog || entry.State != EntityState.Modified) return false;
        var original = entry.OriginalValues[nameof(AuditLog.Hash)] as string;
        if (!string.IsNullOrEmpty(original)) return false;
        var allowed = new HashSet<string> { nameof(AuditLog.Sequence), nameof(AuditLog.PrevHash), nameof(AuditLog.Hash) };
        return entry.Properties.Where(p => p.IsModified).All(p => allowed.Contains(p.Metadata.Name));
    }

    /// <summary>Queries across all tenants (platform console, workers that fan out per tenant). Caller must be trusted code.</summary>
    public IQueryable<T> AllTenants<T>() where T : class => Set<T>().IgnoreQueryFilters([TenantFilter]);
}

public sealed class CrossTenantWriteException(string entity)
    : InvalidOperationException($"Cross-tenant write blocked for {entity}.");

public sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

public sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
    v => v.HasValue ? (v.Value.Kind == DateTimeKind.Local ? v.Value.ToUniversalTime() : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc)) : v,
    v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
