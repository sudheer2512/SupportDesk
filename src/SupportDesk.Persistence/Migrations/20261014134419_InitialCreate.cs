using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupportDesk.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentDelegations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DelegateUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartsAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndsAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReassignOpenTickets = table.Column<bool>(type: "bit", nullable: false),
                    IncludeApprovals = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDelegations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentLeaves",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartsAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndsAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Type = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApprovedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApprovalRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentLeaves", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentMetrics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    TicketsAssigned = table.Column<int>(type: "int", nullable: false),
                    TicketsResolved = table.Column<int>(type: "int", nullable: false),
                    Replies = table.Column<int>(type: "int", nullable: false),
                    Notes = table.Column<int>(type: "int", nullable: false),
                    FirstResponses = table.Column<int>(type: "int", nullable: false),
                    FirstResponseMinutesSum = table.Column<long>(type: "bigint", nullable: false),
                    ResolutionMinutesSum = table.Column<long>(type: "bigint", nullable: false),
                    HandleMinutes = table.Column<int>(type: "int", nullable: false),
                    OnlineMinutes = table.Column<int>(type: "int", nullable: false),
                    CsatCount = table.Column<int>(type: "int", nullable: false),
                    CsatSum = table.Column<int>(type: "int", nullable: false),
                    SlaBreaches = table.Column<int>(type: "int", nullable: false),
                    Reopens = table.Column<int>(type: "int", nullable: false),
                    OpenAtEnd = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentMetrics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentRoutingAttributes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    RefId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentRoutingAttributes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiActionProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AiExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActionType = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Rationale = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Confidence = table.Column<double>(type: "float", nullable: false),
                    Risk = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    RequiresApproval = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    BeforeStateJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DecidedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AppliedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevertedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiActionProposals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiAgents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Kind = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Mode = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ConfigJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Instructions = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RunCount = table.Column<long>(type: "bigint", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiAgents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Endpoint = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ApiKeyProtected = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EmbeddingProvider = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    EmbeddingModel = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    MonthlyBudget = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    BudgetAction = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    InputCostPer1K = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    OutputCostPer1K = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    FeaturesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AutoReplyScenariosJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RedactPii = table.Column<bool>(type: "bit", nullable: false),
                    ExecutionRetentionDays = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiConfigurations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Feature = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    InputHash = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    PromptVersion = table.Column<int>(type: "int", nullable: false),
                    InputTokens = table.Column<int>(type: "int", nullable: false),
                    OutputTokens = table.Column<int>(type: "int", nullable: false),
                    Cost = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    LatencyMs = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    OutputJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CitationsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Confidence = table.Column<double>(type: "float", nullable: true),
                    InjectionFlagsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Outcome = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ReviewedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiExecutions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiPromptTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Feature = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    SystemPrompt = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UserTemplate = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiPromptTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiUsage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PeriodStart = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Feature = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Requests = table.Column<long>(type: "bigint", nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    Cost = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiUsage", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AnomalyAlerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Dimension = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    DimensionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DimensionLabel = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Count = table.Column<int>(type: "int", nullable: false),
                    Baseline = table.Column<double>(type: "float", nullable: false),
                    StdDev = table.Column<double>(type: "float", nullable: false),
                    Score = table.Column<double>(type: "float", nullable: false),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    AcknowledgedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnomalyAlerts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiClients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ClientId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    SecretHash = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ScopesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RateLimitPerMinute = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiClients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Prefix = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    KeyHash = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ScopesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ActAsUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RateLimitPerMinute = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RequestedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApproverUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApproverRoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DecidedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ActorType = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorName = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Summary = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ChangesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    PrevHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutomationRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Trigger = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ExecutionOrder = table.Column<int>(type: "int", nullable: false),
                    StopProcessing = table.Column<bool>(type: "bit", nullable: false),
                    PublishedVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LatestVersionNumber = table.Column<int>(type: "int", nullable: false),
                    ScheduleIntervalMinutes = table.Column<int>(type: "int", nullable: true),
                    NextScheduledRunAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    WebhookSecretHash = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    WebhookKey = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ExecutionCount = table.Column<long>(type: "bigint", nullable: false),
                    LastExecutedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BackgroundJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Type = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    RunAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    MaxAttempts = table.Column<int>(type: "int", nullable: false),
                    LockedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    LockedUntil = table.Column<DateTime>(type: "datetime2", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackgroundJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BacklogSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    OpenCount = table.Column<int>(type: "int", nullable: false),
                    CreatedCount = table.Column<int>(type: "int", nullable: false),
                    ResolvedCount = table.Column<int>(type: "int", nullable: false),
                    Age0To1 = table.Column<int>(type: "int", nullable: false),
                    Age1To3 = table.Column<int>(type: "int", nullable: false),
                    Age3To7 = table.Column<int>(type: "int", nullable: false),
                    Age7To30 = table.Column<int>(type: "int", nullable: false),
                    Age30Plus = table.Column<int>(type: "int", nullable: false),
                    BreachedOpen = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BacklogSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BusinessCalendars",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TimeZone = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Is24x7 = table.Column<bool>(type: "bit", nullable: false),
                    WeeklyHoursJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessCalendars", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CallRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChannelAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalCallId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AgentUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Direction = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    FromNumber = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ToNumber = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AnsweredAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EndedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DurationSeconds = table.Column<int>(type: "int", nullable: false),
                    RecordingUrl = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    RecordingFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Disposition = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CallRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChannelAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    BrandId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SettingsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SecretsProtected = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastPolledAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PollCursor = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    SignatureHtml = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AutoAcknowledge = table.Column<bool>(type: "bit", nullable: false),
                    AcceptUnknownSenders = table.Column<bool>(type: "bit", nullable: false),
                    WebhookKey = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CollaborationChannels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    WebhookUrlProtected = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UrlHint = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EventsJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    DepartmentIdsJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    MinPriority = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    LastSentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastStatusCode = table.Column<int>(type: "int", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollaborationChannels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CommunityTopics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    BodyHtml = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AuthorContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AuthorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AuthorName = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    IsQuestion = table.Column<bool>(type: "bit", nullable: false),
                    IsPinned = table.Column<bool>(type: "bit", nullable: false),
                    AcceptedPostId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    VoteScore = table.Column<int>(type: "int", nullable: false),
                    PostCount = table.Column<int>(type: "int", nullable: false),
                    ViewCount = table.Column<long>(type: "bigint", nullable: false),
                    TagsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LastActivityAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConvertedTicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommunityTopics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CommunityVotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TopicId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PostId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    VoterKey = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Value = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommunityVotes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConfigurationDraft",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceTenant = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    FormatVersion = table.Column<int>(type: "int", nullable: false),
                    BundleJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ResolutionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    PublishedSnapshotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigurationDraft", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConfigurationSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Kind = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    BundleJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SectionsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AppliedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigurationSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ContactMergeLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SurvivorContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MergedContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MergedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MergedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContactMergeLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomerGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomerMergeLog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SurvivorCustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MergedCustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MergedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MergedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerMergeLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomFields",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Entity = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Key = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    FieldType = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    OptionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false),
                    DefaultValue = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    HelpText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ValidationRegex = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    MinValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    MaxValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    MaxLength = table.Column<int>(type: "int", nullable: true),
                    VisibleInPortal = table.Column<bool>(type: "bit", nullable: false),
                    EditableInPortal = table.Column<bool>(type: "bit", nullable: false),
                    DepartmentIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RequiredForStatusIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomFields", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dashboards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Audience = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    IsShared = table.Column<bool>(type: "bit", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    Period = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    WidgetsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dashboards", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResultStorageKey = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RejectionReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApprovedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DownloadTokenHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    DownloadExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DownloadedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResultFileName = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ResultSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EngineeringItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IntegrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ItemType = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    TrackerType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Url = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Project = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    State = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AssignedTo = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    AreaPath = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    IterationPath = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    FixVersion = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ReleaseName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReleasedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeployedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsResolved = table.Column<bool>(type: "bit", nullable: false),
                    IsReleased = table.Column<bool>(type: "bit", nullable: false),
                    IsOrphaned = table.Column<bool>(type: "bit", nullable: false),
                    OrphanReason = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    OrphanedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExternalChangedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSyncError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EngineeringItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EngineeringWebhookDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IntegrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    DeliveryId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EngineeringWebhookDeliveries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FormLayouts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Entity = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Kind = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BrandId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Channel = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    SectionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FormLayouts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InboundEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChannelAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Provider = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    DedupKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboundEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IntegrationLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IntegrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    System = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Direction = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Url = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    StatusCode = table.Column<int>(type: "int", nullable: true),
                    Success = table.Column<bool>(type: "bit", nullable: false),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    RequestExcerpt = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResponseExcerpt = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Integrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    BaseUrl = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    SettingsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SecretsProtected = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    LastSyncAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Integrations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IssueTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KbCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Icon = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Visibility = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    BrandIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KbCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KbCategories_KbCategories_ParentId",
                        column: x => x.ParentId,
                        principalTable: "KbCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeChunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceType = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Locale = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ChunkIndex = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Visibility = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Embedding = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    EmbeddingModel = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Dimensions = table.Column<int>(type: "int", nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeChunks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeSearchLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Query = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    NormalizedQuery = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    ResultCount = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    BrandId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Locale = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeSearchLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LegalHolds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TicketIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FromDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ToDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReleasedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReleasedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegalHolds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LocalReleases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IntegrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProductReleaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PlannedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ReleasedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeployedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalReleases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LocalWorkItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IntegrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    DescriptionHtml = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    State = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Severity = table.Column<int>(type: "int", nullable: true),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    AssigneeUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AreaPath = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    IterationPath = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    TagsJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    FixVersion = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ReleaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CustomerImpactCount = table.Column<int>(type: "int", nullable: false),
                    Rank = table.Column<double>(type: "float", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalWorkItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LookupValues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LookupValues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationPreferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    InApp = table.Column<bool>(type: "bit", nullable: false),
                    Email = table.Column<bool>(type: "bit", nullable: false),
                    Push = table.Column<bool>(type: "bit", nullable: false),
                    Teams = table.Column<bool>(type: "bit", nullable: false),
                    Digest = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationPreferences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Type = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Link = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Severity = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    RequiresAck = table.Column<bool>(type: "bit", nullable: false),
                    AcknowledgedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReadAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EntityType = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DedupKey = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    QuietHoursStart = table.Column<TimeOnly>(type: "time", nullable: true),
                    QuietHoursEnd = table.Column<TimeOnly>(type: "time", nullable: true),
                    TimeZone = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    DailyDigestAt = table.Column<TimeOnly>(type: "time", nullable: false),
                    DesktopNotifications = table.Column<bool>(type: "bit", nullable: false),
                    TeamsWebhookUrlProtected = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    BodyHtml = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OtpChallenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PrincipalKind = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    PrincipalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Purpose = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    CodeHash = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Destination = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OtpChallenges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboxDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Handler = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Succeeded = table.Column<bool>(type: "bit", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxDeliveries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboxEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Type = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    AggregateType = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    AggregateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LockedUntil = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LockedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    CausationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChainDepth = table.Column<int>(type: "int", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorType = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Permissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Module = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsSensitive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Permissions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Plans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MonthlyPricePerAgent = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    AnnualPricePerAgent = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    IsPublic = table.Column<bool>(type: "bit", nullable: false),
                    TrialDays = table.Column<int>(type: "int", nullable: false),
                    MaxAgents = table.Column<int>(type: "int", nullable: true),
                    MaxLightAgents = table.Column<int>(type: "int", nullable: true),
                    MaxPortalUsers = table.Column<int>(type: "int", nullable: true),
                    StorageMb = table.Column<long>(type: "bigint", nullable: true),
                    MaxChannels = table.Column<int>(type: "int", nullable: true),
                    AiTokensMonthly = table.Column<long>(type: "bigint", nullable: true),
                    MaxAttachmentMb = table.Column<int>(type: "int", nullable: true),
                    ApiCallsMonthly = table.Column<long>(type: "bigint", nullable: true),
                    MaxBrands = table.Column<int>(type: "int", nullable: true),
                    FeaturesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Plans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformOperators",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    MfaSecretProtected = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformOperators", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProductFamilies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductFamilies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PushDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Platform = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Token = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    AppKind = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RegisteredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AppVersion = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DeactivatedReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushDevices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReplyTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    BodyHtml = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Visibility = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UsageCount = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReplyTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReportExports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReportKey = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ParametersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Format = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    FileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    RowCount = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportExports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RetentionPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Action = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    AfterDays = table.Column<int>(type: "int", nullable: false),
                    ConditionJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    LastRunAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastRunAffected = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetentionPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false),
                    TicketScope = table.Column<int>(type: "int", nullable: false),
                    CustomerScope = table.Column<int>(type: "int", nullable: false),
                    IsLightAgent = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RuleExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RuleVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Trigger = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ConditionTraceJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ActionResultsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ChainDepth = table.Column<int>(type: "int", nullable: false),
                    Attempt = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuleExecutions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SavedViews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Visibility = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    FilterJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ColumnsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SortJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedViews", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ReportKey = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    ParametersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Format = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Frequency = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    RunAtLocal = table.Column<TimeOnly>(type: "time", nullable: false),
                    DayOfWeek = table.Column<int>(type: "int", nullable: true),
                    DayOfMonth = table.Column<int>(type: "int", nullable: true),
                    TimeZone = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RecipientsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    NextRunAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastRunAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastStatus = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledReports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    LastRunAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NextRunAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LockedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    LockedUntil = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RunCount = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SearchDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Snippet = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Visibility = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Locale = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ScopeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TermCount = table.Column<int>(type: "int", nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchDocuments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Shifts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Color = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TimeZone = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    WeeklyHoursJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shifts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Skills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Skills", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SlaEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MilestoneId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Milestone = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DetailsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StoredFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StorageKey = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ScanStatus = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ScanDetail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UploadedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UploadedByContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoredFiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SupportAccessGrant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperatorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperatorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    DurationMinutes = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    DecidedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecidedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupportAccessGrant", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Color = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantCounters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    NextValue = table.Column<long>(type: "bigint", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantCounters", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    DatabaseMode = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    DedicatedConnectionName = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    DefaultTimeZone = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    DefaultLocale = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Country = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    DataRegion = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    SuspensionReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SuspendedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletionScheduledAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ValueJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TicketCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    VisibleInPortal = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketCategories_TicketCategories_ParentId",
                        column: x => x.ParentId,
                        principalTable: "TicketCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TicketStatuses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Category = table.Column<int>(type: "int", nullable: false),
                    Color = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    PausesSla = table.Column<bool>(type: "bit", nullable: false),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CustomerLabel = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    AllowedNextJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SubstatusesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketStatuses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TrialSignup",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AdminName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AdminEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    AdminEmailNormalized = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    PlanCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CodeHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrialSignup", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TroubleshootingFlows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Visibility = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    StartedCount = table.Column<long>(type: "bigint", nullable: false),
                    ResolvedCount = table.Column<long>(type: "bigint", nullable: false),
                    EscalatedCount = table.Column<long>(type: "bigint", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TroubleshootingFlows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UsageRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Metric = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    PeriodStart = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Quantity = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UsageSnapshot",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Day = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Metric = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Quantity = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageSnapshot", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    EmailNormalized = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    LastName = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Kind = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    PasswordChangedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PasswordHistoryJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MfaEnabled = table.Column<bool>(type: "bit", nullable: false),
                    MfaSecretProtected = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MfaRecoveryCodesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FailedLoginCount = table.Column<int>(type: "int", nullable: false),
                    LockedUntil = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastLoginAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TimeZone = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Locale = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    AvatarUrl = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Signature = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExternalSubject = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ExternalIssuer = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PrincipalKind = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    PrincipalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    FamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IdleExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedReason = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ReplacedByHash = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ClientKind = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    MfaSatisfied = table.Column<bool>(type: "bit", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Webhooks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Url = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    SecretProtected = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EventsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    LastDeliveryAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastStatusCode = table.Column<int>(type: "int", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Webhooks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Workflows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    PublishedVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LatestVersionNumber = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workflows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RuleVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    State = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ChangeNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PublishedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuleVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RuleVersions_AutomationRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "AutomationRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CalendarHolidays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CalendarId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Recurring = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarHolidays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CalendarHolidays_BusinessCalendars_CalendarId",
                        column: x => x.CalendarId,
                        principalTable: "BusinessCalendars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Departments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ParentDepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BusinessCalendarId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DefaultSlaPolicyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DefaultTeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HeadUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Email = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    VisibleInPortal = table.Column<bool>(type: "bit", nullable: false),
                    TransferRequiresApproval = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Departments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Departments_BusinessCalendars_BusinessCalendarId",
                        column: x => x.BusinessCalendarId,
                        principalTable: "BusinessCalendars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Departments_Departments_ParentDepartmentId",
                        column: x => x.ParentDepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SlaPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    EvaluationOrder = table.Column<int>(type: "int", nullable: false),
                    ConditionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CalendarId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UseContractCalendar = table.Column<bool>(type: "bit", nullable: false),
                    PauseStatusIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SlaPolicies_BusinessCalendars_CalendarId",
                        column: x => x.CalendarId,
                        principalTable: "BusinessCalendars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChannelAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ExternalKey = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    AssignedUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastMessageAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ServiceWindowEndsAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    VisitorTokenHash = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    VisitorName = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    VisitorEmail = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    IdentityVerified = table.Column<bool>(type: "bit", nullable: false),
                    MetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Rating = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Conversations_ChannelAccounts_ChannelAccountId",
                        column: x => x.ChannelAccountId,
                        principalTable: "ChannelAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChannelAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ThreadId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Channel = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Direction = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    InternetMessageId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    InReplyTo = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ReferencesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FromAddress = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    FromName = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ToJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CcJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    BodyText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BodyHtml = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ContentType = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    MetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RawStorageKey = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ProcessingError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsAutoReply = table.Column<bool>(type: "bit", nullable: false),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AuthorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Messages_ChannelAccounts_ChannelAccountId",
                        column: x => x.ChannelAccountId,
                        principalTable: "ChannelAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CommunityPosts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TopicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BodyHtml = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AuthorContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AuthorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AuthorName = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    IsAgentResponse = table.Column<bool>(type: "bit", nullable: false),
                    IsAcceptedAnswer = table.Column<bool>(type: "bit", nullable: false),
                    IsHidden = table.Column<bool>(type: "bit", nullable: false),
                    VoteScore = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommunityPosts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommunityPosts_CommunityTopics_TopicId",
                        column: x => x.TopicId,
                        principalTable: "CommunityTopics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Articles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Slug = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Visibility = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ArticleType = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CurrentVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PublishedVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LatestVersionNumber = table.Column<int>(type: "int", nullable: false),
                    PrimaryLocale = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    TagsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RelatedArticleIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BrandIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReviewDueAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ViewCount = table.Column<long>(type: "bigint", nullable: false),
                    HelpfulCount = table.Column<int>(type: "int", nullable: false),
                    NotHelpfulCount = table.Column<int>(type: "int", nullable: false),
                    RatingAverage = table.Column<double>(type: "float", nullable: true),
                    RatingCount = table.Column<int>(type: "int", nullable: false),
                    SourceTicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Articles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Articles_KbCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "KbCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NotificationDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    ScheduledFor = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationDeliveries_Notifications_NotificationId",
                        column: x => x.NotificationId,
                        principalTable: "Notifications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    DefaultDepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DefaultTeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ErpProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Products_ProductFamilies_FamilyId",
                        column: x => x.FamilyId,
                        principalTable: "ProductFamilies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RolePermissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PermissionCode = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolePermissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RolePermissions_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledReportRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScheduledReportId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScheduledFor = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Manual = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RecipientsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Delivered = table.Column<int>(type: "int", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    RowCount = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledReportRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduledReportRuns_ScheduledReports_ScheduledReportId",
                        column: x => x.ScheduledReportId,
                        principalTable: "ScheduledReports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SearchPostings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Term = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Weight = table.Column<float>(type: "real", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchPostings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SearchPostings_SearchDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "SearchDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentShiftAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShiftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveTo = table.Column<DateOnly>(type: "date", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentShiftAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentShiftAssignments_Shifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "Shifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentSkills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SkillId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Level = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSkills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentSkills_Skills_SkillId",
                        column: x => x.SkillId,
                        principalTable: "Skills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Brands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CustomDomain = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: true),
                    LogoUrl = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    FaviconUrl = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    EmailFooterHtml = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    PrimaryColor = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    AccentColor = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    SupportEmail = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    SupportPhone = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    PortalTitle = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    PortalWelcome = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FooterHtml = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DefaultDepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DefaultLocale = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    SupportedLocalesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    PortalEnabled = table.Column<bool>(type: "bit", nullable: false),
                    KnowledgeBaseEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CommunityEnabled = table.Column<bool>(type: "bit", nullable: false),
                    ChatEnabled = table.Column<bool>(type: "bit", nullable: false),
                    AiAssistantEnabled = table.Column<bool>(type: "bit", nullable: false),
                    AllowAnonymousTickets = table.Column<bool>(type: "bit", nullable: false),
                    AllowSelfRegistration = table.Column<bool>(type: "bit", nullable: false),
                    PortalAuthMode = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ChatWidgetSettingsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Brands", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Brands_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Subscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    BillingCycle = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Seats = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TrialEndsAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CurrentPeriodStart = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CurrentPeriodEnd = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CancelAtPeriodEnd = table.Column<bool>(type: "bit", nullable: false),
                    PendingPlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LimitOverridesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    GraceEndsAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SuspendedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    BillingContactName = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    BillingContactEmail = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    BillingAddress = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    BillingTaxId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    PaymentProvider = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Subscriptions_Plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Subscriptions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AgentInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    CodeHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    InvitedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AcceptedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentInvitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentInvitations_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsLightAgent = table.Column<bool>(type: "bit", nullable: false),
                    MaxOpenTickets = table.Column<int>(type: "int", nullable: false),
                    MaxConcurrentChats = table.Column<int>(type: "int", nullable: false),
                    Availability = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    AvailabilityChangedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReportsToUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DefaultShiftId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LanguagesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    HourlyCost = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Agents_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ParentCustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CustomerGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Category = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Industry = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Tier = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    AccountManagerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ServiceRegion = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    PreferredLanguage = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    TimeZone = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Website = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    DomainsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    CommunicationPrefsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CustomFieldsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ErpCustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ErpCode = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ErpSyncedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Customers_CustomerGroups_CustomerGroupId",
                        column: x => x.CustomerGroupId,
                        principalTable: "CustomerGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Customers_Customers_ParentCustomerId",
                        column: x => x.ParentCustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Customers_Users_AccountManagerUserId",
                        column: x => x.AccountManagerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserRoles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserRoles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WebhookDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WebhookId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResponseCode = table.Column<int>(type: "int", nullable: true),
                    ResponseExcerpt = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DurationMs = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DeliveredAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebhookDeliveries_Webhooks_WebhookId",
                        column: x => x.WebhookId,
                        principalTable: "Webhooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    State = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ChangeNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PublishedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowVersions_Workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "Workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentDepartments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsSupervisor = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDepartments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentDepartments_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AgentDepartments_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Teams",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LeadUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RoutingStrategy = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    RoundRobinCursorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OnlyAssignToOnlineAgents = table.Column<bool>(type: "bit", nullable: false),
                    ConsiderShifts = table.Column<bool>(type: "bit", nullable: false),
                    MaxOpenTicketsPerAgent = table.Column<int>(type: "int", nullable: true),
                    FallbackTeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Teams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Teams_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Teams_Teams_FallbackTeamId",
                        column: x => x.FallbackTeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SlaEscalationLevels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Milestone = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Level = table.Column<int>(type: "int", nullable: false),
                    MinutesAfterBreach = table.Column<int>(type: "int", nullable: false),
                    AtPercent = table.Column<int>(type: "int", nullable: true),
                    Action = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    NotifyJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReassignToUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReassignToTeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaEscalationLevels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SlaEscalationLevels_SlaPolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "SlaPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SlaTargets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    FirstResponseMinutes = table.Column<int>(type: "int", nullable: true),
                    NextResponseMinutes = table.Column<int>(type: "int", nullable: true),
                    ResolutionMinutes = table.Column<int>(type: "int", nullable: true),
                    BusinessHours = table.Column<bool>(type: "bit", nullable: false),
                    WarningPercent = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SlaTargets_SlaPolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "SlaPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MessageDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Recipient = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ProviderMessageId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StatusHistoryJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessageDeliveries_Messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ArticleAccessRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArticleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CustomerGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CustomerTier = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArticleAccessRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArticleAccessRules_Articles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "Articles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ArticleAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArticleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArticleAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArticleAttachments_Articles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "Articles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ArticleFeedback",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArticleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Helpful = table.Column<bool>(type: "bit", nullable: false),
                    Rating = table.Column<int>(type: "int", nullable: true),
                    Comment = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Locale = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Reviewed = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArticleFeedback", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArticleFeedback_Articles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "Articles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ArticleTranslations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArticleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Locale = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    BodyHtml = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    TranslatorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceVersionNumber = table.Column<int>(type: "int", nullable: false),
                    MachineTranslated = table.Column<bool>(type: "bit", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArticleTranslations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArticleTranslations_Articles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "Articles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ArticleVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArticleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    Locale = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    BodyHtml = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Keywords = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ChangeNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AuthorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReviewerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReviewNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArticleVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArticleVersions_Articles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "Articles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductModules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductModules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductModules_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ReleaseDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EndOfSupportDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductVersions_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BillingInvoices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Number = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    PeriodStart = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PeriodEnd = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LinesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Subtotal = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Tax = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Total = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    IssuedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DueAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PaidAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PaymentReference = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    PaymentProvider = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingInvoices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BillingInvoices_Subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "Subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Contracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Type = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SlaPolicyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CalendarId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PrepaidHours = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    ConsumedHours = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    IsBillable = table.Column<bool>(type: "bit", nullable: false),
                    HourlyRate = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    ContractValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Currency = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    AutoRenew = table.Column<bool>(type: "bit", nullable: false),
                    RenewalDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CoverageJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ErpContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ErpReference = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contracts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Contracts_BusinessCalendars_CalendarId",
                        column: x => x.CalendarId,
                        principalTable: "BusinessCalendars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Contracts_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Contracts_SlaPolicies_SlaPolicyId",
                        column: x => x.SlaPolicyId,
                        principalTable: "SlaPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerAssets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    InstallationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    SerialNumber = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    AssetTag = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    PurchaseDate = table.Column<DateOnly>(type: "date", nullable: true),
                    WarrantyEndsOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerAssets_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerDepartments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerDepartments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerDepartments_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CustomerLocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    AddressLine1 = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    AddressLine2 = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    City = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    State = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Country = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    PostalCode = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    TimeZone = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ServiceRegion = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    IsHeadOffice = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ErpLocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerLocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerLocations_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ServiceActivities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    InstallationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Type = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ScheduledStart = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ScheduledEnd = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AssignedUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    VisibleToCustomer = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceActivities_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TeamMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsLead = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeamMembers_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TeamMembers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductReleases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    BuildNumber = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Type = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ReleaseDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ReleaseNotes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EngineeringReleaseRef = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductReleases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductReleases_ProductVersions_VersionId",
                        column: x => x.VersionId,
                        principalTable: "ProductVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Entitlements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Type = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    InstallationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IncidentsAllowed = table.Column<int>(type: "int", nullable: true),
                    IncidentsUsed = table.Column<int>(type: "int", nullable: false),
                    HoursAllowed = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    HoursUsed = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Entitlements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Entitlements_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Contacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CustomerDepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FirstName = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    LastName = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    FullName = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    EmailNormalized = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    PhoneNormalized = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Mobile = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    WhatsAppNumber = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    JobTitle = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    PreferredLanguage = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    PreferredChannel = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: true),
                    TimeZone = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    IsVip = table.Column<bool>(type: "bit", nullable: false),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: false),
                    PortalAccess = table.Column<int>(type: "int", nullable: false),
                    PortalPasswordHash = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    PortalLastLoginAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PortalFailedLogins = table.Column<int>(type: "int", nullable: false),
                    PortalLockedUntil = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EmailVerifiedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PhoneVerifiedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    MergedIntoContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CustomFieldsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ErpContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ErasedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Contacts_CustomerDepartments_CustomerDepartmentId",
                        column: x => x.CustomerDepartmentId,
                        principalTable: "CustomerDepartments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Contacts_CustomerLocations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "CustomerLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Contacts_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Installations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReleaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    InstallationCode = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Environment = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    DeploymentMode = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    OperatingSystem = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    DatabaseVersion = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ServerConfiguration = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IntegrationsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModulesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UserCount = table.Column<int>(type: "int", nullable: true),
                    InstalledOn = table.Column<DateOnly>(type: "date", nullable: true),
                    GoLiveOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ErpInstallationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Installations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Installations_CustomerLocations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "CustomerLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Installations_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Installations_ProductVersions_VersionId",
                        column: x => x.VersionId,
                        principalTable: "ProductVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Installations_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ContactConsents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Purpose = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    RecordedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContactConsents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContactConsents_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContactIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Identifier = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    IsVerified = table.Column<bool>(type: "bit", nullable: false),
                    VerifiedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContactIdentities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContactIdentities_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Licences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstallationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    KeyMasked = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Edition = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Seats = table.Column<int>(type: "int", nullable: false),
                    IssuedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    ExpiresOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ErpLicenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Licences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Licences_Installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "Installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Tickets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<long>(type: "bigint", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    DescriptionHtml = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DescriptionText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Channel = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    BrandId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChannelAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AssigneeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    VersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    InstallationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SubcategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IssueTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    Severity = table.Column<int>(type: "int", nullable: true),
                    StatusId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StatusCategory = table.Column<int>(type: "int", nullable: false),
                    Substatus = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Language = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FirstResponseAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastAgentResponseAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastCustomerResponseAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FirstResponseDueAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NextResponseDueAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DueAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReopenedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastActivityAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SlaPolicyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SlaState = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    EntitlementStatus = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EntitlementId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EntitlementNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ParentTicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MergedIntoTicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResolutionCode = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    RootCause = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ResolutionHtml = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AwaitingCustomerConfirmation = table.Column<bool>(type: "bit", nullable: false),
                    ConfirmationRequestedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CsatScore = table.Column<int>(type: "int", nullable: true),
                    CsatComment = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Sentiment = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    SentimentScore = table.Column<double>(type: "float", nullable: true),
                    AiSummary = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AiSummaryAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TimeSpentMinutes = table.Column<int>(type: "int", nullable: false),
                    BillableMinutes = table.Column<int>(type: "int", nullable: false),
                    SupportCost = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    ReopenCount = table.Column<int>(type: "int", nullable: false),
                    AgentReplyCount = table.Column<int>(type: "int", nullable: false),
                    CustomerReplyCount = table.Column<int>(type: "int", nullable: false),
                    AssignmentCount = table.Column<int>(type: "int", nullable: false),
                    LastThreadSeq = table.Column<int>(type: "int", nullable: false),
                    IsEscalated = table.Column<bool>(type: "bit", nullable: false),
                    EscalationLevel = table.Column<int>(type: "int", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WorkflowVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WorkflowStateKey = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    CustomFieldsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsSpam = table.Column<bool>(type: "bit", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tickets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tickets_Brands_BrandId",
                        column: x => x.BrandId,
                        principalTable: "Brands",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_CustomerAssets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "CustomerAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_CustomerLocations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "CustomerLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_Installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "Installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_IssueTypes_IssueTypeId",
                        column: x => x.IssueTypeId,
                        principalTable: "IssueTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_ProductModules_ModuleId",
                        column: x => x.ModuleId,
                        principalTable: "ProductModules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_ProductVersions_VersionId",
                        column: x => x.VersionId,
                        principalTable: "ProductVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_SlaPolicies_SlaPolicyId",
                        column: x => x.SlaPolicyId,
                        principalTable: "SlaPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_TicketCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "TicketCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_TicketCategories_SubcategoryId",
                        column: x => x.SubcategoryId,
                        principalTable: "TicketCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_TicketStatuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "TicketStatuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_Tickets_MergedIntoTicketId",
                        column: x => x.MergedIntoTicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_Tickets_ParentTicketId",
                        column: x => x.ParentTicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_Users_AssigneeId",
                        column: x => x.AssigneeId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EngineeringLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    IntegrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ItemType = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Url = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    State = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    AssignedTo = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    IterationPath = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    FixVersion = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    IsResolved = table.Column<bool>(type: "bit", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CustomerNotified = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EngineeringLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EngineeringLinks_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SatisfactionResponses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AgentUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TokenHash = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Rating = table.Column<int>(type: "int", nullable: true),
                    Comment = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Channel = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RespondedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SatisfactionResponses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SatisfactionResponses_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SlaInstances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PolicyVersion = table.Column<int>(type: "int", nullable: false),
                    PolicySnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CalendarSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    PausedSince = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaInstances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SlaInstances_SlaPolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "SlaPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SlaInstances_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TicketAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ThreadId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Visibility = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    IsInline = table.Column<bool>(type: "bit", nullable: false),
                    ContentId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    UploadedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UploadedByContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketAttachments_StoredFiles_FileId",
                        column: x => x.FileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketAttachments_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TicketDrafts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    BodyHtml = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ToJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CcJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TemplateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BasedOnSeq = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketDrafts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketDrafts_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TicketEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ActorType = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorName = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ChangesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DetailsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Summary = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketEvents_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TicketExternalReferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    System = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RecordType = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecordNumber = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Url = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ContextJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsOrigin = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketExternalReferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketExternalReferences_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TicketFollowUps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RemindAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    FiredAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketFollowUps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketFollowUps_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TicketLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceTicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetTicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LinkType = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketLinks_Tickets_SourceTicketId",
                        column: x => x.SourceTicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TicketLinks_Tickets_TargetTicketId",
                        column: x => x.TargetTicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TicketMetrics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketNumber = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedDay = table.Column<DateOnly>(type: "date", nullable: false),
                    Channel = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    StatusCategory = table.Column<int>(type: "int", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AssigneeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CustomerTier = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    VersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SubcategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IssueTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RootCause = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ResolutionCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Sentiment = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Excluded = table.Column<bool>(type: "bit", nullable: false),
                    FirstResponderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FirstResponseMinutes = table.Column<int>(type: "int", nullable: true),
                    FirstResponseBusinessMinutes = table.Column<int>(type: "int", nullable: true),
                    ResolutionMinutes = table.Column<int>(type: "int", nullable: true),
                    ResolutionBusinessMinutes = table.Column<int>(type: "int", nullable: true),
                    ReopenCount = table.Column<int>(type: "int", nullable: false),
                    AgentReplies = table.Column<int>(type: "int", nullable: false),
                    CustomerReplies = table.Column<int>(type: "int", nullable: false),
                    Handoffs = table.Column<int>(type: "int", nullable: false),
                    Transfers = table.Column<int>(type: "int", nullable: false),
                    FirstContactResolution = table.Column<bool>(type: "bit", nullable: false),
                    HasSla = table.Column<bool>(type: "bit", nullable: false),
                    FirstResponseSlaMet = table.Column<bool>(type: "bit", nullable: false),
                    FirstResponseBreached = table.Column<bool>(type: "bit", nullable: false),
                    ResolutionSlaMet = table.Column<bool>(type: "bit", nullable: false),
                    ResolutionBreached = table.Column<bool>(type: "bit", nullable: false),
                    NextResponseMet = table.Column<int>(type: "int", nullable: false),
                    NextResponseBreaches = table.Column<int>(type: "int", nullable: false),
                    SlaBreached = table.Column<bool>(type: "bit", nullable: false),
                    IsEscalated = table.Column<bool>(type: "bit", nullable: false),
                    EscalationCount = table.Column<int>(type: "int", nullable: false),
                    CsatScore = table.Column<int>(type: "int", nullable: true),
                    FirstResponseAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolvedDay = table.Column<DateOnly>(type: "date", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TimeSpentMinutes = table.Column<int>(type: "int", nullable: false),
                    BillableMinutes = table.Column<int>(type: "int", nullable: false),
                    Cost = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    SourceVersion = table.Column<byte[]>(type: "varbinary(8)", maxLength: 8, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketMetrics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketMetrics_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TicketTags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TagId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketTags_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TicketTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentTaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsChecklistItem = table.Column<bool>(type: "bit", nullable: false),
                    AssigneeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DueAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketTasks_TicketTasks_ParentTaskId",
                        column: x => x.ParentTaskId,
                        principalTable: "TicketTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketTasks_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TicketThreads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Seq = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Visibility = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Channel = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Direction = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    AuthorType = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AuthorContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AuthorName = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    FromAddress = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ToJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CcJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BccJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    BodyHtml = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BodyText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChannelMetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DeliveryStatus = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Sentiment = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    IsRedacted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EditedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClientRequestId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketThreads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketThreads_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TicketWatchers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    AddedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AddedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketWatchers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketWatchers_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TicketWatchers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TimeEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Minutes = table.Column<int>(type: "int", nullable: false),
                    IsBillable = table.Column<bool>(type: "bit", nullable: false),
                    ActivityType = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    HourlyRate = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    CostAmount = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    BillableAmount = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EntitlementId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Source = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimeEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TimeEntries_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TimeEntries_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SlaMilestones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Cycle = table.Column<int>(type: "int", nullable: false),
                    TargetMinutes = table.Column<int>(type: "int", nullable: false),
                    BusinessHours = table.Column<bool>(type: "bit", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DueAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    WarningAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    WarningRaised = table.Column<bool>(type: "bit", nullable: false),
                    ElapsedMinutesBeforeSegment = table.Column<double>(type: "float", nullable: false),
                    SegmentStartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    BreachedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EscalationLevelReached = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaMilestones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SlaMilestones_SlaInstances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "SlaInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentDelegations_TenantId_UserId_StartsAt",
                table: "AgentDelegations",
                columns: new[] { "TenantId", "UserId", "StartsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentDepartments_DepartmentId",
                table: "AgentDepartments",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentDepartments_TenantId_DepartmentId",
                table: "AgentDepartments",
                columns: new[] { "TenantId", "DepartmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentDepartments_UserId_DepartmentId",
                table: "AgentDepartments",
                columns: new[] { "UserId", "DepartmentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentInvitations_TenantId_Email",
                table: "AgentInvitations",
                columns: new[] { "TenantId", "Email" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentInvitations_TenantId_UserId_CreatedAt",
                table: "AgentInvitations",
                columns: new[] { "TenantId", "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentInvitations_UserId",
                table: "AgentInvitations",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentLeaves_ApprovalRequestId",
                table: "AgentLeaves",
                column: "ApprovalRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentLeaves_TenantId_Status_StartsAt",
                table: "AgentLeaves",
                columns: new[] { "TenantId", "Status", "StartsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentLeaves_TenantId_UserId_StartsAt",
                table: "AgentLeaves",
                columns: new[] { "TenantId", "UserId", "StartsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentMetrics_TenantId_UserId_Day",
                table: "AgentMetrics",
                columns: new[] { "TenantId", "UserId", "Day" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentRoutingAttributes_TenantId_Kind_RefId",
                table: "AgentRoutingAttributes",
                columns: new[] { "TenantId", "Kind", "RefId" });

            migrationBuilder.CreateIndex(
                name: "IX_Agents_TenantId_UserId",
                table: "Agents",
                columns: new[] { "TenantId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Agents_UserId",
                table: "Agents",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentShiftAssignments_ShiftId",
                table: "AgentShiftAssignments",
                column: "ShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentShiftAssignments_UserId_EffectiveFrom",
                table: "AgentShiftAssignments",
                columns: new[] { "UserId", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSkills_SkillId",
                table: "AgentSkills",
                column: "SkillId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSkills_UserId_SkillId",
                table: "AgentSkills",
                columns: new[] { "UserId", "SkillId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiActionProposals_TenantId_Status_CreatedAt",
                table: "AiActionProposals",
                columns: new[] { "TenantId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AiActionProposals_TicketId",
                table: "AiActionProposals",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_AiAgents_TenantId",
                table: "AiAgents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AiConfigurations_TenantId",
                table: "AiConfigurations",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiExecutions_TenantId_EntityId",
                table: "AiExecutions",
                columns: new[] { "TenantId", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_AiExecutions_TenantId_Feature_CreatedAt",
                table: "AiExecutions",
                columns: new[] { "TenantId", "Feature", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AiPromptTemplates_TenantId_Feature_Version",
                table: "AiPromptTemplates",
                columns: new[] { "TenantId", "Feature", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiUsage_TenantId_PeriodStart_Feature_Model",
                table: "AiUsage",
                columns: new[] { "TenantId", "PeriodStart", "Feature", "Model" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnomalyAlerts_TenantId_Dimension_DimensionId_Day",
                table: "AnomalyAlerts",
                columns: new[] { "TenantId", "Dimension", "DimensionId", "Day" },
                unique: true,
                filter: "[DimensionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ApiClients_ClientId",
                table: "ApiClients",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_Prefix",
                table: "ApiKeys",
                column: "Prefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_EntityId",
                table: "ApprovalRequests",
                column: "EntityId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_TenantId_Status_CreatedAt",
                table: "ApprovalRequests",
                columns: new[] { "TenantId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ArticleAccessRules_ArticleId",
                table: "ArticleAccessRules",
                column: "ArticleId");

            migrationBuilder.CreateIndex(
                name: "IX_ArticleAttachments_ArticleId",
                table: "ArticleAttachments",
                column: "ArticleId");

            migrationBuilder.CreateIndex(
                name: "IX_ArticleFeedback_ArticleId_CreatedAt",
                table: "ArticleFeedback",
                columns: new[] { "ArticleId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Articles_CategoryId",
                table: "Articles",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Articles_TenantId_ReviewDueAt",
                table: "Articles",
                columns: new[] { "TenantId", "ReviewDueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Articles_TenantId_Slug",
                table: "Articles",
                columns: new[] { "TenantId", "Slug" },
                unique: true,
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Articles_TenantId_Status_Visibility",
                table: "Articles",
                columns: new[] { "TenantId", "Status", "Visibility" });

            migrationBuilder.CreateIndex(
                name: "IX_ArticleTranslations_ArticleId_Locale",
                table: "ArticleTranslations",
                columns: new[] { "ArticleId", "Locale" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArticleVersions_ArticleId_VersionNumber",
                table: "ArticleVersions",
                columns: new[] { "ArticleId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_EntityType_EntityId",
                table: "AuditLogs",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TenantId_OccurredAt",
                table: "AuditLogs",
                columns: new[] { "TenantId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TenantId_Sequence",
                table: "AuditLogs",
                columns: new[] { "TenantId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Unsealed",
                table: "AuditLogs",
                column: "OccurredAt",
                filter: "[Hash] = ''");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRules_TenantId_Trigger_IsActive_ExecutionOrder",
                table: "AutomationRules",
                columns: new[] { "TenantId", "Trigger", "IsActive", "ExecutionOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobs_Due",
                table: "BackgroundJobs",
                columns: new[] { "Status", "RunAt", "Priority" },
                filter: "[Status] IN ('Queued','Running','Failed')");

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobs_IdempotencyKey",
                table: "BackgroundJobs",
                column: "IdempotencyKey",
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BacklogSnapshots_TenantId_Day_DepartmentId_Priority",
                table: "BacklogSnapshots",
                columns: new[] { "TenantId", "Day", "DepartmentId", "Priority" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillingInvoices_IssuedAt",
                table: "BillingInvoices",
                column: "IssuedAt");

            migrationBuilder.CreateIndex(
                name: "IX_BillingInvoices_OnePerPeriod",
                table: "BillingInvoices",
                columns: new[] { "TenantId", "SubscriptionId", "PeriodStart" },
                unique: true,
                filter: "[Kind] = 'Subscription' AND [Status] <> 'Void'");

            migrationBuilder.CreateIndex(
                name: "IX_BillingInvoices_Status_DueAt",
                table: "BillingInvoices",
                columns: new[] { "Status", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BillingInvoices_SubscriptionId",
                table: "BillingInvoices",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_BillingInvoices_TenantId_Number",
                table: "BillingInvoices",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Brands_CustomDomain",
                table: "Brands",
                column: "CustomDomain",
                unique: true,
                filter: "[CustomDomain] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Brands_Slug",
                table: "Brands",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Brands_TenantId",
                table: "Brands",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessCalendars_TenantId",
                table: "BusinessCalendars",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarHolidays_CalendarId_Date",
                table: "CalendarHolidays",
                columns: new[] { "CalendarId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CallRecords_ChannelAccountId_ExternalCallId",
                table: "CallRecords",
                columns: new[] { "ChannelAccountId", "ExternalCallId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CallRecords_TicketId",
                table: "CallRecords",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelAccounts_TenantId_Type",
                table: "ChannelAccounts",
                columns: new[] { "TenantId", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelAccounts_Type_Address",
                table: "ChannelAccounts",
                columns: new[] { "Type", "Address" });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelAccounts_WebhookKey",
                table: "ChannelAccounts",
                column: "WebhookKey",
                unique: true,
                filter: "[WebhookKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CollaborationChannels_TenantId_IsActive",
                table: "CollaborationChannels",
                columns: new[] { "TenantId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_CommunityPosts_TopicId",
                table: "CommunityPosts",
                column: "TopicId");

            migrationBuilder.CreateIndex(
                name: "IX_CommunityTopics_TenantId_BrandId_LastActivityAt",
                table: "CommunityTopics",
                columns: new[] { "TenantId", "BrandId", "LastActivityAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CommunityVotes_TopicId_PostId_VoterKey",
                table: "CommunityVotes",
                columns: new[] { "TopicId", "PostId", "VoterKey" },
                unique: true,
                filter: "[TopicId] IS NOT NULL AND [PostId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationDraft_TenantId_Status_CreatedAt",
                table: "ConfigurationDraft",
                columns: new[] { "TenantId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationSnapshots_TenantId_Version",
                table: "ConfigurationSnapshots",
                columns: new[] { "TenantId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContactConsents_ContactId_Channel_Purpose",
                table: "ContactConsents",
                columns: new[] { "ContactId", "Channel", "Purpose" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContactIdentities_ContactId",
                table: "ContactIdentities",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_ContactIdentities_TenantId_Channel_Identifier",
                table: "ContactIdentities",
                columns: new[] { "TenantId", "Channel", "Identifier" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContactMergeLogs_TenantId_SurvivorContactId",
                table: "ContactMergeLogs",
                columns: new[] { "TenantId", "SurvivorContactId" });

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_CustomerDepartmentId",
                table: "Contacts",
                column: "CustomerDepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_CustomerId",
                table: "Contacts",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_LocationId",
                table: "Contacts",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_TenantId_EmailNormalized",
                table: "Contacts",
                columns: new[] { "TenantId", "EmailNormalized" });

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_TenantId_FullName",
                table: "Contacts",
                columns: new[] { "TenantId", "FullName" });

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_TenantId_PhoneNormalized",
                table: "Contacts",
                columns: new[] { "TenantId", "PhoneNormalized" });

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_CalendarId",
                table: "Contracts",
                column: "CalendarId");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_CustomerId",
                table: "Contracts",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_SlaPolicyId",
                table: "Contracts",
                column: "SlaPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_TenantId_CustomerId_Status_EndDate",
                table: "Contracts",
                columns: new[] { "TenantId", "CustomerId", "Status", "EndDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_TenantId_Number",
                table: "Contracts",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_OpenKey",
                table: "Conversations",
                columns: new[] { "ChannelAccountId", "ExternalKey" },
                unique: true,
                filter: "[Status] <> 'Closed'");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_TenantId_Status_LastMessageAt",
                table: "Conversations",
                columns: new[] { "TenantId", "Status", "LastMessageAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_TicketId",
                table: "Conversations",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAssets_CustomerId",
                table: "CustomerAssets",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAssets_TenantId_CustomerId",
                table: "CustomerAssets",
                columns: new[] { "TenantId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAssets_TenantId_SerialNumber",
                table: "CustomerAssets",
                columns: new[] { "TenantId", "SerialNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDepartments_CustomerId",
                table: "CustomerDepartments",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerGroups_TenantId_Name",
                table: "CustomerGroups",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerLocations_CustomerId",
                table: "CustomerLocations",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerMergeLog_TenantId_MergedCustomerId",
                table: "CustomerMergeLog",
                columns: new[] { "TenantId", "MergedCustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerMergeLog_TenantId_SurvivorCustomerId",
                table: "CustomerMergeLog",
                columns: new[] { "TenantId", "SurvivorCustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_Customers_AccountManagerUserId",
                table: "Customers",
                column: "AccountManagerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_CustomerGroupId",
                table: "Customers",
                column: "CustomerGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_ParentCustomerId",
                table: "Customers",
                column: "ParentCustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TenantId_Code",
                table: "Customers",
                columns: new[] { "TenantId", "Code" },
                unique: true,
                filter: "[Code] IS NOT NULL AND [IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TenantId_ErpCustomerId",
                table: "Customers",
                columns: new[] { "TenantId", "ErpCustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TenantId_Name",
                table: "Customers",
                columns: new[] { "TenantId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomFields_TenantId_Entity_Key",
                table: "CustomFields",
                columns: new[] { "TenantId", "Entity", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_TenantId_Audience",
                table: "Dashboards",
                columns: new[] { "TenantId", "Audience" });

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_TenantId_OwnerUserId",
                table: "Dashboards",
                columns: new[] { "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_DataRequests_DownloadTokenHash",
                table: "DataRequests",
                column: "DownloadTokenHash",
                unique: true,
                filter: "[DownloadTokenHash] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DataRequests_TenantId_ContactId",
                table: "DataRequests",
                columns: new[] { "TenantId", "ContactId" });

            migrationBuilder.CreateIndex(
                name: "IX_DataRequests_TenantId_Status",
                table: "DataRequests",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Departments_BusinessCalendarId",
                table: "Departments",
                column: "BusinessCalendarId");

            migrationBuilder.CreateIndex(
                name: "IX_Departments_ParentDepartmentId",
                table: "Departments",
                column: "ParentDepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Departments_TenantId_Code",
                table: "Departments",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EngineeringItems_TenantId_IntegrationId_ExternalId",
                table: "EngineeringItems",
                columns: new[] { "TenantId", "IntegrationId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EngineeringItems_TenantId_IntegrationId_LastSyncedAt",
                table: "EngineeringItems",
                columns: new[] { "TenantId", "IntegrationId", "LastSyncedAt" },
                filter: "[IsOrphaned] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_EngineeringLinks_TenantId_Provider_ExternalId",
                table: "EngineeringLinks",
                columns: new[] { "TenantId", "Provider", "ExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_EngineeringLinks_TicketId",
                table: "EngineeringLinks",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_EngineeringWebhookDeliveries_IntegrationId_DeliveryId",
                table: "EngineeringWebhookDeliveries",
                columns: new[] { "IntegrationId", "DeliveryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EngineeringWebhookDeliveries_TenantId_IntegrationId_ReceivedAt",
                table: "EngineeringWebhookDeliveries",
                columns: new[] { "TenantId", "IntegrationId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Entitlements_ContractId",
                table: "Entitlements",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_Entitlements_TenantId_CustomerId_ProductId",
                table: "Entitlements",
                columns: new[] { "TenantId", "CustomerId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_FormLayouts_TenantId_Entity_Kind",
                table: "FormLayouts",
                columns: new[] { "TenantId", "Entity", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_InboundEvents_Provider_DedupKey",
                table: "InboundEvents",
                columns: new[] { "Provider", "DedupKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InboundEvents_Status_ReceivedAt",
                table: "InboundEvents",
                columns: new[] { "Status", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Installations_CustomerId",
                table: "Installations",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Installations_LocationId",
                table: "Installations",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_Installations_ProductId",
                table: "Installations",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_Installations_TenantId_CustomerId",
                table: "Installations",
                columns: new[] { "TenantId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_Installations_TenantId_InstallationCode",
                table: "Installations",
                columns: new[] { "TenantId", "InstallationCode" });

            migrationBuilder.CreateIndex(
                name: "IX_Installations_TenantId_ProductId_VersionId",
                table: "Installations",
                columns: new[] { "TenantId", "ProductId", "VersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_Installations_VersionId",
                table: "Installations",
                column: "VersionId");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationLogs_TenantId_CreatedAt",
                table: "IntegrationLogs",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Integrations_TenantId_Type",
                table: "Integrations",
                columns: new[] { "TenantId", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_IssueTypes_TenantId_Name",
                table: "IssueTypes",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KbCategories_ParentId",
                table: "KbCategories",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_KbCategories_TenantId_Slug",
                table: "KbCategories",
                columns: new[] { "TenantId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeChunks_TenantId_SourceType_SourceId",
                table: "KnowledgeChunks",
                columns: new[] { "TenantId", "SourceType", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeChunks_TenantId_Visibility",
                table: "KnowledgeChunks",
                columns: new[] { "TenantId", "Visibility" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeSearchLogs_TenantId_CreatedAt",
                table: "KnowledgeSearchLogs",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeSearchLogs_TenantId_NormalizedQuery",
                table: "KnowledgeSearchLogs",
                columns: new[] { "TenantId", "NormalizedQuery" });

            migrationBuilder.CreateIndex(
                name: "IX_LegalHolds_TenantId_ReleasedAt",
                table: "LegalHolds",
                columns: new[] { "TenantId", "ReleasedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Licences_InstallationId",
                table: "Licences",
                column: "InstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_LocalReleases_TenantId_IntegrationId_Name",
                table: "LocalReleases",
                columns: new[] { "TenantId", "IntegrationId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LocalWorkItems_ReleaseId",
                table: "LocalWorkItems",
                column: "ReleaseId");

            migrationBuilder.CreateIndex(
                name: "IX_LocalWorkItems_TenantId_IntegrationId_State_Rank",
                table: "LocalWorkItems",
                columns: new[] { "TenantId", "IntegrationId", "State", "Rank" });

            migrationBuilder.CreateIndex(
                name: "IX_LocalWorkItems_TenantId_Number",
                table: "LocalWorkItems",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LocalWorkItems_TenantId_UpdatedAt_Id",
                table: "LocalWorkItems",
                columns: new[] { "TenantId", "UpdatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_LookupValues_TenantId_Type_Value",
                table: "LookupValues",
                columns: new[] { "TenantId", "Type", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeliveries_MessageId",
                table: "MessageDeliveries",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeliveries_Status_NextAttemptAt",
                table: "MessageDeliveries",
                columns: new[] { "Status", "NextAttemptAt" },
                filter: "[Status] IN ('Queued','Sending','Failed')");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ChannelAccountId_Direction_ExternalId",
                table: "Messages",
                columns: new[] { "ChannelAccountId", "Direction", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ConversationId_OccurredAt",
                table: "Messages",
                columns: new[] { "ConversationId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_TenantId_InternetMessageId",
                table: "Messages",
                columns: new[] { "TenantId", "InternetMessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_TicketId",
                table: "Messages",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_NotificationId",
                table: "NotificationDeliveries",
                column: "NotificationId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_Status_ScheduledFor",
                table: "NotificationDeliveries",
                columns: new[] { "Status", "ScheduledFor" },
                filter: "[Status] IN ('Pending','Failed')");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationPreferences_UserId_EventType",
                table: "NotificationPreferences",
                columns: new[] { "UserId", "EventType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_ContactId_CreatedAt",
                table: "Notifications",
                columns: new[] { "ContactId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_TenantId_DedupKey",
                table: "Notifications",
                columns: new[] { "TenantId", "DedupKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_CreatedAt",
                table: "Notifications",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationSettings_UserId",
                table: "NotificationSettings",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationTemplates_TenantId_Key",
                table: "NotificationTemplates",
                columns: new[] { "TenantId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OtpChallenges_PrincipalId_Purpose",
                table: "OtpChallenges",
                columns: new[] { "PrincipalId", "Purpose" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxDeliveries_EventId_Handler",
                table: "OutboxDeliveries",
                columns: new[] { "EventId", "Handler" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEvents_Due",
                table: "OutboxEvents",
                columns: new[] { "Status", "NextAttemptAt" },
                filter: "[Status] IN ('Pending','Processing','Failed')");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEvents_TenantId_OccurredAt",
                table: "OutboxEvents",
                columns: new[] { "TenantId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Permissions_Code",
                table: "Permissions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Plans_Code",
                table: "Plans",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformOperators_Email",
                table: "PlatformOperators",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductFamilies_TenantId_Name",
                table: "ProductFamilies",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductModules_ProductId",
                table: "ProductModules",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductReleases_VersionId",
                table: "ProductReleases",
                column: "VersionId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_FamilyId",
                table: "Products",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_TenantId_Code",
                table: "Products",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductVersions_ProductId_Version",
                table: "ProductVersions",
                columns: new[] { "ProductId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushDevices_TenantId_ContactId",
                table: "PushDevices",
                columns: new[] { "TenantId", "ContactId" },
                filter: "[ContactId] IS NOT NULL AND [IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_PushDevices_TenantId_Token",
                table: "PushDevices",
                columns: new[] { "TenantId", "Token" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushDevices_TenantId_UserId",
                table: "PushDevices",
                columns: new[] { "TenantId", "UserId" },
                filter: "[UserId] IS NOT NULL AND [IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_ReplyTemplates_TenantId_Name",
                table: "ReplyTemplates",
                columns: new[] { "TenantId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportExports_TenantId_RequestedById_CreatedAt",
                table: "ReportExports",
                columns: new[] { "TenantId", "RequestedById", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RetentionPolicies_TenantId",
                table: "RetentionPolicies",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_RolePermissions_RoleId_PermissionCode",
                table: "RolePermissions",
                columns: new[] { "RoleId", "PermissionCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_TenantId_Name",
                table: "Roles",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RuleExecutions_RuleVersionId_EventId_EntityId",
                table: "RuleExecutions",
                columns: new[] { "RuleVersionId", "EventId", "EntityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RuleExecutions_TenantId_EntityId",
                table: "RuleExecutions",
                columns: new[] { "TenantId", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_RuleExecutions_TenantId_RuleId_StartedAt",
                table: "RuleExecutions",
                columns: new[] { "TenantId", "RuleId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RuleVersions_RuleId_VersionNumber",
                table: "RuleVersions",
                columns: new[] { "RuleId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SatisfactionResponses_TicketId",
                table: "SatisfactionResponses",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_SatisfactionResponses_TokenHash",
                table: "SatisfactionResponses",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SavedViews_TenantId_Scope_OwnerUserId",
                table: "SavedViews",
                columns: new[] { "TenantId", "Scope", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledReportRuns_ScheduledReportId_ScheduledFor",
                table: "ScheduledReportRuns",
                columns: new[] { "ScheduledReportId", "ScheduledFor" },
                unique: true,
                filter: "[Manual] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledReportRuns_TenantId_ScheduledReportId_CreatedAt",
                table: "ScheduledReportRuns",
                columns: new[] { "TenantId", "ScheduledReportId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledReports_IsActive_NextRunAt",
                table: "ScheduledReports",
                columns: new[] { "IsActive", "NextRunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledReports_TenantId_OwnerUserId",
                table: "ScheduledReports",
                columns: new[] { "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTasks_Name",
                table: "ScheduledTasks",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SearchDocuments_TenantId_EntityType_EntityId",
                table: "SearchDocuments",
                columns: new[] { "TenantId", "EntityType", "EntityId" },
                unique: true)
                .Annotation("SqlServer:Include", new[] { "Visibility", "CustomerId", "TermCount" });

            migrationBuilder.CreateIndex(
                name: "IX_SearchPostings_DocumentId",
                table: "SearchPostings",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_SearchPostings_TenantId_Term",
                table: "SearchPostings",
                columns: new[] { "TenantId", "Term" })
                .Annotation("SqlServer:Include", new[] { "DocumentId", "Weight" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceActivities_CustomerId",
                table: "ServiceActivities",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceActivities_TenantId_AssignedUserId_ScheduledStart",
                table: "ServiceActivities",
                columns: new[] { "TenantId", "AssignedUserId", "ScheduledStart" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceActivities_TenantId_CustomerId_ScheduledStart",
                table: "ServiceActivities",
                columns: new[] { "TenantId", "CustomerId", "ScheduledStart" });

            migrationBuilder.CreateIndex(
                name: "IX_Shifts_TenantId",
                table: "Shifts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Skills_TenantId_Name",
                table: "Skills",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlaEscalationLevels_PolicyId_Milestone_Level",
                table: "SlaEscalationLevels",
                columns: new[] { "PolicyId", "Milestone", "Level" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlaEvents_TenantId_Type_OccurredAt",
                table: "SlaEvents",
                columns: new[] { "TenantId", "Type", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SlaEvents_TicketId_OccurredAt",
                table: "SlaEvents",
                columns: new[] { "TicketId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SlaInstances_PolicyId",
                table: "SlaInstances",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_SlaInstances_TicketId_Status",
                table: "SlaInstances",
                columns: new[] { "TicketId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SlaMilestones_ActiveDue",
                table: "SlaMilestones",
                columns: new[] { "Status", "DueAt" },
                filter: "[Status] = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_SlaMilestones_Breached",
                table: "SlaMilestones",
                columns: new[] { "Status", "BreachedAt" },
                filter: "[Status] = 'Breached'");

            migrationBuilder.CreateIndex(
                name: "IX_SlaMilestones_InstanceId",
                table: "SlaMilestones",
                column: "InstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_SlaMilestones_PendingWarning",
                table: "SlaMilestones",
                columns: new[] { "Status", "WarningAt" },
                filter: "[Status] = 'Active' AND [WarningRaised] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_SlaMilestones_TicketId",
                table: "SlaMilestones",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_SlaPolicies_CalendarId",
                table: "SlaPolicies",
                column: "CalendarId");

            migrationBuilder.CreateIndex(
                name: "IX_SlaPolicies_TenantId_EvaluationOrder",
                table: "SlaPolicies",
                columns: new[] { "TenantId", "EvaluationOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_SlaTargets_PolicyId_Priority",
                table: "SlaTargets",
                columns: new[] { "PolicyId", "Priority" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StoredFiles_StorageKey",
                table: "StoredFiles",
                column: "StorageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StoredFiles_TenantId_Sha256",
                table: "StoredFiles",
                columns: new[] { "TenantId", "Sha256" });

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_PlanId",
                table: "Subscriptions",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_TenantId",
                table: "Subscriptions",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupportAccessGrant_OperatorId_Status",
                table: "SupportAccessGrant",
                columns: new[] { "OperatorId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SupportAccessGrant_TenantId_Status_CreatedAt",
                table: "SupportAccessGrant",
                columns: new[] { "TenantId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Tags_TenantId_Name",
                table: "Tags",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TeamMembers_TeamId_UserId",
                table: "TeamMembers",
                columns: new[] { "TeamId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TeamMembers_UserId",
                table: "TeamMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Teams_DepartmentId",
                table: "Teams",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Teams_FallbackTeamId",
                table: "Teams",
                column: "FallbackTeamId");

            migrationBuilder.CreateIndex(
                name: "IX_Teams_TenantId_DepartmentId",
                table: "Teams",
                columns: new[] { "TenantId", "DepartmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantCounters_TenantId_Name",
                table: "TenantCounters",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Slug",
                table: "Tenants",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantSettings_TenantId_Key",
                table: "TenantSettings",
                columns: new[] { "TenantId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TicketAttachments_FileId",
                table: "TicketAttachments",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketAttachments_TicketId",
                table: "TicketAttachments",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketCategories_ParentId",
                table: "TicketCategories",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketCategories_TenantId_ParentId_Name",
                table: "TicketCategories",
                columns: new[] { "TenantId", "ParentId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketDrafts_TicketId_UserId_Kind",
                table: "TicketDrafts",
                columns: new[] { "TicketId", "UserId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TicketEvents_TicketId_OccurredAt",
                table: "TicketEvents",
                columns: new[] { "TicketId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketExternalReferences_TenantId_System_RecordType_RecordId",
                table: "TicketExternalReferences",
                columns: new[] { "TenantId", "System", "RecordType", "RecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketExternalReferences_TicketId",
                table: "TicketExternalReferences",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketFollowUps_Status_RemindAt",
                table: "TicketFollowUps",
                columns: new[] { "Status", "RemindAt" },
                filter: "[Status] = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_TicketFollowUps_TicketId",
                table: "TicketFollowUps",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketLinks_SourceTicketId_TargetTicketId_LinkType",
                table: "TicketLinks",
                columns: new[] { "SourceTicketId", "TargetTicketId", "LinkType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TicketLinks_TargetTicketId",
                table: "TicketLinks",
                column: "TargetTicketId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketMetrics_Open",
                table: "TicketMetrics",
                columns: new[] { "TenantId", "StatusCategory" },
                filter: "[StatusCategory] < 5 AND [Excluded] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_TicketMetrics_Tenant_CreatedAt",
                table: "TicketMetrics",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketMetrics_Tenant_Department_CreatedAt",
                table: "TicketMetrics",
                columns: new[] { "TenantId", "DepartmentId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketMetrics_Tenant_ResolvedAt",
                table: "TicketMetrics",
                columns: new[] { "TenantId", "ResolvedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketMetrics_TenantId_AssigneeId_CreatedDay",
                table: "TicketMetrics",
                columns: new[] { "TenantId", "AssigneeId", "CreatedDay" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketMetrics_TenantId_CreatedDay",
                table: "TicketMetrics",
                columns: new[] { "TenantId", "CreatedDay" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketMetrics_TenantId_CustomerId_CreatedDay",
                table: "TicketMetrics",
                columns: new[] { "TenantId", "CustomerId", "CreatedDay" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketMetrics_TenantId_ProductId_CreatedDay",
                table: "TicketMetrics",
                columns: new[] { "TenantId", "ProductId", "CreatedDay" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketMetrics_TenantId_ResolvedDay",
                table: "TicketMetrics",
                columns: new[] { "TenantId", "ResolvedDay" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketMetrics_TicketId",
                table: "TicketMetrics",
                column: "TicketId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_AssetId",
                table: "Tickets",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_AssigneeId",
                table: "Tickets",
                column: "AssigneeId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_BrandId",
                table: "Tickets",
                column: "BrandId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_CategoryId",
                table: "Tickets",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_ContactId",
                table: "Tickets",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_ContractId",
                table: "Tickets",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_CustomerId",
                table: "Tickets",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_DepartmentId",
                table: "Tickets",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_InstallationId",
                table: "Tickets",
                column: "InstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_IssueTypeId",
                table: "Tickets",
                column: "IssueTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_Keyset",
                table: "Tickets",
                columns: new[] { "TenantId", "LastActivityAt", "Id" })
                .Annotation("SqlServer:Include", new[] { "DepartmentId", "TeamId", "AssigneeId", "StatusCategory", "IsDeleted", "Priority", "DueAt", "SlaState", "IsEscalated" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_LocationId",
                table: "Tickets",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_MergedIntoTicketId",
                table: "Tickets",
                column: "MergedIntoTicketId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_ModuleId",
                table: "Tickets",
                column: "ModuleId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_OpenByAssignee",
                table: "Tickets",
                columns: new[] { "TenantId", "AssigneeId", "StatusCategory" },
                filter: "[StatusCategory] < 5 AND [IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_OpenByDepartment",
                table: "Tickets",
                columns: new[] { "TenantId", "DepartmentId", "StatusCategory", "Priority" },
                filter: "[StatusCategory] < 5 AND [IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_OpenDue",
                table: "Tickets",
                columns: new[] { "TenantId", "DueAt" },
                filter: "[StatusCategory] < 5 AND [IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_ParentTicketId",
                table: "Tickets",
                column: "ParentTicketId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_PriorityKeyset",
                table: "Tickets",
                columns: new[] { "TenantId", "Priority", "LastActivityAt" })
                .Annotation("SqlServer:Include", new[] { "DepartmentId", "TeamId", "AssigneeId", "StatusCategory", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_ProductId",
                table: "Tickets",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_Resolved",
                table: "Tickets",
                columns: new[] { "TenantId", "ResolvedAt" },
                filter: "[ResolvedAt] IS NOT NULL")
                .Annotation("SqlServer:Include", new[] { "CreatedAt", "CategoryId", "ProductId", "Priority", "IsSpam", "MergedIntoTicketId", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_SlaPolicyId",
                table: "Tickets",
                column: "SlaPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_StatusId",
                table: "Tickets",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_SubcategoryId",
                table: "Tickets",
                column: "SubcategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TeamId",
                table: "Tickets",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_ContactId_CreatedAt",
                table: "Tickets",
                columns: new[] { "TenantId", "ContactId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_CreatedAt",
                table: "Tickets",
                columns: new[] { "TenantId", "CreatedAt" })
                .Annotation("SqlServer:Include", new[] { "DepartmentId", "TeamId", "AssigneeId", "StatusCategory", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_CustomerId_CreatedAt",
                table: "Tickets",
                columns: new[] { "TenantId", "CustomerId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_Number",
                table: "Tickets",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_ProductId_CreatedAt",
                table: "Tickets",
                columns: new[] { "TenantId", "ProductId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_VersionId",
                table: "Tickets",
                column: "VersionId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketStatuses_TenantId_Name",
                table: "TicketStatuses",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TicketTags_TagId",
                table: "TicketTags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketTags_TicketId_TagId",
                table: "TicketTags",
                columns: new[] { "TicketId", "TagId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TicketTasks_ParentTaskId",
                table: "TicketTasks",
                column: "ParentTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketTasks_TenantId_AssigneeId_Status",
                table: "TicketTasks",
                columns: new[] { "TenantId", "AssigneeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketTasks_TicketId",
                table: "TicketTasks",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketThreads_TenantId_AuthorUserId_CreatedAt",
                table: "TicketThreads",
                columns: new[] { "TenantId", "AuthorUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketThreads_TenantId_ClientRequestId",
                table: "TicketThreads",
                columns: new[] { "TenantId", "ClientRequestId" },
                unique: true,
                filter: "[ClientRequestId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TicketThreads_TicketId_Seq",
                table: "TicketThreads",
                columns: new[] { "TicketId", "Seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TicketWatchers_TicketId_UserId",
                table: "TicketWatchers",
                columns: new[] { "TicketId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TicketWatchers_UserId",
                table: "TicketWatchers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntries_TenantId_UserId_StartedAt",
                table: "TimeEntries",
                columns: new[] { "TenantId", "UserId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntries_TicketId",
                table: "TimeEntries",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntries_UserId",
                table: "TimeEntries",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_TrialSignup_AdminEmailNormalized_CreatedAt",
                table: "TrialSignup",
                columns: new[] { "AdminEmailNormalized", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TrialSignup_IpAddress_CreatedAt",
                table: "TrialSignup",
                columns: new[] { "IpAddress", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TrialSignup_Slug_Status",
                table: "TrialSignup",
                columns: new[] { "Slug", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TroubleshootingFlows_TenantId",
                table: "TroubleshootingFlows",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_UsageRecords_TenantId_Metric_PeriodStart",
                table: "UsageRecords",
                columns: new[] { "TenantId", "Metric", "PeriodStart" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UsageSnapshot_Day_Metric",
                table: "UsageSnapshot",
                columns: new[] { "Day", "Metric" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageSnapshot_TenantId_Metric_Day",
                table: "UsageSnapshot",
                columns: new[] { "TenantId", "Metric", "Day" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_RoleId",
                table: "UserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_UserId_RoleId",
                table: "UserRoles",
                columns: new[] { "UserId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_ExternalIssuer_ExternalSubject",
                table: "Users",
                columns: new[] { "ExternalIssuer", "ExternalSubject" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_TenantId_EmailNormalized",
                table: "Users",
                columns: new[] { "TenantId", "EmailNormalized" },
                unique: true,
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_FamilyId",
                table: "UserSessions",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_PrincipalKind_PrincipalId",
                table: "UserSessions",
                columns: new[] { "PrincipalKind", "PrincipalId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_TenantId_PrincipalKind_LastSeenAt",
                table: "UserSessions",
                columns: new[] { "TenantId", "PrincipalKind", "LastSeenAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_TokenHash",
                table: "UserSessions",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_Status_NextAttemptAt",
                table: "WebhookDeliveries",
                columns: new[] { "Status", "NextAttemptAt" },
                filter: "[Status] IN ('Pending','Failed')");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_WebhookId_EventId",
                table: "WebhookDeliveries",
                columns: new[] { "WebhookId", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Webhooks_TenantId",
                table: "Webhooks",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Workflows_TenantId_DepartmentId",
                table: "Workflows",
                columns: new[] { "TenantId", "DepartmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowVersions_WorkflowId_VersionNumber",
                table: "WorkflowVersions",
                columns: new[] { "WorkflowId", "VersionNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentDelegations");

            migrationBuilder.DropTable(
                name: "AgentDepartments");

            migrationBuilder.DropTable(
                name: "AgentInvitations");

            migrationBuilder.DropTable(
                name: "AgentLeaves");

            migrationBuilder.DropTable(
                name: "AgentMetrics");

            migrationBuilder.DropTable(
                name: "AgentRoutingAttributes");

            migrationBuilder.DropTable(
                name: "Agents");

            migrationBuilder.DropTable(
                name: "AgentShiftAssignments");

            migrationBuilder.DropTable(
                name: "AgentSkills");

            migrationBuilder.DropTable(
                name: "AiActionProposals");

            migrationBuilder.DropTable(
                name: "AiAgents");

            migrationBuilder.DropTable(
                name: "AiConfigurations");

            migrationBuilder.DropTable(
                name: "AiExecutions");

            migrationBuilder.DropTable(
                name: "AiPromptTemplates");

            migrationBuilder.DropTable(
                name: "AiUsage");

            migrationBuilder.DropTable(
                name: "AnomalyAlerts");

            migrationBuilder.DropTable(
                name: "ApiClients");

            migrationBuilder.DropTable(
                name: "ApiKeys");

            migrationBuilder.DropTable(
                name: "ApprovalRequests");

            migrationBuilder.DropTable(
                name: "ArticleAccessRules");

            migrationBuilder.DropTable(
                name: "ArticleAttachments");

            migrationBuilder.DropTable(
                name: "ArticleFeedback");

            migrationBuilder.DropTable(
                name: "ArticleTranslations");

            migrationBuilder.DropTable(
                name: "ArticleVersions");

            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "BackgroundJobs");

            migrationBuilder.DropTable(
                name: "BacklogSnapshots");

            migrationBuilder.DropTable(
                name: "BillingInvoices");

            migrationBuilder.DropTable(
                name: "CalendarHolidays");

            migrationBuilder.DropTable(
                name: "CallRecords");

            migrationBuilder.DropTable(
                name: "CollaborationChannels");

            migrationBuilder.DropTable(
                name: "CommunityPosts");

            migrationBuilder.DropTable(
                name: "CommunityVotes");

            migrationBuilder.DropTable(
                name: "ConfigurationDraft");

            migrationBuilder.DropTable(
                name: "ConfigurationSnapshots");

            migrationBuilder.DropTable(
                name: "ContactConsents");

            migrationBuilder.DropTable(
                name: "ContactIdentities");

            migrationBuilder.DropTable(
                name: "ContactMergeLogs");

            migrationBuilder.DropTable(
                name: "Conversations");

            migrationBuilder.DropTable(
                name: "CustomerMergeLog");

            migrationBuilder.DropTable(
                name: "CustomFields");

            migrationBuilder.DropTable(
                name: "Dashboards");

            migrationBuilder.DropTable(
                name: "DataRequests");

            migrationBuilder.DropTable(
                name: "EngineeringItems");

            migrationBuilder.DropTable(
                name: "EngineeringLinks");

            migrationBuilder.DropTable(
                name: "EngineeringWebhookDeliveries");

            migrationBuilder.DropTable(
                name: "Entitlements");

            migrationBuilder.DropTable(
                name: "FormLayouts");

            migrationBuilder.DropTable(
                name: "InboundEvents");

            migrationBuilder.DropTable(
                name: "IntegrationLogs");

            migrationBuilder.DropTable(
                name: "Integrations");

            migrationBuilder.DropTable(
                name: "KnowledgeChunks");

            migrationBuilder.DropTable(
                name: "KnowledgeSearchLogs");

            migrationBuilder.DropTable(
                name: "LegalHolds");

            migrationBuilder.DropTable(
                name: "Licences");

            migrationBuilder.DropTable(
                name: "LocalReleases");

            migrationBuilder.DropTable(
                name: "LocalWorkItems");

            migrationBuilder.DropTable(
                name: "LookupValues");

            migrationBuilder.DropTable(
                name: "MessageDeliveries");

            migrationBuilder.DropTable(
                name: "NotificationDeliveries");

            migrationBuilder.DropTable(
                name: "NotificationPreferences");

            migrationBuilder.DropTable(
                name: "NotificationSettings");

            migrationBuilder.DropTable(
                name: "NotificationTemplates");

            migrationBuilder.DropTable(
                name: "OtpChallenges");

            migrationBuilder.DropTable(
                name: "OutboxDeliveries");

            migrationBuilder.DropTable(
                name: "OutboxEvents");

            migrationBuilder.DropTable(
                name: "Permissions");

            migrationBuilder.DropTable(
                name: "PlatformOperators");

            migrationBuilder.DropTable(
                name: "ProductReleases");

            migrationBuilder.DropTable(
                name: "PushDevices");

            migrationBuilder.DropTable(
                name: "ReplyTemplates");

            migrationBuilder.DropTable(
                name: "ReportExports");

            migrationBuilder.DropTable(
                name: "RetentionPolicies");

            migrationBuilder.DropTable(
                name: "RolePermissions");

            migrationBuilder.DropTable(
                name: "RuleExecutions");

            migrationBuilder.DropTable(
                name: "RuleVersions");

            migrationBuilder.DropTable(
                name: "SatisfactionResponses");

            migrationBuilder.DropTable(
                name: "SavedViews");

            migrationBuilder.DropTable(
                name: "ScheduledReportRuns");

            migrationBuilder.DropTable(
                name: "ScheduledTasks");

            migrationBuilder.DropTable(
                name: "SearchPostings");

            migrationBuilder.DropTable(
                name: "ServiceActivities");

            migrationBuilder.DropTable(
                name: "SlaEscalationLevels");

            migrationBuilder.DropTable(
                name: "SlaEvents");

            migrationBuilder.DropTable(
                name: "SlaMilestones");

            migrationBuilder.DropTable(
                name: "SlaTargets");

            migrationBuilder.DropTable(
                name: "SupportAccessGrant");

            migrationBuilder.DropTable(
                name: "TeamMembers");

            migrationBuilder.DropTable(
                name: "TenantCounters");

            migrationBuilder.DropTable(
                name: "TenantSettings");

            migrationBuilder.DropTable(
                name: "TicketAttachments");

            migrationBuilder.DropTable(
                name: "TicketDrafts");

            migrationBuilder.DropTable(
                name: "TicketEvents");

            migrationBuilder.DropTable(
                name: "TicketExternalReferences");

            migrationBuilder.DropTable(
                name: "TicketFollowUps");

            migrationBuilder.DropTable(
                name: "TicketLinks");

            migrationBuilder.DropTable(
                name: "TicketMetrics");

            migrationBuilder.DropTable(
                name: "TicketTags");

            migrationBuilder.DropTable(
                name: "TicketTasks");

            migrationBuilder.DropTable(
                name: "TicketThreads");

            migrationBuilder.DropTable(
                name: "TicketWatchers");

            migrationBuilder.DropTable(
                name: "TimeEntries");

            migrationBuilder.DropTable(
                name: "TrialSignup");

            migrationBuilder.DropTable(
                name: "TroubleshootingFlows");

            migrationBuilder.DropTable(
                name: "UsageRecords");

            migrationBuilder.DropTable(
                name: "UsageSnapshot");

            migrationBuilder.DropTable(
                name: "UserRoles");

            migrationBuilder.DropTable(
                name: "UserSessions");

            migrationBuilder.DropTable(
                name: "WebhookDeliveries");

            migrationBuilder.DropTable(
                name: "WorkflowVersions");

            migrationBuilder.DropTable(
                name: "Shifts");

            migrationBuilder.DropTable(
                name: "Skills");

            migrationBuilder.DropTable(
                name: "Articles");

            migrationBuilder.DropTable(
                name: "Subscriptions");

            migrationBuilder.DropTable(
                name: "CommunityTopics");

            migrationBuilder.DropTable(
                name: "Messages");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "AutomationRules");

            migrationBuilder.DropTable(
                name: "ScheduledReports");

            migrationBuilder.DropTable(
                name: "SearchDocuments");

            migrationBuilder.DropTable(
                name: "SlaInstances");

            migrationBuilder.DropTable(
                name: "StoredFiles");

            migrationBuilder.DropTable(
                name: "Tags");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropTable(
                name: "Webhooks");

            migrationBuilder.DropTable(
                name: "Workflows");

            migrationBuilder.DropTable(
                name: "KbCategories");

            migrationBuilder.DropTable(
                name: "Plans");

            migrationBuilder.DropTable(
                name: "ChannelAccounts");

            migrationBuilder.DropTable(
                name: "Tickets");

            migrationBuilder.DropTable(
                name: "Brands");

            migrationBuilder.DropTable(
                name: "Contacts");

            migrationBuilder.DropTable(
                name: "Contracts");

            migrationBuilder.DropTable(
                name: "CustomerAssets");

            migrationBuilder.DropTable(
                name: "Installations");

            migrationBuilder.DropTable(
                name: "IssueTypes");

            migrationBuilder.DropTable(
                name: "ProductModules");

            migrationBuilder.DropTable(
                name: "Teams");

            migrationBuilder.DropTable(
                name: "TicketCategories");

            migrationBuilder.DropTable(
                name: "TicketStatuses");

            migrationBuilder.DropTable(
                name: "Tenants");

            migrationBuilder.DropTable(
                name: "CustomerDepartments");

            migrationBuilder.DropTable(
                name: "SlaPolicies");

            migrationBuilder.DropTable(
                name: "CustomerLocations");

            migrationBuilder.DropTable(
                name: "ProductVersions");

            migrationBuilder.DropTable(
                name: "Departments");

            migrationBuilder.DropTable(
                name: "Customers");

            migrationBuilder.DropTable(
                name: "Products");

            migrationBuilder.DropTable(
                name: "BusinessCalendars");

            migrationBuilder.DropTable(
                name: "CustomerGroups");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "ProductFamilies");
        }
    }
}
