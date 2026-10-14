import { Guid, IsoDate, IsoDateTime, JsonValue, OptionDto } from './common';
import { FlowDefinitionDto, TopicListItemDto } from './knowledge';

/**
 * Customer-portal content, tickets and account contracts (mirror SupportDesk.Contracts.Portal).
 * Everything here is a customer-facing projection: no internal notes, events, internal attachments,
 * internal status names or agent-only fields.
 */

// ───── Knowledge base ─────

export interface PortalCategoryRefDto {
  id: Guid;
  name: string;
  slug: string;
}

export interface PortalCategoryDto {
  id: Guid;
  parentId: Guid | null;
  name: string;
  slug: string;
  description: string | null;
  icon: string | null;
  articleCount: number;
  children: PortalCategoryDto[];
}

export interface PortalArticleSummaryDto {
  id: Guid;
  slug: string;
  title: string;
  summary: string | null;
  articleType: string;
  categoryId: Guid | null;
  categoryName: string | null;
  categorySlug: string | null;
  publishedAt: IsoDateTime | null;
  updatedAt: IsoDateTime;
  viewCount: number;
  helpfulPercent: number | null;
  tags: string[];
  /** Search results only: escaped text with matches in <mark>. */
  snippetHtml: string | null;
}

export interface PortalAttachmentDto {
  id: Guid;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  /** API path (relative to /api/v1) — download with PortalTicketsApi/PortalContentApi.download(). */
  url: string;
  createdAt: IsoDateTime;
}

export interface PortalArticleDto {
  id: Guid;
  slug: string;
  title: string;
  summary: string | null;
  /** Server-sanitised HTML. */
  bodyHtml: string;
  articleType: string;
  /** Locale actually served. */
  locale: string;
  primaryLocale: string;
  availableLocales: string[];
  /** The served translation is older than the current source article. */
  translationOutdated: boolean;
  /** The requested locale was unavailable; the primary language is served instead. */
  translationFallback: boolean;
  categoryId: Guid | null;
  categoryName: string | null;
  categorySlug: string | null;
  breadcrumb: PortalCategoryRefDto[];
  tags: string[];
  productName: string | null;
  publishedAt: IsoDateTime | null;
  updatedAt: IsoDateTime;
  viewCount: number;
  helpfulCount: number;
  notHelpfulCount: number;
  ratingAverage: number | null;
  related: PortalArticleSummaryDto[];
  attachments: PortalAttachmentDto[];
}

export interface PortalCategoryPageDto {
  category: PortalCategoryDto;
  breadcrumb: PortalCategoryRefDto[];
  articles: PortalArticleSummaryDto[];
}

export interface PortalSearchResultDto {
  query: string;
  articles: PortalArticleSummaryDto[];
  topics: TopicListItemDto[];
  total: number;
}

export interface PortalFlowSummaryDto {
  id: Guid;
  name: string;
  description: string | null;
  productName: string | null;
}

export interface PortalFlowDto {
  id: Guid;
  name: string;
  description: string | null;
  productName: string | null;
  definition: FlowDefinitionDto;
  /** Articles referenced by article nodes. */
  articles: PortalArticleSummaryDto[];
}

export interface PortalTicketStatsDto {
  open: number;
  awaitingYou: number;
  awaitingConfirmation: number;
}

export interface PortalHomeDto {
  categories: PortalCategoryDto[];
  popular: PortalArticleSummaryDto[];
  recent: PortalArticleSummaryDto[];
  announcements: PortalArticleSummaryDto[];
  flows: PortalFlowSummaryDto[];
  recentTopics: TopicListItemDto[];
  /** Only when signed in. */
  myTickets: PortalTicketStatsDto | null;
}

// ───── Assistant ─────

export interface PortalAssistantRequest {
  message: string;
  locale?: string | null;
}

export interface PortalCitationDto {
  index: number;
  sourceType: string;
  sourceId: Guid;
  title: string;
  url: string | null;
  snippet: string | null;
}

export interface PortalAssistantReplyDto {
  text: string;
  citations: PortalCitationDto[];
  suggestTicket: boolean;
  confidence: number;
  /** ai | kb */
  source: string;
}

// ───── Anonymous submissions ─────

export interface PortalAnonymousTicketRequest {
  name: string;
  email: string;
  phone?: string | null;
  subject: string;
  descriptionHtml?: string | null;
  departmentId?: Guid | null;
  categoryId?: Guid | null;
  productId?: Guid | null;
  customFields?: Record<string, JsonValue> | null;
  /** Honeypot — leave empty (hidden field). */
  website?: string | null;
  /** Epoch ms when the form was shown (time trap). */
  formStartedAt?: number | null;
}

export interface PortalSubmissionResultDto {
  accepted: boolean;
  displayNumber: string | null;
  message: string;
}

// ───── Tickets ─────

export type PortalTicketScope = 'own' | 'location' | 'organisation';

export interface PortalTicketQuery {
  scope?: PortalTicketScope | null;
  /** open | closed | awaiting | all */
  status?: string | null;
  q?: string | null;
  cursor?: string | null;
  limit?: number | null;
}

export interface PortalTicketScopeDto {
  max: PortalTicketScope;
  available: PortalTicketScope[];
}

export interface PortalTicketListItemDto {
  id: Guid;
  number: number;
  displayNumber: string;
  subject: string;
  /** Customer-facing status label. */
  status: string;
  statusCategory: string;
  isOpen: boolean;
  priority: string;
  createdAt: IsoDateTime;
  lastActivityAt: IsoDateTime;
  contactId: Guid | null;
  contactName: string | null;
  isMine: boolean;
  locationName: string | null;
  productName: string | null;
  awaitingConfirmation: boolean;
  awaitingYourReply: boolean;
}

export interface PortalFieldValueDto {
  key: string;
  label: string;
  value: string | null;
}

export interface PortalTicketActionsDto {
  reply: boolean;
  reopen: boolean;
  confirm: boolean;
  rate: boolean;
}

export interface PortalTicketDetailDto {
  id: Guid;
  number: number;
  displayNumber: string;
  subject: string;
  descriptionHtml: string | null;
  status: string;
  statusCategory: string;
  isOpen: boolean;
  priority: string;
  channel: string;
  createdAt: IsoDateTime;
  lastActivityAt: IsoDateTime;
  resolvedAt: IsoDateTime | null;
  closedAt: IsoDateTime | null;
  contactId: Guid | null;
  contactName: string | null;
  isMine: boolean;
  customerName: string | null;
  locationName: string | null;
  departmentName: string | null;
  categoryName: string | null;
  productName: string | null;
  installationName: string | null;
  fields: PortalFieldValueDto[];
  attachments: PortalAttachmentDto[];
  awaitingConfirmation: boolean;
  csatScore: number | null;
  csatComment: string | null;
  actions: PortalTicketActionsDto;
  /** Last moment the customer may reopen (resolved/closed tickets). */
  reopenUntil: IsoDateTime | null;
}

export interface PortalMessageDto {
  id: Guid;
  seq: number;
  kind: 'customer' | 'agent' | 'system';
  authorName: string | null;
  isMine: boolean;
  bodyHtml: string;
  createdAt: IsoDateTime;
  attachments: PortalAttachmentDto[];
}

export interface PortalOptionDto {
  id: Guid;
  name: string;
  description: string | null;
}

export interface PortalCategoryOptionDto {
  id: Guid;
  parentId: Guid | null;
  name: string;
  departmentId: Guid | null;
}

export interface PortalInstallationOptionDto {
  id: Guid;
  name: string;
  productId: Guid;
  productName: string;
  version: string | null;
  locationName: string | null;
  environment: string;
}

export interface PortalFieldDefDto {
  key: string;
  label: string;
  /** Text, TextArea, Number, Decimal, Currency, Date, DateTime, Checkbox, Picklist, MultiPicklist, Url, Email, Phone */
  fieldType: string;
  required: boolean;
  editable: boolean;
  helpText: string | null;
  options: OptionDto[];
  /** Departments where the field applies; empty = all. */
  departmentIds: Guid[];
  defaultValue: string | null;
  min: number | null;
  max: number | null;
  maxLength: number | null;
}

/** Field keys: departmentId, categoryId, productId, installationId, priority or "cf:<key>". */
export interface PortalFormSectionDto {
  title: string;
  columns: number;
  fields: string[];
}

export interface PortalTicketFormDto {
  departments: PortalOptionDto[];
  categories: PortalCategoryOptionDto[];
  products: PortalOptionDto[];
  installations: PortalInstallationOptionDto[];
  fields: PortalFieldDefDto[];
  sections: PortalFormSectionDto[];
  priorities: string[];
  maxAttachmentMb: number;
}

export interface PortalCreateTicketRequest {
  subject: string;
  descriptionHtml?: string | null;
  departmentId?: Guid | null;
  categoryId?: Guid | null;
  subcategoryId?: Guid | null;
  productId?: Guid | null;
  installationId?: Guid | null;
  priority?: string | null;
  customFields?: Record<string, JsonValue> | null;
  attachmentFileIds?: Guid[] | null;
  clientRequestId?: string | null;
  sourceFlowId?: Guid | null;
}

export interface PortalTicketCreatedDto {
  id: Guid;
  number: number;
  displayNumber: string;
}

export interface PortalReplyRequest {
  bodyHtml: string;
  attachmentFileIds?: Guid[] | null;
  clientRequestId?: string | null;
}

// ───── Account ─────

export interface PortalProfileDto {
  id: Guid;
  fullName: string;
  email: string | null;
  phone: string | null;
  mobile: string | null;
  jobTitle: string | null;
  preferredLanguage: string;
  preferredChannel: string | null;
  timeZone: string | null;
  customerName: string | null;
  locationName: string | null;
  accessScope: string;
  canViewContracts: boolean;
  lastLoginAt: IsoDateTime | null;
}

export interface UpdatePortalProfileRequest {
  fullName: string;
  phone: string | null;
  mobile: string | null;
  jobTitle: string | null;
  preferredLanguage: string;
  preferredChannel: string | null;
  timeZone: string | null;
}

export interface PortalConsentDto {
  /** Email | WhatsApp | Sms */
  channel: string;
  /** Transactional | Marketing | Surveys */
  purpose: string;
  optedIn: boolean;
}

export interface PortalPreferencesDto {
  preferredChannel: string | null;
  preferredLanguage: string;
  consents: PortalConsentDto[];
}

export interface UpdatePortalPreferencesRequest {
  preferredChannel: string | null;
  preferredLanguage: string | null;
  consents: PortalConsentDto[];
}

export interface ChangePortalPasswordRequest {
  currentPassword: string;
  newPassword: string;
}

export interface PortalEntitlementDto {
  id: Guid;
  name: string;
  type: string;
  productName: string | null;
  installationName: string | null;
  incidentsAllowed: number | null;
  incidentsUsed: number;
  hoursAllowed: number | null;
  hoursUsed: number;
  startDate: IsoDate;
  endDate: IsoDate;
  isActive: boolean;
}

export interface PortalContractDto {
  id: Guid;
  number: string;
  name: string;
  type: string;
  status: string;
  startDate: IsoDate;
  endDate: IsoDate;
  daysRemaining: number;
  prepaidHours: number | null;
  consumedHours: number;
  consumedPercent: number | null;
  autoRenew: boolean;
  renewalDate: IsoDate | null;
  coveredChannels: string[];
  onsite: boolean;
  entitlements: PortalEntitlementDto[];
}

export interface PortalLicenceDto {
  id: Guid;
  productName: string;
  edition: string | null;
  seats: number;
  keyMasked: string;
  issuedOn: IsoDate;
  expiresOn: IsoDate | null;
  status: string;
  installationName: string | null;
}

export interface PortalInstallationDto {
  id: Guid;
  name: string;
  code: string | null;
  productName: string;
  version: string | null;
  environment: string;
  deploymentMode: string;
  locationName: string | null;
  status: string;
  goLiveOn: IsoDate | null;
  userCount: number | null;
}

export interface PortalServiceActivityDto {
  id: Guid;
  type: string;
  title: string;
  description: string | null;
  scheduledStart: IsoDateTime;
  scheduledEnd: IsoDateTime;
  status: string;
  locationName: string | null;
  installationName: string | null;
  outcome: string | null;
}

export interface PortalAccountOverviewDto {
  customerName: string;
  tier: string | null;
  contracts: PortalContractDto[];
  licences: PortalLicenceDto[];
  installations: PortalInstallationDto[];
  activities: PortalServiceActivityDto[];
}

export interface PortalPersonDto {
  name: string;
  email: string | null;
  phone: string | null;
  title: string | null;
}

export interface PortalDepartmentContactDto {
  name: string;
  description: string | null;
  email: string | null;
}

export interface PortalSupportInfoDto {
  supportEmail: string | null;
  supportPhone: string | null;
  accountManager: PortalPersonDto | null;
  departments: PortalDepartmentContactDto[];
}

/** Realtime events on /hubs/portal. */
export interface PortalTicketThreadEvent {
  ticketId: Guid;
  threadId: Guid;
  seq: number;
  kind: string;
  visibility: string;
}

export interface PortalTicketChangedEvent {
  ticketId: Guid;
  number: number;
}
