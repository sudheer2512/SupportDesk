using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SupportDesk.Api.Infrastructure;
using SupportDesk.Application.Administration;
using SupportDesk.Application.Identity;
using SupportDesk.Contracts.Administration;
using SupportDesk.Contracts.Common;

namespace SupportDesk.Api.Controllers.Administration;

/// <summary>Base URL of this API as the caller reached it (portal links, SSO callback, token endpoint).</summary>
internal static class RequestUrls
{
    public static string BaseUrl(HttpRequest request) => $"{request.Scheme}://{request.Host}";
}

/// <summary>Roles &amp; permissions (admin.roles; the role list is also readable with admin.users for role pickers) and user role assignment.</summary>
[Route("api/v1/admin")]
public sealed class AdminRolesController(RoleAdminService roles) : DeskControllerBase
{
    /// <summary>Roles with permission and user counts.</summary>
    [HttpGet("roles")]
    public Task<List<AdminRoleListItemDto>> List(CancellationToken ct) => roles.ListAsync(ct);

    /// <summary>Permission matrix metadata (grouped catalog, data scopes, what the caller may grant).</summary>
    [HttpGet("roles/catalog")]
    public Task<PermissionCatalogDto> Catalog(CancellationToken ct) => roles.CatalogAsync(ct);

    [HttpGet("roles/{id:guid}")]
    public Task<AdminRoleDto> Get(Guid id, CancellationToken ct) => roles.GetAsync(id, ct);

    [HttpPost("roles")]
    public Task<AdminRoleDto> Create(SaveRoleRequest request, CancellationToken ct) => roles.CreateAsync(request, ct);

    /// <summary>Updates a custom role (system roles are read-only: clone them).</summary>
    [HttpPut("roles/{id:guid}")]
    public Task<AdminRoleDto> Update(Guid id, SaveRoleRequest request, CancellationToken ct) => roles.UpdateAsync(id, request, ct);

    /// <summary>Deletes a custom role; members are moved to <paramref name="reassignTo"/>.</summary>
    [HttpDelete("roles/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] Guid? reassignTo, CancellationToken ct)
    {
        await roles.DeleteAsync(id, reassignTo, ct);
        return NoContent();
    }

    [HttpPost("roles/{id:guid}/clone")]
    public Task<AdminRoleDto> Clone(Guid id, CloneRoleRequest request, CancellationToken ct) => roles.CloneAsync(id, request, ct);

    [HttpPost("roles/{id:guid}/members")]
    public Task<AdminRoleDto> AddMember(Guid id, AddRoleMemberRequest request, CancellationToken ct) => roles.AddMemberAsync(id, request.UserId, ct);

    [HttpDelete("roles/{id:guid}/members/{userId:guid}")]
    public Task<AdminRoleDto> RemoveMember(Guid id, Guid userId, CancellationToken ct) => roles.RemoveMemberAsync(id, userId, ct);

    [HttpGet("users/{id:guid}/roles")]
    public Task<UserRolesDto> UserRoles(Guid id, CancellationToken ct) => roles.GetUserRolesAsync(id, ct);

    /// <summary>Replaces a user's roles (no privilege escalation; the last administrator is protected).</summary>
    [HttpPut("users/{id:guid}/roles")]
    public Task<UserRolesDto> SetUserRoles(Guid id, SetUserRolesRequest request, CancellationToken ct) => roles.SetUserRolesAsync(id, request.RoleIds, ct);
}

/// <summary>Ticket configuration (admin.ticket_config): statuses, categories, issue types, display, numbering &amp; lifecycle, custom fields and form layouts.</summary>
[Route("api/v1/admin/ticket-config")]
public sealed class AdminTicketConfigController(TicketConfigService config, FieldLayoutService fields) : DeskControllerBase
{
    [HttpGet("statuses")]
    public Task<List<AdminStatusDto>> Statuses(CancellationToken ct) => config.ListStatusesAsync(ct);

    [HttpPost("statuses")]
    public Task<AdminStatusDto> CreateStatus(SaveStatusRequest request, CancellationToken ct) => config.CreateStatusAsync(request, ct);

    [HttpPut("statuses/{id:guid}")]
    public Task<AdminStatusDto> UpdateStatus(Guid id, SaveStatusRequest request, CancellationToken ct) => config.UpdateStatusAsync(id, request, ct);

    [HttpDelete("statuses/{id:guid}")]
    public async Task<IActionResult> DeleteStatus(Guid id, CancellationToken ct)
    {
        await config.DeleteStatusAsync(id, ct);
        return NoContent();
    }

    [HttpPut("statuses/order")]
    public Task<List<AdminStatusDto>> ReorderStatuses(ReorderRequest request, CancellationToken ct) => config.ReorderStatusesAsync(request, ct);

    [HttpGet("categories")]
    public Task<List<AdminCategoryDto>> Categories(CancellationToken ct) => config.ListCategoriesAsync(ct);

    [HttpPost("categories")]
    public Task<AdminCategoryDto> CreateCategory(SaveCategoryRequest request, CancellationToken ct) => config.SaveCategoryAsync(null, request, ct);

    [HttpPut("categories/{id:guid}")]
    public Task<AdminCategoryDto> UpdateCategory(Guid id, SaveCategoryRequest request, CancellationToken ct) => config.SaveCategoryAsync(id, request, ct);

    [HttpDelete("categories/{id:guid}")]
    public async Task<IActionResult> DeleteCategory(Guid id, CancellationToken ct)
    {
        await config.DeleteCategoryAsync(id, ct);
        return NoContent();
    }

    [HttpPut("categories/order")]
    public Task<List<AdminCategoryDto>> ReorderCategories(ReorderRequest request, CancellationToken ct) => config.ReorderCategoriesAsync(request, ct);

    [HttpGet("issue-types")]
    public Task<List<AdminIssueTypeDto>> IssueTypes(CancellationToken ct) => config.ListIssueTypesAsync(ct);

    [HttpPost("issue-types")]
    public Task<AdminIssueTypeDto> CreateIssueType(SaveIssueTypeRequest request, CancellationToken ct) => config.SaveIssueTypeAsync(null, request, ct);

    [HttpPut("issue-types/{id:guid}")]
    public Task<AdminIssueTypeDto> UpdateIssueType(Guid id, SaveIssueTypeRequest request, CancellationToken ct) => config.SaveIssueTypeAsync(id, request, ct);

    [HttpDelete("issue-types/{id:guid}")]
    public async Task<IActionResult> DeleteIssueType(Guid id, CancellationToken ct)
    {
        await config.DeleteIssueTypeAsync(id, ct);
        return NoContent();
    }

    [HttpPut("issue-types/order")]
    public Task<List<AdminIssueTypeDto>> ReorderIssueTypes(ReorderRequest request, CancellationToken ct) => config.ReorderIssueTypesAsync(request, ct);

    /// <summary>Priority/severity labels and colours (readable by every agent).</summary>
    [HttpGet("display")]
    public Task<TicketDisplayDto> Display(CancellationToken ct) => config.GetDisplayAsync(ct);

    [HttpPut("display")]
    public Task<TicketDisplayDto> SaveDisplay(TicketDisplayDto request, CancellationToken ct) => config.SaveDisplayAsync(request, ct);

    [HttpGet("lifecycle")]
    public Task<TicketLifecycleDto> Lifecycle(CancellationToken ct) => config.GetLifecycleAsync(ct);

    [HttpPut("lifecycle")]
    public Task<TicketLifecycleDto> SaveLifecycle(SaveTicketLifecycleRequest request, CancellationToken ct) => config.SaveLifecycleAsync(request, ct);

    [HttpGet("fields")]
    public Task<List<AdminCustomFieldDto>> Fields([FromQuery] string? entity, CancellationToken ct) => fields.ListFieldsAsync(entity, ct);

    [HttpPost("fields")]
    public Task<AdminCustomFieldDto> CreateField(SaveCustomFieldRequest request, CancellationToken ct) => fields.CreateFieldAsync(request, ct);

    [HttpPut("fields/{id:guid}")]
    public Task<AdminCustomFieldDto> UpdateField(Guid id, SaveCustomFieldRequest request, CancellationToken ct) => fields.UpdateFieldAsync(id, request, ct);

    [HttpDelete("fields/{id:guid}")]
    public async Task<IActionResult> DeleteField(Guid id, CancellationToken ct)
    {
        await fields.DeleteFieldAsync(id, ct);
        return NoContent();
    }

    [HttpPut("fields/order")]
    public Task<List<AdminCustomFieldDto>> ReorderFields([FromQuery] string entity, ReorderRequest request, CancellationToken ct) => fields.ReorderFieldsAsync(entity, request, ct);

    [HttpGet("layouts")]
    public Task<List<AdminLayoutDto>> Layouts(CancellationToken ct) => fields.ListLayoutsAsync(ct);

    /// <summary>Fields the form builder can place (standard + active ticket custom fields).</summary>
    [HttpGet("layouts/fields")]
    public Task<List<LayoutFieldOptionDto>> LayoutFields(CancellationToken ct) => fields.LayoutFieldsAsync(ct);

    [HttpGet("layouts/{id:guid}")]
    public Task<AdminLayoutDto> Layout(Guid id, CancellationToken ct) => fields.GetLayoutAsync(id, ct);

    [HttpPost("layouts")]
    public Task<AdminLayoutDto> CreateLayout(SaveLayoutRequest request, CancellationToken ct) => fields.SaveLayoutAsync(null, request, ct);

    [HttpPut("layouts/{id:guid}")]
    public Task<AdminLayoutDto> UpdateLayout(Guid id, SaveLayoutRequest request, CancellationToken ct) => fields.SaveLayoutAsync(id, request, ct);

    [HttpDelete("layouts/{id:guid}")]
    public async Task<IActionResult> DeleteLayout(Guid id, CancellationToken ct)
    {
        await fields.DeleteLayoutAsync(id, ct);
        return NoContent();
    }
}

/// <summary>Brands &amp; portals (admin.organisation).</summary>
[Route("api/v1/admin/brands")]
public sealed class AdminBrandsController(BrandAdminService brands) : DeskControllerBase
{
    private string Base => RequestUrls.BaseUrl(Request);

    [HttpGet]
    public Task<List<AdminBrandListItemDto>> List(CancellationToken ct) => brands.ListAsync(Base, ct);

    [HttpGet("{id:guid}")]
    public Task<AdminBrandDto> Get(Guid id, CancellationToken ct) => brands.GetAsync(id, Base, ct);

    [HttpPost]
    public Task<AdminBrandDto> Create(SaveBrandRequest request, CancellationToken ct) => brands.CreateAsync(request, Base, ct);

    [HttpPut("{id:guid}")]
    public Task<AdminBrandDto> Update(Guid id, SaveBrandRequest request, CancellationToken ct) => brands.UpdateAsync(id, request, Base, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await brands.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/default")]
    public Task<AdminBrandDto> MakeDefault(Guid id, CancellationToken ct) => brands.MakeDefaultAsync(id, Base, ct);

    /// <summary>Uploads the logo or favicon (kind = logo | favicon; PNG, JPEG, GIF or WebP, at most 2 MB).</summary>
    [HttpPost("{id:guid}/assets/{kind}")]
    [RequestSizeLimit(5 * 1024 * 1024)]
    public async Task<AdminBrandDto> UploadAsset(Guid id, string kind, IFormFile file, CancellationToken ct)
    {
        if (file.Length > BrandAdminService.MaxAssetBytes) throw new Domain.DomainException("file_too_large", "Brand images may be at most 2 MB.", 413);
        await using var stream = file.OpenReadStream();
        return await brands.UploadAssetAsync(id, kind, stream, file.FileName, file.ContentType, Base, ct);
    }

    [HttpDelete("{id:guid}/assets/{kind}")]
    public Task<AdminBrandDto> RemoveAsset(Guid id, string kind, CancellationToken ct) => brands.RemoveAssetAsync(id, kind, Base, ct);

    /// <summary>Resolves the custom domain and checks that it points to the portal host.</summary>
    [HttpPost("{id:guid}/check-domain")]
    public Task<DomainCheckResultDto> CheckDomain(Guid id, CancellationToken ct) => brands.CheckDomainAsync(id, Base, ct);
}

/// <summary>Public brand logos and favicons (unauthenticated, unguessable ids, inline-safe images only).</summary>
[ApiController]
[Route("api/v1/brand-assets")]
[AllowAnonymous]
[EnableRateLimiting(HostingSetup.PublicLimiter)]
public sealed class BrandAssetsController(BrandAssetService assets) : ControllerBase
{
    [HttpGet("{fileId:guid}")]
    public async Task<IActionResult> Get(Guid fileId, CancellationToken ct)
    {
        var (file, stream) = await assets.OpenAsync(fileId, ct);
        Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
        Response.Headers.CacheControl = "public, max-age=86400";
        return File(stream, file.ContentType);
    }
}

/// <summary>Notification defaults and e-mail templates (admin.organisation or admin.channels).</summary>
[Route("api/v1/admin/notifications")]
public sealed class AdminNotificationsController(NotificationAdminService notifications) : DeskControllerBase
{
    [HttpGet("defaults")]
    public Task<NotificationPolicyDto> Defaults(CancellationToken ct) => notifications.GetPolicyAsync(ct);

    [HttpPut("defaults")]
    public Task<NotificationPolicyDto> SaveDefaults(SaveNotificationPolicyRequest request, CancellationToken ct) => notifications.SavePolicyAsync(request, ct);

    [HttpGet("templates")]
    public Task<List<NotificationTemplateDto>> Templates(CancellationToken ct) => notifications.ListTemplatesAsync(ct);

    [HttpGet("templates/{key}")]
    public Task<NotificationTemplateDto> Template(string key, CancellationToken ct) => notifications.GetTemplateAsync(key, ct);

    [HttpPut("templates/{key}")]
    public Task<NotificationTemplateDto> SaveTemplate(string key, SaveNotificationTemplateRequest request, CancellationToken ct) => notifications.SaveTemplateAsync(key, request, ct);

    /// <summary>Removes the override (the built-in default applies again).</summary>
    [HttpDelete("templates/{key}")]
    public Task<NotificationTemplateDto> ResetTemplate(string key, CancellationToken ct) => notifications.ResetTemplateAsync(key, ct);

    [HttpPost("templates/{key}/preview")]
    public Task<TemplatePreviewDto> Preview(string key, [FromBody] PreviewTemplateRequest? request, CancellationToken ct) =>
        notifications.PreviewAsync(key, request ?? new PreviewTemplateRequest(), ct);
}

/// <summary>Security policies, SSO and active sessions (admin.security).</summary>
[Route("api/v1/admin/security")]
public sealed class AdminSecurityController(SecurityAdminService security) : DeskControllerBase
{
    [HttpGet("policy")]
    public Task<SecurityPolicyDto> Policy(CancellationToken ct) => security.GetPolicyAsync(ct);

    [HttpPut("policy")]
    public Task<SecurityPolicyDto> SavePolicy(SaveSecurityPolicyRequest request, CancellationToken ct) => security.SavePolicyAsync(request, ct);

    [HttpGet("sso")]
    public Task<SsoConfigDto> Sso(CancellationToken ct) => security.GetSsoAsync(RequestUrls.BaseUrl(Request), ct);

    [HttpPut("sso")]
    public Task<SsoConfigDto> SaveSso(SaveSsoConfigRequest request, CancellationToken ct) => security.SaveSsoAsync(request, RequestUrls.BaseUrl(Request), ct);

    /// <summary>Loads the identity provider's discovery document and keys (optionally for an unsaved authority).</summary>
    [HttpPost("sso/test")]
    public Task<SsoTestResultDto> TestSso([FromBody] TestSsoRequest? request, CancellationToken ct) => security.TestSsoAsync(request?.Authority, ct);

    [HttpGet("sessions")]
    public Task<PagedResult<AdminSessionDto>> Sessions([FromQuery] Guid? userId, [FromQuery] string? cursor, [FromQuery] int limit = 50, CancellationToken ct = default) =>
        security.ListSessionsAsync(userId, cursor, limit, CurrentSessionId, ct);

    [HttpDelete("sessions/{id:guid}")]
    public async Task<RevokeSessionsResultDto> RevokeSession(Guid id, CancellationToken ct)
    {
        var ids = await security.RevokeSessionAsync(id, ct);
        foreach (var s in ids) RequestContextMiddleware.Forget(s);
        return new RevokeSessionsResultDto(ids.Count);
    }

    /// <summary>Signs a user out on every device.</summary>
    [HttpDelete("users/{userId:guid}/sessions")]
    public async Task<RevokeSessionsResultDto> RevokeUserSessions(Guid userId, CancellationToken ct)
    {
        var ids = await security.RevokeUserSessionsAsync(userId, ct);
        foreach (var s in ids) RequestContextMiddleware.Forget(s);
        return new RevokeSessionsResultDto(ids.Count);
    }

    private Guid? CurrentSessionId => Guid.TryParse(User.FindFirst(DeskClaims.Session)?.Value, out var s) ? s : null;
}

/// <summary>API keys and OAuth clients (admin.integrations).</summary>
[Route("api/v1/admin")]
public sealed class AdminApiCredentialsController(ApiCredentialAdminService credentials) : DeskControllerBase
{
    [HttpGet("api-scopes")]
    public List<ApiScopeDto> Scopes() => credentials.Scopes();

    [HttpGet("api-keys")]
    public Task<List<AdminApiKeyDto>> Keys(CancellationToken ct) => credentials.ListKeysAsync(ct);

    /// <summary>Creates a key; the response carries the full secret once.</summary>
    [HttpPost("api-keys")]
    public Task<ApiKeySecretDto> CreateKey(CreateApiKeyRequest request, CancellationToken ct) => credentials.CreateKeyAsync(request, ct);

    [HttpDelete("api-keys/{id:guid}")]
    public async Task<IActionResult> RevokeKey(Guid id, CancellationToken ct)
    {
        await credentials.RevokeKeyAsync(id, ct);
        return NoContent();
    }

    [HttpGet("oauth-clients")]
    public Task<List<AdminOAuthClientDto>> Clients(CancellationToken ct) => credentials.ListClientsAsync(ct);

    /// <summary>Creates a client-credentials client; the response carries the secret once.</summary>
    [HttpPost("oauth-clients")]
    public Task<OAuthClientSecretDto> CreateClient(CreateOAuthClientRequest request, CancellationToken ct) =>
        credentials.CreateClientAsync(request, RequestUrls.BaseUrl(Request), ct);

    [HttpPost("oauth-clients/{id:guid}/rotate-secret")]
    public Task<OAuthClientSecretDto> RotateSecret(Guid id, CancellationToken ct) => credentials.RotateClientSecretAsync(id, RequestUrls.BaseUrl(Request), ct);

    [HttpDelete("oauth-clients/{id:guid}")]
    public async Task<IActionResult> RevokeClient(Guid id, CancellationToken ct)
    {
        await credentials.RevokeClientAsync(id, ct);
        return NoContent();
    }
}
