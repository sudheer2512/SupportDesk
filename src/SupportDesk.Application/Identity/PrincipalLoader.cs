using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Contracts.Auth;
using SupportDesk.Contracts.Common;
using SupportDesk.Domain;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Organisation;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Identity;

/// <summary>Effective authorisation data of an agent, cached briefly so role changes apply within seconds.</summary>
public sealed record AgentPrincipal(
    Guid UserId,
    string Name,
    bool IsActive,
    bool IsLightAgent,
    HashSet<string> Permissions,
    TicketScope TicketScope,
    CustomerScope CustomerScope,
    List<Guid> DepartmentIds,
    List<Guid> TeamIds);

/// <summary>Loads principals into the <see cref="RequestContext"/> from validated token claims.</summary>
public sealed class PrincipalLoader(DeskDbContext db, RequestContext ctx, ITenantCache cache, LimitGuard limits)
{
    public static string CacheKey(Guid userId) => $"principal:{userId:N}";

    public async Task<AgentPrincipal?> LoadAgentAsync(Guid userId, CancellationToken ct = default) =>
        await cache.GetOrCreateAsync(CacheKey(userId), () => ComputeAsync(userId, ct), TimeSpan.FromSeconds(30), ct);

    private async Task<AgentPrincipal?> ComputeAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return null;
        var roles = await db.UserRoles.AsNoTracking().Where(ur => ur.UserId == userId).Select(ur => ur.Role!).ToListAsync(ct);
        var roleIds = roles.Select(r => r.Id).ToList();
        var permissions = await db.RolePermissions.AsNoTracking().Where(p => roleIds.Contains(p.RoleId)).Select(p => p.PermissionCode).Distinct().ToListAsync(ct);
        var departments = await db.AgentDepartments.AsNoTracking().Where(a => a.UserId == userId).Select(a => a.DepartmentId).ToListAsync(ct);
        var teams = await db.TeamMembers.AsNoTracking().Where(t => t.UserId == userId).Select(t => t.TeamId).ToListAsync(ct);
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.UserId == userId, ct);
        var isLight = user.Kind == UserKind.LightAgent || agent?.IsLightAgent == true || roles.Any(r => r.IsLightAgent);
        var permissionSet = permissions.ToHashSet();
        if (isLight)
        {
            // Light agents (collaborators) can never reply to customers or change ownership, whatever their roles say.
            permissionSet.ExceptWith([Domain.Rules.Permissions.TicketsReply, Domain.Rules.Permissions.TicketsAssign,
                Domain.Rules.Permissions.TicketsTransfer, Domain.Rules.Permissions.TicketsDelete, Domain.Rules.Permissions.TicketsBulk]);
        }
        return new AgentPrincipal(
            user.Id, user.DisplayName, user.IsActive && !user.IsDeleted, isLight, permissionSet,
            roles.Count == 0 ? TicketScope.Own : roles.Max(r => r.TicketScope),
            roles.Count == 0 ? CustomerScope.AccountManaged : roles.Max(r => r.CustomerScope),
            departments, teams);
    }

    /// <summary>Populates the request context for an authenticated agent. Returns false if the user is gone or disabled.</summary>
    public async Task<bool> ApplyAgentAsync(Guid tenantId, Guid userId, CancellationToken ct = default)
    {
        ctx.SetTenant(tenantId);
        var p = await LoadAgentAsync(userId, ct);
        if (p is null || !p.IsActive) return false;
        ctx.SetUser(tenantId, userId, p.Name, p.Permissions, p.TicketScope, p.CustomerScope, p.DepartmentIds, p.TeamIds, p.IsLightAgent);
        return true;
    }

    public async Task<MeDto> BuildMeAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == user.TenantId, ct);
        var p = await ComputeAsync(userId, ct) ?? throw new NotFoundException("User", userId);
        var depts = await db.Departments.AsNoTracking().Where(d => p.DepartmentIds.Contains(d.Id)).OrderBy(d => d.Name).Select(d => new IdName(d.Id, d.Name)).ToListAsync(ct);
        var teams = await db.Teams.AsNoTracking().Where(t => p.TeamIds.Contains(t.Id)).OrderBy(t => t.Name).Select(t => new IdName(t.Id, t.Name)).ToListAsync(ct);
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.UserId == userId, ct);
        var limitsInfo = await limits.GetLimitsAsync(ct);
        var roles = await db.UserRoles.AsNoTracking().Where(ur => ur.UserId == userId).Select(ur => ur.Role!.Name).OrderBy(n => n).ToListAsync(ct);
        return new MeDto(user.Id, tenant.Id, tenant.Slug, tenant.Name, user.DisplayName, user.Email, user.AvatarUrl, user.TimeZone, user.Locale,
            user.Kind.ToString(), p.IsLightAgent, p.Permissions.OrderBy(x => x).ToList(), p.TicketScope.ToString(), depts, teams,
            (agent?.Availability ?? AgentAvailability.Offline).ToString(), limitsInfo.Features, limitsInfo.Status.ToString(), user.MfaEnabled, user.Signature, roles);
    }

    public Task InvalidateAsync(Guid userId, CancellationToken ct = default) => cache.RemoveAsync(CacheKey(userId), ct);
}
