using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Organisation;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Routing;

/// <summary>One team member as seen by the routing engine (used by the routing preview).</summary>
public sealed record RoutingCandidateInfo(Guid UserId, int Open, int Capacity, AgentAvailability Availability, bool OnShift, bool OnLeave, bool Delegated,
    bool Eligible, string? ExcludedReason, double? Score);

/// <summary>A routing decision with the evaluated candidates (nothing is persisted by the caller of a preview).</summary>
public sealed record RoutingExplanation(RoutingDecision Decision, Guid? TeamId, bool UsedFallback, IReadOnlyList<RoutingCandidateInfo> Candidates);

/// <summary>
/// Automatic assignment. Team: ticket team → product default team → department default team. Strategy per team:
/// Manual (queue only), RoundRobin (cursor), LoadBalanced (lowest load/capacity), SkillBased (routing attributes:
/// customer, territory, product, category, language, tier, skill levels — then load) or Territory (agents whose
/// territory matches the customer's service region first — then load). Dedicated account agents always win when
/// eligible. Eligible = active, full agent, department member, not on approved leave, not delegated away, within
/// capacity (the lower of the agent's and the team's cap), Online when the team requires it and on shift when the
/// team considers shifts. When no member is eligible the ticket moves to the team's fallback queue (same department),
/// which is routed once with its own strategy.
/// </summary>
public sealed class RoutingService(DeskDbContext db) : IRoutingService
{
    private sealed record Candidate(Guid UserId, int Open, int Capacity, AgentAvailability Availability, string LanguagesJson)
    {
        public double Load => Capacity <= 0 ? 1 : (double)Open / Capacity;
    }

    private sealed record TeamResult(RoutingDecision Decision, List<RoutingCandidateInfo> Infos);

    public async Task<RoutingDecision> RouteAsync(Ticket ticket, CancellationToken ct) => (await EvaluateAsync(ticket, apply: true, ct)).Decision;

    /// <summary>Routing preview for a sample (unsaved) ticket: the decision plus every member's eligibility. Never mutates teams.</summary>
    public Task<RoutingExplanation> ExplainAsync(Ticket sample, CancellationToken ct) => EvaluateAsync(sample, apply: false, ct);

    private async Task<RoutingExplanation> EvaluateAsync(Ticket ticket, bool apply, CancellationToken ct)
    {
        if (ticket.AssigneeId is not null)
            return new RoutingExplanation(new RoutingDecision(ticket.TeamId, ticket.AssigneeId, "preassigned", "Already assigned"), ticket.TeamId, false, []);
        var now = db.Clock.UtcNow;
        var department = await db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == ticket.DepartmentId, ct);
        if (department is null) return new RoutingExplanation(new RoutingDecision(null, null, "none", "Unknown department"), null, false, []);

        var teamId = ticket.TeamId;
        if (teamId is null && ticket.ProductId is { } pid)
            teamId = await db.Products.AsNoTracking().Where(p => p.Id == pid).Select(p => p.DefaultTeamId).FirstOrDefaultAsync(ct) is { } pt &&
                     await db.Teams.AnyAsync(t => t.Id == pt && t.DepartmentId == department.Id && t.IsActive, ct) ? pt : null;
        teamId ??= department.DefaultTeamId;
        var team = teamId is null ? null : await LoadTeamAsync(teamId.Value, apply, ct);
        ticket.TeamId = team?.Id ?? ticket.TeamId;
        if (team is null || team.RoutingStrategy == RoutingStrategy.Manual)
            return new RoutingExplanation(new RoutingDecision(ticket.TeamId, null, "manual", team is null ? "No routing team configured" : "Team uses manual assignment"),
                ticket.TeamId, false, []);

        var result = await RouteInTeamAsync(ticket, department, team, now, apply, ct);
        if (result.Decision.AssigneeId is null && team.FallbackTeamId is { } fallbackId && fallbackId != team.Id)
        {
            var fallback = await LoadTeamAsync(fallbackId, apply, ct);
            if (fallback is not null && fallback.DepartmentId == department.Id)
            {
                ticket.TeamId = fallback.Id;
                if (fallback.RoutingStrategy == RoutingStrategy.Manual)
                    return new RoutingExplanation(new RoutingDecision(fallback.Id, null, "fallback",
                        $"No eligible agent in {team.Name} — moved to the {fallback.Name} queue"), fallback.Id, true, result.Infos);
                var second = await RouteInTeamAsync(ticket, department, fallback, now, apply, ct);
                var reason = second.Decision.AssigneeId is null
                    ? $"No eligible agent in {team.Name} or {fallback.Name} — left in the {fallback.Name} queue"
                    : $"No eligible agent in {team.Name}; fallback {fallback.Name}: {second.Decision.Reason}";
                return new RoutingExplanation(second.Decision with { Reason = reason }, fallback.Id, true, [.. result.Infos, .. second.Infos]);
            }
        }
        return new RoutingExplanation(result.Decision, team.Id, false, result.Infos);
    }

    private async Task<Team?> LoadTeamAsync(Guid id, bool tracked, CancellationToken ct) =>
        tracked
            ? await db.Teams.FirstOrDefaultAsync(t => t.Id == id && t.IsActive, ct)
            : await db.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.IsActive, ct);

    private async Task<TeamResult> RouteInTeamAsync(Ticket ticket, Department department, Team team, DateTime now, bool apply, CancellationToken ct)
    {
        var memberIds = await db.TeamMembers.AsNoTracking().Where(m => m.TeamId == team.Id).Select(m => m.UserId).ToListAsync(ct);
        var (candidates, infos) = await EligibleAsync(memberIds, department.Id, team, now, ct);

        // Dedicated agents for the customer (key accounts) take precedence over the team strategy.
        if (ticket.CustomerId is { } cid && candidates.Count > 0)
        {
            var dedicated = await db.AgentRoutingAttributes.AsNoTracking()
                .Where(a => a.Kind == RoutingAttributeKind.Customer && a.RefId == cid).Select(a => a.UserId).ToListAsync(ct);
            var pick = candidates.Where(c => dedicated.Contains(c.UserId)).OrderBy(c => c.Load).FirstOrDefault();
            if (pick is not null) return new TeamResult(Assign(ticket, team, pick.UserId, "customer", "Dedicated agent for this customer"), infos);
        }
        if (candidates.Count == 0)
            return new TeamResult(new RoutingDecision(team.Id, null, team.RoutingStrategy.ToString(), "No eligible agent available — left in the team queue"), infos);

        switch (team.RoutingStrategy)
        {
            case RoutingStrategy.RoundRobin:
            {
                var ordered = memberIds.OrderBy(id => id).ToList();
                var start = team.RoundRobinCursorUserId is { } cursor ? ordered.IndexOf(cursor) : -1;
                for (var i = 1; i <= ordered.Count; i++)
                {
                    var next = ordered[(start + i + ordered.Count) % ordered.Count];
                    if (candidates.Any(c => c.UserId == next))
                    {
                        if (apply) team.RoundRobinCursorUserId = next;
                        return new TeamResult(Assign(ticket, team, next, "round_robin", "Next agent in rotation"), infos);
                    }
                }
                break;
            }
            case RoutingStrategy.LoadBalanced:
            {
                var pick = candidates.OrderBy(c => c.Load).ThenBy(c => c.Open).First();
                return new TeamResult(Assign(ticket, team, pick.UserId, "load_balanced", $"Lowest workload ({pick.Open}/{pick.Capacity})"), infos);
            }
            case RoutingStrategy.SkillBased:
            {
                var scores = await ScoreAsync(ticket, candidates, ct);
                var best = candidates.OrderByDescending(c => scores.GetValueOrDefault(c.UserId)).ThenBy(c => c.Load).First();
                return new TeamResult(Assign(ticket, team, best.UserId, "skill_based", $"Best match (score {scores.GetValueOrDefault(best.UserId):0.#})"),
                    WithScores(infos, scores));
            }
            case RoutingStrategy.Territory:
            {
                var scores = await TerritoryScoresAsync(ticket, candidates, ct);
                var best = candidates.OrderByDescending(c => scores.GetValueOrDefault(c.UserId)).ThenBy(c => c.Load).First();
                var reason = scores.GetValueOrDefault(best.UserId) > 0
                    ? $"Territory match, lowest workload ({best.Open}/{best.Capacity})"
                    : $"No territory match — lowest workload ({best.Open}/{best.Capacity})";
                return new TeamResult(Assign(ticket, team, best.UserId, "territory", reason), WithScores(infos, scores));
            }
        }
        return new TeamResult(new RoutingDecision(team.Id, null, team.RoutingStrategy.ToString(), "No eligible agent"), infos);
    }

    private static List<RoutingCandidateInfo> WithScores(List<RoutingCandidateInfo> infos, Dictionary<Guid, double> scores) =>
        infos.Select(i => i.Eligible ? i with { Score = Math.Round(scores.GetValueOrDefault(i.UserId), 2) } : i).ToList();

    private static RoutingDecision Assign(Ticket ticket, Team team, Guid userId, string strategy, string reason)
    {
        ticket.TeamId = team.Id;
        ticket.AssigneeId = userId;
        return new RoutingDecision(team.Id, userId, strategy, reason);
    }

    private async Task<(List<Candidate> Eligible, List<RoutingCandidateInfo> Infos)> EligibleAsync(List<Guid> userIds, Guid departmentId, Team team, DateTime now,
        CancellationToken ct)
    {
        if (userIds.Count == 0) return ([], []);
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).Select(u => new { u.Id, u.IsActive, u.Kind }).ToDictionaryAsync(u => u.Id, ct);
        var agents = await db.Agents.AsNoTracking().Where(a => userIds.Contains(a.UserId)).ToDictionaryAsync(a => a.UserId, ct);
        var members = await db.AgentDepartments.AsNoTracking().Where(d => userIds.Contains(d.UserId) && d.DepartmentId == departmentId)
            .Select(d => d.UserId).ToListAsync(ct);
        var onLeave = await db.AgentLeaves.AsNoTracking()
            .Where(l => userIds.Contains(l.UserId) && l.Status == LeaveStatus.Approved && l.StartsAt <= now && l.EndsAt > now).Select(l => l.UserId).ToListAsync(ct);
        var delegated = await db.AgentDelegations.AsNoTracking()
            .Where(d => userIds.Contains(d.UserId) && d.IsActive && d.StartsAt <= now && d.EndsAt > now).Select(d => d.UserId).ToListAsync(ct);
        var loads = await db.Tickets.AsNoTracking()
            .Where(t => t.AssigneeId != null && userIds.Contains(t.AssigneeId.Value) && t.StatusCategory < StatusCategory.Resolved)
            .GroupBy(t => t.AssigneeId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var offShift = team.ConsiderShifts ? await OffShiftAsync(userIds, agents.Values.ToList(), now, ct) : [];

        var eligible = new List<Candidate>();
        var infos = new List<RoutingCandidateInfo>();
        foreach (var id in userIds.Distinct())
        {
            agents.TryGetValue(id, out var agent);
            users.TryGetValue(id, out var user);
            var open = loads.GetValueOrDefault(id);
            var capacity = Math.Min(agent?.MaxOpenTickets ?? 0, team.MaxOpenTicketsPerAgent ?? int.MaxValue);
            var availability = agent?.Availability ?? AgentAvailability.Offline;
            var isOnLeave = onLeave.Contains(id);
            var isDelegated = delegated.Contains(id);
            var isOnShift = !offShift.Contains(id);
            string? reason =
                user is null || agent is null ? "No agent profile" :
                !user.IsActive ? "Deactivated" :
                agent.IsLightAgent || user.Kind == UserKind.LightAgent ? "Light agent (cannot own tickets)" :
                !members.Contains(id) ? "Not a member of the department" :
                isOnLeave ? "On leave" :
                isDelegated ? "Out of office (delegated)" :
                team.OnlyAssignToOnlineAgents && availability != AgentAvailability.Online ? $"Not online ({availability})" :
                !isOnShift ? "Off shift" :
                open >= capacity ? $"At capacity ({open}/{capacity})" :
                null;
            if (reason is null) eligible.Add(new Candidate(id, open, capacity, availability, agent!.LanguagesJson));
            infos.Add(new RoutingCandidateInfo(id, open, capacity, availability, isOnShift, isOnLeave, isDelegated, reason is null, reason, null));
        }
        return (eligible, infos);
    }

    /// <summary>Members whose effective shift (assignment for today, else their default shift) is not working now.</summary>
    private async Task<HashSet<Guid>> OffShiftAsync(List<Guid> userIds, List<Agent> agents, DateTime now, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(now);
        var assignments = await db.AgentShiftAssignments.AsNoTracking()
            .Where(a => userIds.Contains(a.UserId) && a.EffectiveFrom <= today && (a.EffectiveTo == null || a.EffectiveTo >= today)).ToListAsync(ct);
        var shiftIds = assignments.Select(a => a.ShiftId).Concat(agents.Where(a => a.DefaultShiftId != null).Select(a => a.DefaultShiftId!.Value)).Distinct().ToList();
        if (shiftIds.Count == 0) return [];
        var shifts = await db.Shifts.AsNoTracking().Where(s => shiftIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var result = new HashSet<Guid>();
        foreach (var id in userIds)
        {
            var assignment = ShiftSchedule.EffectiveAssignment(assignments.Where(a => a.UserId == id), today);
            var shiftId = assignment?.ShiftId ?? agents.FirstOrDefault(a => a.UserId == id)?.DefaultShiftId;
            if (shiftId is { } sid && shifts.TryGetValue(sid, out var shift) && !ShiftSchedule.IsOnShift(shift, now)) result.Add(id);
        }
        return result;
    }

    private async Task<Dictionary<Guid, double>> ScoreAsync(Ticket ticket, List<Candidate> candidates, CancellationToken ct)
    {
        var ids = candidates.Select(c => c.UserId).ToList();
        var attrs = await db.AgentRoutingAttributes.AsNoTracking().Where(a => ids.Contains(a.UserId)).ToListAsync(ct);
        var customer = ticket.CustomerId is null ? null : await db.Customers.AsNoTracking().Where(c => c.Id == ticket.CustomerId)
            .Select(c => new { c.ServiceRegion, c.Tier }).FirstOrDefaultAsync(ct);
        var skillLevels = await db.AgentSkills.AsNoTracking().Where(s => ids.Contains(s.UserId))
            .GroupBy(s => s.UserId).Select(g => new { g.Key, Avg = g.Average(x => (double)x.Level) }).ToDictionaryAsync(x => x.Key, x => x.Avg, ct);
        var scores = new Dictionary<Guid, double>();
        foreach (var c in candidates)
        {
            double score = 0;
            foreach (var a in attrs.Where(a => a.UserId == c.UserId))
            {
                score += a.Kind switch
                {
                    RoutingAttributeKind.Product when a.RefId == ticket.ProductId => 5,
                    RoutingAttributeKind.Category when a.RefId == ticket.CategoryId || a.RefId == ticket.SubcategoryId => 4,
                    RoutingAttributeKind.Territory when customer?.ServiceRegion is { } r && string.Equals(a.Value, r, StringComparison.OrdinalIgnoreCase) => 3,
                    RoutingAttributeKind.CustomerTier when customer?.Tier is { } tier && string.Equals(a.Value, tier, StringComparison.OrdinalIgnoreCase) => 2,
                    RoutingAttributeKind.Language when string.Equals(a.Value, ticket.Language, StringComparison.OrdinalIgnoreCase) => 2,
                    _ => 0
                };
            }
            if (c.LanguagesJson.Contains($"\"{ticket.Language}\"", StringComparison.OrdinalIgnoreCase)) score += 1;
            score += skillLevels.GetValueOrDefault(c.UserId) * 0.5;
            scores[c.UserId] = score;
        }
        return scores;
    }

    private async Task<Dictionary<Guid, double>> TerritoryScoresAsync(Ticket ticket, List<Candidate> candidates, CancellationToken ct)
    {
        var ids = candidates.Select(c => c.UserId).ToList();
        var region = ticket.CustomerId is null ? null : await db.Customers.AsNoTracking().Where(c => c.Id == ticket.CustomerId)
            .Select(c => c.ServiceRegion).FirstOrDefaultAsync(ct);
        var territories = await db.AgentRoutingAttributes.AsNoTracking()
            .Where(a => ids.Contains(a.UserId) && a.Kind == RoutingAttributeKind.Territory).ToListAsync(ct);
        var scores = new Dictionary<Guid, double>();
        foreach (var c in candidates)
        {
            double score = 0;
            if (region is not null && territories.Any(a => a.UserId == c.UserId && string.Equals(a.Value, region, StringComparison.OrdinalIgnoreCase))) score += 10;
            if (c.LanguagesJson.Contains($"\"{ticket.Language}\"", StringComparison.OrdinalIgnoreCase)) score += 1;
            scores[c.UserId] = score;
        }
        return scores;
    }

    public async Task<Guid?> ResolveDelegateAsync(Guid userId, CancellationToken ct)
    {
        var now = db.Clock.UtcNow;
        return await db.AgentDelegations.AsNoTracking()
            .Where(d => d.UserId == userId && d.IsActive && d.StartsAt <= now && d.EndsAt > now)
            .OrderByDescending(d => d.StartsAt).Select(d => (Guid?)d.DelegateUserId).FirstOrDefaultAsync(ct);
    }
}
