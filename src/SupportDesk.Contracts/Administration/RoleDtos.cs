using SupportDesk.Contracts.Common;

namespace SupportDesk.Contracts.Administration;

/// <summary>
/// Role picker / list row (GET /api/v1/admin/roles). Scopes are enum names (TicketScope: Own, Team, Departments, All;
/// CustomerScope: AccountManaged, All). <c>IsAdministrative</c>: the role grants admin.roles, so its holders count as
/// administrators for last-administrator protection.
/// </summary>
public sealed record AdminRoleListItemDto(
    Guid Id,
    string Name,
    string? Description,
    bool IsSystem,
    bool IsLightAgent,
    string TicketScope,
    string CustomerScope,
    int PermissionCount,
    int UserCount,
    bool IsAdministrative);

public sealed record AdminRoleMemberDto(Guid UserId, string Name, string Email, bool IsActive, IReadOnlyList<string> OtherRoles);

public sealed record AdminRoleDto(
    Guid Id,
    string Name,
    string? Description,
    bool IsSystem,
    bool IsLightAgent,
    string TicketScope,
    string CustomerScope,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<AdminRoleMemberDto> Members,
    DateTime UpdatedAt,
    string RowVersion);

/// <summary>Create (POST) or update (PUT, rowVersion required) a custom role. Permissions must be catalog codes the caller holds.</summary>
public sealed record SaveRoleRequest(
    string Name,
    string? Description,
    string TicketScope,
    string CustomerScope,
    bool IsLightAgent,
    IReadOnlyList<string> Permissions,
    string? RowVersion = null);

public sealed record CloneRoleRequest(string Name, string? Description = null);

public sealed record PermissionItemDto(string Code, string Description, bool Sensitive);

public sealed record PermissionGroupDto(string Module, IReadOnlyList<PermissionItemDto> Permissions);

/// <summary>Permission matrix metadata. <see cref="Grantable"/> lists the codes the caller may grant (their own permissions).</summary>
public sealed record PermissionCatalogDto(
    IReadOnlyList<PermissionGroupDto> Groups,
    IReadOnlyList<OptionDto> TicketScopes,
    IReadOnlyList<OptionDto> CustomerScopes,
    IReadOnlyList<string> Grantable,
    string GrantableTicketScope,
    string GrantableCustomerScope,
    bool CustomRolesAllowed);

public sealed record SetUserRolesRequest(IReadOnlyList<Guid> RoleIds);

public sealed record AddRoleMemberRequest(Guid UserId);

public sealed record UserRolesDto(Guid UserId, string Name, string Email, IReadOnlyList<IdName> Roles);
