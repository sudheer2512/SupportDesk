import { ApiError, ArticleDetailDto, ArticleType, KbVisibility, SaveArticleRequest } from '@sd/core';
import { dateValueToIso, isoToDateValue } from '../kb-common';

/** One editable access rule (Restricted visibility). All non-null conditions must match. */
export interface RuleDraft {
  /** Client-side key for @for tracking. */
  key: string;
  customerId: string | null;
  /** Display name of the selected customer (seeds the async picker). */
  customerName: string | null;
  customerGroupId: string | null;
  customerTier: string | null;
  productId: string | null;
}

/** Everything the editor lets an author change, in form-friendly shapes. */
export interface ArticleDraft {
  title: string;
  slug: string;
  summary: string;
  bodyHtml: string;
  keywords: string;
  categoryId: string | null;
  articleType: ArticleType;
  visibility: KbVisibility;
  tags: string[];
  relatedIds: string[];
  brandIds: string[];
  productId: string | null;
  ownerUserId: string | null;
  /** '' = tenant default. */
  primaryLocale: string;
  /** "YYYY-MM-DD" */
  reviewDue: string | null;
  /** "YYYY-MM-DD" */
  expires: string | null;
  rules: RuleDraft[];
}

let ruleCounter = 0;
export function ruleKey(): string {
  ruleCounter += 1;
  return `rule-${ruleCounter}`;
}

export function emptyRule(): RuleDraft {
  return { key: ruleKey(), customerId: null, customerName: null, customerGroupId: null, customerTier: null, productId: null };
}

export function emptyDraft(init: Partial<ArticleDraft> = {}): ArticleDraft {
  return {
    title: '',
    slug: '',
    summary: '',
    bodyHtml: '',
    keywords: '',
    categoryId: null,
    articleType: 'HowTo',
    visibility: 'Public',
    tags: [],
    relatedIds: [],
    brandIds: [],
    productId: null,
    ownerUserId: null,
    primaryLocale: '',
    reviewDue: null,
    expires: null,
    rules: [],
    ...init,
  };
}

export function draftFromArticle(a: ArticleDetailDto): ArticleDraft {
  return {
    title: a.title,
    slug: a.slug ?? '',
    summary: a.summary ?? '',
    bodyHtml: a.bodyHtml ?? '',
    keywords: a.keywords ?? '',
    categoryId: a.categoryId,
    articleType: a.articleType,
    visibility: a.visibility,
    tags: [...a.tags],
    relatedIds: a.relatedArticles.map((r) => r.id),
    brandIds: [...a.brandIds],
    productId: a.productId,
    ownerUserId: a.ownerUserId,
    primaryLocale: a.primaryLocale ?? '',
    reviewDue: isoToDateValue(a.reviewDueAt),
    expires: isoToDateValue(a.expiresAt),
    rules: a.accessRules.map((r) => ({
      key: ruleKey(),
      customerId: r.customerId,
      customerName: r.customerName,
      customerGroupId: r.customerGroupId,
      customerTier: r.customerTier,
      productId: r.productId,
    })),
  };
}

/** Stable serialisation used for dirty checking (ignores client-only keys and display names). */
export function draftKey(d: ArticleDraft): string {
  return JSON.stringify([
    d.title,
    d.slug,
    d.summary,
    d.bodyHtml,
    d.keywords,
    d.categoryId,
    d.articleType,
    d.visibility,
    d.tags,
    d.relatedIds,
    d.brandIds,
    d.productId,
    d.ownerUserId,
    d.primaryLocale,
    d.reviewDue,
    d.expires,
    d.visibility === 'Restricted' ? d.rules.map((r) => [r.customerId, r.customerGroupId, r.customerTier, r.productId]) : [],
  ]);
}

export function ruleIsEmpty(r: RuleDraft): boolean {
  return !r.customerId && !r.customerGroupId && !r.customerTier && !r.productId;
}

export function toSaveRequest(d: ArticleDraft, changeNote: string, rowVersion: string | null): SaveArticleRequest {
  const restricted = d.visibility === 'Restricted';
  return {
    title: d.title.trim(),
    bodyHtml: d.bodyHtml,
    summary: d.summary.trim() || null,
    keywords: d.keywords.trim() || null,
    slug: d.slug.trim() || null,
    categoryId: d.categoryId,
    articleType: d.articleType,
    visibility: d.visibility,
    tags: d.tags,
    relatedArticleIds: d.relatedIds,
    brandIds: d.brandIds,
    productId: d.productId,
    ownerUserId: d.ownerUserId,
    reviewDueAt: dateValueToIso(d.reviewDue),
    expiresAt: dateValueToIso(d.expires),
    primaryLocale: d.primaryLocale || null,
    changeNote: changeNote.trim() || null,
    accessRules: restricted
      ? d.rules
          .filter((r) => !ruleIsEmpty(r))
          .map((r) => ({ customerId: r.customerId, customerGroupId: r.customerGroupId, customerTier: r.customerTier, productId: r.productId }))
      : [],
    rowVersion,
  };
}

/** Field names the editor can show validation messages for (SaveArticleRequest members). */
export const ARTICLE_FIELDS = [
  'title',
  'slug',
  'summary',
  'bodyHtml',
  'keywords',
  'categoryId',
  'articleType',
  'visibility',
  'tags',
  'relatedArticleIds',
  'brandIds',
  'productId',
  'ownerUserId',
  'reviewDueAt',
  'expiresAt',
  'primaryLocale',
  'changeNote',
  'accessRules',
] as const;

export type ArticleField = (typeof ARTICLE_FIELDS)[number];
export type ArticleErrors = Partial<Record<ArticleField, string | null>>;

/** Maps problem-details validation errors onto editor fields (also matches "accessRules[0].customerId"-style keys). */
export function fieldErrorsFrom(err: ApiError): ArticleErrors {
  const result: ArticleErrors = {};
  const keys = Object.keys(err.errors);
  for (const field of ARTICLE_FIELDS) {
    const f = field.toLowerCase();
    const key = keys.find((k) => {
      const lower = k.toLowerCase().replace(/^\$\./, '');
      return lower === f || lower.startsWith(`${f}[`) || lower.startsWith(`${f}.`);
    });
    if (key) result[field] = err.errors[key]?.[0] ?? null;
  }
  return result;
}
