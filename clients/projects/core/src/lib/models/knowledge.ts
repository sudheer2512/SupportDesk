import { CountDto, Guid, IdName, IsoDate, IsoDateTime } from './common';

/**
 * Knowledge base, community, guided troubleshooting and global search contracts
 * (mirror SupportDesk.Contracts.Knowledge).
 */

export type KbVisibility = 'Public' | 'Authenticated' | 'Internal' | 'Restricted';
export type ArticleStatus = 'Draft' | 'InReview' | 'Approved' | 'Published' | 'Archived' | 'Expired';
export type ArticleType = 'FAQ' | 'HowTo' | 'Troubleshooting' | 'Reference' | 'ReleaseNote' | 'ProductDoc';
export type TranslationStatus = 'Draft' | 'InTranslation' | 'InReview' | 'Published' | 'Outdated';
export type ArticleTransitionAction = 'submit' | 'approve' | 'reject' | 'publish' | 'archive' | 'reopen';
export type TranslationTransitionAction = 'start' | 'submit' | 'publish' | 'reopen';

// ───── Categories ─────

export interface KbCategoryDto {
  id: Guid;
  parentId: Guid | null;
  name: string;
  slug: string;
  description: string | null;
  icon: string | null;
  visibility: KbVisibility;
  brandIds: Guid[];
  sortOrder: number;
  articleCount: number;
}

export interface SaveKbCategoryRequest {
  parentId: Guid | null;
  name: string;
  slug: string | null;
  description: string | null;
  icon: string | null;
  visibility: KbVisibility;
  brandIds: Guid[] | null;
  sortOrder: number;
}

// ───── Articles ─────

export interface ArticleQuery {
  q?: string | null;
  status?: ArticleStatus | null;
  visibility?: KbVisibility | null;
  categoryId?: Guid | null;
  locale?: string | null;
  ownerUserId?: Guid | null;
  articleType?: ArticleType | null;
  productId?: Guid | null;
  tag?: string | null;
  reviewDue?: boolean | null;
  live?: boolean | null;
  /** updated (default) | title | views | helpful | reviewDue */
  sort?: string | null;
  page?: number | null;
  pageSize?: number | null;
}

export interface ArticleListItemDto {
  id: Guid;
  slug: string;
  title: string;
  summary: string | null;
  status: ArticleStatus;
  visibility: KbVisibility;
  articleType: ArticleType;
  categoryId: Guid | null;
  categoryName: string | null;
  ownerUserId: Guid | null;
  ownerName: string | null;
  primaryLocale: string;
  translationLocales: string[];
  tags: string[];
  productId: Guid | null;
  productName: string | null;
  /** Currently visible to readers (has a published version and is not archived/expired). */
  isLive: boolean;
  /** A newer working version exists than the published one. */
  hasPendingChanges: boolean;
  latestVersionNumber: number;
  publishedVersionNumber: number | null;
  publishedAt: IsoDateTime | null;
  reviewDueAt: IsoDateTime | null;
  expiresAt: IsoDateTime | null;
  viewCount: number;
  helpfulCount: number;
  notHelpfulCount: number;
  ratingAverage: number | null;
  ratingCount: number;
  updatedAt: IsoDateTime;
}

export interface ArticleRefDto {
  id: Guid;
  title: string;
  slug: string;
  status: ArticleStatus;
  isLive: boolean;
}

export interface ArticleActionsDto {
  edit: boolean;
  submit: boolean;
  approve: boolean;
  reject: boolean;
  publish: boolean;
  archive: boolean;
  reopen: boolean;
  delete: boolean;
  manageTranslations: boolean;
}

export interface ArticleAccessRuleDto {
  id: Guid;
  customerId: Guid | null;
  customerName: string | null;
  customerGroupId: Guid | null;
  customerGroupName: string | null;
  customerTier: string | null;
  productId: Guid | null;
  productName: string | null;
}

/** All non-null fields of a rule must match; any matching rule grants access to a Restricted article. */
export interface ArticleAccessRuleInput {
  customerId: Guid | null;
  customerGroupId: Guid | null;
  customerTier: string | null;
  productId: Guid | null;
}

export interface ArticleAttachmentDto {
  id: Guid;
  fileId: Guid;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  isImage: boolean;
  /** Images: public asset URL (embeddable). Other files: authenticated download path. */
  url: string;
  createdAt: IsoDateTime;
}

export interface ArticleTranslationSummaryDto {
  id: Guid;
  locale: string;
  title: string;
  status: TranslationStatus;
  sourceVersionNumber: number;
  isOutdated: boolean;
  updatedAt: IsoDateTime;
}

export interface ArticleDetailDto {
  id: Guid;
  slug: string;
  title: string;
  summary: string | null;
  bodyHtml: string;
  keywords: string | null;
  status: ArticleStatus;
  visibility: KbVisibility;
  articleType: ArticleType;
  categoryId: Guid | null;
  categoryName: string | null;
  ownerUserId: Guid | null;
  ownerName: string | null;
  primaryLocale: string;
  tags: string[];
  relatedArticles: ArticleRefDto[];
  brandIds: Guid[];
  productId: Guid | null;
  productName: string | null;
  isLive: boolean;
  hasPendingChanges: boolean;
  currentVersionNumber: number;
  publishedVersionNumber: number | null;
  publishedAt: IsoDateTime | null;
  reviewDueAt: IsoDateTime | null;
  expiresAt: IsoDateTime | null;
  viewCount: number;
  helpfulCount: number;
  notHelpfulCount: number;
  ratingAverage: number | null;
  ratingCount: number;
  sourceTicketId: Guid | null;
  sourceTicketNumber: string | null;
  accessRules: ArticleAccessRuleDto[];
  attachments: ArticleAttachmentDto[];
  translations: ArticleTranslationSummaryDto[];
  actions: ArticleActionsDto;
  lastReviewNote: string | null;
  createdAt: IsoDateTime;
  updatedAt: IsoDateTime;
  /** Send back on update (optimistic concurrency → 409 concurrency_conflict). */
  rowVersion: string;
}

export interface SaveArticleRequest {
  title: string;
  bodyHtml: string;
  summary?: string | null;
  keywords?: string | null;
  slug?: string | null;
  categoryId?: Guid | null;
  articleType: ArticleType;
  visibility: KbVisibility;
  tags?: string[] | null;
  relatedArticleIds?: Guid[] | null;
  brandIds?: Guid[] | null;
  productId?: Guid | null;
  ownerUserId?: Guid | null;
  reviewDueAt?: IsoDateTime | null;
  expiresAt?: IsoDateTime | null;
  primaryLocale?: string | null;
  changeNote?: string | null;
  /** Replaces the rules when provided (Restricted visibility). */
  accessRules?: ArticleAccessRuleInput[] | null;
  rowVersion?: string | null;
}

export interface ArticleTransitionRequest {
  action: ArticleTransitionAction;
  note?: string | null;
}

// ───── Versions ─────

export interface ArticleVersionDto {
  id: Guid;
  versionNumber: number;
  locale: string;
  title: string;
  summary: string | null;
  status: ArticleStatus;
  changeNote: string | null;
  authorId: Guid | null;
  authorName: string | null;
  createdAt: IsoDateTime;
  reviewerId: Guid | null;
  reviewerName: string | null;
  reviewedAt: IsoDateTime | null;
  reviewNote: string | null;
  isCurrent: boolean;
  isPublished: boolean;
}

export interface ArticleVersionContentDto {
  version: ArticleVersionDto;
  bodyHtml: string;
  keywords: string | null;
}

export interface FieldDiffDto {
  from: string | null;
  to: string | null;
}

export interface DiffLineDto {
  op: 'equal' | 'insert' | 'delete';
  text: string;
}

export interface ArticleDiffDto {
  fromVersion: number;
  toVersion: number;
  title: FieldDiffDto | null;
  summary: FieldDiffDto | null;
  keywords: FieldDiffDto | null;
  body: DiffLineDto[];
  added: number;
  removed: number;
}

// ───── Translations ─────

export interface ArticleTranslationDto {
  id: Guid;
  articleId: Guid;
  locale: string;
  title: string;
  summary: string | null;
  bodyHtml: string;
  status: TranslationStatus;
  sourceVersionNumber: number;
  isOutdated: boolean;
  machineTranslated: boolean;
  translatorUserId: Guid | null;
  translatorName: string | null;
  publishedAt: IsoDateTime | null;
  updatedAt: IsoDateTime;
}

export interface SaveTranslationRequest {
  title: string;
  bodyHtml: string;
  summary?: string | null;
  machineTranslated?: boolean;
}

// ───── Feedback ─────

export interface SubmitArticleFeedbackRequest {
  helpful: boolean;
  rating?: number | null;
  comment?: string | null;
  locale?: string | null;
}

export interface ArticleFeedbackDto {
  id: Guid;
  articleId: Guid;
  articleTitle: string;
  helpful: boolean;
  rating: number | null;
  comment: string | null;
  locale: string;
  authorName: string | null;
  /** Contact | Agent | Anonymous */
  authorType: string;
  reviewed: boolean;
  createdAt: IsoDateTime;
}

export interface ArticleFeedbackQuery {
  articleId?: Guid | null;
  reviewed?: boolean | null;
  helpful?: boolean | null;
  withComments?: boolean | null;
  page?: number | null;
  pageSize?: number | null;
}

export interface ArticleFromTicketRequest {
  title?: string | null;
  includeConversation?: boolean;
  categoryId?: Guid | null;
}

export interface KbImportIssueDto {
  item: string;
  /** error | warning */
  severity: string;
  message: string;
}

export interface KbImportResultDto {
  created: number;
  skipped: number;
  categoriesCreated: number;
  issues: KbImportIssueDto[];
  articleIds: Guid[];
}

// ───── Options & analytics ─────

export interface KbOptionsDto {
  articleTypes: ArticleType[];
  visibilities: KbVisibility[];
  statuses: ArticleStatus[];
  locales: string[];
  brands: IdName[];
  products: IdName[];
  customerGroups: IdName[];
  customerTiers: string[];
  owners: IdName[];
  tags: string[];
}

export interface KbTotalsDto {
  articles: number;
  live: number;
  drafts: number;
  inReview: number;
  reviewOverdue: number;
  expiringSoon: number;
  views: number;
  helpful: number;
  notHelpful: number;
  helpfulPercent: number | null;
  averageRating: number | null;
  pendingFeedback: number;
  searches: number;
  searchesWithoutResults: number;
}

export interface KbArticleStatDto {
  id: Guid;
  title: string;
  views: number;
  helpful: number;
  notHelpful: number;
  helpfulPercent: number | null;
  ratingAverage: number | null;
}

export interface KbSearchTermDto {
  query: string;
  count: number;
  averageResults: number;
  lastSearchedAt: IsoDateTime;
}

export interface KbDailyPointDto {
  date: IsoDate;
  searches: number;
  noResults: number;
  helpful: number;
  notHelpful: number;
}

export interface FlowUsageStatDto {
  id: Guid;
  name: string;
  started: number;
  resolved: number;
  escalated: number;
}

export interface KbAnalyticsDto {
  days: number;
  totals: KbTotalsDto;
  topViewed: KbArticleStatDto[];
  leastHelpful: KbArticleStatDto[];
  topSearches: KbSearchTermDto[];
  noResultSearches: KbSearchTermDto[];
  daily: KbDailyPointDto[];
  flows: FlowUsageStatDto[];
}

// ───── Community ─────

export type TopicStatus = 'Open' | 'Answered' | 'Closed' | 'Hidden';

export interface TopicListItemDto {
  id: Guid;
  title: string;
  excerpt: string;
  authorName: string;
  authorIsAgent: boolean;
  status: TopicStatus;
  isQuestion: boolean;
  isPinned: boolean;
  isAnswered: boolean;
  voteScore: number;
  postCount: number;
  viewCount: number;
  tags: string[];
  createdAt: IsoDateTime;
  lastActivityAt: IsoDateTime;
  convertedTicketId: Guid | null;
}

export interface PostDto {
  id: Guid;
  bodyHtml: string;
  authorName: string;
  isAgentResponse: boolean;
  isMine: boolean;
  isAcceptedAnswer: boolean;
  isHidden: boolean;
  voteScore: number;
  myVote: number;
  createdAt: IsoDateTime;
  updatedAt: IsoDateTime;
}

export interface TopicActionsDto {
  reply: boolean;
  vote: boolean;
  accept: boolean;
  moderate: boolean;
  edit: boolean;
  convert: boolean;
}

export interface TopicDetailDto {
  id: Guid;
  title: string;
  bodyHtml: string;
  authorName: string;
  authorIsAgent: boolean;
  isMine: boolean;
  status: TopicStatus;
  isQuestion: boolean;
  isPinned: boolean;
  acceptedPostId: Guid | null;
  voteScore: number;
  myVote: number;
  postCount: number;
  viewCount: number;
  tags: string[];
  createdAt: IsoDateTime;
  lastActivityAt: IsoDateTime;
  posts: PostDto[];
  actions: TopicActionsDto;
  convertedTicketId: Guid | null;
  convertedTicketNumber: string | null;
}

export interface TopicQuery {
  q?: string | null;
  tag?: string | null;
  /** Open | Answered | Closed | Hidden (agents) | unanswered */
  status?: string | null;
  /** activity (default) | newest | votes | unanswered */
  sort?: string | null;
  page?: number | null;
  pageSize?: number | null;
}

export interface CreateTopicRequest {
  title: string;
  bodyHtml: string;
  isQuestion?: boolean;
  tags?: string[] | null;
}

export interface UpdateTopicRequest {
  title: string;
  bodyHtml: string;
  tags?: string[] | null;
}

export interface CreatePostRequest {
  bodyHtml: string;
}

export interface UpdatePostRequest {
  bodyHtml: string;
}

export interface VoteResultDto {
  voteScore: number;
  myVote: number;
}

/** Topics: hide | unhide | close | reopen | pin | unpin. Posts: hide | unhide. */
export interface ModerateRequest {
  action: string;
  reason?: string | null;
}

export interface ConvertTopicResultDto {
  ticketId: Guid;
  number: number;
  displayNumber: string;
}

// ───── Guided troubleshooting ─────

export type FlowNodeType = 'question' | 'solution' | 'article' | 'escalate';

export interface FlowOptionDto {
  label: string;
  /** Id of the node this answer leads to. */
  next: string;
}

export interface FlowNodeDto {
  id: string;
  type: FlowNodeType;
  /** Short label used in the designer. */
  title: string | null;
  /** Question text / solution (HTML allowed) / escalation message. */
  text: string;
  articleId: Guid | null;
  options: FlowOptionDto[];
}

export interface FlowDefinitionDto {
  startNodeId: string;
  nodes: FlowNodeDto[];
}

export interface FlowValidationIssueDto {
  nodeId: string | null;
  message: string;
}

export interface FlowListItemDto {
  id: Guid;
  name: string;
  description: string | null;
  status: ArticleStatus;
  visibility: KbVisibility;
  productId: Guid | null;
  productName: string | null;
  categoryId: Guid | null;
  categoryName: string | null;
  version: number;
  nodeCount: number;
  started: number;
  resolved: number;
  escalated: number;
  updatedAt: IsoDateTime;
}

export interface FlowDetailDto {
  id: Guid;
  name: string;
  description: string | null;
  status: ArticleStatus;
  visibility: KbVisibility;
  productId: Guid | null;
  productName: string | null;
  categoryId: Guid | null;
  categoryName: string | null;
  version: number;
  started: number;
  resolved: number;
  escalated: number;
  definition: FlowDefinitionDto;
  issues: FlowValidationIssueDto[];
  updatedAt: IsoDateTime;
}

export interface SaveFlowRequest {
  name: string;
  description: string | null;
  visibility: KbVisibility;
  productId: Guid | null;
  categoryId: Guid | null;
  definition: FlowDefinitionDto;
}

/** publish | unpublish | archive */
export interface FlowTransitionRequest {
  action: 'publish' | 'unpublish' | 'archive';
}

export interface FlowUsageRequest {
  outcome: 'started' | 'resolved' | 'escalated';
  path?: string[] | null;
}

// ───── Global search ─────

export type SearchEntityType = 'Ticket' | 'Article' | 'Contact' | 'Customer' | 'Topic';

export interface SearchHitDto {
  type: SearchEntityType;
  id: Guid;
  title: string;
  subtitle: string | null;
  /** HTML-escaped text with matches wrapped in <mark>. */
  snippetHtml: string | null;
  score: number;
  /** Desk route of the record, e.g. /tickets/{id}. */
  url: string;
  badge: string | null;
  updatedAt: IsoDateTime;
}

export interface GlobalSearchResultDto {
  query: string;
  hits: SearchHitDto[];
  counts: CountDto[];
  tookMs: number;
}

export interface SearchIndexStatusDto {
  documents: CountDto[];
  postings: number;
  lastIndexedAt: IsoDateTime | null;
}
