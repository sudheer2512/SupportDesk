using System.Text.Json;

namespace SupportDesk.Contracts.Ai;

// AI module wire contracts. Enum-valued fields are strings:
//   risk: Low, Medium, High            proposal status: Proposed, Approved, Rejected, Applied, Reverted, Expired, Failed
//   execution status: Succeeded, Failed, Blocked, BudgetExceeded, Disabled
//   outcome: Pending, Accepted, Rejected, Applied, Reverted, Expired
//   provider: Local, OpenAI, AzureOpenAI, OpenAICompatible, Anthropic

public sealed record CitationDto(int Index, string SourceType, Guid SourceId, string Title, string? Url, string? Snippet);

// ───────────── Classification ─────────────

public sealed record ClassificationFieldDto(string Field, string? Value, string? Label, double Confidence, string? CurrentValue, string? CurrentLabel, bool Differs);

public sealed record ClassificationSuggestionDto(
    Guid ExecutionId,
    Guid TicketId,
    IReadOnlyList<ClassificationFieldDto> Fields,
    double Confidence,
    string? Explanation,
    bool AutoApplied,
    string Provider,
    string Model);

/// <summary>Applies a stored classification suggestion. Fields: category, product, priority, department (default: all that differ).</summary>
public sealed record ApplyClassificationRequest(Guid ExecutionId, IReadOnlyList<string>? Fields = null);

public sealed record ClassificationApplyResultDto(Guid TicketId, IReadOnlyList<string> AppliedFields, IReadOnlyList<string> SkippedFields, string Message);

// ───────────── Summaries, replies, sentiment ─────────────

public sealed record TicketSummaryDto(Guid ExecutionId, Guid TicketId, string Summary, string CurrentStatus, IReadOnlyList<string> PendingActions,
    DateTime GeneratedAt, string Provider);

public sealed record SuggestReplyRequest(string? Instructions = null, string? Tone = null);

public sealed record SuggestedReplyDto(Guid ExecutionId, Guid TicketId, string BodyHtml, string BodyText, IReadOnlyList<CitationDto> Citations,
    double Confidence, bool Grounded, string Provider);

public sealed record MessageSentimentDto(Guid ThreadId, int Seq, string Sentiment, DateTime CreatedAt);

public sealed record SentimentDto(Guid TicketId, string Sentiment, double? Score, bool Frustrated, IReadOnlyList<MessageSentimentDto> Messages);

// ───────────── Search, duplicates, root cause, prediction ─────────────

public sealed record SemanticSearchRequest(string Query, string? Scope = "all", int? Take = 20);

public sealed record SemanticSearchHitDto(string SourceType, Guid SourceId, string Title, string? Snippet, string? Url, double Score,
    long? TicketNumber, string? Status, string Visibility);

public sealed record SemanticSearchResultDto(string Query, IReadOnlyList<SemanticSearchHitDto> Items, string EmbeddingModel);

public sealed record SimilarTicketDto(Guid TicketId, long Number, string DisplayNumber, string Subject, string Status, string StatusCategory,
    double Similarity, string? ResolutionSnippet, string? RootCause, DateTime CreatedAt, DateTime? ResolvedAt);

public sealed record DuplicateCandidateDto(Guid TicketId, long Number, string DisplayNumber, string Subject, string Status, DateTime CreatedAt,
    double Similarity, bool SameContact, bool SameCustomer, string Reason, bool CanMerge);

public sealed record DuplicateCheckDto(Guid? ExecutionId, Guid TicketId, IReadOnlyList<DuplicateCandidateDto> Candidates);

public sealed record MergeDuplicateRequest(Guid TargetTicketId, string? Note = null);

public sealed record RootCauseEvidenceDto(Guid TicketId, long Number, string Subject, string? ResolutionSnippet, double Similarity);

public sealed record RootCauseCandidateDto(string Cause, string? Code, double Likelihood, string? Explanation, IReadOnlyList<RootCauseEvidenceDto> Evidence);

public sealed record RootCauseDto(Guid ExecutionId, Guid TicketId, IReadOnlyList<RootCauseCandidateDto> Causes, int SampleSize, string Provider);

public sealed record ResolutionPredictionDto(Guid? ExecutionId, Guid TicketId, double? MedianHours, double? P25Hours, double? P75Hours, int SampleSize,
    string Basis, bool LowConfidence, double Confidence, DateTime? PredictedResolutionAt, string Explanation);

// ───────────── Knowledge generation & translation ─────────────

public sealed record KnowledgeDraftDto(Guid ExecutionId, Guid TicketId, Guid? ArticleId, bool Created, string Title, string? Summary, string BodyHtml,
    string Message);

public sealed record TranslateRequest(string Text, string TargetLocale, string? SourceLocale = null);

public sealed record TranslationDto(Guid? ExecutionId, bool Supported, string? Text, string TargetLocale, string Provider, string? Message);

// ───────────── Natural-language analytics ─────────────

public sealed record AskAnalyticsRequest(string Question);

public sealed record AnalyticsFilterDto(string Field, string Value, string? Label);

public sealed record AnalyticsIntentDto(string Metric, string Dimension, IReadOnlyList<AnalyticsFilterDto> Filters, string Period, string Sort, int Limit);

public sealed record AnalyticsColumnDto(string Key, string Label, string Type);

public sealed record AnalyticsChartDto(string Type, string X, string Y);

public sealed record AnalyticsAnswerDto(
    Guid ExecutionId,
    string Question,
    AnalyticsIntentDto Intent,
    string Interpretation,
    IReadOnlyList<AnalyticsColumnDto> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    AnalyticsChartDto Chart,
    DateTime PeriodFrom,
    DateTime PeriodTo,
    string Parser);

// ───────────── Proposals & agents ─────────────

public sealed record AiProposalDto(
    Guid Id,
    Guid TicketId,
    long? TicketNumber,
    string? TicketSubject,
    string ActionType,
    string ActionLabel,
    JsonElement Payload,
    string? Rationale,
    double Confidence,
    string Risk,
    bool RequiresApproval,
    bool Reversible,
    string Status,
    Guid? AgentId,
    string? AgentName,
    Guid? ExecutionId,
    DateTime CreatedAt,
    string? DecidedByName,
    DateTime? DecidedAt,
    DateTime? AppliedAt,
    DateTime? RevertedAt,
    string? Error);

public sealed record DecideProposalRequest(string? Note = null);

public sealed record AiAgentDto(Guid Id, string Name, string Kind, bool IsEnabled, string Mode, JsonElement Config, string? Instructions, long RunCount,
    DateTime CreatedAt, DateTime UpdatedAt);

public sealed record SaveAiAgentRequest(string Name, string Kind, bool IsEnabled, string Mode, JsonElement? Config, string? Instructions);

public sealed record AgentRunResultDto(Guid TicketId, int AgentsRun, int ProposalsCreated, int ActionsApplied, bool AutoReplied, IReadOnlyList<string> Notes);

public sealed record ExecutionFeedbackRequest(bool Accepted, string? Note = null);

// ───────────── Anomalies ─────────────

public sealed record AnomalyAlertDto(Guid Id, string Dimension, Guid? DimensionId, string DimensionLabel, DateOnly Day, int Count, double Baseline,
    double StdDev, double Score, string Status, DateTime CreatedAt);

// ───────────── Portal assistant ─────────────

public sealed record PortalAssistantRequest(string Message, string? Locale = null, Guid? ConversationId = null);

public sealed record PortalAssistantResponse(string Text, IReadOnlyList<CitationDto> Citations, bool SuggestHandoff, double Confidence);

// ───────────── Administration ─────────────

public sealed record AiFeatureSettingDto(string Feature, bool Enabled, IReadOnlyList<string> Roles, bool AutoApply, double MinConfidence);

public sealed record AutoReplyScenarioDto(string Key, string Name, string? Description, IReadOnlyList<string> Keywords, IReadOnlyList<Guid> CategoryIds,
    IReadOnlyList<Guid> ArticleIds, IReadOnlyList<string> Channels, double MinConfidence, bool Enabled);

public sealed record AiConfigurationDto(
    bool IsEnabled,
    string Provider,
    string Model,
    string? Endpoint,
    bool HasApiKey,
    string EmbeddingProvider,
    string EmbeddingModel,
    decimal MonthlyBudget,
    string Currency,
    string BudgetAction,
    decimal InputCostPer1K,
    decimal OutputCostPer1K,
    bool RedactPii,
    int ExecutionRetentionDays,
    IReadOnlyList<AiFeatureSettingDto> Features,
    IReadOnlyList<AutoReplyScenarioDto> AutoReplyScenarios,
    decimal SpentThisMonth,
    long TokensThisMonth,
    long? TokenAllowance,
    bool PlanIncludesAi,
    bool PlanIncludesAgents,
    IReadOnlyList<string> Providers,
    string RowVersion);

/// <summary>Partial update. ApiKey is write-only (never returned); ClearApiKey removes the stored key.</summary>
public sealed record UpdateAiConfigurationRequest
{
    public string? RowVersion { get; init; }
    public bool? IsEnabled { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? Endpoint { get; init; }
    public string? ApiKey { get; init; }
    public bool ClearApiKey { get; init; }
    public string? EmbeddingProvider { get; init; }
    public string? EmbeddingModel { get; init; }
    public decimal? MonthlyBudget { get; init; }
    public string? Currency { get; init; }
    public string? BudgetAction { get; init; }
    public decimal? InputCostPer1K { get; init; }
    public decimal? OutputCostPer1K { get; init; }
    public bool? RedactPii { get; init; }
    public int? ExecutionRetentionDays { get; init; }
    public IReadOnlyList<AiFeatureSettingDto>? Features { get; init; }
    public IReadOnlyList<AutoReplyScenarioDto>? AutoReplyScenarios { get; init; }
}

public sealed record PromptTemplateVersionDto(Guid Id, int Version, bool IsActive, string SystemPrompt, string UserTemplate, DateTime CreatedAt);

public sealed record PromptTemplateDto(string Feature, string Source, int ActiveVersion, string SystemPrompt, string UserTemplate,
    IReadOnlyList<string> Placeholders, string DefaultSystemPrompt, string DefaultUserTemplate, IReadOnlyList<PromptTemplateVersionDto> Versions);

public sealed record SavePromptTemplateRequest(string SystemPrompt, string UserTemplate);

public sealed record AiUsageRowDto(DateTime PeriodStart, string Feature, string Model, long Requests, long InputTokens, long OutputTokens, decimal Cost);

public sealed record AiUsageGroupDto(string Key, long Requests, long Tokens, decimal Cost);

public sealed record AiOutcomeStatsDto(string Feature, int Total, int Accepted, int Applied, int Rejected, int Reverted, int Failed);

public sealed record AiUsageSummaryDto(
    DateTime PeriodStart,
    decimal MonthlyBudget,
    string Currency,
    decimal SpentThisMonth,
    long TokensThisMonth,
    long? TokenAllowance,
    IReadOnlyList<AiUsageGroupDto> ByFeature,
    IReadOnlyList<AiUsageGroupDto> ByModel,
    IReadOnlyList<AiUsageGroupDto> ByMonth,
    IReadOnlyList<AiOutcomeStatsDto> Outcomes,
    IReadOnlyList<AiUsageRowDto> Rows);

public sealed record AiExecutionDto(
    Guid Id,
    string Feature,
    string Provider,
    string Model,
    string? EntityType,
    Guid? EntityId,
    Guid? UserId,
    string? UserName,
    Guid? AgentId,
    int PromptVersion,
    int InputTokens,
    int OutputTokens,
    decimal Cost,
    int LatencyMs,
    string Status,
    double? Confidence,
    IReadOnlyList<string> InjectionFlags,
    string Outcome,
    string? Error,
    DateTime CreatedAt,
    JsonElement? Output,
    JsonElement? Citations,
    string InputHash);

public sealed record TestProviderRequest(string? Prompt = null);

public sealed record TestProviderResultDto(bool Success, string Provider, string Model, int LatencyMs, string? Output, string? Error,
    string EmbeddingProvider, string EmbeddingModel, int? EmbeddingDimensions, string? EmbeddingError);

public sealed record ReindexResultDto(int Articles, int Tickets, int Reembedded, int Removed);
