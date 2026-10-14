using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Administration;
using SupportDesk.Application.Common;
using SupportDesk.Application.Identity;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Routing;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Organisation;
using SupportDesk.Domain;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Organisation;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Organisation;

/// <summary>
/// Plan seat limits for agents. Full agents (incl. administrators and pending invitations) count against the plan's
/// MaxAgents; light agents against the subscription override "MaxLightAgents" (default: the same number as MaxAgents).
/// Deactivated users free their seat. Keeps the "agents" usage gauge in sync for the subscription module.
/// </summary>
public sealed class AgentSeatGuard(DeskDbContext db, LimitGuard limits, UsageMeter usage)
{
    public async Task<SeatUsageDto> UsageAsync(CancellationToken ct = default)
    {
        var plan = await limits.GetLimitsAsync(ct);
        var (full, light) = await CountAsync(ct);
        var now = db.Clock.UtcNow;
        var pending = await db.Set<AgentInvitation>().AsNoTracking().CountAsync(i => i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now, ct);
        return new SeatUsageDto(plan.PlanName, full, plan.MaxAgents, light, await MaxLightAsync(plan, ct), pending);
    }

    public async Task EnsureCanAddAsync(bool light, CancellationToken ct = default)
    {
        await limits.EnsureActiveAsync(ct);
        var plan = await limits.GetLimitsAsync(ct);
        var (full, lightCount) = await CountAsync(ct);
        if (light)
        {
            var max = await MaxLightAsync(plan, ct);
            if (max is { } m && lightCount + 1 > m)
                throw new LimitExceededException("light_agents", $"Your plan ({plan.PlanName}) allows {m} light agents and {lightCount} are in use. Deactivate a light agent or upgrade the plan.");
        }
        else if (plan.MaxAgents is { } m && full + 1 > m)
        {
            throw new LimitExceededException(UsageMetrics.Agents, $"Your plan ({plan.PlanName}) allows {m} agents and {full} are in use. Deactivate an agent or upgrade the plan.");
        }
    }

    public async Task SyncGaugeAsync(CancellationToken ct = default)
    {
        var (full, _) = await CountAsync(ct);
        await usage.SetGaugeAsync(UsageMetrics.Agents, full, ct);
    }

    private async Task<(int Full, int Light)> CountAsync(CancellationToken ct)
    {
        var rows = await db.Users.AsNoTracking().Where(u => u.IsActive && u.Kind != UserKind.System)
            .Select(u => new { u.Kind, Light = db.Agents.Any(a => a.UserId == u.Id && a.IsLightAgent) }).ToListAsync(ct);
        var light = rows.Count(r => r.Kind == UserKind.LightAgent || r.Light);
        return (rows.Count - light, light);
    }

    private async Task<int?> MaxLightAsync(PlanLimits plan, CancellationToken ct)
    {
        var overrides = await db.Subscriptions.AsNoTracking().Select(s => s.LimitOverridesJson).FirstOrDefaultAsync(ct);
        var o = Json.Object(overrides);
        var v = o.FirstOrDefault(kv => string.Equals(kv.Key, "MaxLightAgents", StringComparison.OrdinalIgnoreCase)).Value;
        return v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetInt32(out var n) ? n : plan.MaxAgents;
    }
}

/// <summary>
/// Agent administration: keyset-paged list, detail, profile, access (roles, departments, teams, light agent),
/// routing profile (capacity, languages, skills, routing attributes), availability, avatar, deactivation (sessions
/// revoked, open tickets reassigned by a durable job) and reactivation (plan limits). admin.users manages agents;
/// supervisors (agents.manage) manage availability and routing profiles of agents in their departments.
/// </summary>
public sealed class AgentAdminService(
    DeskDbContext db,
    RequestContext ctx,
    IAuditWriter audit,
    IEventPublisher events,
    IJobQueue jobs,
    PrincipalLoader principals,
    AgentSeatGuard seats,
    HtmlSanitization html,
    FileService files,
    RoleAdminService roleGuard)
{
    public const string AvatarPurpose = "avatar";
    private const int MaxLimit = 200;

    // ───────────────────────────── Queries ─────────────────────────────

    public async Task<PagedResult<AgentListItemDto>> ListAsync(AgentQuery q, CancellationToken ct = default)
    {
        ctx.RequireAny(OrgPermissions.ReadStructure);
        var users = db.Users.AsNoTracking().Where(u => u.Kind != UserKind.System && db.Agents.Any(a => a.UserId == u.Id));
        if (!SeesAllAgents())
        {
            var depts = ctx.DepartmentIds.ToList();
            users = users.Where(u => db.AgentDepartments.Any(a => a.UserId == u.Id && depts.Contains(a.DepartmentId)));
        }
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = q.Q.Trim();
            users = users.Where(u => u.DisplayName.Contains(term) || u.Email.Contains(term));
        }
        var statuses = (q.Status ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.ToLowerInvariant()).ToHashSet();
        if (statuses.Count > 0)
        {
            var active = statuses.Contains("active");
            var invited = statuses.Contains("invited");
            var deactivated = statuses.Contains("deactivated");
            users = users.Where(u => (active && u.IsActive && u.PasswordHash != null) || (invited && u.IsActive && u.PasswordHash == null) || (deactivated && !u.IsActive));
        }
        if (q.DepartmentId is { } d) users = users.Where(u => db.AgentDepartments.Any(a => a.UserId == u.Id && a.DepartmentId == d));
        if (q.TeamId is { } t) users = users.Where(u => db.TeamMembers.Any(m => m.UserId == u.Id && m.TeamId == t));
        if (q.Light is { } light)
            users = light
                ? users.Where(u => u.Kind == UserKind.LightAgent || db.Agents.Any(a => a.UserId == u.Id && a.IsLightAgent))
                : users.Where(u => u.Kind != UserKind.LightAgent && !db.Agents.Any(a => a.UserId == u.Id && a.IsLightAgent));

        var limit = Math.Clamp(q.Limit ?? 50, 1, MaxLimit);
        var cursor = DbExtensions.DecodeCursor(q.Cursor);
        if (cursor is not null)
        {
            var v = cursor.V;
            users = users.Where(u => string.Compare(u.DisplayName, v) > 0 || (u.DisplayName == v && u.Id.CompareTo(cursor.Id) > 0));
        }
        var total = cursor is null ? await users.CountAsync(ct) : (int?)null;
        var page = await users.OrderBy(u => u.DisplayName).ThenBy(u => u.Id).Take(limit + 1).ToListAsync(ct);
        var hasMore = page.Count > limit;
        if (hasMore) page.RemoveAt(page.Count - 1);
        var items = await MapListAsync(page, ct);
        return new PagedResult<AgentListItemDto>(items, hasMore ? DbExtensions.EncodeCursor(page[^1].DisplayName, page[^1].Id) : null, total);
    }

    private async Task<List<AgentListItemDto>> MapListAsync(List<User> page, CancellationToken ct)
    {
        var ids = page.Select(u => u.Id).ToList();
        var agents = await db.Agents.AsNoTracking().Where(a => ids.Contains(a.UserId)).ToDictionaryAsync(a => a.UserId, ct);
        var roles = await db.UserRoles.AsNoTracking().Where(r => ids.Contains(r.UserId)).Select(r => new { r.UserId, r.RoleId, r.Role!.Name }).ToListAsync(ct);
        var depts = await db.AgentDepartments.AsNoTracking().Where(a => ids.Contains(a.UserId))
            .Join(db.Departments.AsNoTracking(), a => a.DepartmentId, d => d.Id, (a, d) => new { a.UserId, d.Id, d.Name }).ToListAsync(ct);
        var loads = await OpenLoadsAsync(ids, ct);
        var now = db.Clock.UtcNow;
        var onLeave = await db.AgentLeaves.AsNoTracking().Where(l => ids.Contains(l.UserId) && l.Status == LeaveStatus.Approved && l.StartsAt <= now && l.EndsAt > now)
            .Select(l => l.UserId).Distinct().ToListAsync(ct);
        return page.Select(u =>
        {
            var a = agents.GetValueOrDefault(u.Id);
            return new AgentListItemDto(u.Id, u.DisplayName, u.Email, a?.Title, u.AvatarUrl, StatusOf(u), IsLight(u, a),
                (a?.Availability ?? AgentAvailability.Offline).ToString(),
                roles.Where(r => r.UserId == u.Id).Select(r => new IdName(r.RoleId, r.Name)).ToList(),
                depts.Where(d => d.UserId == u.Id).Select(d => new IdName(d.Id, d.Name)).ToList(),
                loads.GetValueOrDefault(u.Id), a?.MaxOpenTickets ?? 0, onLeave.Contains(u.Id), u.LastLoginAt);
        }).ToList();
    }

    public async Task<AgentDetailDto> GetAsync(Guid userId, CancellationToken ct = default)
    {
        ctx.RequireAny(OrgPermissions.ReadStructure);
        await EnsureInScopeAsync(userId, ct);
        return await BuildDetailAsync(userId, ct);
    }

    internal async Task<AgentDetailDto> BuildDetailAsync(Guid userId, CancellationToken ct)
    {
        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId && x.Kind != UserKind.System, ct) ?? throw new NotFoundException("Agent", userId);
        var a = await db.Agents.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct) ?? throw new NotFoundException("Agent profile", userId);
        var roles = await db.UserRoles.AsNoTracking().Where(r => r.UserId == userId).Select(r => new IdName(r.RoleId, r.Role!.Name)).ToListAsync(ct);
        var depts = await db.AgentDepartments.AsNoTracking().Where(x => x.UserId == userId)
            .Join(db.Departments.AsNoTracking(), x => x.DepartmentId, d => d.Id, (x, d) => new AgentDepartmentDto(d.Id, d.Name, x.IsSupervisor)).ToListAsync(ct);
        var teams = await db.TeamMembers.AsNoTracking().Where(m => m.UserId == userId)
            .Join(db.Teams.AsNoTracking(), m => m.TeamId, t => t.Id, (m, t) => new IdName(t.Id, t.Name)).ToListAsync(ct);
        var skills = await db.AgentSkills.AsNoTracking().Where(s => s.UserId == userId)
            .Join(db.Skills.AsNoTracking(), s => s.SkillId, k => k.Id, (s, k) => new AgentSkillDto(k.Id, k.Name, k.Category, s.Level)).ToListAsync(ct);
        var attrs = await db.AgentRoutingAttributes.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct);
        var labels = await AttributeLabelsAsync(attrs, ct);
        var shifts = await db.AgentShiftAssignments.AsNoTracking().Where(s => s.UserId == userId)
            .Join(db.Shifts.AsNoTracking(), s => s.ShiftId, sh => sh.Id, (s, sh) => new AgentShiftDto(s.Id, sh.Id, sh.Name, s.EffectiveFrom, s.EffectiveTo))
            .ToListAsync(ct);
        var open = await db.Tickets.AsNoTracking().CountAsync(t => t.AssigneeId == userId && t.StatusCategory < StatusCategory.Resolved, ct);
        var managed = await db.Customers.AsNoTracking().Where(c => c.AccountManagerUserId == userId).OrderBy(c => c.Name).Take(50)
            .Select(c => new IdName(c.Id, c.Name)).ToListAsync(ct);
        var now = db.Clock.UtcNow;
        var invitation = u.PasswordHash is null
            ? await db.Set<AgentInvitation>().AsNoTracking().Where(i => i.UserId == userId && i.AcceptedAt == null && i.RevokedAt == null)
                .OrderByDescending(i => i.CreatedAt).Select(i => new AgentInvitationDto(i.CreatedAt, i.ExpiresAt, i.ExpiresAt <= now)).FirstOrDefaultAsync(ct)
            : null;
        var reportsTo = a.ReportsToUserId is { } rt ? await db.Users.AsNoTracking().Where(x => x.Id == rt).Select(x => x.DisplayName).FirstOrDefaultAsync(ct) : null;
        return new AgentDetailDto(u.Id, u.Email, u.DisplayName, u.FirstName, u.LastName, a.Title, u.Phone, u.TimeZone, u.Locale, u.Signature, u.AvatarUrl,
            StatusOf(u), u.Kind.ToString(), IsLight(u, a), a.Availability.ToString(), a.AvailabilityChangedAt, u.LastLoginAt, u.MfaEnabled,
            roles.OrderBy(r => r.Name).ToList(), depts.OrderBy(d => d.Name).ToList(), teams.OrderBy(t => t.Name).ToList(), a.ReportsToUserId, reportsTo,
            a.MaxOpenTickets, a.MaxConcurrentChats, Json.StringList(a.LanguagesJson), skills.OrderBy(s => s.Name).ToList(),
            attrs.Select(x => new RoutingAttributeDto(x.Id, x.Kind.ToString(), x.Value, x.RefId, labels.GetValueOrDefault(x.Id))).ToList(),
            shifts.OrderByDescending(s => s.EffectiveFrom).ToList(), open, managed, invitation,
            OrgRowVersion.Encode(a.RowVersion));
    }

    public Task<SeatUsageDto> SeatsAsync(CancellationToken ct = default)
    {
        ctx.RequireAny(OrgPermissions.ReadStructure);
        return seats.UsageAsync(ct);
    }

    // ───────────────────────────── Commands ─────────────────────────────

    public async Task<AgentDetailDto> UpdateProfileAsync(Guid userId, UpdateAgentProfileRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminUsers);
        var (u, a) = await LoadAsync(userId, ct);
        OrgRowVersion.Apply(db, a, r.RowVersion, "agent");
        var errors = new Dictionary<string, string>();
        var name = r.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120) errors["displayName"] = "Enter a name of up to 120 characters.";
        if (!string.IsNullOrWhiteSpace(r.TimeZone) && !TimeZoneInfo.TryFindSystemTimeZoneById(r.TimeZone, out _)) errors["timeZone"] = "Unknown time zone.";
        if (r.Locale is { Length: > 16 }) errors["locale"] = "Invalid locale.";
        if (r.Phone is { Length: > 40 }) errors["phone"] = "Phone number is too long.";
        if (errors.Count > 0) throw new ValidationException(errors);
        u.DisplayName = name!;
        u.FirstName = string.IsNullOrWhiteSpace(r.FirstName) ? null : r.FirstName.Trim();
        u.LastName = string.IsNullOrWhiteSpace(r.LastName) ? null : r.LastName.Trim();
        u.Phone = string.IsNullOrWhiteSpace(r.Phone) ? null : r.Phone.Trim();
        if (!string.IsNullOrWhiteSpace(r.TimeZone)) u.TimeZone = r.TimeZone;
        if (!string.IsNullOrWhiteSpace(r.Locale)) u.Locale = r.Locale.Trim();
        u.Signature = string.IsNullOrWhiteSpace(r.Signature) ? null : html.Sanitize(r.Signature);
        a.Title = string.IsNullOrWhiteSpace(r.Title) ? null : r.Title.Trim();
        a.UpdatedAt = db.Clock.UtcNow; // bump the agent rowversion for the profile aggregate
        audit.Write(AuditActions.UserUpdated, "User", u.Id, $"Profile of {u.DisplayName} updated");
        await db.SaveChangesAsync(ct);
        await principals.InvalidateAsync(u.Id, ct);
        return await BuildDetailAsync(u.Id, ct);
    }

    public async Task<AgentDetailDto> UpdateAccessAsync(Guid userId, UpdateAgentAccessRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminUsers);
        var (u, a) = await LoadAsync(userId, ct);
        OrgRowVersion.Apply(db, a, r.RowVersion, "agent");
        var errors = new Dictionary<string, string>();
        var roleIds = r.RoleIds.Distinct().ToList();
        var deptIds = r.DepartmentIds.Distinct().ToList();
        var teamIds = r.TeamIds.Distinct().ToList();
        if (roleIds.Count == 0) errors["roleIds"] = "Choose at least one role.";
        else if (await db.Roles.CountAsync(x => roleIds.Contains(x.Id), ct) != roleIds.Count) errors["roleIds"] = "Unknown role.";
        if (deptIds.Count == 0) errors["departmentIds"] = "Choose at least one department.";
        else if (await db.Departments.CountAsync(d => deptIds.Contains(d.Id), ct) != deptIds.Count) errors["departmentIds"] = "Unknown department.";
        if (r.SupervisorDepartmentIds.Except(deptIds).Any()) errors["supervisorDepartmentIds"] = "Supervised departments must be among the agent's departments.";
        if (teamIds.Count > 0 && await db.Teams.CountAsync(t => teamIds.Contains(t.Id) && deptIds.Contains(t.DepartmentId), ct) != teamIds.Count)
            errors["teamIds"] = "Teams must belong to the selected departments.";
        if (r.ReportsToUserId is { } rt && (rt == userId || !await db.Users.AnyAsync(x => x.Id == rt && x.IsActive, ct))) errors["reportsToUserId"] = "Choose another active user.";
        if (errors.Count > 0) throw new ValidationException(errors);
        // No privilege escalation: the caller must hold every permission of the roles added and removed.
        await roleGuard.EnsureCanAssignAsync(userId, roleIds, ct);

        var adminRole = await db.Roles.Where(x => x.Name == "Administrator").Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
        var wasAdmin = adminRole is { } ar && await db.UserRoles.AnyAsync(x => x.UserId == userId && x.RoleId == ar, ct);
        if (wasAdmin && !roleIds.Contains(adminRole!.Value) && !await OtherActiveAdminExistsAsync(userId, adminRole.Value, ct))
            throw new ConflictException("last_administrator", "This is the last active administrator. Give another user the Administrator role first.");

        var becomesLight = r.IsLightAgent;
        if (IsLight(u, a) && !becomesLight) await seats.EnsureCanAddAsync(light: false, ct);
        else if (!IsLight(u, a) && becomesLight)
        {
            await seats.EnsureCanAddAsync(light: true, ct);
            if (await db.Tickets.AnyAsync(t => t.AssigneeId == userId && t.StatusCategory < StatusCategory.Resolved, ct))
                throw new ConflictException("has_open_tickets", "Light agents cannot own tickets. Reassign this agent's open tickets first.");
        }
        var removedDepts = await db.AgentDepartments.Where(x => x.UserId == userId && !deptIds.Contains(x.DepartmentId)).Select(x => x.DepartmentId).ToListAsync(ct);
        if (removedDepts.Count > 0 && await db.Tickets.AnyAsync(t => t.AssigneeId == userId && removedDepts.Contains(t.DepartmentId) && t.StatusCategory < StatusCategory.Resolved, ct))
            throw new ConflictException("has_open_tickets", "The agent still owns open tickets in a department being removed. Reassign them first.");

        var before = new { roles = await db.UserRoles.Where(x => x.UserId == userId).Select(x => x.RoleId).ToListAsync(ct), departments = removedDepts };
        // Roles
        var currentRoles = await db.UserRoles.Where(x => x.UserId == userId).ToListAsync(ct);
        db.UserRoles.RemoveRange(currentRoles.Where(x => !roleIds.Contains(x.RoleId)));
        foreach (var id in roleIds.Where(id => currentRoles.All(x => x.RoleId != id))) db.UserRoles.Add(new UserRole { UserId = userId, RoleId = id });
        // Departments
        var currentDepts = await db.AgentDepartments.Where(x => x.UserId == userId).ToListAsync(ct);
        db.AgentDepartments.RemoveRange(currentDepts.Where(x => !deptIds.Contains(x.DepartmentId)));
        foreach (var x in currentDepts.Where(x => deptIds.Contains(x.DepartmentId))) x.IsSupervisor = r.SupervisorDepartmentIds.Contains(x.DepartmentId);
        foreach (var id in deptIds.Where(id => currentDepts.All(x => x.DepartmentId != id)))
            db.AgentDepartments.Add(new AgentDepartment { UserId = userId, DepartmentId = id, IsSupervisor = r.SupervisorDepartmentIds.Contains(id) });
        // Teams
        var currentTeams = await db.TeamMembers.Where(x => x.UserId == userId).ToListAsync(ct);
        db.TeamMembers.RemoveRange(currentTeams.Where(x => !teamIds.Contains(x.TeamId)));
        foreach (var id in teamIds.Where(id => currentTeams.All(x => x.TeamId != id))) db.TeamMembers.Add(new TeamMember { TeamId = id, UserId = userId });

        a.IsLightAgent = becomesLight;
        if (u.Kind != UserKind.Administrator) u.Kind = becomesLight ? UserKind.LightAgent : UserKind.Agent;
        a.ReportsToUserId = r.ReportsToUserId;
        a.UpdatedAt = db.Clock.UtcNow;
        audit.Write(OrgAuditActions.AgentAccessChanged, "User", userId, $"Access of {u.DisplayName} changed",
            new { before, after = new { roles = roleIds, departments = deptIds, teams = teamIds, light = becomesLight } });
        await db.SaveChangesAsync(ct);
        await principals.InvalidateAsync(userId, ct);
        await seats.SyncGaugeAsync(ct);
        return await BuildDetailAsync(userId, ct);
    }

    public async Task<AgentDetailDto> UpdateRoutingProfileAsync(Guid userId, UpdateRoutingProfileRequest r, CancellationToken ct = default)
    {
        await RequireManageAgentAsync(userId, ct);
        var (_, a) = await LoadAsync(userId, ct);
        OrgRowVersion.Apply(db, a, r.RowVersion, "agent");
        var errors = new Dictionary<string, string>();
        if (r.MaxOpenTickets is < 1 or > 1000) errors["maxOpenTickets"] = "Use 1–1000.";
        if (r.MaxConcurrentChats is < 0 or > 50) errors["maxConcurrentChats"] = "Use 0–50.";
        var languages = r.Languages.Select(l => l.Trim().ToLowerInvariant()).Where(l => l.Length > 0).Distinct().ToList();
        if (languages.Any(l => l.Length > 10 || !l.All(c => char.IsLetter(c) || c == '-'))) errors["languages"] = "Use language codes such as en, hi, ta.";
        var skillIds = r.Skills.Select(s => s.SkillId).Distinct().ToList();
        if (r.Skills.Any(s => s.Level is < 1 or > 5)) errors["skills"] = "Proficiency is 1 (basic) to 5 (expert).";
        else if (await db.Skills.CountAsync(s => skillIds.Contains(s.Id), ct) != skillIds.Count) errors["skills"] = "Unknown skill.";
        var attributes = new List<AgentRoutingAttribute>();
        foreach (var (attr, i) in r.Attributes.Select((x, i) => (x, i)))
        {
            if (!Enum.TryParse<RoutingAttributeKind>(attr.Kind, true, out var kind) || !Enum.IsDefined(kind)) { errors[$"attributes[{i}].kind"] = "Unknown attribute kind."; continue; }
            var needsRef = kind is RoutingAttributeKind.Customer or RoutingAttributeKind.Product or RoutingAttributeKind.Category;
            if (needsRef && attr.RefId is null) { errors[$"attributes[{i}].refId"] = $"Choose a {kind.ToString().ToLowerInvariant()}."; continue; }
            if (!needsRef && string.IsNullOrWhiteSpace(attr.Value)) { errors[$"attributes[{i}].value"] = "Enter a value."; continue; }
            var refOk = kind switch
            {
                RoutingAttributeKind.Customer => await db.Customers.AnyAsync(c => c.Id == attr.RefId, ct),
                RoutingAttributeKind.Product => await db.Products.AnyAsync(p => p.Id == attr.RefId, ct),
                RoutingAttributeKind.Category => await db.TicketCategories.AnyAsync(c => c.Id == attr.RefId, ct),
                _ => true
            };
            if (!refOk) { errors[$"attributes[{i}].refId"] = "Unknown reference."; continue; }
            attributes.Add(new AgentRoutingAttribute { UserId = userId, Kind = kind, Value = needsRef ? null : attr.Value!.Trim(), RefId = needsRef ? attr.RefId : null });
        }
        if (errors.Count > 0) throw new ValidationException(errors);

        a.MaxOpenTickets = r.MaxOpenTickets;
        a.MaxConcurrentChats = r.MaxConcurrentChats;
        a.LanguagesJson = Json.Serialize(languages.Count == 0 ? ["en"] : languages);
        a.UpdatedAt = db.Clock.UtcNow;
        await db.AgentSkills.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);
        await db.AgentRoutingAttributes.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);
        foreach (var s in r.Skills.GroupBy(s => s.SkillId).Select(g => g.Last())) db.AgentSkills.Add(new AgentSkill { UserId = userId, SkillId = s.SkillId, Level = s.Level });
        db.AgentRoutingAttributes.AddRange(attributes);
        audit.Write(AuditActions.UserUpdated, "User", userId, "Routing profile updated", new { r.MaxOpenTickets, r.MaxConcurrentChats, languages, skills = r.Skills.Count, attributes = attributes.Count });
        await db.SaveChangesAsync(ct);
        return await BuildDetailAsync(userId, ct);
    }

    public async Task<AgentDetailDto> SetAvailabilityAsync(Guid userId, SetAvailabilityRequest r, CancellationToken ct = default)
    {
        if (userId != ctx.UserId) await RequireManageAgentAsync(userId, ct);
        if (!Enum.TryParse<AgentAvailability>(r.Availability, true, out var value) || !Enum.IsDefined(value))
            throw new ValidationException(new Dictionary<string, string> { ["availability"] = "Use Online, Away, Busy or Offline." });
        var (u, a) = await LoadAsync(userId, ct);
        if (!u.IsActive) throw new ConflictException("agent_inactive", "The agent is deactivated.");
        if (a.Availability != value)
        {
            var from = a.Availability;
            a.Availability = value;
            a.AvailabilityChangedAt = db.Clock.UtcNow;
            events.Publish(OrgEvents.AvailabilityChanged, "Agent", userId, new AgentEventPayload(userId, value.ToString(), userId == ctx.UserId ? null : $"Set by {ctx.ActorName}"));
            if (userId != ctx.UserId)
                audit.Write(OrgAuditActions.AvailabilityChanged, "User", userId, $"{u.DisplayName}'s availability set to {value} by {ctx.ActorName}", new { from = from.ToString(), to = value.ToString() });
            await db.SaveChangesAsync(ct);
        }
        return await BuildDetailAsync(userId, ct);
    }

    public async Task<DeactivateAgentResultDto> DeactivateAsync(Guid userId, DeactivateAgentRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminUsers);
        if (userId == ctx.UserId) throw new ConflictException("cannot_deactivate_self", "You cannot deactivate your own account.");
        var (u, a) = await LoadAsync(userId, ct);
        if (!u.IsActive) throw new ConflictException("already_deactivated", "The agent is already deactivated.");
        var mode = (r.Mode ?? "route").Trim().ToLowerInvariant();
        if (mode is not ("reassign" or "unassign" or "route"))
            throw new ValidationException(new Dictionary<string, string> { ["mode"] = "Use reassign, unassign or route." });
        if (mode == "reassign")
        {
            if (r.ReassignToUserId is not { } target || target == userId ||
                !await db.Users.AnyAsync(x => x.Id == target && x.IsActive && x.Kind != UserKind.LightAgent, ct) ||
                await db.Agents.AnyAsync(x => x.UserId == target && x.IsLightAgent, ct))
                throw new ValidationException(new Dictionary<string, string> { ["reassignToUserId"] = "Choose another active (full) agent." });
        }
        // Only users who could grant the target's roles may deactivate them (a supervisor cannot lock out an administrator).
        var targetRoles = await db.UserRoles.AsNoTracking().Where(x => x.UserId == userId).Select(x => x.RoleId).ToListAsync(ct);
        await roleGuard.EnsureCanAssignAsync(userId, targetRoles, ct);
        await roleGuard.EnsureCanDeactivateAsync(userId, ct);
        var adminRole = await db.Roles.Where(x => x.Name == "Administrator").Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
        if (adminRole is { } ar && await db.UserRoles.AnyAsync(x => x.UserId == userId && x.RoleId == ar, ct) && !await OtherActiveAdminExistsAsync(userId, ar, ct))
            throw new ConflictException("last_administrator", "This is the last active administrator.");

        var now = db.Clock.UtcNow;
        u.IsActive = false;
        a.Availability = AgentAvailability.Offline;
        a.AvailabilityChangedAt = now;
        a.UpdatedAt = now;
        await db.UserSessions.Where(s => s.PrincipalKind == PrincipalKind.User && s.PrincipalId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now).SetProperty(x => x.RevokedReason, "deactivated"), ct);
        foreach (var inv in await db.Set<AgentInvitation>().Where(i => i.UserId == userId && i.AcceptedAt == null && i.RevokedAt == null).ToListAsync(ct)) inv.RevokedAt = now;
        foreach (var del in await db.AgentDelegations.Where(d => (d.DelegateUserId == userId || d.UserId == userId) && d.IsActive && d.EndsAt > now).ToListAsync(ct))
            del.IsActive = false;
        var open = await db.Tickets.CountAsync(t => t.AssigneeId == userId && t.StatusCategory < StatusCategory.Resolved, ct);
        if (open > 0) jobs.Enqueue(AgentOffboardJobHandler.JobType, new AgentOffboardPayload(userId, mode, r.ReassignToUserId), idempotencyKey: $"org.offboard:{userId:N}:{now.Ticks}");
        events.Publish(OrgEvents.AgentDeactivated, "Agent", userId, new AgentEventPayload(userId, "Offline", r.Reason));
        audit.Write(AuditActions.UserDeactivated, "User", userId, $"{u.DisplayName} deactivated", new { mode, r.ReassignToUserId, openTickets = open, r.Reason });
        await db.SaveChangesAsync(ct);
        await principals.InvalidateAsync(userId, ct);
        await seats.SyncGaugeAsync(ct);
        return new DeactivateAgentResultDto(await BuildDetailAsync(userId, ct), open, mode);
    }

    public async Task<AgentDetailDto> ReactivateAsync(Guid userId, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminUsers);
        var (u, a) = await LoadAsync(userId, ct);
        if (u.IsActive) return await BuildDetailAsync(userId, ct);
        await seats.EnsureCanAddAsync(IsLight(u, a), ct);
        u.IsActive = true;
        u.FailedLoginCount = 0;
        u.LockedUntil = null;
        a.UpdatedAt = db.Clock.UtcNow;
        events.Publish(OrgEvents.AgentReactivated, "Agent", userId, new AgentEventPayload(userId));
        audit.Write(OrgAuditActions.AgentReactivated, "User", userId, $"{u.DisplayName} reactivated");
        await db.SaveChangesAsync(ct);
        await principals.InvalidateAsync(userId, ct);
        await seats.SyncGaugeAsync(ct);
        return await BuildDetailAsync(userId, ct);
    }

    public async Task<AgentDetailDto> UploadAvatarAsync(Guid userId, Stream content, string fileName, string? contentType, CancellationToken ct = default)
    {
        if (userId != ctx.UserId) ctx.Require(Permissions.AdminUsers);
        var (u, _) = await LoadAsync(userId, ct);
        var file = await files.UploadAsync(content, fileName, contentType, AvatarPurpose, ct);
        if (file.ContentType is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp"))
            throw new ValidationException(new Dictionary<string, string> { ["file"] = "Upload a PNG, JPEG, GIF or WebP image." });
        if (file.SizeBytes > 2 * 1024 * 1024) throw new ValidationException(new Dictionary<string, string> { ["file"] = "Avatars may be at most 2 MB." });
        u.AvatarUrl = $"/api/v1/org/public/avatars/{file.Id}";
        audit.Write(AuditActions.UserUpdated, "User", u.Id, "Avatar changed");
        await db.SaveChangesAsync(ct);
        await principals.InvalidateAsync(u.Id, ct);
        return await BuildDetailAsync(u.Id, ct);
    }

    public async Task<AgentDetailDto> RemoveAvatarAsync(Guid userId, CancellationToken ct = default)
    {
        if (userId != ctx.UserId) ctx.Require(Permissions.AdminUsers);
        var (u, _) = await LoadAsync(userId, ct);
        u.AvatarUrl = null;
        await db.SaveChangesAsync(ct);
        return await BuildDetailAsync(u.Id, ct);
    }

    /// <summary>Anonymous avatar download (unguessable file id; images of purpose "avatar" only).</summary>
    public async Task<(StoredFile File, Stream Content)> OpenAvatarAsync(Guid fileId, IFileStorage storage, CancellationToken ct = default)
    {
        var file = await db.AllTenants<StoredFile>().AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId && f.Purpose == AvatarPurpose, ct);
        if (file is null || file.ScanStatus is ScanStatus.Infected or ScanStatus.Blocked || !file.ContentType.StartsWith("image/", StringComparison.Ordinal)
            || !FileService.IsInlineSafe(file.ContentType))
            throw new NotFoundException("Avatar", fileId);
        if (ctx.HasTenant && ctx.TenantId != file.TenantId) throw new NotFoundException("Avatar", fileId);
        return (file, await storage.OpenReadAsync(file.StorageKey, ct));
    }

    // ───────────────────────────── Helpers ─────────────────────────────

    private bool SeesAllAgents() => ctx.IsSystem || ctx.Has(Permissions.AdminUsers) || ctx.Has(Permissions.AdminOrganisation) || ctx.TicketScope == TicketScope.All;

    private async Task EnsureInScopeAsync(Guid userId, CancellationToken ct)
    {
        if (SeesAllAgents() || userId == ctx.UserId) return;
        var depts = ctx.DepartmentIds.ToList();
        if (!await db.AgentDepartments.AnyAsync(a => a.UserId == userId && depts.Contains(a.DepartmentId), ct)) throw new NotFoundException("Agent", userId);
    }

    /// <summary>admin.users, or agents.manage for agents sharing one of the supervisor's departments.</summary>
    internal async Task RequireManageAgentAsync(Guid userId, CancellationToken ct)
    {
        if (ctx.Has(Permissions.AdminUsers)) return;
        ctx.Require(Permissions.AgentsManage);
        if (ctx.TicketScope == TicketScope.All) return;
        var depts = ctx.DepartmentIds.ToList();
        if (!await db.AgentDepartments.AnyAsync(a => a.UserId == userId && depts.Contains(a.DepartmentId), ct))
            throw new NotFoundException("Agent", userId);
    }

    private async Task<(User User, Agent Agent)> LoadAsync(Guid userId, CancellationToken ct)
    {
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == userId && x.Kind != UserKind.System, ct) ?? throw new NotFoundException("Agent", userId);
        var a = await db.Agents.FirstOrDefaultAsync(x => x.UserId == userId, ct) ?? throw new NotFoundException("Agent profile", userId);
        return (u, a);
    }

    private async Task<bool> OtherActiveAdminExistsAsync(Guid userId, Guid adminRole, CancellationToken ct) =>
        await db.UserRoles.AnyAsync(x => x.RoleId == adminRole && x.UserId != userId && db.Users.Any(u => u.Id == x.UserId && u.IsActive && u.PasswordHash != null), ct);

    private async Task<Dictionary<Guid, int>> OpenLoadsAsync(List<Guid> ids, CancellationToken ct) =>
        await db.Tickets.AsNoTracking().Where(t => t.AssigneeId != null && ids.Contains(t.AssigneeId.Value) && t.StatusCategory < StatusCategory.Resolved)
            .GroupBy(t => t.AssigneeId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    private async Task<Dictionary<Guid, string>> AttributeLabelsAsync(List<AgentRoutingAttribute> attrs, CancellationToken ct)
    {
        var refIds = attrs.Where(a => a.RefId != null).Select(a => a.RefId!.Value).Distinct().ToList();
        if (refIds.Count == 0) return attrs.ToDictionary(a => a.Id, a => a.Value ?? "");
        var customers = await db.Customers.AsNoTracking().Where(c => refIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var products = await db.Products.AsNoTracking().Where(c => refIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var categories = await db.TicketCategories.AsNoTracking().Where(c => refIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return attrs.ToDictionary(a => a.Id, a => a.RefId is { } r
            ? customers.GetValueOrDefault(r) ?? products.GetValueOrDefault(r) ?? categories.GetValueOrDefault(r) ?? "Unknown"
            : a.Value ?? "");
    }

    internal static string StatusOf(User u) => !u.IsActive ? "Deactivated" : u.PasswordHash is null && u.ExternalSubject is null ? "Invited" : "Active";

    internal static bool IsLight(User u, Agent? a) => u.Kind == UserKind.LightAgent || a?.IsLightAgent == true;
}

/// <summary>
/// Agent invitations: creates the user + agent profile (roles, departments, teams) within plan limits and e-mails a
/// one-time code (72 h) through the system mailer; the invitee sets a password with the code (anonymous endpoint).
/// The code is shown in the response only in demo mode, like the MFA code on the login screen.
/// </summary>
public sealed class AgentInvitationService(
    DeskDbContext db,
    RequestContext ctx,
    IAuditWriter audit,
    ISystemMailer mailer,
    TenantSettingsService settings,
    AgentSeatGuard seats,
    AuthService auth,
    AgentAdminService agents,
    RoleAdminService roleGuard,
    IConfiguration configuration)
{
    public static readonly TimeSpan Validity = TimeSpan.FromHours(72);
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public async Task<InviteResultDto> InviteAsync(InviteAgentRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminUsers);
        var errors = new Dictionary<string, string>();
        var email = TextNormalization.NormalizeEmail(r.Email);
        if (email is null || !email.Contains('@') || email.Length > 254) errors["email"] = "Enter a valid e-mail address.";
        else if (await db.Users.AnyAsync(u => u.EmailNormalized == email, ct)) errors["email"] = "A user with this e-mail already exists.";
        var name = (r.Name ?? "").Trim();
        if (name.Length is < 2 or > 120) errors["name"] = "Enter the agent's name.";
        var deptIds = r.DepartmentIds.Distinct().ToList();
        if (deptIds.Count == 0) errors["departmentIds"] = "Choose at least one department.";
        else if (await db.Departments.CountAsync(d => deptIds.Contains(d.Id) && d.IsActive, ct) != deptIds.Count) errors["departmentIds"] = "Choose active departments.";
        var teamIds = r.TeamIds.Distinct().ToList();
        if (teamIds.Count > 0 && await db.Teams.CountAsync(t => teamIds.Contains(t.Id) && deptIds.Contains(t.DepartmentId) && t.IsActive, ct) != teamIds.Count)
            errors["teamIds"] = "Teams must be active teams of the selected departments.";
        var roleIds = r.RoleIds.Distinct().ToList();
        if (roleIds.Count == 0)
        {
            var fallback = await db.Roles.Where(x => x.Name == (r.IsLightAgent ? "Light Agent" : "Agent")).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
            if (fallback is { } f) roleIds.Add(f);
            else errors["roleIds"] = "Choose a role.";
        }
        else if (await db.Roles.CountAsync(x => roleIds.Contains(x.Id), ct) != roleIds.Count) errors["roleIds"] = "Unknown role.";
        if (!string.IsNullOrWhiteSpace(r.TimeZone) && !TimeZoneInfo.TryFindSystemTimeZoneById(r.TimeZone, out _)) errors["timeZone"] = "Unknown time zone.";
        if (errors.Count > 0) throw new ValidationException(errors);
        // No privilege escalation through invitations either (a new user has no roles yet, so only the added roles are checked).
        await roleGuard.EnsureCanAssignAsync(Guid.NewGuid(), roleIds, ct);
        await seats.EnsureCanAddAsync(r.IsLightAgent, ct);

        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == ctx.TenantId, ct);
        var user = new User
        {
            Email = r.Email.Trim(), EmailNormalized = email!, DisplayName = name, FirstName = name.Split(' ')[0],
            LastName = name.Contains(' ') ? name[(name.IndexOf(' ') + 1)..] : null, Kind = r.IsLightAgent ? UserKind.LightAgent : UserKind.Agent,
            IsActive = true, TimeZone = string.IsNullOrWhiteSpace(r.TimeZone) ? tenant.DefaultTimeZone : r.TimeZone, Locale = string.IsNullOrWhiteSpace(r.Locale) ? "en" : r.Locale
        };
        db.Users.Add(user);
        db.Agents.Add(new Agent { UserId = user.Id, Title = string.IsNullOrWhiteSpace(r.Title) ? null : r.Title.Trim(), IsLightAgent = r.IsLightAgent, Availability = AgentAvailability.Offline });
        foreach (var id in roleIds) db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = id });
        foreach (var id in deptIds) db.AgentDepartments.Add(new AgentDepartment { UserId = user.Id, DepartmentId = id, IsSupervisor = r.SupervisorDepartmentIds.Contains(id) });
        foreach (var id in teamIds) db.TeamMembers.Add(new TeamMember { TeamId = id, UserId = user.Id });
        var (code, expires) = NewInvitation(user);
        audit.Write(AuditActions.UserCreated, "User", user.Id, $"{name} <{email}> invited", new { roles = roleIds, departments = deptIds, light = r.IsLightAgent });
        audit.Write(OrgAuditActions.AgentInvited, "User", user.Id, $"Invitation sent to {email}");
        await db.SaveChangesAsync(ct);
        await SendAsync(user, tenant, code, expires, ct);
        await seats.SyncGaugeAsync(ct);
        var demo = (await settings.SecurityAsync(ct)).DemoMode;
        return new InviteResultDto(await agents.BuildDetailAsync(user.Id, ct), email!, expires, AcceptUrl(tenant.Slug, email!), demo ? code : null);
    }

    public async Task<InviteResultDto> ResendAsync(Guid userId, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminUsers);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new NotFoundException("Agent", userId);
        if (!user.IsActive) throw new ConflictException("agent_inactive", "Reactivate the agent before re-sending the invitation.");
        if (user.PasswordHash is not null) throw new ConflictException("already_accepted", "The agent has already set a password.");
        var now = db.Clock.UtcNow;
        foreach (var old in await db.Set<AgentInvitation>().Where(i => i.UserId == userId && i.AcceptedAt == null && i.RevokedAt == null).ToListAsync(ct)) old.RevokedAt = now;
        var (code, expires) = NewInvitation(user);
        audit.Write(OrgAuditActions.AgentInvited, "User", user.Id, $"Invitation re-sent to {user.EmailNormalized}");
        await db.SaveChangesAsync(ct);
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == ctx.TenantId, ct);
        await SendAsync(user, tenant, code, expires, ct);
        var demo = (await settings.SecurityAsync(ct)).DemoMode;
        return new InviteResultDto(await agents.BuildDetailAsync(user.Id, ct), user.EmailNormalized, expires, AcceptUrl(tenant.Slug, user.EmailNormalized), demo ? code : null);
    }

    /// <summary>Revokes pending invitations and deactivates the invited (never signed-in) user, freeing the seat.</summary>
    public async Task<AgentDetailDto> RevokeAsync(Guid userId, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminUsers);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new NotFoundException("Agent", userId);
        if (user.PasswordHash is not null) throw new ConflictException("already_accepted", "The invitation was already accepted; deactivate the agent instead.");
        var now = db.Clock.UtcNow;
        foreach (var inv in await db.Set<AgentInvitation>().Where(i => i.UserId == userId && i.AcceptedAt == null && i.RevokedAt == null).ToListAsync(ct)) inv.RevokedAt = now;
        user.IsActive = false;
        audit.Write(OrgAuditActions.InvitationRevoked, "User", user.Id, $"Invitation for {user.EmailNormalized} revoked");
        await db.SaveChangesAsync(ct);
        await seats.SyncGaugeAsync(ct);
        return await agents.BuildDetailAsync(userId, ct);
    }

    /// <summary>Anonymous: sets the invitee's password with the e-mailed code. Errors never reveal whether the account exists.</summary>
    public async Task<AcceptInvitationResultDto> AcceptAsync(AcceptInvitationRequest r, CancellationToken ct = default)
    {
        var invalid = new DomainException("invitation_invalid", "The invitation code is not valid or has expired. Ask your administrator to send a new invitation.", 400);
        var tenant = await auth.ResolveTenantAsync(r.Tenant, ct) ?? throw invalid;
        ctx.SetTenant(tenant.Id, tenant.Slug);
        var email = TextNormalization.NormalizeEmail(r.Email) ?? "";
        var user = await db.Users.FirstOrDefaultAsync(u => u.EmailNormalized == email && u.IsActive, ct);
        if (user is null || user.PasswordHash is not null) throw invalid;
        var now = db.Clock.UtcNow;
        var invitation = await db.Set<AgentInvitation>().Where(i => i.UserId == user.Id && i.AcceptedAt == null && i.RevokedAt == null)
            .OrderByDescending(i => i.CreatedAt).FirstOrDefaultAsync(ct);
        if (invitation is null || invitation.ExpiresAt <= now || invitation.Attempts >= 5) throw invalid;
        invitation.Attempts++;
        if (!TokenHasher.FixedEquals(TokenHasher.Hash(Normalize(r.Code)), invitation.CodeHash))
        {
            await db.SaveChangesAsync(ct);
            throw invalid;
        }
        await auth.SetPasswordAsync(user, r.Password ?? "", ct); // password policy → 422 validation_failed with the rules
        invitation.AcceptedAt = now;
        if (!string.IsNullOrWhiteSpace(r.DisplayName) && r.DisplayName.Trim().Length <= 120) user.DisplayName = r.DisplayName.Trim();
        audit.Write(OrgAuditActions.InvitationAccepted, "User", user.Id, $"{user.DisplayName} accepted the invitation and set a password");
        await db.SaveChangesAsync(ct);
        return new AcceptInvitationResultDto(tenant.Slug, user.EmailNormalized, user.DisplayName);
    }

    private (string Code, DateTime Expires) NewInvitation(User user)
    {
        var code = NewCode();
        var now = db.Clock.UtcNow;
        var expires = now.Add(Validity);
        db.Set<AgentInvitation>().Add(new AgentInvitation
        {
            UserId = user.Id, Email = user.EmailNormalized, CodeHash = TokenHasher.Hash(Normalize(code)), InvitedById = ctx.UserId, CreatedAt = now, ExpiresAt = expires
        });
        return (code, expires);
    }

    private async Task SendAsync(User user, Tenant tenant, string code, DateTime expires, CancellationToken ct)
    {
        var url = AcceptUrl(tenant.Slug, user.EmailNormalized);
        var who = System.Net.WebUtility.HtmlEncode(ctx.ActorName ?? "Your administrator");
        var org = System.Net.WebUtility.HtmlEncode(tenant.Name);
        var html = $"<p>Hello {System.Net.WebUtility.HtmlEncode(user.FirstName ?? user.DisplayName)},</p>" +
                   $"<p>{who} invited you to join <strong>{org}</strong> on SupportDesk.</p>" +
                   $"<p>Your invitation code is:</p><p style=\"font-size:22px;letter-spacing:4px\"><strong>{code}</strong></p>" +
                   $"<p><a href=\"{url}\">Accept the invitation and choose a password</a> (organisation <code>{tenant.Slug}</code>). The code expires on {expires:yyyy-MM-dd HH:mm} UTC.</p>";
        await mailer.SendAsync(tenant.Id, user.Email, $"You're invited to {tenant.Name} on SupportDesk", html,
            $"You're invited to {tenant.Name}. Code: {code}. Accept at {url} (organisation {tenant.Slug}).", null, ct);
    }

    private string AcceptUrl(string tenant, string email)
    {
        var baseUrl = (configuration["Desk:PublicBaseUrl"] ?? "").TrimEnd('/');
        return $"{baseUrl}/accept-invite?tenant={Uri.EscapeDataString(tenant)}&email={Uri.EscapeDataString(email)}";
    }

    private static string NewCode()
    {
        Span<char> chars = stackalloc char[9];
        for (var i = 0; i < 9; i++) chars[i] = i == 4 ? '-' : Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    private static string Normalize(string? code) => new string((code ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
}

public sealed record AgentOffboardPayload(Guid UserId, string Mode, Guid? TargetUserId);

/// <summary>
/// Reassigns a deactivated agent's open tickets: to the chosen agent where they belong to the ticket's department,
/// otherwise back to the queue; "route" re-runs automatic routing. Idempotent (only tickets still owned are touched).
/// </summary>
public sealed class AgentOffboardJobHandler(DeskDbContext db, TicketService tickets, RoutingService routing, ILogger<AgentOffboardJobHandler> logger)
    : JobHandler<AgentOffboardPayload>
{
    public const string JobType = "org.agent-offboard";
    public override string Type => JobType;

    protected override async Task ExecuteAsync(AgentOffboardPayload p, BackgroundJob job, CancellationToken ct)
    {
        var processed = new HashSet<Guid>();
        for (var round = 0; round < 200; round++)
        {
            var batch = await db.Tickets.AsNoTracking()
                .Where(t => t.AssigneeId == p.UserId && t.StatusCategory < StatusCategory.Resolved && !processed.Contains(t.Id)).OrderBy(t => t.Id).Select(t => t.Id)
                .Take(50).ToListAsync(ct);
            if (batch.Count == 0) return;
            foreach (var id in batch)
            {
                processed.Add(id);
                try
                {
                    // Loaded per ticket: the change tracker is cleared after each one.
                    var t = await db.Tickets.Include(x => x.Tags).FirstOrDefaultAsync(x => x.Id == id && x.AssigneeId == p.UserId, ct);
                    if (t is null) continue;
                    await ReassignAsync(t, p, ct);
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateConcurrencyException)
                {
                    processed.Remove(id);
                    logger.LogInformation("Ticket {Ticket} changed concurrently during off-boarding; retrying", id);
                }
                catch (DomainException ex)
                {
                    logger.LogWarning("Off-boarding could not reassign ticket {Ticket}: {Message}", id, ex.Message);
                }
                finally
                {
                    db.ChangeTracker.Clear();
                }
            }
        }
    }

    private async Task ReassignAsync(Ticket t, AgentOffboardPayload p, CancellationToken ct)
    {
        const string note = "Previous owner was deactivated";
        if (p.Mode == "reassign" && p.TargetUserId is { } target &&
            await db.AgentDepartments.AnyAsync(d => d.UserId == target && d.DepartmentId == t.DepartmentId, ct))
        {
            await tickets.AssignCoreAsync(t, target, null, note, ct);
            return;
        }
        await tickets.AssignCoreAsync(t, null, null, note, ct);
        if (p.Mode == "unassign") return;
        var decision = await routing.RouteAsync(t, ct);
        if (decision.AssigneeId is { } routed)
        {
            t.AssigneeId = null; // let the assignment record history, events and the status change
            await tickets.AssignCoreAsync(t, routed, null, $"{note}; re-routed ({decision.Strategy})", ct);
        }
    }
}
