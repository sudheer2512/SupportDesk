namespace SupportDesk.Domain.Ai;

public static class AiFeatures
{
    public const string Classification = "classification";
    public const string Summarization = "summarization";
    public const string ReplySuggestion = "reply_suggestion";
    public const string SemanticSearch = "semantic_search";
    public const string Sentiment = "sentiment";
    public const string DuplicateDetection = "duplicate_detection";
    public const string RootCause = "root_cause";
    public const string KnowledgeGeneration = "knowledge_generation";
    public const string Translation = "translation";
    public const string ResolutionPrediction = "resolution_prediction";
    public const string AnomalyDetection = "anomaly_detection";
    public const string NlAnalytics = "nl_analytics";
    public const string Agents = "agents";
    public const string PortalAssistant = "portal_assistant";

    public static readonly string[] All =
    [
        Classification, Summarization, ReplySuggestion, SemanticSearch, Sentiment, DuplicateDetection, RootCause,
        KnowledgeGeneration, Translation, ResolutionPrediction, AnomalyDetection, NlAnalytics, Agents, PortalAssistant
    ];
}

public enum BudgetAction { Warn, Block }

/// <summary>
/// Tenant AI configuration. Provider: Local (built-in, offline), OpenAI, AzureOpenAI, Anthropic, OpenAICompatible.
/// FeaturesJson: {"classification":{"enabled":true,"roles":["Agent"],"autoApply":false,"minConfidence":0.8},…}
/// AutoReplyScenariosJson: approved low-risk scenarios for fully automated replies.
/// </summary>
public class AiConfiguration : AuditedEntity, IConcurrencyVersioned
{
    public bool IsEnabled { get; set; } = true;
    public string Provider { get; set; } = "Local";
    public string Model { get; set; } = "local-heuristic-v1";
    public string? Endpoint { get; set; }
    public string? ApiKeyProtected { get; set; }
    public string EmbeddingProvider { get; set; } = "Local";
    public string EmbeddingModel { get; set; } = "local-hash-384";
    public decimal MonthlyBudget { get; set; } = 100m;
    public string Currency { get; set; } = "USD";
    public BudgetAction BudgetAction { get; set; } = BudgetAction.Block;
    /// <summary>Price per 1K tokens (input, output) for cost monitoring.</summary>
    public decimal InputCostPer1K { get; set; }
    public decimal OutputCostPer1K { get; set; }
    public string FeaturesJson { get; set; } = "{}";
    public string AutoReplyScenariosJson { get; set; } = "[]";
    public bool RedactPii { get; set; } = true;
    public int ExecutionRetentionDays { get; set; } = 180;
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Versioned prompt template per feature (tenant override of the built-in default).</summary>
public class AiPromptTemplate : AuditedEntity
{
    public string Feature { get; set; } = "";
    public int Version { get; set; } = 1;
    public string SystemPrompt { get; set; } = "";
    public string UserTemplate { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public enum AiExecutionStatus { Succeeded, Failed, Blocked, BudgetExceeded, Disabled }
public enum AiOutcome { Pending, Accepted, Rejected, Applied, Reverted, Expired }

/// <summary>Audit record of every model call: inputs (by reference/hash), output, citations, cost, injection flags, human outcome.</summary>
public class AiExecution : TenantEntity
{
    public string Feature { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string? EntityType { get; set; }
    public Guid? EntityId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? AgentId { get; set; }
    public string InputHash { get; set; } = "";
    public int PromptVersion { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public decimal Cost { get; set; }
    public int LatencyMs { get; set; }
    public AiExecutionStatus Status { get; set; }
    public string? OutputJson { get; set; }
    public string? CitationsJson { get; set; }
    public double? Confidence { get; set; }
    /// <summary>Prompt-injection heuristics that fired on the untrusted input.</summary>
    public string? InjectionFlagsJson { get; set; }
    public string? Error { get; set; }
    public AiOutcome Outcome { get; set; } = AiOutcome.Pending;
    public Guid? ReviewedById { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Monthly AI usage roll-up per feature/model (cost monitoring and budget enforcement).</summary>
public class AiUsage : TenantEntity
{
    public DateTime PeriodStart { get; set; }
    public string Feature { get; set; } = "";
    public string Model { get; set; } = "";
    public long Requests { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal Cost { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum AiAgentKind { Triage, InfoCollector, KnowledgeResolver, AutoResponder }
public enum AiAgentMode { SuggestOnly, AutoLowRisk }

/// <summary>
/// Configurable autonomous AI agent. ConfigJson: {"departments":[…],"channels":[…],"minConfidence":0.85,
/// "allowedActions":["set_category","set_priority","add_tag","request_info","suggest_article","auto_reply"],"maxRepliesPerTicket":1}
/// </summary>
public class AiAgent : AuditedEntity
{
    public string Name { get; set; } = "";
    public AiAgentKind Kind { get; set; }
    public bool IsEnabled { get; set; }
    public AiAgentMode Mode { get; set; } = AiAgentMode.SuggestOnly;
    public string ConfigJson { get; set; } = "{}";
    public string? Instructions { get; set; }
    public long RunCount { get; set; }
}

public enum AiRiskLevel { Low, Medium, High }
public enum ProposalStatus { Proposed, Approved, Rejected, Applied, Reverted, Expired, Failed }

/// <summary>
/// An action proposed by an AI agent or feature. Consequential actions always require human approval;
/// BeforeStateJson allows reverting an applied action.
/// </summary>
public class AiActionProposal : TenantEntity
{
    public Guid? AiExecutionId { get; set; }
    public Guid? AgentId { get; set; }
    public Guid TicketId { get; set; }
    public string ActionType { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public string? Rationale { get; set; }
    public double Confidence { get; set; }
    public AiRiskLevel Risk { get; set; }
    public bool RequiresApproval { get; set; } = true;
    public ProposalStatus Status { get; set; } = ProposalStatus.Proposed;
    public string? BeforeStateJson { get; set; }
    public Guid? DecidedById { get; set; }
    public DateTime? DecidedAt { get; set; }
    public DateTime? AppliedAt { get; set; }
    public DateTime? RevertedAt { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
}

public enum AnomalyStatus { Open, Acknowledged, Dismissed }

/// <summary>A detected spike in ticket volume for a product/version/customer/category.</summary>
public class AnomalyAlert : TenantEntity
{
    public string Dimension { get; set; } = "";
    public Guid? DimensionId { get; set; }
    public string DimensionLabel { get; set; } = "";
    public DateOnly Day { get; set; }
    public int Count { get; set; }
    public double Baseline { get; set; }
    public double StdDev { get; set; }
    public double Score { get; set; }
    public AnomalyStatus Status { get; set; } = AnomalyStatus.Open;
    public Guid? AcknowledgedById { get; set; }
    public DateTime CreatedAt { get; set; }
}
