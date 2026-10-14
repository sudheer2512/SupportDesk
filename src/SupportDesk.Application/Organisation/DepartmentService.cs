using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Routing;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Organisation;
using SupportDesk.Domain;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Organisation;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Organisation;

/// <summary>
/// Departments: list with counts, create (with a default routing team), update (optimistic concurrency), archive with
/// open-ticket reassignment (moved by a durable job) and restore. Structure changes need admin.organisation; reading
/// needs any organisation/supervision permission.
/// </summary>
public sealed class DepartmentService(DeskDbContext db, RequestContext ctx, IAuditWriter audit, IJobQueue jobs)
{
    public async Task<List<DepartmentDto>> ListAsync(bool includeArchived, CancellationToken ct = default)
    {
        ctx.RequireAny(OrgPermissions.ReadStructure);
        var q = db.Departments.AsNoTracking();
        if (!includeArchived) q = q.Where(d => d.IsActive);
        var rows = await q.OrderBy(d => d.SortOrder).ThenBy(d => d.Name).ToListAsync(ct);
        return await MapAsync(rows, ct);
    }

    public async Task<DepartmentDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        ctx.RequireAny(OrgPermissions.ReadStructure);
        var d = await db.Departments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Department", id);
        return (await MapAsync([d], ct))[0];
    }

    public async Task<DepartmentDto> CreateAsync(SaveDepartmentRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminOrganisation);
        var d = new Department();
        await ApplyAsync(d, r, isNew: true, ct);
        var team = new Team { DepartmentId = d.Id, Name = $"{d.Name} – Level 1", RoutingStrategy = RoutingStrategy.LoadBalanced, OnlyAssignToOnlineAgents = false };
        d.DefaultTeamId = team.Id;
        db.Departments.Add(d);
        db.Teams.Add(team);
        audit.Write(OrgAuditActions.DepartmentChanged, "Department", d.Id, $"Department '{d.Name}' created");
        await SaveAsync(ct);
        return await GetAsync(d.Id, ct);
    }

    public async Task<DepartmentDto> UpdateAsync(Guid id, SaveDepartmentRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminOrganisation);
        var d = await db.Departments.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Department", id);
        OrgRowVersion.Apply(db, d, r.RowVersion, "department");
        var before = new { d.Name, d.Code, d.BusinessCalendarId, d.DefaultSlaPolicyId, d.DefaultTeamId, d.VisibleInPortal, d.TransferRequiresApproval };
        await ApplyAsync(d, r, isNew: false, ct);
        audit.Write(OrgAuditActions.DepartmentChanged, "Department", d.Id, $"Department '{d.Name}' updated",
            new { from = before, to = new { d.Name, d.Code, d.BusinessCalendarId, d.DefaultSlaPolicyId, d.DefaultTeamId, d.VisibleInPortal, d.TransferRequiresApproval } });
        await SaveAsync(ct);
        return await GetAsync(d.Id, ct);
    }

    private async Task ApplyAsync(Department d, SaveDepartmentRequest r, bool isNew, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        var name = (r.Name ?? "").Trim();
        if (name.Length is < 2 or > 120) errors["name"] = "Enter a name of 2–120 characters.";
        var code = string.IsNullOrWhiteSpace(r.Code) ? CodeFrom(name) : new string(r.Code.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (code.Length is < 2 or > 12) errors["code"] = "Use 2–12 letters or digits.";
        else if (await db.Departments.AnyAsync(x => x.Code == code && x.Id != d.Id, ct)) errors["code"] = "Another department uses this code.";
        if (await db.Departments.AnyAsync(x => x.Name == name && x.Id != d.Id && x.IsActive, ct)) errors["name"] = "Another department has this name.";
        var email = string.IsNullOrWhiteSpace(r.Email) ? null : TextNormalization.NormalizeEmail(r.Email);
        if (!string.IsNullOrWhiteSpace(r.Email) && (email is null || !email.Contains('@'))) errors["email"] = "Enter a valid e-mail alias.";
        if (r.BusinessCalendarId is { } cal && !await db.BusinessCalendars.AnyAsync(c => c.Id == cal, ct)) errors["businessCalendarId"] = "Unknown calendar.";
        if (r.DefaultSlaPolicyId is { } pol && !await db.SlaPolicies.AnyAsync(p => p.Id == pol, ct)) errors["defaultSlaPolicyId"] = "Unknown SLA policy.";
        if (r.DefaultTeamId is { } team && (isNew || !await db.Teams.AnyAsync(t => t.Id == team && t.DepartmentId == d.Id && t.IsActive, ct)))
            errors["defaultTeamId"] = "The default team must be an active team of this department.";
        if (r.ParentDepartmentId is { } parent && (parent == d.Id || !await db.Departments.AnyAsync(x => x.Id == parent, ct)))
            errors["parentDepartmentId"] = "Unknown parent department.";
        if (r.HeadUserId is { } head && !await db.Users.AnyAsync(u => u.Id == head && u.IsActive, ct)) errors["headUserId"] = "Unknown or inactive user.";
        if (errors.Count > 0) throw new ValidationException(errors);

        d.Name = name;
        d.Code = code;
        d.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
        d.Email = email;
        d.BusinessCalendarId = r.BusinessCalendarId;
        d.DefaultSlaPolicyId = r.DefaultSlaPolicyId;
        if (!isNew) d.DefaultTeamId = r.DefaultTeamId ?? d.DefaultTeamId;
        d.ParentDepartmentId = r.ParentDepartmentId;
        d.HeadUserId = r.HeadUserId;
        d.VisibleInPortal = r.VisibleInPortal && d.IsActive;
        d.TransferRequiresApproval = r.TransferRequiresApproval;
        d.SortOrder = r.SortOrder;
    }

    public async Task<ArchiveDepartmentResultDto> ArchiveAsync(Guid id, ArchiveDepartmentRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminOrganisation);
        var d = await db.Departments.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Department", id);
        if (!string.IsNullOrWhiteSpace(r.RowVersion)) OrgRowVersion.Apply(db, d, r.RowVersion, "department");
        if (!d.IsActive) throw new ConflictException("already_archived", "The department is already archived.");
        if (!await db.Departments.AnyAsync(x => x.Id != id && x.IsActive, ct))
            throw new ConflictException("last_department", "At least one active department is required.");
        if (await db.Brands.AnyAsync(b => b.DefaultDepartmentId == id, ct))
            throw new ConflictException("department_in_use", "A brand/portal uses this department as its default. Change the brand first.");
        var open = await db.Tickets.CountAsync(t => t.DepartmentId == id && t.StatusCategory < StatusCategory.Resolved, ct);
        if (open > 0)
        {
            if (r.ReassignToDepartmentId is not { } target)
                throw new ConflictException("department_has_open_tickets", $"The department has {open} open tickets. Choose a department to move them to.");
            if (target == id || !await db.Departments.AnyAsync(x => x.Id == target && x.IsActive, ct))
                throw new ValidationException(new Dictionary<string, string> { ["reassignToDepartmentId"] = "Choose another active department." });
            jobs.Enqueue(DepartmentReassignJobHandler.JobType, new DepartmentReassignPayload(id, target), idempotencyKey: $"org.dept-reassign:{id:N}:{db.Clock.UtcNow.Ticks}");
        }
        d.IsActive = false;
        d.ArchivedAt = db.Clock.UtcNow;
        d.VisibleInPortal = false;
        audit.Write(OrgAuditActions.DepartmentArchived, "Department", d.Id, $"Department '{d.Name}' archived",
            new { openTickets = open, r.ReassignToDepartmentId });
        await SaveAsync(ct);
        return new ArchiveDepartmentResultDto(await GetAsync(id, ct), open, open > 0 ? r.ReassignToDepartmentId : null);
    }

    public async Task<DepartmentDto> RestoreAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminOrganisation);
        var d = await db.Departments.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Department", id);
        if (d.IsActive) return await GetAsync(id, ct);
        d.IsActive = true;
        d.ArchivedAt = null;
        audit.Write(OrgAuditActions.DepartmentChanged, "Department", d.Id, $"Department '{d.Name}' restored");
        await SaveAsync(ct);
        return await GetAsync(id, ct);
    }

    private async Task<List<DepartmentDto>> MapAsync(List<Department> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.Id).ToList();
        var teams = await db.Teams.AsNoTracking().Where(t => ids.Contains(t.DepartmentId)).Select(t => new { t.Id, t.DepartmentId, t.Name, t.IsActive }).ToListAsync(ct);
        var agentCounts = await db.AgentDepartments.AsNoTracking().Where(a => ids.Contains(a.DepartmentId))
            .Join(db.Users.AsNoTracking().Where(u => u.IsActive), a => a.UserId, u => u.Id, (a, u) => a.DepartmentId)
            .GroupBy(x => x).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var open = await db.Tickets.AsNoTracking().Where(t => ids.Contains(t.DepartmentId) && t.StatusCategory < StatusCategory.Resolved)
            .GroupBy(t => t.DepartmentId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var calIds = rows.Where(r => r.BusinessCalendarId != null).Select(r => r.BusinessCalendarId!.Value).Distinct().ToList();
        var calendars = await db.BusinessCalendars.AsNoTracking().Where(c => calIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var polIds = rows.Where(r => r.DefaultSlaPolicyId != null).Select(r => r.DefaultSlaPolicyId!.Value).Distinct().ToList();
        var policies = await db.SlaPolicies.AsNoTracking().Where(p => polIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var heads = await OrgNames.UsersAsync(db, rows.Select(r => r.HeadUserId), ct);
        return rows.Select(d => new DepartmentDto(
            d.Id, d.Name, d.Code, d.Description, d.Email, d.IsActive, d.ArchivedAt, d.ParentDepartmentId,
            d.DefaultTeamId, teams.FirstOrDefault(t => t.Id == d.DefaultTeamId)?.Name,
            d.BusinessCalendarId, d.BusinessCalendarId is { } c ? calendars.GetValueOrDefault(c) : null,
            d.DefaultSlaPolicyId, d.DefaultSlaPolicyId is { } p ? policies.GetValueOrDefault(p) : null,
            d.HeadUserId, d.HeadUserId is { } h ? heads.GetValueOrDefault(h) : null,
            d.VisibleInPortal, d.TransferRequiresApproval, d.SortOrder,
            teams.Count(t => t.DepartmentId == d.Id && t.IsActive), agentCounts.GetValueOrDefault(d.Id), open.GetValueOrDefault(d.Id),
            OrgRowVersion.Encode(d.RowVersion))).ToList();
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("IX_Departments_TenantId_Code", StringComparison.Ordinal) == true)
        {
            throw new ValidationException(new Dictionary<string, string> { ["code"] = "Another department uses this code." });
        }
    }

    private static string CodeFrom(string name)
    {
        var letters = new string(name.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        return letters.Length <= 8 ? letters : letters[..8];
    }
}

public sealed record DepartmentReassignPayload(Guid DepartmentId, Guid TargetDepartmentId);

/// <summary>Moves the open tickets of an archived department to the chosen department (batches; idempotent).</summary>
public sealed class DepartmentReassignJobHandler(DeskDbContext db, TicketService tickets, ILogger<DepartmentReassignJobHandler> logger)
    : JobHandler<DepartmentReassignPayload>
{
    public const string JobType = "org.department-reassign";
    public override string Type => JobType;

    protected override async Task ExecuteAsync(DepartmentReassignPayload p, Domain.Platform.BackgroundJob job, CancellationToken ct)
    {
        for (var round = 0; round < 200; round++)
        {
            var batch = await db.Tickets.AsNoTracking()
                .Where(t => t.DepartmentId == p.DepartmentId && t.StatusCategory < StatusCategory.Resolved).OrderBy(t => t.Id).Select(t => t.Id).Take(50).ToListAsync(ct);
            if (batch.Count == 0) return;
            foreach (var id in batch)
            {
                try
                {
                    var t = await db.Tickets.Include(x => x.Tags).FirstOrDefaultAsync(x => x.Id == id && x.DepartmentId == p.DepartmentId, ct);
                    if (t is null) continue;
                    await tickets.TransferCoreAsync(t, p.TargetDepartmentId, null, null, "Department archived", ct);
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateConcurrencyException)
                {
                    logger.LogInformation("Ticket {Ticket} changed concurrently; retrying in the next round", id);
                }
                finally
                {
                    db.ChangeTracker.Clear();
                }
            }
        }
    }
}

/// <summary>
/// Teams within departments: members, lead, routing configuration (mode, availability/shift awareness, capacity cap,
/// fallback queue) and the routing preview. Administrators manage every team; supervisors (agents.manage) manage the
/// teams of their own departments.
/// </summary>
public sealed class TeamService(DeskDbContext db, RequestContext ctx, IAuditWriter audit, RoutingService routing)
{
    public async Task<List<TeamDto>> ListAsync(Guid? departmentId, bool includeInactive, CancellationToken ct = default)
    {
        ctx.RequireAny(OrgPermissions.ReadStructure);
        var q = db.Teams.AsNoTracking();
        if (departmentId is { } d) q = q.Where(t => t.DepartmentId == d);
        if (!includeInactive) q = q.Where(t => t.IsActive);
        var rows = await q.OrderBy(t => t.Name).ToListAsync(ct);
        return await MapAsync(rows, ct);
    }

    public async Task<TeamDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        ctx.RequireAny(OrgPermissions.ReadStructure);
        var t = await db.Teams.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Team", id);
        return (await MapAsync([t], ct))[0];
    }

    public async Task<TeamDto> CreateAsync(SaveTeamRequest r, CancellationToken ct = default)
    {
        EnsureCanManage(r.DepartmentId);
        var team = new Team { DepartmentId = r.DepartmentId };
        await ApplyAsync(team, r, ct);
        db.Teams.Add(team);
        audit.Write(OrgAuditActions.TeamChanged, "Team", team.Id, $"Team '{team.Name}' created");
        await db.SaveChangesAsync(ct);
        return await GetAsync(team.Id, ct);
    }

    public async Task<TeamDto> UpdateAsync(Guid id, SaveTeamRequest r, CancellationToken ct = default)
    {
        var team = await db.Teams.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Team", id);
        EnsureCanManage(team.DepartmentId);
        if (r.DepartmentId != team.DepartmentId) throw new ValidationException(new Dictionary<string, string> { ["departmentId"] = "Teams cannot move between departments." });
        OrgRowVersion.Apply(db, team, r.RowVersion, "team");
        await ApplyAsync(team, r, ct);
        audit.Write(OrgAuditActions.TeamChanged, "Team", team.Id, $"Team '{team.Name}' updated",
            new { routing = team.RoutingStrategy.ToString(), members = r.MemberUserIds.Count, team.FallbackTeamId, team.MaxOpenTicketsPerAgent });
        await db.SaveChangesAsync(ct);
        return await GetAsync(team.Id, ct);
    }

    /// <summary>Routing-only update (routing configuration screen).</summary>
    public async Task<TeamDto> UpdateRoutingAsync(Guid id, TeamRoutingRequest r, string? rowVersion, CancellationToken ct = default)
    {
        var team = await db.Teams.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Team", id);
        EnsureCanManage(team.DepartmentId);
        OrgRowVersion.Apply(db, team, rowVersion, "team");
        await ApplyRoutingAsync(team, r, ct);
        audit.Write(OrgAuditActions.TeamChanged, "Team", team.Id, $"Routing of '{team.Name}' changed to {team.RoutingStrategy}",
            new { r.Mode, r.OnlyOnlineAgents, r.ConsiderShifts, r.MaxOpenTicketsPerAgent, r.FallbackTeamId });
        await db.SaveChangesAsync(ct);
        return await GetAsync(team.Id, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var team = await db.Teams.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Team", id);
        EnsureCanManage(team.DepartmentId);
        if (await db.Departments.AnyAsync(d => d.DefaultTeamId == id, ct))
            throw new ConflictException("team_is_default", "This is a department's default routing team. Choose another default team first.");
        var open = await db.Tickets.CountAsync(t => t.TeamId == id && t.StatusCategory < StatusCategory.Resolved, ct);
        if (open > 0) throw new ConflictException("team_has_open_tickets", $"{open} open tickets are queued in this team. Move them before deleting the team.");
        foreach (var other in await db.Teams.Where(t => t.FallbackTeamId == id).ToListAsync(ct)) other.FallbackTeamId = null;
        var referenced = await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AnyAsync(t => t.TeamId == id, ct)
                         || await db.Products.AnyAsync(p => p.DefaultTeamId == id, ct)
                         || await db.SlaEscalationLevels.AnyAsync(e => e.ReassignToTeamId == id, ct);
        if (referenced)
        {
            team.IsActive = false; // keep history; inactive teams receive no routing
            await db.TeamMembers.Where(m => m.TeamId == id).ExecuteDeleteAsync(ct);
        }
        else db.Teams.Remove(team);
        audit.Write(OrgAuditActions.TeamChanged, "Team", id, $"Team '{team.Name}' {(referenced ? "deactivated" : "deleted")}");
        await db.SaveChangesAsync(ct);
    }

    public async Task<RoutingPreviewDto> PreviewAsync(RoutingPreviewRequest r, CancellationToken ct = default)
    {
        ctx.RequireAny(OrgPermissions.ReadStructure);
        Ticket sample;
        if (r.TicketId is { } tid)
        {
            var real = await db.Tickets.AsNoTracking().VisibleTo(ctx, db).FirstOrDefaultAsync(t => t.Id == tid, ct) ?? throw new NotFoundException("Ticket", tid);
            sample = new Ticket
            {
                DepartmentId = r.DepartmentId ?? real.DepartmentId, TeamId = r.TeamId, CustomerId = real.CustomerId, ProductId = real.ProductId,
                CategoryId = real.CategoryId, SubcategoryId = real.SubcategoryId, Language = real.Language, Priority = real.Priority
            };
        }
        else
        {
            if (r.DepartmentId is not { } dept) throw new ValidationException(new Dictionary<string, string> { ["departmentId"] = "Choose a department." });
            sample = new Ticket
            {
                DepartmentId = dept, TeamId = r.TeamId, CustomerId = r.CustomerId, ProductId = r.ProductId, CategoryId = r.CategoryId,
                Language = string.IsNullOrWhiteSpace(r.Language) ? "en" : r.Language.Trim().ToLowerInvariant(),
                Priority = TicketService.ParseEnum(r.Priority, TicketPriority.Medium, "priority")
            };
        }
        if (sample.TeamId is { } team && !await db.Teams.AnyAsync(t => t.Id == team && t.DepartmentId == sample.DepartmentId, ct))
            throw new ValidationException(new Dictionary<string, string> { ["teamId"] = "The team does not belong to the department." });
        var result = await routing.ExplainAsync(sample, ct);
        var names = await OrgNames.UsersAsync(db, result.Candidates.Select(c => (Guid?)c.UserId).Append(result.Decision.AssigneeId), ct);
        var teamName = result.TeamId is { } tId ? await db.Teams.AsNoTracking().Where(t => t.Id == tId).Select(t => t.Name).FirstOrDefaultAsync(ct) : null;
        return new RoutingPreviewDto(result.TeamId, teamName, result.Decision.AssigneeId,
            result.Decision.AssigneeId is { } a ? names.GetValueOrDefault(a) : null, result.Decision.Strategy, result.Decision.Reason, result.UsedFallback,
            result.Candidates.Select(c => new RoutingCandidateDto(c.UserId, names.GetValueOrDefault(c.UserId) ?? "Unknown", c.Availability.ToString(), c.Open, c.Capacity,
                c.OnShift, c.OnLeave, c.Delegated, c.Eligible, c.ExcludedReason, c.Score)).ToList());
    }

    private void EnsureCanManage(Guid departmentId)
    {
        if (ctx.Has(Permissions.AdminOrganisation)) return;
        ctx.Require(Permissions.AgentsManage);
        if (ctx.TicketScope != TicketScope.All && !ctx.DepartmentIds.Contains(departmentId))
            throw new ForbiddenException("Supervisors can only manage teams of their own departments.");
    }

    private async Task ApplyAsync(Team team, SaveTeamRequest r, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        var name = (r.Name ?? "").Trim();
        if (name.Length is < 2 or > 120) errors["name"] = "Enter a name of 2–120 characters.";
        else if (await db.Teams.AnyAsync(t => t.DepartmentId == r.DepartmentId && t.Name == name && t.Id != team.Id && t.IsActive, ct))
            errors["name"] = "The department already has a team with this name.";
        if (!await db.Departments.AnyAsync(d => d.Id == r.DepartmentId && d.IsActive, ct)) errors["departmentId"] = "Choose an active department.";
        var memberIds = r.MemberUserIds.Distinct().ToList();
        var valid = await db.AgentDepartments.AsNoTracking().Where(a => a.DepartmentId == r.DepartmentId && memberIds.Contains(a.UserId))
            .Join(db.Users.AsNoTracking().Where(u => u.IsActive), a => a.UserId, u => u.Id, (a, u) => u.Id).ToListAsync(ct);
        var invalid = memberIds.Except(valid).ToList();
        if (invalid.Count > 0) errors["memberUserIds"] = "Members must be active agents of the department.";
        if (r.LeadUserId is { } lead && !memberIds.Contains(lead)) errors["leadUserId"] = "The team lead must be a member.";
        if (errors.Count > 0) throw new ValidationException(errors);

        team.Name = name;
        team.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
        team.LeadUserId = r.LeadUserId;
        team.IsActive = r.IsActive;
        if (r.Routing is not null) await ApplyRoutingAsync(team, r.Routing, ct);

        var existing = await db.TeamMembers.Where(m => m.TeamId == team.Id).ToListAsync(ct);
        foreach (var m in existing.Where(m => !memberIds.Contains(m.UserId))) db.TeamMembers.Remove(m);
        foreach (var uid in memberIds.Where(id => existing.All(m => m.UserId != id))) db.TeamMembers.Add(new TeamMember { TeamId = team.Id, UserId = uid });
        foreach (var m in existing.Where(m => memberIds.Contains(m.UserId))) m.IsLead = m.UserId == r.LeadUserId;
        foreach (var m in db.TeamMembers.Local.Where(m => m.TeamId == team.Id)) m.IsLead = m.UserId == r.LeadUserId;
    }

    private async Task ApplyRoutingAsync(Team team, TeamRoutingRequest r, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        if (!Enum.TryParse<RoutingStrategy>(r.Mode, true, out var mode) || !Enum.IsDefined(mode)) errors["routing.mode"] = "Unknown routing mode.";
        if (r.MaxOpenTicketsPerAgent is { } max && (max < 1 || max > 1000)) errors["routing.maxOpenTicketsPerAgent"] = "Use 1–1000 (or leave empty).";
        if (r.FallbackTeamId is { } fb && (fb == team.Id || !await db.Teams.AnyAsync(t => t.Id == fb && t.DepartmentId == team.DepartmentId && t.IsActive, ct)))
            errors["routing.fallbackTeamId"] = "The fallback queue must be another active team of the same department.";
        if (errors.Count > 0) throw new ValidationException(errors);
        team.RoutingStrategy = mode;
        team.OnlyAssignToOnlineAgents = r.OnlyOnlineAgents;
        team.ConsiderShifts = r.ConsiderShifts;
        team.MaxOpenTicketsPerAgent = r.MaxOpenTicketsPerAgent;
        team.FallbackTeamId = r.FallbackTeamId;
    }

    private async Task<List<TeamDto>> MapAsync(List<Team> rows, CancellationToken ct)
    {
        var ids = rows.Select(t => t.Id).ToList();
        var deptIds = rows.Select(t => t.DepartmentId).Distinct().ToList();
        var departments = await db.Departments.AsNoTracking().Where(d => deptIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);
        var members = await db.TeamMembers.AsNoTracking().Where(m => ids.Contains(m.TeamId)).ToListAsync(ct);
        var userIds = members.Select(m => m.UserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).Select(u => new { u.Id, u.DisplayName, u.Email, u.IsActive }).ToDictionaryAsync(u => u.Id, ct);
        var agents = await db.Agents.AsNoTracking().Where(a => userIds.Contains(a.UserId)).ToDictionaryAsync(a => a.UserId, ct);
        var loads = await db.Tickets.AsNoTracking().Where(t => t.AssigneeId != null && userIds.Contains(t.AssigneeId.Value) && t.StatusCategory < StatusCategory.Resolved)
            .GroupBy(t => t.AssigneeId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var teamOpen = await db.Tickets.AsNoTracking().Where(t => t.TeamId != null && ids.Contains(t.TeamId.Value) && t.StatusCategory < StatusCategory.Resolved)
            .GroupBy(t => t.TeamId!.Value).Select(g => new { g.Key, Open = g.Count(), Unassigned = g.Count(x => x.AssigneeId == null) })
            .ToDictionaryAsync(x => x.Key, ct);
        var fallbackIds = rows.Where(r => r.FallbackTeamId != null).Select(r => r.FallbackTeamId!.Value).ToList();
        var fallbacks = await db.Teams.AsNoTracking().Where(t => fallbackIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        return rows.Select(t =>
        {
            var dept = departments.GetValueOrDefault(t.DepartmentId);
            var ms = members.Where(m => m.TeamId == t.Id && users.ContainsKey(m.UserId)).Select(m =>
            {
                var u = users[m.UserId];
                var a = agents.GetValueOrDefault(m.UserId);
                var cap = Math.Min(a?.MaxOpenTickets ?? 0, t.MaxOpenTicketsPerAgent ?? int.MaxValue);
                return new TeamMemberDto(m.UserId, u.DisplayName, u.Email, m.IsLead || t.LeadUserId == m.UserId, (a?.Availability ?? AgentAvailability.Offline).ToString(),
                    u.IsActive, loads.GetValueOrDefault(m.UserId), cap);
            }).OrderByDescending(m => m.IsLead).ThenBy(m => m.Name).ToList();
            var counts = teamOpen.GetValueOrDefault(t.Id);
            return new TeamDto(t.Id, t.DepartmentId, dept?.Name ?? "", t.Name, t.Description, t.LeadUserId,
                t.LeadUserId is { } l && users.TryGetValue(l, out var lu) ? lu.DisplayName : null, t.IsActive, dept?.DefaultTeamId == t.Id,
                new TeamRoutingDto(t.RoutingStrategy.ToString(), t.OnlyAssignToOnlineAgents, t.ConsiderShifts, t.MaxOpenTicketsPerAgent, t.FallbackTeamId,
                    t.FallbackTeamId is { } f ? fallbacks.GetValueOrDefault(f) : null),
                ms, counts?.Open ?? 0, counts?.Unassigned ?? 0, OrgRowVersion.Encode(t.RowVersion));
        }).ToList();
    }
}

/// <summary>Skills catalogue (used for skill-based routing with per-agent proficiency 1–5) and role options for pickers.</summary>
public sealed class SkillService(DeskDbContext db, RequestContext ctx, IAuditWriter audit)
{
    public async Task<List<SkillDto>> ListAsync(CancellationToken ct = default)
    {
        ctx.RequireAny(OrgPermissions.ReadStructure);
        var counts = await db.AgentSkills.AsNoTracking().GroupBy(s => s.SkillId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var rows = await db.Skills.AsNoTracking().OrderBy(s => s.Category).ThenBy(s => s.Name).ToListAsync(ct);
        return rows.Select(s => new SkillDto(s.Id, s.Name, s.Category, s.Description, counts.GetValueOrDefault(s.Id))).ToList();
    }

    public async Task<SkillDto> SaveAsync(Guid? id, SaveSkillRequest r, CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.AdminOrganisation, Permissions.AgentsManage);
        var name = (r.Name ?? "").Trim();
        if (name.Length is < 2 or > 80) throw new ValidationException(new Dictionary<string, string> { ["name"] = "Enter a name of 2–80 characters." });
        if (await db.Skills.AnyAsync(s => s.Name == name && s.Id != id, ct))
            throw new ValidationException(new Dictionary<string, string> { ["name"] = "A skill with this name exists." });
        var skill = id is { } existing ? await db.Skills.FirstOrDefaultAsync(s => s.Id == existing, ct) ?? throw new NotFoundException("Skill", existing) : new Skill();
        skill.Name = name;
        skill.Category = string.IsNullOrWhiteSpace(r.Category) ? null : r.Category.Trim();
        skill.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
        if (id is null) db.Skills.Add(skill);
        audit.Write(OrgAuditActions.DepartmentChanged, "Skill", skill.Id, $"Skill '{skill.Name}' {(id is null ? "created" : "updated")}");
        await db.SaveChangesAsync(ct);
        var count = await db.AgentSkills.CountAsync(s => s.SkillId == skill.Id, ct);
        return new SkillDto(skill.Id, skill.Name, skill.Category, skill.Description, count);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        ctx.RequireAny(Permissions.AdminOrganisation, Permissions.AgentsManage);
        var skill = await db.Skills.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException("Skill", id);
        db.Skills.Remove(skill);
        audit.Write(OrgAuditActions.DepartmentChanged, "Skill", id, $"Skill '{skill.Name}' deleted");
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<RoleOptionDto>> RoleOptionsAsync(CancellationToken ct = default)
    {
        ctx.RequireAny(OrgPermissions.ReadStructure);
        var roles = await db.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct);
        var ids = roles.Select(r => r.Id).ToList();
        var perms = await db.RolePermissions.AsNoTracking().Where(p => ids.Contains(p.RoleId)).ToListAsync(ct);
        return roles.Select(r =>
        {
            var codes = perms.Where(p => p.RoleId == r.Id).Select(p => p.PermissionCode).ToHashSet();
            var light = codes.Contains(Permissions.TicketsView) && !codes.Contains(Permissions.TicketsReply);
            return new RoleOptionDto(r.Id, r.Name, r.Description, r.TicketScope.ToString(), r.IsSystem, light);
        }).ToList();
    }
}
