import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient, ApiRequestOptions } from '../http/api-client';
import { Guid, PageResult } from '../models/common';
import {
  AgentRunResultDto,
  AiAgentDto,
  AiConfigurationDto,
  AiExecutionDto,
  AiExecutionQuery,
  AiProposalDto,
  AiProposalStatus,
  AiUsageSummaryDto,
  AnalyticsAnswerDto,
  AnomalyAlertDto,
  AnomalyStatus,
  ApplyClassificationRequest,
  ClassificationApplyResultDto,
  ClassificationSuggestionDto,
  DecideProposalRequest,
  DuplicateCheckDto,
  KnowledgeDraftDto,
  MergeDuplicateRequest,
  PortalAssistantRequest,
  PortalAssistantResponse,
  PromptTemplateDto,
  ReindexResultDto,
  ResolutionPredictionDto,
  RootCauseDto,
  SaveAiAgentRequest,
  SavePromptTemplateRequest,
  SemanticSearchRequest,
  SemanticSearchResultDto,
  SentimentDto,
  SimilarTicketDto,
  SuggestReplyRequest,
  SuggestedReplyDto,
  TestProviderRequest,
  TestProviderResultDto,
  TicketSummaryDto,
  TranslateRequest,
  TranslationDto,
  UpdateAiConfigurationRequest,
} from '../models/ai';

const enc = encodeURIComponent;

/**
 * AI assistance (`/api/v1/ai…`): ticket summaries, classification, grounded replies, sentiment, similar/duplicate
 * tickets, root cause, resolution prediction, knowledge drafts, semantic search, translation, natural-language
 * analytics, AI action proposals and anomaly alerts. Refusals surface as ApiError codes such as `ai_disabled`,
 * `ai_feature_disabled`, `ai_budget_exceeded` (402), `ai_provider_error` (502) and `nl_unsupported` (422).
 */
@Injectable({ providedIn: 'root' })
export class AiApi {
  private readonly api = inject(ApiClient);

  // ───── Ticket assistance ─────

  classify(ticketId: Guid, options: ApiRequestOptions = {}): Observable<ClassificationSuggestionDto> {
    return this.api.post<ClassificationSuggestionDto>(`ai/tickets/${enc(ticketId)}/classify`, {}, options);
  }

  applyClassification(ticketId: Guid, request: ApplyClassificationRequest): Observable<ClassificationApplyResultDto> {
    return this.api.post<ClassificationApplyResultDto>(`ai/tickets/${enc(ticketId)}/classify/apply`, request);
  }

  summarize(ticketId: Guid, options: ApiRequestOptions = {}): Observable<TicketSummaryDto> {
    return this.api.post<TicketSummaryDto>(`ai/tickets/${enc(ticketId)}/summarize`, {}, options);
  }

  suggestReply(ticketId: Guid, request: SuggestReplyRequest = {}, options: ApiRequestOptions = {}): Observable<SuggestedReplyDto> {
    return this.api.post<SuggestedReplyDto>(`ai/tickets/${enc(ticketId)}/suggest-reply`, request, options);
  }

  sentiment(ticketId: Guid, options: ApiRequestOptions = {}): Observable<SentimentDto> {
    return this.api.get<SentimentDto>(`ai/tickets/${enc(ticketId)}/sentiment`, options);
  }

  analyzeSentiment(ticketId: Guid): Observable<SentimentDto> {
    return this.api.post<SentimentDto>(`ai/tickets/${enc(ticketId)}/sentiment`);
  }

  similar(ticketId: Guid, options: ApiRequestOptions = {}): Observable<SimilarTicketDto[]> {
    return this.api.get<SimilarTicketDto[]>(`ai/tickets/${enc(ticketId)}/similar`, options);
  }

  duplicates(ticketId: Guid, options: ApiRequestOptions = {}): Observable<DuplicateCheckDto> {
    return this.api.get<DuplicateCheckDto>(`ai/tickets/${enc(ticketId)}/duplicates`, options);
  }

  /** Merges this ticket into `targetTicketId` (the surviving ticket). */
  mergeDuplicate(ticketId: Guid, request: MergeDuplicateRequest): Observable<{ survivingTicketId: Guid }> {
    return this.api.post<{ survivingTicketId: Guid }>(`ai/tickets/${enc(ticketId)}/duplicates/merge`, request);
  }

  rootCause(ticketId: Guid, options: ApiRequestOptions = {}): Observable<RootCauseDto> {
    return this.api.post<RootCauseDto>(`ai/tickets/${enc(ticketId)}/root-cause`, {}, options);
  }

  resolutionPrediction(ticketId: Guid, options: ApiRequestOptions = {}): Observable<ResolutionPredictionDto> {
    return this.api.get<ResolutionPredictionDto>(`ai/tickets/${enc(ticketId)}/resolution-prediction`, options);
  }

  knowledgeDraft(ticketId: Guid, options: ApiRequestOptions = {}): Observable<KnowledgeDraftDto> {
    return this.api.post<KnowledgeDraftDto>(`ai/tickets/${enc(ticketId)}/knowledge-draft`, {}, options);
  }

  ticketProposals(ticketId: Guid, status?: AiProposalStatus | null, options: ApiRequestOptions = {}): Observable<AiProposalDto[]> {
    return this.api.get<AiProposalDto[]>(`ai/tickets/${enc(ticketId)}/proposals`, { ...options, params: { status } });
  }

  /** Runs the enabled AI agents now (proposals only — manual runs never auto-reply). */
  runAgents(ticketId: Guid): Observable<AgentRunResultDto> {
    return this.api.post<AgentRunResultDto>(`ai/tickets/${enc(ticketId)}/agents/run`);
  }

  // ───── Search, translation, analytics ─────

  search(request: SemanticSearchRequest, options: ApiRequestOptions = {}): Observable<SemanticSearchResultDto> {
    return this.api.post<SemanticSearchResultDto>('ai/search', request, options);
  }

  translate(request: TranslateRequest): Observable<TranslationDto> {
    return this.api.post<TranslationDto>('ai/translate', request);
  }

  ask(question: string, options: ApiRequestOptions = {}): Observable<AnalyticsAnswerDto> {
    return this.api.post<AnalyticsAnswerDto>('ai/analytics/ask', { question }, options);
  }

  /** Records whether a suggestion was used (acceptance statistics). */
  feedback(executionId: Guid, accepted: boolean, note?: string | null): Observable<void> {
    return this.api.post<void>(`ai/executions/${enc(executionId)}/feedback`, { accepted, note: note ?? null }, { silent: true });
  }

  // ───── Proposals ─────

  proposals(query: { ticketId?: Guid | null; status?: AiProposalStatus | null; limit?: number } = {}, options: ApiRequestOptions = {}): Observable<AiProposalDto[]> {
    return this.api.get<AiProposalDto[]>('ai/proposals', { ...options, params: query });
  }

  approve(id: Guid, request: DecideProposalRequest = {}): Observable<AiProposalDto> {
    return this.api.post<AiProposalDto>(`ai/proposals/${enc(id)}/approve`, request, { silent: true });
  }

  reject(id: Guid, request: DecideProposalRequest = {}): Observable<AiProposalDto> {
    return this.api.post<AiProposalDto>(`ai/proposals/${enc(id)}/reject`, request, { silent: true });
  }

  revert(id: Guid, request: DecideProposalRequest = {}): Observable<AiProposalDto> {
    return this.api.post<AiProposalDto>(`ai/proposals/${enc(id)}/revert`, request, { silent: true });
  }

  // ───── Anomalies ─────

  anomalies(status?: AnomalyStatus | null, days = 30, options: ApiRequestOptions = {}): Observable<AnomalyAlertDto[]> {
    return this.api.get<AnomalyAlertDto[]>('ai/anomalies', { ...options, params: { status, days } });
  }

  acknowledgeAnomaly(id: Guid): Observable<AnomalyAlertDto> {
    return this.api.post<AnomalyAlertDto>(`ai/anomalies/${enc(id)}/acknowledge`);
  }

  dismissAnomaly(id: Guid): Observable<AnomalyAlertDto> {
    return this.api.post<AnomalyAlertDto>(`ai/anomalies/${enc(id)}/dismiss`);
  }

  // ───── Customer portal ─────

  /** Help-center assistant (anonymous: public knowledge only; signed-in contacts: their accessible knowledge). */
  portalAssistant(brand: string, request: PortalAssistantRequest): Observable<PortalAssistantResponse> {
    return this.api.post<PortalAssistantResponse>(`portal/${enc(brand)}/assistant`, request, { silent: true });
  }
}

/**
 * AI administration (`/api/v1/admin/ai…`, permission admin.ai): provider/model/budget configuration (the API key is
 * write-only), prompt templates and versions, usage & cost, the execution log, provider test, agents and reindexing.
 */
@Injectable({ providedIn: 'root' })
export class AiAdminApi {
  private readonly api = inject(ApiClient);

  config(): Observable<AiConfigurationDto> {
    return this.api.get<AiConfigurationDto>('admin/ai/config');
  }

  updateConfig(request: UpdateAiConfigurationRequest): Observable<AiConfigurationDto> {
    return this.api.put<AiConfigurationDto>('admin/ai/config', request, { silent: true });
  }

  testProvider(request: TestProviderRequest = {}): Observable<TestProviderResultDto> {
    return this.api.post<TestProviderResultDto>('admin/ai/test-provider', request);
  }

  reindex(): Observable<ReindexResultDto> {
    return this.api.post<ReindexResultDto>('admin/ai/reindex');
  }

  prompts(): Observable<PromptTemplateDto[]> {
    return this.api.get<PromptTemplateDto[]>('admin/ai/prompts');
  }

  /** Saves a tenant override as a new active version. */
  savePrompt(feature: string, request: SavePromptTemplateRequest): Observable<PromptTemplateDto> {
    return this.api.put<PromptTemplateDto>(`admin/ai/prompts/${enc(feature)}`, request, { silent: true });
  }

  /** Activates a saved version (0 = built-in default). */
  activatePrompt(feature: string, version: number): Observable<PromptTemplateDto> {
    return this.api.post<PromptTemplateDto>(`admin/ai/prompts/${enc(feature)}/versions/${version}/activate`);
  }

  resetPrompt(feature: string): Observable<PromptTemplateDto> {
    return this.api.delete<PromptTemplateDto>(`admin/ai/prompts/${enc(feature)}`);
  }

  usage(months = 6): Observable<AiUsageSummaryDto> {
    return this.api.get<AiUsageSummaryDto>('admin/ai/usage', { params: { months } });
  }

  executions(query: AiExecutionQuery = {}, options: ApiRequestOptions = {}): Observable<PageResult<AiExecutionDto>> {
    return this.api.get<PageResult<AiExecutionDto>>('admin/ai/executions', { ...options, params: query });
  }

  execution(id: Guid): Observable<AiExecutionDto> {
    return this.api.get<AiExecutionDto>(`admin/ai/executions/${enc(id)}`);
  }

  agents(): Observable<AiAgentDto[]> {
    return this.api.get<AiAgentDto[]>('admin/ai/agents');
  }

  createAgent(request: SaveAiAgentRequest): Observable<AiAgentDto> {
    return this.api.post<AiAgentDto>('admin/ai/agents', request, { silent: true });
  }

  updateAgent(id: Guid, request: SaveAiAgentRequest): Observable<AiAgentDto> {
    return this.api.put<AiAgentDto>(`admin/ai/agents/${enc(id)}`, request, { silent: true });
  }

  deleteAgent(id: Guid): Observable<void> {
    return this.api.delete<void>(`admin/ai/agents/${enc(id)}`);
  }
}
