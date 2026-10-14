import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { toApiPath } from '../api/knowledge.api';
import { ApiClient } from '../http/api-client';
import { Guid, PageResult } from '../models/common';
import {
  CreatePostRequest,
  CreateTopicRequest,
  FlowUsageRequest,
  SubmitArticleFeedbackRequest,
  TopicDetailDto,
  TopicListItemDto,
  TopicQuery,
  VoteResultDto,
} from '../models/knowledge';
import {
  PortalAnonymousTicketRequest,
  PortalArticleDto,
  PortalArticleSummaryDto,
  PortalAssistantReplyDto,
  PortalAssistantRequest,
  PortalCategoryDto,
  PortalCategoryPageDto,
  PortalFlowDto,
  PortalFlowSummaryDto,
  PortalHomeDto,
  PortalSearchResultDto,
  PortalSubmissionResultDto,
  PortalSupportInfoDto,
} from '../models/portal-content';

const enc = encodeURIComponent;

/**
 * Customer-portal content (`/api/v1/portal/{brand}/…`): home, knowledge base (browse, search, article,
 * feedback), deflection suggestions, guided troubleshooting, community, support contacts, anonymous
 * submissions and the assistant. Works signed-out (public content) and signed-in (authenticated/restricted
 * articles, posting in the community).
 */
@Injectable({ providedIn: 'root' })
export class PortalContentApi {
  private readonly api = inject(ApiClient);

  home(brand: string): Observable<PortalHomeDto> {
    return this.api.get<PortalHomeDto>(`${base(brand)}/home`);
  }

  // ───── Knowledge base ─────

  categories(brand: string): Observable<PortalCategoryDto[]> {
    return this.api.get<PortalCategoryDto[]>(`${base(brand)}/kb/categories`);
  }

  category(brand: string, slug: string): Observable<PortalCategoryPageDto> {
    return this.api.get<PortalCategoryPageDto>(`${base(brand)}/kb/categories/${enc(slug)}`, { silent: true });
  }

  /** sort: popular | recent | title */
  articles(brand: string, params: { category?: string | null; tag?: string | null; sort?: string | null; limit?: number | null } = {}): Observable<PortalArticleSummaryDto[]> {
    return this.api.get<PortalArticleSummaryDto[]>(`${base(brand)}/kb/articles`, { params });
  }

  /** By slug or id; `locale` selects a translation (falls back to the primary language). */
  article(brand: string, slugOrId: string, locale?: string | null): Observable<PortalArticleDto> {
    return this.api.get<PortalArticleDto>(`${base(brand)}/kb/articles/${enc(slugOrId)}`, { params: { locale: locale ?? null }, silent: true });
  }

  feedback(brand: string, articleId: Guid, body: SubmitArticleFeedbackRequest): Observable<void> {
    return this.api.post<void>(`${base(brand)}/kb/articles/${enc(articleId)}/feedback`, body);
  }

  search(brand: string, q: string): Observable<PortalSearchResultDto> {
    return this.api.get<PortalSearchResultDto>(`${base(brand)}/search`, { params: { q } });
  }

  /** Lightweight suggestions while typing (ticket-form deflection); not logged as searches. */
  suggest(brand: string, q: string, limit = 5): Observable<PortalArticleSummaryDto[]> {
    return this.api.get<PortalArticleSummaryDto[]>(`${base(brand)}/kb/suggest`, { params: { q, limit }, silent: true });
  }

  /** Downloads any portal attachment URL returned by the API (`/api/v1/portal/...`). */
  download(url: string): Observable<Blob> {
    return this.api.blob(toApiPath(url));
  }

  // ───── Guided troubleshooting ─────

  flows(brand: string): Observable<PortalFlowSummaryDto[]> {
    return this.api.get<PortalFlowSummaryDto[]>(`${base(brand)}/flows`);
  }

  flow(brand: string, id: Guid): Observable<PortalFlowDto> {
    return this.api.get<PortalFlowDto>(`${base(brand)}/flows/${enc(id)}`, { silent: true });
  }

  flowUsage(brand: string, id: Guid, body: FlowUsageRequest): Observable<void> {
    return this.api.post<void>(`${base(brand)}/flows/${enc(id)}/usage`, body, { silent: true });
  }

  // ───── Community ─────

  topics(brand: string, query: TopicQuery = {}): Observable<PageResult<TopicListItemDto>> {
    return this.api.get<PageResult<TopicListItemDto>>(`${base(brand)}/community/topics`, { params: query });
  }

  topic(brand: string, id: Guid): Observable<TopicDetailDto> {
    return this.api.get<TopicDetailDto>(`${base(brand)}/community/topics/${enc(id)}`, { silent: true });
  }

  communityTags(brand: string): Observable<string[]> {
    return this.api.get<string[]>(`${base(brand)}/community/tags`);
  }

  createTopic(brand: string, body: CreateTopicRequest): Observable<TopicDetailDto> {
    return this.api.post<TopicDetailDto>(`${base(brand)}/community/topics`, body, { silent: true });
  }

  reply(brand: string, topicId: Guid, body: CreatePostRequest): Observable<TopicDetailDto> {
    return this.api.post<TopicDetailDto>(`${base(brand)}/community/topics/${enc(topicId)}/posts`, body, { silent: true });
  }

  voteTopic(brand: string, topicId: Guid, value: number): Observable<VoteResultDto> {
    return this.api.post<VoteResultDto>(`${base(brand)}/community/topics/${enc(topicId)}/vote`, { value });
  }

  votePost(brand: string, postId: Guid, value: number): Observable<VoteResultDto> {
    return this.api.post<VoteResultDto>(`${base(brand)}/community/posts/${enc(postId)}/vote`, { value });
  }

  /** The topic author accepts an answer. */
  accept(brand: string, topicId: Guid, postId: Guid): Observable<TopicDetailDto> {
    return this.api.post<TopicDetailDto>(`${base(brand)}/community/topics/${enc(topicId)}/accept/${enc(postId)}`, {});
  }

  // ───── Support, submissions, assistant ─────

  support(brand: string): Observable<PortalSupportInfoDto> {
    return this.api.get<PortalSupportInfoDto>(`${base(brand)}/support`);
  }

  /** Anonymous ticket (only when the brand allows it). */
  submitAnonymous(brand: string, body: PortalAnonymousTicketRequest): Observable<PortalSubmissionResultDto> {
    return this.api.post<PortalSubmissionResultDto>(`${base(brand)}/submit`, body, { silent: true, skipAuth: true });
  }

  assistant(brand: string, body: PortalAssistantRequest): Observable<PortalAssistantReplyDto> {
    return this.api.post<PortalAssistantReplyDto>(`${base(brand)}/assistant`, body, { silent: true });
  }
}

function base(brand: string): string {
  return `portal/${enc(brand.toLowerCase())}`;
}
