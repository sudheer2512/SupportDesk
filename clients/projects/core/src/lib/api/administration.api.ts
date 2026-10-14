import { HttpEventType, HttpResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, filter, map } from 'rxjs';
import { ApiClient } from '../http/api-client';
import {
  AdminApiKeyDto,
  AdminBrandDto,
  AdminBrandListItemDto,
  AdminCategoryDto,
  AdminCustomFieldDto,
  AdminFieldEntity,
  AdminIssueTypeDto,
  AdminLayoutDto,
  AdminOAuthClientDto,
  AdminRoleDto,
  AdminRoleListItemDto,
  AdminSessionDto,
  AdminStatusDto,
  ApiKeySecretDto,
  ApiScopeDto,
  CloneRoleRequest,
  CreateApiKeyRequest,
  CreateOAuthClientRequest,
  DomainCheckResultDto,
  LayoutFieldOptionDto,
  NotificationPolicyDto,
  NotificationTemplateDto,
  OAuthClientSecretDto,
  PermissionCatalogDto,
  PreviewTemplateRequest,
  RevokeSessionsResultDto,
  SaveBrandRequest,
  SaveCategoryRequest,
  SaveCustomFieldRequest,
  SaveIssueTypeRequest,
  SaveLayoutRequest,
  SaveNotificationPolicyRequest,
  SaveNotificationTemplateRequest,
  SaveRoleRequest,
  SaveSecurityPolicyRequest,
  SaveSsoConfigRequest,
  SaveStatusRequest,
  SaveTicketLifecycleRequest,
  SecurityPolicyDto,
  SsoConfigDto,
  SsoTestResultDto,
  TemplatePreviewDto,
  TicketDisplayDto,
  TicketLifecycleDto,
  UserRolesDto,
} from '../models/administration';
import { Guid, PagedResult } from '../models/common';

const id = encodeURIComponent;

/**
 * Roles & permissions (`/admin/roles`, `admin.roles`; the role list is also readable with `admin.users`) and user role
 * assignment (`/admin/users/{id}/roles`). The API refuses privilege escalation (403 `privilege_escalation`) and removing
 * the last administrator (409 `last_administrator`).
 */
@Injectable({ providedIn: 'root' })
export class AdminRolesApi {
  private readonly api = inject(ApiClient);

  list(): Observable<AdminRoleListItemDto[]> {
    return this.api.get<AdminRoleListItemDto[]>('admin/roles');
  }

  catalog(): Observable<PermissionCatalogDto> {
    return this.api.get<PermissionCatalogDto>('admin/roles/catalog');
  }

  get(roleId: Guid): Observable<AdminRoleDto> {
    return this.api.get<AdminRoleDto>(`admin/roles/${id(roleId)}`);
  }

  create(request: SaveRoleRequest): Observable<AdminRoleDto> {
    return this.api.post<AdminRoleDto>('admin/roles', request, { silent: true });
  }

  update(roleId: Guid, request: SaveRoleRequest): Observable<AdminRoleDto> {
    return this.api.put<AdminRoleDto>(`admin/roles/${id(roleId)}`, request, { silent: true });
  }

  clone(roleId: Guid, request: CloneRoleRequest): Observable<AdminRoleDto> {
    return this.api.post<AdminRoleDto>(`admin/roles/${id(roleId)}/clone`, request, { silent: true });
  }

  /** Members are moved to `reassignTo` (required when the role has members). */
  delete(roleId: Guid, reassignTo: Guid | null): Observable<void> {
    return this.api.delete<void>(`admin/roles/${id(roleId)}`, { params: { reassignTo }, silent: true });
  }

  addMember(roleId: Guid, userId: Guid): Observable<AdminRoleDto> {
    return this.api.post<AdminRoleDto>(`admin/roles/${id(roleId)}/members`, { userId }, { silent: true });
  }

  removeMember(roleId: Guid, userId: Guid): Observable<AdminRoleDto> {
    return this.api.delete<AdminRoleDto>(`admin/roles/${id(roleId)}/members/${id(userId)}`, { silent: true });
  }

  userRoles(userId: Guid): Observable<UserRolesDto> {
    return this.api.get<UserRolesDto>(`admin/users/${id(userId)}/roles`);
  }

  setUserRoles(userId: Guid, roleIds: Guid[]): Observable<UserRolesDto> {
    return this.api.put<UserRolesDto>(`admin/users/${id(userId)}/roles`, { roleIds }, { silent: true });
  }
}

/**
 * Ticket configuration (`/admin/ticket-config`, `admin.ticket_config`): statuses, categories, issue types, priority/severity
 * display, numbering & lifecycle, custom fields and form layouts. After a change, call `MetaApi.refresh()`; other open desks
 * refresh automatically on the realtime `config.changed` event.
 */
@Injectable({ providedIn: 'root' })
export class TicketConfigApi {
  private readonly api = inject(ApiClient);
  private readonly base = 'admin/ticket-config';

  statuses(): Observable<AdminStatusDto[]> {
    return this.api.get<AdminStatusDto[]>(`${this.base}/statuses`);
  }

  createStatus(request: SaveStatusRequest): Observable<AdminStatusDto> {
    return this.api.post<AdminStatusDto>(`${this.base}/statuses`, request, { silent: true });
  }

  updateStatus(statusId: Guid, request: SaveStatusRequest): Observable<AdminStatusDto> {
    return this.api.put<AdminStatusDto>(`${this.base}/statuses/${id(statusId)}`, request, { silent: true });
  }

  deleteStatus(statusId: Guid): Observable<void> {
    return this.api.delete<void>(`${this.base}/statuses/${id(statusId)}`, { silent: true });
  }

  reorderStatuses(ids: Guid[]): Observable<AdminStatusDto[]> {
    return this.api.put<AdminStatusDto[]>(`${this.base}/statuses/order`, { ids }, { silent: true });
  }

  categories(): Observable<AdminCategoryDto[]> {
    return this.api.get<AdminCategoryDto[]>(`${this.base}/categories`);
  }

  saveCategory(categoryId: Guid | null, request: SaveCategoryRequest): Observable<AdminCategoryDto> {
    return categoryId
      ? this.api.put<AdminCategoryDto>(`${this.base}/categories/${id(categoryId)}`, request, { silent: true })
      : this.api.post<AdminCategoryDto>(`${this.base}/categories`, request, { silent: true });
  }

  deleteCategory(categoryId: Guid): Observable<void> {
    return this.api.delete<void>(`${this.base}/categories/${id(categoryId)}`, { silent: true });
  }

  reorderCategories(ids: Guid[]): Observable<AdminCategoryDto[]> {
    return this.api.put<AdminCategoryDto[]>(`${this.base}/categories/order`, { ids }, { silent: true });
  }

  issueTypes(): Observable<AdminIssueTypeDto[]> {
    return this.api.get<AdminIssueTypeDto[]>(`${this.base}/issue-types`);
  }

  saveIssueType(issueTypeId: Guid | null, request: SaveIssueTypeRequest): Observable<AdminIssueTypeDto> {
    return issueTypeId
      ? this.api.put<AdminIssueTypeDto>(`${this.base}/issue-types/${id(issueTypeId)}`, request, { silent: true })
      : this.api.post<AdminIssueTypeDto>(`${this.base}/issue-types`, request, { silent: true });
  }

  deleteIssueType(issueTypeId: Guid): Observable<void> {
    return this.api.delete<void>(`${this.base}/issue-types/${id(issueTypeId)}`, { silent: true });
  }

  reorderIssueTypes(ids: Guid[]): Observable<AdminIssueTypeDto[]> {
    return this.api.put<AdminIssueTypeDto[]>(`${this.base}/issue-types/order`, { ids }, { silent: true });
  }

  /** Priority/severity labels and colours (readable by every agent). */
  display(): Observable<TicketDisplayDto> {
    return this.api.get<TicketDisplayDto>(`${this.base}/display`);
  }

  saveDisplay(request: TicketDisplayDto): Observable<TicketDisplayDto> {
    return this.api.put<TicketDisplayDto>(`${this.base}/display`, request, { silent: true });
  }

  lifecycle(): Observable<TicketLifecycleDto> {
    return this.api.get<TicketLifecycleDto>(`${this.base}/lifecycle`);
  }

  saveLifecycle(request: SaveTicketLifecycleRequest): Observable<TicketLifecycleDto> {
    return this.api.put<TicketLifecycleDto>(`${this.base}/lifecycle`, request, { silent: true });
  }

  fields(entity?: AdminFieldEntity | null): Observable<AdminCustomFieldDto[]> {
    return this.api.get<AdminCustomFieldDto[]>(`${this.base}/fields`, { params: { entity: entity ?? null } });
  }

  createField(request: SaveCustomFieldRequest): Observable<AdminCustomFieldDto> {
    return this.api.post<AdminCustomFieldDto>(`${this.base}/fields`, request, { silent: true });
  }

  updateField(fieldId: Guid, request: SaveCustomFieldRequest): Observable<AdminCustomFieldDto> {
    return this.api.put<AdminCustomFieldDto>(`${this.base}/fields/${id(fieldId)}`, request, { silent: true });
  }

  deleteField(fieldId: Guid): Observable<void> {
    return this.api.delete<void>(`${this.base}/fields/${id(fieldId)}`, { silent: true });
  }

  reorderFields(entity: AdminFieldEntity, ids: Guid[]): Observable<AdminCustomFieldDto[]> {
    return this.api.put<AdminCustomFieldDto[]>(`${this.base}/fields/order`, { ids }, { params: { entity }, silent: true });
  }

  layouts(): Observable<AdminLayoutDto[]> {
    return this.api.get<AdminLayoutDto[]>(`${this.base}/layouts`);
  }

  layout(layoutId: Guid): Observable<AdminLayoutDto> {
    return this.api.get<AdminLayoutDto>(`${this.base}/layouts/${id(layoutId)}`);
  }

  layoutFields(): Observable<LayoutFieldOptionDto[]> {
    return this.api.get<LayoutFieldOptionDto[]>(`${this.base}/layouts/fields`);
  }

  saveLayout(layoutId: Guid | null, request: SaveLayoutRequest): Observable<AdminLayoutDto> {
    return layoutId
      ? this.api.put<AdminLayoutDto>(`${this.base}/layouts/${id(layoutId)}`, request, { silent: true })
      : this.api.post<AdminLayoutDto>(`${this.base}/layouts`, request, { silent: true });
  }

  deleteLayout(layoutId: Guid): Observable<void> {
    return this.api.delete<void>(`${this.base}/layouts/${id(layoutId)}`, { silent: true });
  }
}

/** Brands & portals (`/admin/brands`, `admin.organisation`). Logos/favicons are served publicly from `/api/v1/brand-assets/{id}`. */
@Injectable({ providedIn: 'root' })
export class AdminBrandsApi {
  private readonly api = inject(ApiClient);

  list(): Observable<AdminBrandListItemDto[]> {
    return this.api.get<AdminBrandListItemDto[]>('admin/brands');
  }

  get(brandId: Guid): Observable<AdminBrandDto> {
    return this.api.get<AdminBrandDto>(`admin/brands/${id(brandId)}`);
  }

  create(request: SaveBrandRequest): Observable<AdminBrandDto> {
    return this.api.post<AdminBrandDto>('admin/brands', request, { silent: true });
  }

  update(brandId: Guid, request: SaveBrandRequest): Observable<AdminBrandDto> {
    return this.api.put<AdminBrandDto>(`admin/brands/${id(brandId)}`, request, { silent: true });
  }

  delete(brandId: Guid): Observable<void> {
    return this.api.delete<void>(`admin/brands/${id(brandId)}`, { silent: true });
  }

  makeDefault(brandId: Guid): Observable<AdminBrandDto> {
    return this.api.post<AdminBrandDto>(`admin/brands/${id(brandId)}/default`, {}, { silent: true });
  }

  /** Uploads the logo or favicon (PNG, JPEG, GIF or WebP, ≤ 2 MB; malware-scanned) and returns the updated brand. */
  uploadAsset(brandId: Guid, kind: 'logo' | 'favicon', file: File): Observable<AdminBrandDto> {
    const form = new FormData();
    form.append('file', file, file.name);
    return this.api.upload<AdminBrandDto>(`admin/brands/${id(brandId)}/assets/${kind}`, form, { silent: true }).pipe(
      filter((event): event is HttpResponse<AdminBrandDto> => event.type === HttpEventType.Response && !!(event as HttpResponse<AdminBrandDto>).body),
      map((event) => event.body!),
    );
  }

  removeAsset(brandId: Guid, kind: 'logo' | 'favicon'): Observable<AdminBrandDto> {
    return this.api.delete<AdminBrandDto>(`admin/brands/${id(brandId)}/assets/${kind}`, { silent: true });
  }

  checkDomain(brandId: Guid): Observable<DomainCheckResultDto> {
    return this.api.post<DomainCheckResultDto>(`admin/brands/${id(brandId)}/check-domain`, {}, { silent: true });
  }
}

/** Notification defaults and e-mail templates (`/admin/notifications`, `admin.organisation` or `admin.channels`). */
@Injectable({ providedIn: 'root' })
export class AdminNotificationsApi {
  private readonly api = inject(ApiClient);

  defaults(): Observable<NotificationPolicyDto> {
    return this.api.get<NotificationPolicyDto>('admin/notifications/defaults');
  }

  saveDefaults(request: SaveNotificationPolicyRequest): Observable<NotificationPolicyDto> {
    return this.api.put<NotificationPolicyDto>('admin/notifications/defaults', request, { silent: true });
  }

  templates(): Observable<NotificationTemplateDto[]> {
    return this.api.get<NotificationTemplateDto[]>('admin/notifications/templates');
  }

  template(key: string): Observable<NotificationTemplateDto> {
    return this.api.get<NotificationTemplateDto>(`admin/notifications/templates/${id(key)}`);
  }

  saveTemplate(key: string, request: SaveNotificationTemplateRequest): Observable<NotificationTemplateDto> {
    return this.api.put<NotificationTemplateDto>(`admin/notifications/templates/${id(key)}`, request, { silent: true });
  }

  /** Removes the override; the built-in default applies again. */
  resetTemplate(key: string): Observable<NotificationTemplateDto> {
    return this.api.delete<NotificationTemplateDto>(`admin/notifications/templates/${id(key)}`, { silent: true });
  }

  preview(key: string, request: PreviewTemplateRequest): Observable<TemplatePreviewDto> {
    return this.api.post<TemplatePreviewDto>(`admin/notifications/templates/${id(key)}/preview`, request, { silent: true });
  }
}

/** Security policies, SSO and active sessions (`/admin/security`, `admin.security`). */
@Injectable({ providedIn: 'root' })
export class AdminSecurityApi {
  private readonly api = inject(ApiClient);

  policy(): Observable<SecurityPolicyDto> {
    return this.api.get<SecurityPolicyDto>('admin/security/policy');
  }

  savePolicy(request: SaveSecurityPolicyRequest): Observable<SecurityPolicyDto> {
    return this.api.put<SecurityPolicyDto>('admin/security/policy', request, { silent: true });
  }

  sso(): Observable<SsoConfigDto> {
    return this.api.get<SsoConfigDto>('admin/security/sso');
  }

  saveSso(request: SaveSsoConfigRequest): Observable<SsoConfigDto> {
    return this.api.put<SsoConfigDto>('admin/security/sso', request, { silent: true });
  }

  /** Loads the identity provider's discovery document (optionally for an unsaved authority). */
  testSso(authority: string | null): Observable<SsoTestResultDto> {
    return this.api.post<SsoTestResultDto>('admin/security/sso/test', { authority }, { silent: true });
  }

  sessions(query: { userId?: Guid | null; cursor?: string | null; limit?: number } = {}): Observable<PagedResult<AdminSessionDto>> {
    return this.api.get<PagedResult<AdminSessionDto>>('admin/security/sessions', { params: query });
  }

  revokeSession(sessionId: Guid): Observable<RevokeSessionsResultDto> {
    return this.api.delete<RevokeSessionsResultDto>(`admin/security/sessions/${id(sessionId)}`, { silent: true });
  }

  /** Signs a user out on every device. */
  revokeUserSessions(userId: Guid): Observable<RevokeSessionsResultDto> {
    return this.api.delete<RevokeSessionsResultDto>(`admin/security/users/${id(userId)}/sessions`, { silent: true });
  }
}

/** API keys and OAuth clients (`/admin/api-keys`, `/admin/oauth-clients`, `admin.integrations`). Secrets are returned once. */
@Injectable({ providedIn: 'root' })
export class ApiCredentialsApi {
  private readonly api = inject(ApiClient);

  scopes(): Observable<ApiScopeDto[]> {
    return this.api.get<ApiScopeDto[]>('admin/api-scopes');
  }

  keys(): Observable<AdminApiKeyDto[]> {
    return this.api.get<AdminApiKeyDto[]>('admin/api-keys');
  }

  createKey(request: CreateApiKeyRequest): Observable<ApiKeySecretDto> {
    return this.api.post<ApiKeySecretDto>('admin/api-keys', request, { silent: true });
  }

  revokeKey(keyId: Guid): Observable<void> {
    return this.api.delete<void>(`admin/api-keys/${id(keyId)}`, { silent: true });
  }

  clients(): Observable<AdminOAuthClientDto[]> {
    return this.api.get<AdminOAuthClientDto[]>('admin/oauth-clients');
  }

  createClient(request: CreateOAuthClientRequest): Observable<OAuthClientSecretDto> {
    return this.api.post<OAuthClientSecretDto>('admin/oauth-clients', request, { silent: true });
  }

  rotateClientSecret(clientId: Guid): Observable<OAuthClientSecretDto> {
    return this.api.post<OAuthClientSecretDto>(`admin/oauth-clients/${id(clientId)}/rotate-secret`, {}, { silent: true });
  }

  revokeClient(clientId: Guid): Observable<void> {
    return this.api.delete<void>(`admin/oauth-clients/${id(clientId)}`, { silent: true });
  }
}
