/**
 * Administration console DTOs (mirror SupportDesk.Contracts.Administration): roles & permissions, ticket configuration,
 * custom fields and form layouts, brands & portals, notification defaults and e-mail templates, security, API credentials.
 */
import { Guid, IdName, IsoDateTime, JsonValue, OptionDto } from './common';

// ───────────── Roles & permissions ─────────────

export type AdminTicketScope = 'Own' | 'Team' | 'Departments' | 'All';
export type AdminCustomerScope = 'AccountManaged' | 'All';

export interface AdminRoleListItemDto {
  id: Guid;
  name: string;
  description: string | null;
  isSystem: boolean;
  isLightAgent: boolean;
  ticketScope: AdminTicketScope;
  customerScope: AdminCustomerScope;
  permissionCount: number;
  userCount: number;
  /** Grants admin.roles: holders count as administrators (last-administrator protection). */
  isAdministrative: boolean;
}

export interface AdminRoleMemberDto {
  userId: Guid;
  name: string;
  email: string;
  isActive: boolean;
  otherRoles: string[];
}

export interface AdminRoleDto {
  id: Guid;
  name: string;
  description: string | null;
  isSystem: boolean;
  isLightAgent: boolean;
  ticketScope: AdminTicketScope;
  customerScope: AdminCustomerScope;
  permissions: string[];
  members: AdminRoleMemberDto[];
  updatedAt: IsoDateTime;
  rowVersion: string;
}

export interface SaveRoleRequest {
  name: string;
  description: string | null;
  ticketScope: AdminTicketScope;
  customerScope: AdminCustomerScope;
  isLightAgent: boolean;
  permissions: string[];
  rowVersion?: string | null;
}

export interface CloneRoleRequest {
  name: string;
  description?: string | null;
}

export interface PermissionItemDto {
  code: string;
  description: string;
  sensitive: boolean;
}

export interface PermissionGroupDto {
  module: string;
  permissions: PermissionItemDto[];
}

export interface PermissionCatalogDto {
  groups: PermissionGroupDto[];
  ticketScopes: OptionDto[];
  customerScopes: OptionDto[];
  /** Codes the caller may grant (their own permissions). */
  grantable: string[];
  grantableTicketScope: AdminTicketScope;
  grantableCustomerScope: AdminCustomerScope;
  customRolesAllowed: boolean;
}

export interface SetUserRolesRequest {
  roleIds: Guid[];
}

export interface UserRolesDto {
  userId: Guid;
  name: string;
  email: string;
  roles: IdName[];
}

// ───────────── Ticket configuration ─────────────

export type AdminStatusCategory = 'Open' | 'Assigned' | 'InProgress' | 'Waiting' | 'Resolved' | 'Closed';

export interface AdminStatusDto {
  id: Guid;
  name: string;
  category: AdminStatusCategory;
  color: string;
  sortOrder: number;
  isDefault: boolean;
  pausesSla: boolean;
  isSystem: boolean;
  isActive: boolean;
  customerLabel: string | null;
  allowedNext: Guid[];
  substatuses: string[];
  ticketCount: number;
  rowVersion: string;
}

export interface SaveStatusRequest {
  name: string;
  category: AdminStatusCategory;
  color: string;
  isDefault: boolean;
  pausesSla: boolean;
  isActive: boolean;
  customerLabel: string | null;
  allowedNext: Guid[];
  substatuses: string[];
  rowVersion?: string | null;
}

export interface ReorderRequest {
  ids: Guid[];
}

export interface AdminCategoryDto {
  id: Guid;
  name: string;
  parentId: Guid | null;
  departmentId: Guid | null;
  visibleInPortal: boolean;
  isActive: boolean;
  sortOrder: number;
  ticketCount: number;
}

export interface SaveCategoryRequest {
  name: string;
  parentId: Guid | null;
  departmentId: Guid | null;
  visibleInPortal: boolean;
  isActive: boolean;
}

export interface AdminIssueTypeDto {
  id: Guid;
  name: string;
  description: string | null;
  isActive: boolean;
  sortOrder: number;
  ticketCount: number;
}

export interface SaveIssueTypeRequest {
  name: string;
  description: string | null;
  isActive: boolean;
}

export interface DisplayOptionDto {
  value: string;
  label: string;
  color: string;
}

export interface TicketDisplayDto {
  priorities: DisplayOptionDto[];
  severities: DisplayOptionDto[];
}

export interface TicketLifecycleDto {
  numberPrefix: string;
  /** Read-only: the number the next ticket will get. */
  nextNumber: number;
  reopenWindowDays: number;
  allowCustomerReopen: boolean;
  autoCloseResolvedAfterDays: number;
  requireCustomerConfirmation: boolean;
  requireResolutionCode: boolean;
  softLockMinutes: number;
  allowMergeAcrossCustomers: boolean;
  allowMergeAcrossDepartments: boolean;
  maxMergeSources: number;
  allowSplit: boolean;
  csatEnabled: boolean;
  csatExpiryDays: number;
  autoAcknowledge: boolean;
  version: number;
}

export type SaveTicketLifecycleRequest = Omit<TicketLifecycleDto, 'nextNumber' | 'version'>;

export type AdminCustomFieldType =
  | 'Text'
  | 'TextArea'
  | 'Number'
  | 'Decimal'
  | 'Currency'
  | 'Date'
  | 'DateTime'
  | 'Checkbox'
  | 'Picklist'
  | 'MultiPicklist'
  | 'Url'
  | 'Email'
  | 'Phone'
  | 'Lookup';

export type AdminFieldEntity = 'Ticket' | 'Contact' | 'Customer';

export interface AdminCustomFieldDto {
  id: Guid;
  entity: AdminFieldEntity;
  key: string;
  label: string;
  fieldType: AdminCustomFieldType;
  /** Picklists: [{ value, label }]; Lookup: { entity }. */
  options: JsonValue;
  isRequired: boolean;
  defaultValue: string | null;
  helpText: string | null;
  validationRegex: string | null;
  minValue: number | null;
  maxValue: number | null;
  maxLength: number | null;
  visibleInPortal: boolean;
  editableInPortal: boolean;
  departmentIds: Guid[];
  requiredForStatusIds: Guid[];
  sortOrder: number;
  isActive: boolean;
  usedInLayouts: number;
  rowVersion: string;
}

export interface SaveCustomFieldRequest {
  entity: AdminFieldEntity;
  key: string;
  label: string;
  fieldType: AdminCustomFieldType;
  options: JsonValue | null;
  isRequired: boolean;
  defaultValue: string | null;
  helpText: string | null;
  validationRegex: string | null;
  minValue: number | null;
  maxValue: number | null;
  maxLength: number | null;
  visibleInPortal: boolean;
  editableInPortal: boolean;
  departmentIds: Guid[];
  requiredForStatusIds: Guid[];
  isActive: boolean;
  rowVersion?: string | null;
}

export type AdminLayoutKind = 'AgentForm' | 'PortalForm';

/** One field placed on a form layout. */
export interface LayoutFieldRef {
  key: string;
  required: boolean;
}

/** One section of a form layout. */
export interface LayoutSectionDef {
  title: string;
  columns: number;
  fields: LayoutFieldRef[];
}

export interface AdminLayoutDto {
  id: Guid;
  entity: 'Ticket';
  name: string;
  kind: AdminLayoutKind;
  departmentId: Guid | null;
  brandId: Guid | null;
  channel: string | null;
  sections: LayoutSectionDef[];
  isDefault: boolean;
  isActive: boolean;
  updatedAt: IsoDateTime;
  rowVersion: string;
}

export interface SaveLayoutRequest {
  entity: 'Ticket';
  name: string;
  kind: AdminLayoutKind;
  departmentId: Guid | null;
  brandId: Guid | null;
  channel: string | null;
  sections: LayoutSectionDef[];
  isDefault: boolean;
  isActive: boolean;
  rowVersion?: string | null;
}

export interface LayoutFieldOptionDto {
  key: string;
  label: string;
  type: string;
  custom: boolean;
  agentForm: boolean;
  portalForm: boolean;
  alwaysRequired: boolean;
}

// ───────────── Brands & portals ─────────────

export type AdminPortalAuthMode = 'Local' | 'Sso' | 'LocalAndSso';

export interface AdminBrandListItemDto {
  id: Guid;
  name: string;
  slug: string;
  customDomain: string | null;
  logoUrl: string | null;
  primaryColor: string;
  accentColor: string;
  isDefault: boolean;
  portalEnabled: boolean;
  portalUrl: string;
}

export interface BrandDnsDto {
  recordType: string;
  host: string;
  target: string;
  instructions: string;
}

export interface AdminBrandDto {
  id: Guid;
  name: string;
  slug: string;
  customDomain: string | null;
  logoUrl: string | null;
  faviconUrl: string | null;
  primaryColor: string;
  accentColor: string;
  supportEmail: string | null;
  supportPhone: string | null;
  portalTitle: string;
  portalWelcome: string | null;
  footerHtml: string | null;
  emailFooterHtml: string | null;
  defaultDepartmentId: Guid | null;
  defaultLocale: string;
  locales: string[];
  isDefault: boolean;
  portalEnabled: boolean;
  knowledgeBaseEnabled: boolean;
  communityEnabled: boolean;
  chatEnabled: boolean;
  aiAssistantEnabled: boolean;
  allowAnonymousTickets: boolean;
  allowSelfRegistration: boolean;
  portalAuthMode: AdminPortalAuthMode;
  portalUrl: string;
  dns: BrandDnsDto | null;
  updatedAt: IsoDateTime;
  rowVersion: string;
}

export interface SaveBrandRequest {
  name: string;
  slug: string;
  customDomain: string | null;
  primaryColor: string;
  accentColor: string;
  supportEmail: string | null;
  supportPhone: string | null;
  portalTitle: string;
  portalWelcome: string | null;
  footerHtml: string | null;
  emailFooterHtml: string | null;
  defaultDepartmentId: Guid | null;
  defaultLocale: string;
  locales: string[];
  portalEnabled: boolean;
  knowledgeBaseEnabled: boolean;
  communityEnabled: boolean;
  chatEnabled: boolean;
  aiAssistantEnabled: boolean;
  allowAnonymousTickets: boolean;
  allowSelfRegistration: boolean;
  portalAuthMode: AdminPortalAuthMode;
  rowVersion?: string | null;
}

export interface DomainCheckResultDto {
  domain: string;
  resolves: boolean;
  pointsToPortal: boolean;
  addresses: string[];
  portalAddresses: string[];
  message: string;
}

// ───────────── Notifications ─────────────

export interface NotificationEventDefaultDto {
  eventType: string;
  label: string;
  description: string;
  inApp: boolean;
  email: boolean;
  push: boolean;
  digest: boolean;
}

export type AdminDigestFrequency = 'Hourly' | 'Daily';

export interface NotificationPolicyDto {
  events: NotificationEventDefaultDto[];
  quietHoursStart: string | null;
  quietHoursEnd: string | null;
  timeZone: string;
  digestFrequency: AdminDigestFrequency;
  dailyDigestAt: string;
  version: number;
}

export type SaveNotificationPolicyRequest = Omit<NotificationPolicyDto, 'version'>;

export interface TemplatePlaceholderDto {
  name: string;
  description: string;
  sample: string;
}

export interface NotificationTemplateDto {
  key: string;
  name: string;
  audience: 'Customer' | 'Agent';
  description: string;
  subject: string;
  bodyHtml: string;
  isCustomized: boolean;
  isActive: boolean;
  placeholders: TemplatePlaceholderDto[];
  defaultSubject: string;
  defaultBodyHtml: string;
  updatedAt: IsoDateTime | null;
  rowVersion: string | null;
}

export interface SaveNotificationTemplateRequest {
  subject: string;
  bodyHtml: string;
  isActive: boolean;
  rowVersion?: string | null;
}

export interface PreviewTemplateRequest {
  subject?: string | null;
  bodyHtml?: string | null;
  brandId?: Guid | null;
}

export interface TemplatePreviewDto {
  subject: string;
  bodyHtml: string;
  unknownPlaceholders: string[];
}

// ───────────── Security ─────────────

export type AdminMfaPolicy = 'Optional' | 'RequiredForAdmins' | 'RequiredForAll';

export interface PasswordPolicyDto {
  minLength: number;
  requireUpper: boolean;
  requireLower: boolean;
  requireDigit: boolean;
  requireSymbol: boolean;
  historyCount: number;
  expiryDays: number;
  maxFailedAttempts: number;
  lockoutMinutes: number;
}

export interface SessionPolicyDto {
  accessTokenMinutes: number;
  idleTimeoutMinutes: number;
  absoluteLifetimeHours: number;
  maxConcurrentSessions: number;
}

export interface IpAllowListDto {
  enabled: boolean;
  ranges: string[];
}

export interface SecurityPolicyDto {
  mfaPolicy: AdminMfaPolicy;
  password: PasswordPolicyDto;
  session: SessionPolicyDto;
  ipAllowList: IpAllowListDto;
  /** The caller's address as the API sees it. */
  currentIp: string | null;
  version: number;
}

export type SaveSecurityPolicyRequest = Omit<SecurityPolicyDto, 'currentIp' | 'version'>;

export interface SsoConfigDto {
  enabled: boolean;
  authority: string | null;
  clientId: string | null;
  hasClientSecret: boolean;
  scopes: string;
  emailClaim: string;
  nameClaim: string;
  autoProvision: boolean;
  defaultRoleId: Guid | null;
  enforceSso: boolean;
  callbackUrl: string;
  startUrl: string;
  planIncludesSso: boolean;
}

export interface SaveSsoConfigRequest {
  enabled: boolean;
  authority: string | null;
  clientId: string | null;
  /** Null keeps the stored secret. */
  clientSecret: string | null;
  clearClientSecret: boolean;
  scopes: string;
  emailClaim: string;
  nameClaim: string;
  autoProvision: boolean;
  defaultRoleId: Guid | null;
  enforceSso: boolean;
}

export interface SsoTestResultDto {
  success: boolean;
  issuer: string | null;
  authorizationEndpoint: string | null;
  tokenEndpoint: string | null;
  signingKeys: number;
  message: string;
}

export interface AdminSessionDto {
  id: Guid;
  userId: Guid;
  userName: string;
  email: string | null;
  clientKind: string | null;
  ipAddress: string | null;
  userAgent: string | null;
  createdAt: IsoDateTime;
  lastSeenAt: IsoDateTime;
  expiresAt: IsoDateTime;
  mfaSatisfied: boolean;
  current: boolean;
}

export interface RevokeSessionsResultDto {
  revoked: number;
}

// ───────────── API credentials ─────────────

export interface ApiScopeDto {
  scope: string;
  description: string;
  permissions: string[];
  /** The caller holds every permission the scope grants. */
  grantable: boolean;
}

export type AdminCredentialStatus = 'Active' | 'Expired' | 'Revoked';

export interface AdminApiKeyDto {
  id: Guid;
  name: string;
  prefix: string;
  scopes: string[];
  actAsUserId: Guid | null;
  actAsUserName: string | null;
  status: AdminCredentialStatus;
  expiresAt: IsoDateTime | null;
  lastUsedAt: IsoDateTime | null;
  createdAt: IsoDateTime;
  createdBy: string | null;
  rateLimitPerMinute: number;
}

export interface CreateApiKeyRequest {
  name: string;
  scopes: string[];
  actAsUserId: Guid | null;
  expiresAt: IsoDateTime | null;
}

export interface ApiKeySecretDto {
  key: AdminApiKeyDto;
  /** The full key — shown once. */
  secret: string;
}

export interface AdminOAuthClientDto {
  id: Guid;
  name: string;
  clientId: string;
  scopes: string[];
  status: AdminCredentialStatus;
  expiresAt: IsoDateTime | null;
  lastUsedAt: IsoDateTime | null;
  createdAt: IsoDateTime;
  createdBy: string | null;
  rateLimitPerMinute: number;
}

export interface CreateOAuthClientRequest {
  name: string;
  scopes: string[];
  expiresAt: IsoDateTime | null;
}

export interface OAuthClientSecretDto {
  client: AdminOAuthClientDto;
  /** Shown once. */
  clientSecret: string;
  tokenEndpoint: string;
}
