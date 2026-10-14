/**
 * AI module wire types mirroring SupportDesk.Contracts.Ai (camelCase, enums as strings, dates as ISO strings).
 */
import { Guid, IsoDate, IsoDateTime, JsonValue } from './common';

export type AiProvider = 'Local' | 'OpenAI' | 'AzureOpenAI' | 'OpenAICompatible' | 'Anthropic';
export type AiRiskLevel = 'Low' | 'Medium' | 'High';
export type AiProposalStatus = 'Proposed' | 'Approved' | 'Rejected' | 'Applied' | 'Reverted' | 'Expired' | 'Failed';
export type AiExecutionStatus = 'Succeeded' | 'Failed' | 'Blocked' | 'BudgetExceeded' | 'Disabled';
export type AiOutcome = 'Pending' | 'Accepted' | 'Rejected' | 'Applied' | 'Reverted' | 'Expired';
export type AiAgentKind = 'Triage' | 'InfoCollector' | 'KnowledgeResolver' | 'AutoResponder';
export type AiAgentMode = 'SuggestOnly' | 'AutoLowRisk';
export type AnomalyStatus = 'Open' | 'Acknowledged' | 'Dismissed';
export type BudgetAction = 'Warn' | 'Block';

/** Feature keys (SupportDesk.Domain.Ai.AiFeatures). */
export const AI_FEATURES = [
  'classification',
  'summarization',
  'reply_suggestion',
  'semantic_search',
  'sentiment',
  'duplicate_detection',
  'root_cause',
  'knowledge_generation',
  'translation',
  'resolution_prediction',
  'anomaly_detection',
  'nl_analytics',
  'agents',
  'portal_assistant',
] as const;
export type AiFeature = (typeof AI_FEATURES)[number];

export const AI_PROVIDERS: readonly AiProvider[] = ['Local', 'OpenAI', 'AzureOpenAI', 'OpenAICompatible', 'Anthropic'];
export const AI_AGENT_KINDS: readonly AiAgentKind[] = ['Triage', 'InfoCollector', 'KnowledgeResolver', 'AutoResponder'];
/** Allowed actions an agent can be granted (AiAgent.ConfigJson.allowedActions). */
export const AI_AGENT_ACTIONS = ['set_category', 'set_department', 'set_priority', 'add_tag', 'request_info', 'suggest_article', 'send_reply', 'auto_reply'] as const;
/** Information items the info collector can ask for. */
export const AI_INFO_ITEMS = ['version', 'environment', 'steps', 'error_message', 'screenshot', 'impact'] as const;

export interface CitationDto {
  index: number;
  sourceType: string;
  sourceId: Guid;
  title: string;
  url: string | null;
  snippet: string | null;
}

// ───── Classification ─────

export interface ClassificationFieldDto {
  field: 'category' | 'product' | 'priority' | 'department' | string;
  value: string | null;
  label: string | null;
  confidence: number;
  currentValue: string | null;
  currentLabel: string | null;
  differs: boolean;
}

export interface ClassificationSuggestionDto {
  executionId: Guid;
  ticketId: Guid;
  fields: ClassificationFieldDto[];
  confidence: number;
  explanation: string | null;
  autoApplied: boolean;
  provider: string;
  model: string;
}

export interface ApplyClassificationRequest {
  executionId: Guid;
  fields?: string[] | null;
}

export interface ClassificationApplyResultDto {
  ticketId: Guid;
  appliedFields: string[];
  skippedFields: string[];
  message: string;
}

// ───── Summaries, replies, sentiment ─────

export interface TicketSummaryDto {
  executionId: Guid;
  ticketId: Guid;
  summary: string;
  currentStatus: string;
  pendingActions: string[];
  generatedAt: IsoDateTime;
  provider: string;
}

export interface SuggestReplyRequest {
  instructions?: string | null;
  tone?: string | null;
}

export interface SuggestedReplyDto {
  executionId: Guid;
  ticketId: Guid;
  bodyHtml: string;
  bodyText: string;
  citations: CitationDto[];
  confidence: number;
  grounded: boolean;
  provider: string;
}

export interface MessageSentimentDto {
  threadId: Guid;
  seq: number;
  sentiment: string;
  createdAt: IsoDateTime;
}

export interface SentimentDto {
  ticketId: Guid;
  sentiment: string;
  score: number | null;
  frustrated: boolean;
  messages: MessageSentimentDto[];
}

// ───── Search, duplicates, root cause, prediction ─────

export interface SemanticSearchRequest {
  query: string;
  scope?: 'all' | 'tickets' | 'articles' | null;
  take?: number | null;
}

export interface SemanticSearchHitDto {
  sourceType: 'Article' | 'Ticket' | 'OpenTicket' | string;
  sourceId: Guid;
  title: string;
  snippet: string | null;
  url: string | null;
  score: number;
  ticketNumber: number | null;
  status: string | null;
  visibility: string;
}

export interface SemanticSearchResultDto {
  query: string;
  items: SemanticSearchHitDto[];
  embeddingModel: string;
}

export interface SimilarTicketDto {
  ticketId: Guid;
  number: number;
  displayNumber: string;
  subject: string;
  status: string;
  statusCategory: string;
  similarity: number;
  resolutionSnippet: string | null;
  rootCause: string | null;
  createdAt: IsoDateTime;
  resolvedAt: IsoDateTime | null;
}

export interface DuplicateCandidateDto {
  ticketId: Guid;
  number: number;
  displayNumber: string;
  subject: string;
  status: string;
  createdAt: IsoDateTime;
  similarity: number;
  sameContact: boolean;
  sameCustomer: boolean;
  reason: string;
  canMerge: boolean;
}

export interface DuplicateCheckDto {
  executionId: Guid | null;
  ticketId: Guid;
  candidates: DuplicateCandidateDto[];
}

export interface MergeDuplicateRequest {
  targetTicketId: Guid;
  note?: string | null;
}

export interface RootCauseEvidenceDto {
  ticketId: Guid;
  number: number;
  subject: string;
  resolutionSnippet: string | null;
  similarity: number;
}

export interface RootCauseCandidateDto {
  cause: string;
  code: string | null;
  likelihood: number;
  explanation: string | null;
  evidence: RootCauseEvidenceDto[];
}

export interface RootCauseDto {
  executionId: Guid;
  ticketId: Guid;
  causes: RootCauseCandidateDto[];
  sampleSize: number;
  provider: string;
}

export interface ResolutionPredictionDto {
  executionId: Guid | null;
  ticketId: Guid;
  medianHours: number | null;
  p25Hours: number | null;
  p75Hours: number | null;
  sampleSize: number;
  basis: string;
  lowConfidence: boolean;
  confidence: number;
  predictedResolutionAt: IsoDateTime | null;
  explanation: string;
}

// ───── Knowledge generation & translation ─────

export interface KnowledgeDraftDto {
  executionId: Guid;
  ticketId: Guid;
  articleId: Guid | null;
  created: boolean;
  title: string;
  summary: string | null;
  bodyHtml: string;
  message: string;
}

export interface TranslateRequest {
  text: string;
  targetLocale: string;
  sourceLocale?: string | null;
}

export interface TranslationDto {
  executionId: Guid | null;
  supported: boolean;
  text: string | null;
  targetLocale: string;
  provider: string;
  message: string | null;
}

// ───── Natural-language analytics ─────

export interface AskAnalyticsRequest {
  question: string;
}

export interface AnalyticsFilterDto {
  field: string;
  value: string;
  label: string | null;
}

export interface AnalyticsIntentDto {
  metric: string;
  dimension: string;
  filters: AnalyticsFilterDto[];
  period: string;
  sort: 'asc' | 'desc' | string;
  limit: number;
}

export interface AnalyticsColumnDto {
  key: string;
  label: string;
  type: 'string' | 'number' | string;
}

export interface AnalyticsChartDto {
  type: 'bar' | 'line' | 'pie' | 'kpi' | string;
  x: string;
  y: string;
}

export interface AnalyticsRowDto {
  key: string | null;
  label: string;
  value: number;
  count: number;
  [column: string]: string | number | null;
}

export interface AnalyticsAnswerDto {
  executionId: Guid;
  question: string;
  intent: AnalyticsIntentDto;
  interpretation: string;
  columns: AnalyticsColumnDto[];
  rows: AnalyticsRowDto[];
  chart: AnalyticsChartDto;
  periodFrom: IsoDateTime;
  periodTo: IsoDateTime;
  parser: string;
}

// ───── Proposals & agents ─────

export interface AiProposalChangeDto {
  field: string;
  before: string | null;
  after: string | null;
}

export interface AiProposalDto {
  id: Guid;
  ticketId: Guid;
  ticketNumber: number | null;
  ticketSubject: string | null;
  actionType: string;
  actionLabel: string;
  payload: JsonValue;
  rationale: string | null;
  confidence: number;
  risk: AiRiskLevel;
  requiresApproval: boolean;
  reversible: boolean;
  status: AiProposalStatus;
  agentId: Guid | null;
  agentName: string | null;
  executionId: Guid | null;
  createdAt: IsoDateTime;
  decidedByName: string | null;
  decidedAt: IsoDateTime | null;
  appliedAt: IsoDateTime | null;
  revertedAt: IsoDateTime | null;
  error: string | null;
  changes: AiProposalChangeDto[];
}

export interface DecideProposalRequest {
  note?: string | null;
}

/** Parsed AiAgent.ConfigJson. */
export interface AiAgentConfig {
  departments?: Guid[];
  channels?: string[];
  minConfidence?: number;
  allowedActions?: string[];
  maxRepliesPerTicket?: number;
  requiredInfo?: string[];
  scenarios?: string[];
}

export interface AiAgentDto {
  id: Guid;
  name: string;
  kind: AiAgentKind;
  isEnabled: boolean;
  mode: AiAgentMode;
  config: AiAgentConfig;
  instructions: string | null;
  runCount: number;
  createdAt: IsoDateTime;
  updatedAt: IsoDateTime;
}

export interface SaveAiAgentRequest {
  name: string;
  kind: AiAgentKind;
  isEnabled: boolean;
  mode: AiAgentMode;
  config: AiAgentConfig | null;
  instructions: string | null;
}

export interface AgentRunResultDto {
  ticketId: Guid;
  agentsRun: number;
  proposalsCreated: number;
  actionsApplied: number;
  autoReplied: boolean;
  notes: string[];
}

export interface ExecutionFeedbackRequest {
  accepted: boolean;
  note?: string | null;
}

// ───── Anomalies ─────

export interface AnomalyAlertDto {
  id: Guid;
  dimension: 'product' | 'version' | 'customer' | 'category' | string;
  dimensionId: Guid | null;
  dimensionLabel: string;
  day: IsoDate;
  count: number;
  baseline: number;
  stdDev: number;
  score: number;
  status: AnomalyStatus;
  createdAt: IsoDateTime;
}

// ───── Administration ─────

export interface AiFeatureSettingDto {
  feature: AiFeature | string;
  enabled: boolean;
  roles: string[];
  autoApply: boolean;
  minConfidence: number;
}

export interface AutoReplyScenarioDto {
  key: string;
  name: string;
  description: string | null;
  keywords: string[];
  categoryIds: Guid[];
  articleIds: Guid[];
  channels: string[];
  minConfidence: number;
  enabled: boolean;
}

export interface AiConfigurationDto {
  isEnabled: boolean;
  provider: AiProvider;
  model: string;
  endpoint: string | null;
  hasApiKey: boolean;
  embeddingProvider: AiProvider;
  embeddingModel: string;
  monthlyBudget: number;
  currency: string;
  budgetAction: BudgetAction;
  inputCostPer1K: number;
  outputCostPer1K: number;
  redactPii: boolean;
  executionRetentionDays: number;
  features: AiFeatureSettingDto[];
  autoReplyScenarios: AutoReplyScenarioDto[];
  spentThisMonth: number;
  tokensThisMonth: number;
  tokenAllowance: number | null;
  planIncludesAi: boolean;
  planIncludesAgents: boolean;
  providers: AiProvider[];
  rowVersion: string;
}

/** Partial update; `apiKey` is write-only and `clearApiKey` removes the stored key. */
export interface UpdateAiConfigurationRequest {
  rowVersion?: string | null;
  isEnabled?: boolean | null;
  provider?: AiProvider | null;
  model?: string | null;
  endpoint?: string | null;
  apiKey?: string | null;
  clearApiKey?: boolean;
  embeddingProvider?: AiProvider | null;
  embeddingModel?: string | null;
  monthlyBudget?: number | null;
  currency?: string | null;
  budgetAction?: BudgetAction | null;
  inputCostPer1K?: number | null;
  outputCostPer1K?: number | null;
  redactPii?: boolean | null;
  executionRetentionDays?: number | null;
  features?: AiFeatureSettingDto[] | null;
  autoReplyScenarios?: AutoReplyScenarioDto[] | null;
}

export interface PromptTemplateVersionDto {
  id: Guid;
  version: number;
  isActive: boolean;
  systemPrompt: string;
  userTemplate: string;
  createdAt: IsoDateTime;
}

export interface PromptTemplateDto {
  feature: string;
  source: 'default' | 'custom' | string;
  activeVersion: number;
  systemPrompt: string;
  userTemplate: string;
  placeholders: string[];
  defaultSystemPrompt: string;
  defaultUserTemplate: string;
  versions: PromptTemplateVersionDto[];
}

export interface SavePromptTemplateRequest {
  systemPrompt: string;
  userTemplate: string;
}

export interface AiUsageRowDto {
  periodStart: IsoDateTime;
  feature: string;
  model: string;
  requests: number;
  inputTokens: number;
  outputTokens: number;
  cost: number;
}

export interface AiUsageGroupDto {
  key: string;
  requests: number;
  tokens: number;
  cost: number;
}

export interface AiOutcomeStatsDto {
  feature: string;
  total: number;
  accepted: number;
  applied: number;
  rejected: number;
  reverted: number;
  failed: number;
}

export interface AiUsageSummaryDto {
  periodStart: IsoDateTime;
  monthlyBudget: number;
  currency: string;
  spentThisMonth: number;
  tokensThisMonth: number;
  tokenAllowance: number | null;
  byFeature: AiUsageGroupDto[];
  byModel: AiUsageGroupDto[];
  byMonth: AiUsageGroupDto[];
  outcomes: AiOutcomeStatsDto[];
  rows: AiUsageRowDto[];
}

export interface AiExecutionDto {
  id: Guid;
  feature: string;
  provider: string;
  model: string;
  entityType: string | null;
  entityId: Guid | null;
  userId: Guid | null;
  userName: string | null;
  agentId: Guid | null;
  promptVersion: number;
  inputTokens: number;
  outputTokens: number;
  cost: number;
  latencyMs: number;
  status: AiExecutionStatus;
  confidence: number | null;
  injectionFlags: string[];
  outcome: AiOutcome;
  error: string | null;
  createdAt: IsoDateTime;
  output: JsonValue | null;
  citations: JsonValue | null;
  inputHash: string;
}

export interface AiExecutionQuery {
  feature?: string | null;
  status?: AiExecutionStatus | null;
  outcome?: AiOutcome | null;
  entityId?: Guid | null;
  from?: IsoDateTime | null;
  to?: IsoDateTime | null;
  page?: number;
  pageSize?: number;
}

export interface TestProviderRequest {
  prompt?: string | null;
}

export interface TestProviderResultDto {
  success: boolean;
  provider: string;
  model: string;
  latencyMs: number;
  output: string | null;
  error: string | null;
  embeddingProvider: string;
  embeddingModel: string;
  embeddingDimensions: number | null;
  embeddingError: string | null;
}

export interface ReindexResultDto {
  articles: number;
  tickets: number;
  reembedded: number;
  removed: number;
}
