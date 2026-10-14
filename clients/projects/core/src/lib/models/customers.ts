/**
 * Customers module wire types — mirror SupportDesk.Contracts.Customers (camelCase, enums as strings,
 * UTC ISO-8601 timestamps, DateOnly as "YYYY-MM-DD", GUIDs as strings).
 */
import { CountDto, Guid, IdName, IsoDateTime, JsonValue } from './common';
import { AssetDto, ContractListItemDto, EntitlementDto, InstallationListItemDto, LicenceDto } from './products';

export const CUSTOMER_STATUSES = ['Active', 'Inactive', 'Prospect', 'OnHold'] as const;
export type CustomerStatus = (typeof CUSTOMER_STATUSES)[number];

export const PORTAL_ACCESS_SCOPES = ['None', 'Own', 'Location', 'Organisation'] as const;
export type PortalAccessScope = (typeof PORTAL_ACCESS_SCOPES)[number];

export const CONSENT_PURPOSES = ['Transactional', 'Marketing', 'Surveys'] as const;
export type ConsentPurpose = (typeof CONSENT_PURPOSES)[number];

export const CONSENT_STATUSES = ['Unknown', 'OptedIn', 'OptedOut'] as const;
export type ConsentStatus = (typeof CONSENT_STATUSES)[number];

export const SERVICE_ACTIVITY_TYPES = ['OnsiteVisit', 'RemoteSession', 'Training', 'PreventiveMaintenance', 'Upgrade', 'Call', 'Meeting'] as const;
export type ServiceActivityType = (typeof SERVICE_ACTIVITY_TYPES)[number];

export const SERVICE_ACTIVITY_STATUSES = ['Scheduled', 'InProgress', 'Completed', 'Cancelled', 'Missed'] as const;
export type ServiceActivityStatus = (typeof SERVICE_ACTIVITY_STATUSES)[number];

export type CustomerSort = 'name' | 'code' | 'created' | 'updated';

export interface CustomerQuery {
  q?: string | null;
  status?: string | string[] | null;
  tier?: string | string[] | null;
  region?: string | string[] | null;
  industry?: string | string[] | null;
  category?: string | null;
  groupId?: Guid | null;
  accountManagerId?: Guid | null;
  parentId?: Guid | null;
  topLevelOnly?: boolean | null;
  mine?: boolean | null;
  hasOpenTickets?: boolean | null;
  sort?: CustomerSort | null;
  dir?: 'asc' | 'desc' | null;
  cursor?: string | null;
  limit?: number | null;
  includeTotal?: boolean | null;
}

export interface CustomerListItemDto {
  id: Guid;
  name: string;
  code: string | null;
  status: CustomerStatus;
  tier: string;
  category: string | null;
  industry: string | null;
  serviceRegion: string | null;
  customerGroupId: Guid | null;
  groupName: string | null;
  accountManagerUserId: Guid | null;
  accountManagerName: string | null;
  parentCustomerId: Guid | null;
  parentName: string | null;
  childCount: number;
  contactCount: number;
  openTickets: number;
  erpCode: string | null;
  createdAt: IsoDateTime;
  updatedAt: IsoDateTime;
}

export interface CommunicationPreferencesDto {
  email: boolean;
  phone: boolean;
  sms: boolean;
  whatsApp: boolean;
  preferredChannel: string | null;
  notes: string | null;
}

export interface CustomerChildDto {
  id: Guid;
  name: string;
  code: string | null;
  tier: string;
  status: CustomerStatus;
  openTickets: number;
  childCount: number;
}

export interface CustomerRollupDto {
  descendants: number;
  contacts: number;
  locations: number;
  openTickets: number;
  installations: number;
  activeContracts: number;
}

export interface CustomerPermissionsDto {
  edit: boolean;
  delete: boolean;
  manageContacts: boolean;
  mergeContacts: boolean;
  viewContracts: boolean;
  manageContracts: boolean;
  viewProducts: boolean;
  manageProducts: boolean;
  createTickets: boolean;
}

export interface CustomerDetailDto {
  id: Guid;
  name: string;
  code: string | null;
  status: CustomerStatus;
  tier: string;
  category: string | null;
  industry: string | null;
  serviceRegion: string | null;
  customerGroupId: Guid | null;
  groupName: string | null;
  accountManagerUserId: Guid | null;
  accountManagerName: string | null;
  parentCustomerId: Guid | null;
  parentName: string | null;
  ancestors: IdName[];
  children: CustomerChildDto[];
  rollup: CustomerRollupDto;
  preferredLanguage: string;
  timeZone: string | null;
  website: string | null;
  phone: string | null;
  email: string | null;
  domains: string[];
  communicationPreferences: CommunicationPreferencesDto;
  notes: string | null;
  customFields: Record<string, JsonValue>;
  erpCustomerId: Guid | null;
  erpCode: string | null;
  erpSyncedAt: IsoDateTime | null;
  isDeleted: boolean;
  createdAt: IsoDateTime;
  updatedAt: IsoDateTime;
  rowVersion: string;
  permissions: CustomerPermissionsDto;
}

export interface SaveCustomerRequest {
  name: string;
  code?: string | null;
  status?: CustomerStatus | null;
  parentCustomerId?: Guid | null;
  customerGroupId?: Guid | null;
  category?: string | null;
  industry?: string | null;
  tier?: string | null;
  accountManagerUserId?: Guid | null;
  serviceRegion?: string | null;
  preferredLanguage?: string | null;
  timeZone?: string | null;
  website?: string | null;
  phone?: string | null;
  email?: string | null;
  domains?: string[] | null;
  communicationPreferences?: CommunicationPreferencesDto | null;
  notes?: string | null;
  customFields?: Record<string, JsonValue> | null;
  erpCustomerId?: Guid | null;
  erpCode?: string | null;
  rowVersion?: string | null;
}

export interface CustomerFacetsDto {
  tiers: string[];
  regions: string[];
  industries: string[];
  categories: string[];
  groups: IdName[];
  accountManagers: IdName[];
  statuses: CustomerStatus[];
}

export interface CustomerGroupDto {
  id: Guid;
  name: string;
  description: string | null;
  customerCount: number;
}

export interface SaveCustomerGroupRequest {
  name: string;
  description?: string | null;
}

export interface LocationDto {
  id: Guid;
  customerId: Guid;
  name: string;
  code: string | null;
  addressLine1: string | null;
  addressLine2: string | null;
  city: string | null;
  state: string | null;
  country: string | null;
  postalCode: string | null;
  phone: string | null;
  timeZone: string | null;
  serviceRegion: string | null;
  isHeadOffice: boolean;
  isActive: boolean;
  erpLocationId: Guid | null;
  contactCount: number;
  installationCount: number;
}

export interface SaveLocationRequest {
  name: string;
  code?: string | null;
  addressLine1?: string | null;
  addressLine2?: string | null;
  city?: string | null;
  state?: string | null;
  country?: string | null;
  postalCode?: string | null;
  phone?: string | null;
  timeZone?: string | null;
  serviceRegion?: string | null;
  isHeadOffice: boolean;
  isActive: boolean;
}

export interface CustomerDepartmentDto {
  id: Guid;
  customerId: Guid;
  name: string;
  contactCount: number;
}

// ───── Customer 360 ─────

export interface Customer360KpisDto {
  openTickets: number;
  overdueTickets: number;
  escalatedTickets: number;
  ticketsLast90Days: number;
  resolvedLast90Days: number;
  slaBreachesLast90Days: number;
  slaBreachesOpen: number;
  csatAverage90Days: number | null;
  csatResponses90Days: number;
  avgResolutionHours90Days: number | null;
  activeContracts: number;
  contractsExpiringSoon: number;
  installations: number;
  contacts: number;
  upcomingActivities: number;
  lastTicketAt: IsoDateTime | null;
}

export interface CsatPointDto {
  month: string;
  average: number | null;
  responses: number;
  positive: number;
  negative: number;
}

export interface TicketVolumePointDto {
  month: string;
  created: number;
  resolved: number;
}

export interface Customer360TicketDto {
  id: Guid;
  number: number;
  displayNumber: string;
  subject: string;
  priority: string;
  status: string;
  statusCategory: string;
  isEscalated: boolean;
  slaState: string;
  sentiment: string;
  assigneeName: string | null;
  createdAt: IsoDateTime;
  reasons: string[];
}

export interface ConversationExcerptDto {
  ticketId: Guid;
  displayNumber: string;
  subject: string;
  threadId: Guid;
  kind: string;
  channel: string;
  direction: string;
  authorName: string | null;
  excerpt: string;
  createdAt: IsoDateTime;
}

export interface Customer360Dto {
  customer: CustomerDetailDto;
  kpis: Customer360KpisDto;
  openByPriority: CountDto[];
  csatTrend: CsatPointDto[];
  ticketTrend: TicketVolumePointDto[];
  outstandingComplaints: Customer360TicketDto[];
  recentConversations: ConversationExcerptDto[];
  contracts: ContractListItemDto[];
  entitlements: EntitlementDto[];
  installations: InstallationListItemDto[];
  assets: AssetDto[];
  licences: LicenceDto[];
  upcomingActivities: ServiceActivityDto[];
  keyContacts: ContactListItemDto[];
  locations: LocationDto[];
  erpConnected: boolean;
}

// ───── Service activities ─────

export interface ServiceActivityQuery {
  from?: IsoDateTime | null;
  to?: IsoDateTime | null;
  customerId?: Guid | null;
  assignedUserId?: Guid | null;
  status?: string | null;
  type?: string | null;
  limit?: number | null;
}

export interface ServiceActivityDto {
  id: Guid;
  customerId: Guid;
  customerName: string;
  locationId: Guid | null;
  locationName: string | null;
  ticketId: Guid | null;
  ticketNumber: string | null;
  installationId: Guid | null;
  installationName: string | null;
  contractId: Guid | null;
  contractNumber: string | null;
  type: ServiceActivityType;
  title: string;
  description: string | null;
  scheduledStart: IsoDateTime;
  scheduledEnd: IsoDateTime;
  assignedUserId: Guid | null;
  assignedUserName: string | null;
  status: ServiceActivityStatus;
  outcome: string | null;
  visibleToCustomer: boolean;
  createdAt: IsoDateTime;
}

export interface SaveServiceActivityRequest {
  customerId: Guid;
  locationId?: Guid | null;
  ticketId?: Guid | null;
  installationId?: Guid | null;
  contractId?: Guid | null;
  type: ServiceActivityType;
  title: string;
  description?: string | null;
  scheduledStart: IsoDateTime;
  scheduledEnd: IsoDateTime;
  assignedUserId?: Guid | null;
  status?: ServiceActivityStatus | null;
  outcome?: string | null;
  visibleToCustomer: boolean;
}

// ───── Contacts ─────

export interface ContactQuery {
  q?: string | null;
  customerId?: Guid | null;
  locationId?: Guid | null;
  status?: string | null;
  isVip?: boolean | null;
  portalAccess?: string | null;
  sort?: 'name' | 'created' | 'updated' | null;
  dir?: 'asc' | 'desc' | null;
  cursor?: string | null;
  limit?: number | null;
  includeTotal?: boolean | null;
}

export interface ContactListItemDto {
  id: Guid;
  fullName: string;
  email: string | null;
  phone: string | null;
  jobTitle: string | null;
  customerId: Guid | null;
  customerName: string | null;
  locationId: Guid | null;
  locationName: string | null;
  isVip: boolean;
  isPrimary: boolean;
  status: string;
  portalAccess: PortalAccessScope;
  hasPortalPassword: boolean;
  openTickets: number;
  createdAt: IsoDateTime;
  updatedAt: IsoDateTime;
}

export interface ContactIdentityDto {
  id: Guid;
  channel: string;
  identifier: string;
  isVerified: boolean;
  verifiedAt: IsoDateTime | null;
  createdAt: IsoDateTime;
}

export interface ContactConsentDto {
  channel: string;
  purpose: ConsentPurpose;
  status: ConsentStatus;
  source: string | null;
  recordedAt: IsoDateTime | null;
}

export interface ContactStatsDto {
  openTickets: number;
  totalTickets: number;
  lastTicketAt: IsoDateTime | null;
  csatAverage: number | null;
  conversations: number;
}

export interface ContactDetailDto {
  id: Guid;
  fullName: string;
  firstName: string | null;
  lastName: string | null;
  email: string | null;
  phone: string | null;
  mobile: string | null;
  whatsAppNumber: string | null;
  jobTitle: string | null;
  customerId: Guid | null;
  customerName: string | null;
  locationId: Guid | null;
  locationName: string | null;
  customerDepartmentId: Guid | null;
  customerDepartmentName: string | null;
  preferredLanguage: string;
  preferredChannel: string | null;
  timeZone: string | null;
  isVip: boolean;
  isPrimary: boolean;
  status: string;
  mergedIntoContactId: Guid | null;
  portalAccess: PortalAccessScope;
  hasPortalPassword: boolean;
  portalLastLoginAt: IsoDateTime | null;
  portalLocked: boolean;
  emailVerifiedAt: IsoDateTime | null;
  phoneVerifiedAt: IsoDateTime | null;
  customFields: Record<string, JsonValue>;
  erpContactId: Guid | null;
  identities: ContactIdentityDto[];
  consents: ContactConsentDto[];
  stats: ContactStatsDto;
  createdAt: IsoDateTime;
  updatedAt: IsoDateTime;
  rowVersion: string;
  permissions: { edit: boolean; merge: boolean; managePortal: boolean };
}

export interface SaveContactRequest {
  fullName?: string | null;
  firstName?: string | null;
  lastName?: string | null;
  email?: string | null;
  phone?: string | null;
  mobile?: string | null;
  whatsAppNumber?: string | null;
  jobTitle?: string | null;
  customerId?: Guid | null;
  locationId?: Guid | null;
  customerDepartmentId?: Guid | null;
  preferredLanguage?: string | null;
  preferredChannel?: string | null;
  timeZone?: string | null;
  isVip: boolean;
  isPrimary: boolean;
  status?: string | null;
  portalAccess?: PortalAccessScope | null;
  customFields?: Record<string, JsonValue> | null;
  rowVersion?: string | null;
}

export interface PortalInviteResultDto {
  mode: 'invite' | 'reset';
  sentTo: string;
  loginUrl: string;
  expiresAt: IsoDateTime;
  demoCode: string | null;
}

export interface DuplicateCandidateDto {
  contact: ContactListItemDto;
  score: number;
  confidence: 'High' | 'Medium' | 'Low';
  reasons: string[];
}

export interface DuplicatePairDto {
  first: ContactListItemDto;
  second: ContactListItemDto;
  score: number;
  confidence: 'High' | 'Medium' | 'Low';
  reasons: string[];
}

export interface MergeContactsRequest {
  survivorId: Guid;
  mergedId: Guid;
  preferMergedFields?: string[] | null;
}

export interface ContactMergeResultDto {
  survivorId: Guid;
  mergedId: Guid;
  alreadyMerged: boolean;
  tickets: number;
  threads: number;
  conversations: number;
  messages: number;
  identities: number;
  consents: number;
  satisfactionResponses: number;
  otherRecords: number;
  fieldsCopied: string[];
  mergeLogId: Guid | null;
}

export interface ContactMergeLogDto {
  id: Guid;
  survivorContactId: Guid;
  mergedContactId: Guid;
  mergedContactName: string | null;
  mergedById: Guid | null;
  mergedByName: string | null;
  mergedAt: IsoDateTime;
  snapshot: JsonValue;
}
