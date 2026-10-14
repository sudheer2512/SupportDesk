using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Identity;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Administration;
using SupportDesk.Contracts.Common;
using SupportDesk.Domain;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Administration;

/// <summary>Pure role rules: privilege-escalation checks and last-administrator detection.</summary>
public static class RoleRules
{
    /// <summary>Permission that makes its holders "administrators" for last-administrator protection (they can repair roles).</summary>
    public const string AdministratorPermission = Permissions.AdminRoles;

    /// <summary>Permissions in <paramref name="requested"/> the actor does not hold (granting them would be an escalation).</summary>
    public static IReadOnlyList<string> NotHeld(IReadOnlySet<string> actor, IEnumerable<string> requested) =>
        requested.Where(p => !actor.Contains(p)).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();

    /// <summary>True when the requested data scopes are no broader than the actor's own.</summary>
    public static bool ScopeWithin(TicketScope actorTicket, CustomerScope actorCustomer, TicketScope requestedTicket, CustomerScope requestedCustomer) =>
        requestedTicket <= actorTicket && requestedCustomer <= actorCustomer;

    /// <summary>
    /// Users that would still be administrators: <paramref name="userRoles"/> (active users → role ids, already reflecting the
    /// change) where any role is in <paramref name="administrativeRoles"/> (also reflecting the change).
    /// </summary>
    public static int CountAdministrators(IReadOnlyDictionary<Guid, IReadOnlyCollection<Guid>> userRoles, IReadOnlySet<Guid> administrativeRoles) =>
        userRoles.Count(kv => kv.Value.Any(administrativeRoles.Contains));
}

/// <summary>
/// Roles &amp; permissions administration (admin.roles): list with user counts, permission matrix, custom role CRUD and
/// cloning (system roles are read-only), data scope and light-agent flag, role membership. Guard rails: nobody can grant
/// permissions or data scope they do not hold themselves, and no change may leave the tenant without an administrator.
/// Every change is audited and the affected users' cached permissions are invalidated immediately.
/// </summary>
public sealed class RoleAdminService(
    DeskDbContext db,
    RequestContext ctx,
    IAuditWriter audit,
    PrincipalLoader principals,
    LimitGuard limits,
    AdminChangeNotifier changes)
{
    public async Task<List<AdminRoleListItemDto>> ListAsync(CancellationToken ct)
    {
        ctx.RequireAny(Permissions.AdminRoles, Permissions.AdminUsers);
        var roles = await db.Roles.AsNoTracking().OrderByDescending(r => r.IsSystem).ThenBy(r => r.Name).ToListAsync(ct);
        var permCounts = await db.RolePermissions.AsNoTracking().GroupBy(p => p.RoleId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var adminRoles = await AdministrativeRoleIdsAsync(ct);
        var userCounts = await db.UserRoles.AsNoTracking().Join(db.Users.AsNoTracking(), ur => ur.UserId, u => u.Id, (ur, u) => ur.RoleId)
            .GroupBy(r => r).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return roles.Select(r => new AdminRoleListItemDto(r.Id, r.Name, r.Description, r.IsSystem, r.IsLightAgent, r.TicketScope.ToString(), r.CustomerScope.ToString(),
            permCounts.GetValueOrDefault(r.Id), userCounts.GetValueOrDefault(r.Id), adminRoles.Contains(r.Id))).ToList();
    }

    public async Task<AdminRoleDto> GetAsync(Guid id, CancellationToken ct)
    {
        ctx.RequireAny(Permissions.AdminRoles, Permissions.AdminUsers);
        var role = await db.Roles.AsNoTracking().Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("Role", id);
        return await MapAsync(role, ct);
    }

    public async Task<PermissionCatalogDto> CatalogAsync(CancellationToken ct)
    {
        ctx.RequireAny(Permissions.AdminRoles, Permissions.AdminUsers);
        var groups = Permissions.Catalog.GroupBy(p => p.Module)
            .Select(g => new PermissionGroupDto(g.Key, g.Select(p => new PermissionItemDto(p.Code, p.Description, p.Sensitive)).ToList())).ToList();
        var ticketScopes = new List<OptionDto>
        {
            new(nameof(TicketScope.Own), "Own tickets — assigned to or raised by the agent"),
            new(nameof(TicketScope.Team), "Team — tickets of the agent's teams"),
            new(nameof(TicketScope.Departments), "Departments — tickets of the agent's departments"),
            new(nameof(TicketScope.All), "All tickets in the organisation")
        };
        var customerScopes = new List<OptionDto>
        {
            new(nameof(CustomerScope.AccountManaged), "Account-managed customers only"),
            new(nameof(CustomerScope.All), "All customers")
        };
        return new PermissionCatalogDto(groups, ticketScopes, customerScopes,
            Permissions.Catalog.Select(p => p.Code).Where(ctx.Has).ToList(), ctx.TicketScope.ToString(), ctx.CustomerScope.ToString(),
            await limits.HasFeatureAsync(PlanFeatures.CustomRoles, ct));
    }

    public async Task<AdminRoleDto> CreateAsync(SaveRoleRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminRoles);
        await limits.RequireFeatureAsync(PlanFeatures.CustomRoles, ct);
        var (ticketScope, customerScope, perms) = await ValidateAsync(null, r, ct);
        var role = new Role
        {
            Name = r.Name.Trim(), Description = AdminRules.Clean(r.Description, 500), TicketScope = ticketScope, CustomerScope = customerScope,
            IsLightAgent = r.IsLightAgent
        };
        db.Roles.Add(role);
        foreach (var p in perms) db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionCode = p });
        audit.Write(AdminAuditActions.RoleCreated, "Role", role.Id, $"Role '{role.Name}' created with {perms.Count} permission(s)",
            new { permissions = perms, ticketScope = ticketScope.ToString(), customerScope = customerScope.ToString(), role.IsLightAgent });
        changes.Changed("roles", role.Id);
        await db.SaveChangesAsync(ct);
        return await GetAsync(role.Id, ct);
    }

    public async Task<AdminRoleDto> CloneAsync(Guid id, CloneRoleRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminRoles);
        var source = await db.Roles.AsNoTracking().Include(x => x.Permissions).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Role", id);
        return await CreateAsync(new SaveRoleRequest(r.Name, r.Description ?? source.Description, source.TicketScope.ToString(), source.CustomerScope.ToString(),
            source.IsLightAgent, source.Permissions.Select(p => p.PermissionCode).ToList()), ct);
    }

    public async Task<AdminRoleDto> UpdateAsync(Guid id, SaveRoleRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminRoles);
        var role = await db.Roles.Include(x => x.Permissions).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Role", id);
        if (role.IsSystem) throw new ConflictException("system_role_read_only", "System roles cannot be changed. Clone the role to customise it.");
        AdminRules.ApplyRowVersion(db, role, r.RowVersion, "role");
        EnsureCanManage(role.Permissions.Select(p => p.PermissionCode), role.TicketScope, role.CustomerScope, $"the role '{role.Name}'");
        var (ticketScope, customerScope, perms) = await ValidateAsync(role.Id, r, ct);

        var before = role.Permissions.Select(p => p.PermissionCode).ToHashSet();
        var wasAdministrative = before.Contains(RoleRules.AdministratorPermission);
        if (wasAdministrative && !perms.Contains(RoleRules.AdministratorPermission))
            await EnsureAdministratorRemainsAsync(roleAdministrative: new Dictionary<Guid, bool> { [role.Id] = false }, userRoles: null, ct);

        var added = perms.Except(before).OrderBy(x => x).ToList();
        var removed = before.Except(perms).OrderBy(x => x).ToList();
        var scopeChanged = role.TicketScope != ticketScope || role.CustomerScope != customerScope || role.IsLightAgent != r.IsLightAgent;
        var oldScope = new { ticketScope = role.TicketScope.ToString(), customerScope = role.CustomerScope.ToString(), role.IsLightAgent };
        role.Name = r.Name.Trim();
        role.Description = AdminRules.Clean(r.Description, 500);
        role.TicketScope = ticketScope;
        role.CustomerScope = customerScope;
        role.IsLightAgent = r.IsLightAgent;
        foreach (var p in role.Permissions.Where(p => removed.Contains(p.PermissionCode)).ToList()) db.RolePermissions.Remove(p);
        foreach (var p in added) db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionCode = p });

        if (added.Count > 0 || removed.Count > 0)
            audit.Write(AuditActions.PermissionsChanged, "Role", role.Id, $"Permissions of role '{role.Name}' changed (+{added.Count} / -{removed.Count})", new { added, removed });
        if (scopeChanged)
            audit.Write(AuditActions.RoleChanged, "Role", role.Id, $"Data scope of role '{role.Name}' changed",
                new { from = oldScope, to = new { ticketScope = ticketScope.ToString(), customerScope = customerScope.ToString(), role.IsLightAgent } });
        else if (added.Count == 0 && removed.Count == 0)
            audit.Write(AuditActions.RoleChanged, "Role", role.Id, $"Role '{role.Name}' updated");
        changes.Changed("roles", role.Id);
        await db.SaveChangesAsync(ct);
        await InvalidateMembersAsync(role.Id, ct);
        return await GetAsync(role.Id, ct);
    }

    /// <summary>Deletes a custom role. Members must be moved to <paramref name="reassignTo"/> (which the caller may grant).</summary>
    public async Task DeleteAsync(Guid id, Guid? reassignTo, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminRoles);
        var role = await db.Roles.Include(x => x.Permissions).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Role", id);
        if (role.IsSystem) throw new ConflictException("system_role_read_only", "System roles cannot be deleted.");
        EnsureCanManage(role.Permissions.Select(p => p.PermissionCode), role.TicketScope, role.CustomerScope, $"the role '{role.Name}'");
        var memberships = await db.UserRoles.Where(ur => ur.RoleId == id).ToListAsync(ct);
        Role? target = null;
        if (memberships.Count > 0)
        {
            if (reassignTo is not { } targetId || targetId == id)
                throw new ConflictException("role_in_use", $"{memberships.Count} user(s) hold this role. Choose a role to move them to.");
            target = await db.Roles.Include(x => x.Permissions).FirstOrDefaultAsync(x => x.Id == targetId, ct) ?? throw new NotFoundException("Role", targetId);
            EnsureCanManage(target.Permissions.Select(p => p.PermissionCode), target.TicketScope, target.CustomerScope, $"the role '{target.Name}'");
        }

        var memberIds = memberships.Select(m => m.UserId).ToList();
        if (role.Permissions.Any(p => p.PermissionCode == RoleRules.AdministratorPermission))
        {
            var userRoles = await ActiveUserRolesAsync(ct);
            foreach (var uid in memberIds.Where(userRoles.ContainsKey))
            {
                var set = userRoles[uid].Where(r => r != id).ToHashSet();
                if (target is not null) set.Add(target.Id);
                userRoles[uid] = set;
            }
            await EnsureAdministratorRemainsAsync(new Dictionary<Guid, bool> { [id] = false }, userRoles, ct);
        }

        if (target is not null)
        {
            var already = await db.UserRoles.Where(ur => ur.RoleId == target.Id && memberIds.Contains(ur.UserId)).Select(ur => ur.UserId).ToListAsync(ct);
            foreach (var uid in memberIds.Except(already)) db.UserRoles.Add(new UserRole { UserId = uid, RoleId = target.Id });
        }
        db.UserRoles.RemoveRange(memberships);
        db.Roles.Remove(role);
        audit.Write(AdminAuditActions.RoleDeleted, "Role", role.Id, $"Role '{role.Name}' deleted" + (target is null ? "" : $"; {memberIds.Count} user(s) moved to '{target.Name}'"),
            new { permissions = role.Permissions.Select(p => p.PermissionCode), movedTo = target?.Id, users = memberIds });
        changes.Changed("roles", role.Id);
        await db.SaveChangesAsync(ct);
        foreach (var uid in memberIds) await principals.InvalidateAsync(uid, ct);
    }

    // ───────────────────────────── Membership ─────────────────────────────

    public async Task<UserRolesDto> GetUserRolesAsync(Guid userId, CancellationToken ct)
    {
        ctx.RequireAny(Permissions.AdminRoles, Permissions.AdminUsers);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new NotFoundException("User", userId);
        var roles = await db.UserRoles.AsNoTracking().Where(ur => ur.UserId == userId).Join(db.Roles.AsNoTracking(), ur => ur.RoleId, r => r.Id, (ur, r) => r)
            .OrderBy(r => r.Name).Select(r => new IdName(r.Id, r.Name)).ToListAsync(ct);
        return new UserRolesDto(user.Id, user.DisplayName, user.Email, roles);
    }

    /// <summary>Replaces a user's roles (PUT /admin/users/{id}/roles). Use <see cref="EnsureCanAssignAsync"/> from other modules that assign roles.</summary>
    public async Task<UserRolesDto> SetUserRolesAsync(Guid userId, IReadOnlyList<Guid> roleIds, CancellationToken ct)
    {
        ctx.RequireAny(Permissions.AdminUsers, Permissions.AdminRoles);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new NotFoundException("User", userId);
        if (user.Kind == UserKind.System) throw new ConflictException("system_user", "System users cannot be given roles.");
        var wanted = roleIds.Distinct().ToList();
        await EnsureCanAssignAsync(userId, wanted, ct);
        var current = await db.UserRoles.Where(ur => ur.UserId == userId).ToListAsync(ct);
        var currentIds = current.Select(c => c.RoleId).ToHashSet();
        var added = wanted.Where(r => !currentIds.Contains(r)).ToList();
        var removed = current.Where(c => !wanted.Contains(c.RoleId)).ToList();
        if (added.Count == 0 && removed.Count == 0) return await GetUserRolesAsync(userId, ct);
        foreach (var r in added) db.UserRoles.Add(new UserRole { UserId = userId, RoleId = r });
        db.UserRoles.RemoveRange(removed);
        var names = await db.Roles.AsNoTracking().Where(r => added.Contains(r.Id) || removed.Select(x => x.RoleId).Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        audit.Write(AdminAuditActions.UserRolesChanged, "User", userId, $"Roles of {user.DisplayName} changed",
            new { added = added.Select(r => names.GetValueOrDefault(r, r.ToString())), removed = removed.Select(r => names.GetValueOrDefault(r.RoleId, r.RoleId.ToString())) });
        changes.Changed("roles", userId);
        await db.SaveChangesAsync(ct);
        await principals.InvalidateAsync(userId, ct);
        return await GetUserRolesAsync(userId, ct);
    }

    public async Task<AdminRoleDto> AddMemberAsync(Guid roleId, Guid userId, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminRoles);
        if (!await db.Roles.AnyAsync(r => r.Id == roleId, ct)) throw new NotFoundException("Role", roleId);
        var current = await db.UserRoles.Where(ur => ur.UserId == userId).Select(ur => ur.RoleId).ToListAsync(ct);
        if (!current.Contains(roleId)) await SetUserRolesAsync(userId, [.. current, roleId], ct);
        return await GetAsync(roleId, ct);
    }

    public async Task<AdminRoleDto> RemoveMemberAsync(Guid roleId, Guid userId, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminRoles);
        if (!await db.Roles.AnyAsync(r => r.Id == roleId, ct)) throw new NotFoundException("Role", roleId);
        var current = await db.UserRoles.Where(ur => ur.UserId == userId).Select(ur => ur.RoleId).ToListAsync(ct);
        if (current.Contains(roleId)) await SetUserRolesAsync(userId, current.Where(r => r != roleId).ToList(), ct);
        return await GetAsync(roleId, ct);
    }

    /// <summary>
    /// Guard for any role assignment (also for the agent form of the organisation module): every role must exist, the caller
    /// must hold all permissions of the roles added and removed (and of the user's current access), and at least one
    /// administrator must remain.
    /// </summary>
    public async Task EnsureCanAssignAsync(Guid userId, IReadOnlyCollection<Guid> roleIds, CancellationToken ct)
    {
        var roles = await db.Roles.AsNoTracking().Include(r => r.Permissions).Where(r => roleIds.Contains(r.Id)).ToListAsync(ct);
        if (roles.Count != roleIds.Distinct().Count())
            throw new ValidationException(new Dictionary<string, string> { ["roleIds"] = "One or more roles do not exist." });
        var currentIds = await db.UserRoles.AsNoTracking().Where(ur => ur.UserId == userId).Select(ur => ur.RoleId).ToListAsync(ct);
        var currentRoles = await db.Roles.AsNoTracking().Include(r => r.Permissions).Where(r => currentIds.Contains(r.Id)).ToListAsync(ct);
        foreach (var r in roles.Concat(currentRoles).DistinctBy(r => r.Id))
            EnsureCanManage(r.Permissions.Select(p => p.PermissionCode), r.TicketScope, r.CustomerScope, $"the role '{r.Name}'");

        var userRoles = await ActiveUserRolesAsync(ct);
        if (userRoles.ContainsKey(userId) || await db.Users.AnyAsync(u => u.Id == userId && u.IsActive, ct)) userRoles[userId] = roleIds.ToHashSet();
        await EnsureAdministratorRemainsAsync(null, userRoles, ct);
    }

    /// <summary>Guard for deactivating or deleting a user (organisation module): the last administrator cannot be removed.</summary>
    public async Task EnsureCanDeactivateAsync(Guid userId, CancellationToken ct)
    {
        var userRoles = await ActiveUserRolesAsync(ct);
        userRoles.Remove(userId);
        await EnsureAdministratorRemainsAsync(null, userRoles, ct);
    }

    // ───────────────────────────── Internals ─────────────────────────────

    private async Task<(TicketScope, CustomerScope, HashSet<string>)> ValidateAsync(Guid? id, SaveRoleRequest r, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        var name = r.Name?.Trim() ?? "";
        if (name.Length is < 2 or > 100) errors["name"] = "Enter a name of 2–100 characters.";
        else if (await db.Roles.AnyAsync(x => x.Name == name && x.Id != id, ct)) errors["name"] = "Another role already has this name.";
        var ticketScope = AdminRules.ParseEnum(r.TicketScope, "ticketScope", errors, TicketScope.Own);
        var customerScope = AdminRules.ParseEnum(r.CustomerScope, "customerScope", errors, CustomerScope.AccountManaged);
        var perms = (r.Permissions ?? []).Select(p => p.Trim()).ToHashSet(StringComparer.Ordinal);
        var unknown = perms.Where(p => !Permissions.AllCodes.Contains(p)).ToList();
        if (unknown.Count > 0) errors["permissions"] = $"Unknown permissions: {string.Join(", ", unknown)}.";
        if (errors.Count > 0) throw new ValidationException(errors);
        EnsureCanManage(perms, ticketScope, customerScope, "these permissions");
        return (ticketScope, customerScope, perms);
    }

    /// <summary>No privilege escalation: the caller must hold every permission and at least the data scope involved.</summary>
    private void EnsureCanManage(IEnumerable<string> permissions, TicketScope ticketScope, CustomerScope customerScope, string what)
    {
        var notHeld = RoleRules.NotHeld(ctx.Permissions, permissions);
        if (notHeld.Count > 0)
            throw new DomainException("privilege_escalation", $"You cannot manage {what}: it includes permissions you do not have ({string.Join(", ", notHeld)}).", 403);
        if (!RoleRules.ScopeWithin(ctx.TicketScope, ctx.CustomerScope, ticketScope, customerScope))
            throw new DomainException("privilege_escalation", $"You cannot manage {what}: its data scope is broader than yours.", 403);
    }

    private async Task<HashSet<Guid>> AdministrativeRoleIdsAsync(CancellationToken ct) =>
        (await db.RolePermissions.AsNoTracking().Where(p => p.PermissionCode == RoleRules.AdministratorPermission).Select(p => p.RoleId).ToListAsync(ct)).ToHashSet();

    /// <summary>Active (not disabled, not deleted) users → their role ids.</summary>
    private async Task<Dictionary<Guid, IReadOnlyCollection<Guid>>> ActiveUserRolesAsync(CancellationToken ct)
    {
        var rows = await db.UserRoles.AsNoTracking()
            .Join(db.Users.AsNoTracking().Where(u => u.IsActive && u.Kind != UserKind.System), ur => ur.UserId, u => u.Id, (ur, u) => new { ur.UserId, ur.RoleId })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.UserId).ToDictionary(g => g.Key, g => (IReadOnlyCollection<Guid>)g.Select(x => x.RoleId).ToHashSet());
    }

    private async Task EnsureAdministratorRemainsAsync(Dictionary<Guid, bool>? roleAdministrative, Dictionary<Guid, IReadOnlyCollection<Guid>>? userRoles, CancellationToken ct)
    {
        var adminRoles = await AdministrativeRoleIdsAsync(ct);
        var current = await ActiveUserRolesAsync(ct);
        var before = RoleRules.CountAdministrators(current, adminRoles);
        if (before == 0) return; // nothing to protect (should not happen in a provisioned tenant)
        var afterRoles = adminRoles.ToHashSet();
        foreach (var (roleId, isAdmin) in roleAdministrative ?? [])
            if (isAdmin) afterRoles.Add(roleId); else afterRoles.Remove(roleId);
        var after = RoleRules.CountAdministrators(userRoles ?? current, afterRoles);
        if (after == 0)
            throw new ConflictException("last_administrator", "This change would leave the organisation without an administrator (a user who can manage roles). Make someone else an administrator first.");
    }

    private async Task InvalidateMembersAsync(Guid roleId, CancellationToken ct)
    {
        var users = await db.UserRoles.AsNoTracking().Where(ur => ur.RoleId == roleId).Select(ur => ur.UserId).ToListAsync(ct);
        foreach (var u in users) await principals.InvalidateAsync(u, ct);
    }

    private async Task<AdminRoleDto> MapAsync(Role role, CancellationToken ct)
    {
        var members = await db.UserRoles.AsNoTracking().Where(ur => ur.RoleId == role.Id)
            .Join(db.Users.AsNoTracking(), ur => ur.UserId, u => u.Id, (ur, u) => u).OrderBy(u => u.DisplayName)
            .Select(u => new { u.Id, u.DisplayName, u.Email, u.IsActive }).ToListAsync(ct);
        var memberIds = members.Select(m => m.Id).ToList();
        var others = (await db.UserRoles.AsNoTracking().Where(ur => memberIds.Contains(ur.UserId) && ur.RoleId != role.Id)
                .Join(db.Roles.AsNoTracking(), ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name }).ToListAsync(ct))
            .GroupBy(x => x.UserId).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Name).OrderBy(n => n).ToList());
        return new AdminRoleDto(role.Id, role.Name, role.Description, role.IsSystem, role.IsLightAgent, role.TicketScope.ToString(), role.CustomerScope.ToString(),
            role.Permissions.Select(p => p.PermissionCode).OrderBy(p => p, StringComparer.Ordinal).ToList(),
            members.Select(m => new AdminRoleMemberDto(m.Id, m.DisplayName, m.Email, m.IsActive, others.GetValueOrDefault(m.Id) ?? [])).ToList(),
            role.UpdatedAt, AdminRules.Version(role.RowVersion));
    }
}
