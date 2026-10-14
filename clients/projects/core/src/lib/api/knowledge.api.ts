import { HttpEventType } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, filter, map } from 'rxjs';
import { ApiClient } from '../http/api-client';
import { Guid, IdName, PageResult } from '../models/common';
import {
  ArticleAttachmentDto,
  ArticleDetailDto,
  ArticleDiffDto,
  ArticleFeedbackDto,
  ArticleFeedbackQuery,
  ArticleFromTicketRequest,
  ArticleListItemDto,
  ArticleQuery,
  ArticleTransitionRequest,
  ArticleTranslationDto,
  ArticleVersionContentDto,
  ArticleVersionDto,
  FlowDefinitionDto,
  FlowDetailDto,
  FlowListItemDto,
  FlowTransitionRequest,
  FlowValidationIssueDto,
  KbAnalyticsDto,
  KbCategoryDto,
  KbImportResultDto,
  KbOptionsDto,
  SaveArticleRequest,
  SaveFlowRequest,
  SaveKbCategoryRequest,
  SaveTranslationRequest,
  TranslationTransitionAction,
} from '../models/knowledge';

const enc = encodeURIComponent;

/** Strips a leading `/api/v1/` so server-provided absolute API paths can go through ApiClient. */
export function toApiPath(url: string): string {
  return url.replace(/^\/?api\/v1\//i, '');
}

/** Hands a blob to the browser as a file download. */
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  a.rel = 'noopener';
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}

/**
 * Knowledge base (agent side): categories, articles (versions, diff/restore, workflow transitions,
 * translations, access rules, attachments), feedback review queue, ticket→article, import/export,
 * analytics and guided-troubleshooting flows. Base `/api/v1/kb…`.
 */
@Injectable({ providedIn: 'root' })
export class KnowledgeApi {
  private readonly api = inject(ApiClient);

  // ───── Options ─────

  options(): Observable<KbOptionsDto> {
    return this.api.get<KbOptionsDto>('kb/options');
  }

  /** Customers for access-rule pickers. */
  customers(q: string): Observable<IdName[]> {
    return this.api.get<IdName[]>('kb/options/customers', { params: { q } });
  }

  // ───── Categories ─────

  categories(): Observable<KbCategoryDto[]> {
    return this.api.get<KbCategoryDto[]>('kb/categories');
  }

  createCategory(body: SaveKbCategoryRequest): Observable<KbCategoryDto> {
    return this.api.post<KbCategoryDto>('kb/categories', body);
  }

  updateCategory(id: Guid, body: SaveKbCategoryRequest): Observable<KbCategoryDto> {
    return this.api.put<KbCategoryDto>(`kb/categories/${enc(id)}`, body);
  }

  deleteCategory(id: Guid): Observable<void> {
    return this.api.delete(`kb/categories/${enc(id)}`);
  }

  // ───── Articles ─────

  articles(query: ArticleQuery = {}): Observable<PageResult<ArticleListItemDto>> {
    return this.api.get<PageResult<ArticleListItemDto>>('kb/articles', { params: query });
  }

  article(id: Guid): Observable<ArticleDetailDto> {
    return this.api.get<ArticleDetailDto>(`kb/articles/${enc(id)}`);
  }

  create(body: SaveArticleRequest): Observable<ArticleDetailDto> {
    return this.api.post<ArticleDetailDto>('kb/articles', body, { silent: true });
  }

  update(id: Guid, body: SaveArticleRequest): Observable<ArticleDetailDto> {
    return this.api.put<ArticleDetailDto>(`kb/articles/${enc(id)}`, body, { silent: true });
  }

  delete(id: Guid): Observable<void> {
    return this.api.delete(`kb/articles/${enc(id)}`);
  }

  transition(id: Guid, body: ArticleTransitionRequest): Observable<ArticleDetailDto> {
    return this.api.post<ArticleDetailDto>(`kb/articles/${enc(id)}/transition`, body, { silent: true });
  }

  /** Creates a draft article from a (resolved) ticket. */
  fromTicket(ticketId: Guid, body: ArticleFromTicketRequest = {}): Observable<ArticleDetailDto> {
    return this.api.post<ArticleDetailDto>(`kb/articles/from-ticket/${enc(ticketId)}`, body);
  }

  // ───── Versions ─────

  versions(id: Guid): Observable<ArticleVersionDto[]> {
    return this.api.get<ArticleVersionDto[]>(`kb/articles/${enc(id)}/versions`);
  }

  version(id: Guid, versionNumber: number): Observable<ArticleVersionContentDto> {
    return this.api.get<ArticleVersionContentDto>(`kb/articles/${enc(id)}/versions/${versionNumber}`);
  }

  diff(id: Guid, from: number, to: number): Observable<ArticleDiffDto> {
    return this.api.get<ArticleDiffDto>(`kb/articles/${enc(id)}/diff`, { params: { from, to } });
  }

  restore(id: Guid, versionNumber: number, changeNote?: string | null): Observable<ArticleDetailDto> {
    return this.api.post<ArticleDetailDto>(`kb/articles/${enc(id)}/versions/${versionNumber}/restore`, { changeNote: changeNote ?? null });
  }

  // ───── Translations ─────

  translations(id: Guid): Observable<ArticleTranslationDto[]> {
    return this.api.get<ArticleTranslationDto[]>(`kb/articles/${enc(id)}/translations`);
  }

  saveTranslation(id: Guid, locale: string, body: SaveTranslationRequest): Observable<ArticleTranslationDto> {
    return this.api.put<ArticleTranslationDto>(`kb/articles/${enc(id)}/translations/${enc(locale)}`, body, { silent: true });
  }

  transitionTranslation(id: Guid, locale: string, action: TranslationTransitionAction): Observable<ArticleTranslationDto> {
    return this.api.post<ArticleTranslationDto>(`kb/articles/${enc(id)}/translations/${enc(locale)}/transition`, { action }, { silent: true });
  }

  deleteTranslation(id: Guid, locale: string): Observable<void> {
    return this.api.delete(`kb/articles/${enc(id)}/translations/${enc(locale)}`);
  }

  // ───── Attachments & images ─────

  /** Attaches a downloadable file to an article. */
  uploadAttachment(id: Guid, file: File): Observable<ArticleAttachmentDto> {
    return this.uploadTo(`kb/articles/${enc(id)}/attachments`, file);
  }

  deleteAttachment(id: Guid, attachmentId: Guid): Observable<void> {
    return this.api.delete(`kb/articles/${enc(id)}/attachments/${enc(attachmentId)}`);
  }

  downloadAttachment(id: Guid, attachmentId: Guid): Observable<Blob> {
    return this.api.blob(`kb/articles/${enc(id)}/attachments/${enc(attachmentId)}/download`);
  }

  /** Uploads an image for embedding in article bodies; `url` is a public asset URL usable in <img src>. */
  uploadImage(file: File): Observable<ArticleAttachmentDto> {
    return this.uploadTo('kb/assets', file);
  }

  // ───── Feedback ─────

  feedback(query: ArticleFeedbackQuery = {}): Observable<PageResult<ArticleFeedbackDto>> {
    return this.api.get<PageResult<ArticleFeedbackDto>>('kb/feedback', { params: query });
  }

  markFeedbackReviewed(ids: Guid[]): Observable<void> {
    return this.api.post<void>('kb/feedback/review', { ids });
  }

  // ───── Import / export ─────

  /** format: json | zip (Markdown + HTML files with a manifest). */
  export(format: 'json' | 'zip' = 'json', ids?: Guid[]): Observable<Blob> {
    return this.api.blob('kb/export', { params: { format, ids: ids ?? null } });
  }

  /** Accepts the JSON export, a zip (manifest or .md/.html files) or a single .md/.html file. Creates drafts. */
  import(file: File): Observable<KbImportResultDto> {
    const form = new FormData();
    form.append('file', file, file.name);
    return this.api.upload<KbImportResultDto>('kb/import', form, { silent: true }).pipe(
      filter((e) => e.type === HttpEventType.Response),
      map((e) => (e as { body: KbImportResultDto }).body),
    );
  }

  // ───── Analytics ─────

  analytics(days = 30): Observable<KbAnalyticsDto> {
    return this.api.get<KbAnalyticsDto>('kb/analytics', { params: { days } });
  }

  // ───── Guided troubleshooting ─────

  flows(): Observable<FlowListItemDto[]> {
    return this.api.get<FlowListItemDto[]>('kb/flows');
  }

  flow(id: Guid): Observable<FlowDetailDto> {
    return this.api.get<FlowDetailDto>(`kb/flows/${enc(id)}`);
  }

  createFlow(body: SaveFlowRequest): Observable<FlowDetailDto> {
    return this.api.post<FlowDetailDto>('kb/flows', body, { silent: true });
  }

  updateFlow(id: Guid, body: SaveFlowRequest): Observable<FlowDetailDto> {
    return this.api.put<FlowDetailDto>(`kb/flows/${enc(id)}`, body, { silent: true });
  }

  deleteFlow(id: Guid): Observable<void> {
    return this.api.delete(`kb/flows/${enc(id)}`);
  }

  transitionFlow(id: Guid, body: FlowTransitionRequest): Observable<FlowDetailDto> {
    return this.api.post<FlowDetailDto>(`kb/flows/${enc(id)}/transition`, body, { silent: true });
  }

  validateFlow(definition: FlowDefinitionDto): Observable<FlowValidationIssueDto[]> {
    return this.api.post<FlowValidationIssueDto[]>('kb/flows/validate', definition);
  }

  private uploadTo(path: string, file: File): Observable<ArticleAttachmentDto> {
    const form = new FormData();
    form.append('file', file, file.name);
    return this.api.upload<ArticleAttachmentDto>(path, form, { silent: true }).pipe(
      filter((e) => e.type === HttpEventType.Response),
      map((e) => (e as { body: ArticleAttachmentDto }).body),
    );
  }
}
