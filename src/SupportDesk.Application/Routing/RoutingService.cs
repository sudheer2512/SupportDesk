using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Organisation;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Routing;

/// <summary>
/// Automatic assignment. Team: ticket team → product default team → department default team. Strategy per team:
/// Manual (queue only), RoundRobin (cursor), LoadBalanced (lowest load/capacity) or SkillBased (routing attributes:
/// customer, territory, product, category, language, tier — then load). Dedicated account agents always win when
/// eligible. Eligible = active, full agent, department member, not on leave, within capacity and (optionally) Online.
/// Out-of-office delegation redirects to the delegate.
/// </summary>
public sealed class RoutingService(DeskDbContext db) : IRoutingService
{
    private sealed record Candidate(Guid UserId, int Open, int Capacity, AgentAvailability Availability, string LanguagesJson)
    {
        public double Load => Capacity <= 0 ? 1 : (double)Open / Capacity;
    }

    public async Task<RoutingDecision> RouteAsync(Ticket ticket, CancellationToken ct)
    {
        if (ticket.AssigneeId is not null) return new RoutingDecision(ticket.TeamId, ticket.AssigneeId, "preassigned", "Already assigned");
        var now = db.Clock.UtcNow;
        var department = await db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == ticket.DepartmentId, ct);
        if (department is null) return new RoutingDecision(null, null, "none", "Unknown department");

        var teamId = ticket.TeamId;
        if (teamId is null && ticket.ProductId is { } pid)
            teamId = await db.Products.AsNoTracking().Where(p => p.Id == pid).Select(p => p.DefaultTeamId).FirstOrDefaultAsync(ct) is { } pt &&
                     await db.Teams.AnyAsync(t => t.Id == pt && t.DepartmentId == department.Id && t.IsActive, ct) ? pt : null;
        teamId ??= department.DefaultTeamId;
        var team = teamId is null ? null : await db.Teams.FirstOrDefaultAsync(t => t.Id == teamId && t.IsActive, ct);
        ticket.TeamId = team?.Id ?? ticket.TeamId;
        if (team is null || team.RoutingStrategy == RoutingStrategy.Manual)
            return new RoutingDecision(ticket.TeamId, null, "manual", team is null ? "No routing team configured" : "Team uses manual assignment");

        var memberIds = await db.TeamMembers.AsNoTracking().Where(m => m.TeamId == team.Id).Select(m => m.UserId).ToListAsync(ct);
        var candidates = await EligibleAsync(memberIds, department.Id, team.OnlyAssignToOnlineAgents, now, ct);

        // Dedicated agents for the customer (key accounts) take precedence over the team strategy.
        if (ticket.CustomerId is { } cid)
        {
            var dedicated = await db.AgentRoutingAttributes.AsNoTracking()
                .Where(a => a.Kind == RoutingAttributeKind.Customer && a.RefId == cid).Select(a => a.UserId).ToListAsync(ct);
            var pick = candidates.Where(c => dedicated.Contains(c.UserId)).OrderBy(c => c.Load).FirstOrDefault();
            if (pick is not null) return Assign(ticket, team, pick.UserId, "customer", "Dedicated agent for this customer");
        }
        if (candidates.Count == 0) return new RoutingDecision(team.Id, null, team.RoutingStrategy.ToString(), "No eligible agent available — left in the team queue");

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
                        team.RoundRobinCursorUserId = next;
                        return Assign(ticket, team, next, "round_robin", "Next agent in rotation");
                    }
                }
                break;
            }
            case RoutingStrategy.LoadBalanced:
            {
                var pick = candidates.OrderBy(c => c.Load).ThenBy(c => c.Open).First();
                return Assign(ticket, team, pick.UserId, "load_balanced", $"Lowest workload ({pick.Open}/{pick.Capacity})");
            }
            case RoutingStrategy.SkillBased:
            {
                var scores = await ScoreAsync(ticket, candidates, ct);
                var best = candidates.OrderByDescending(c => scores.GetValueOrDefault(c.UserId)).ThenBy(c => c.Load).First();
                return Assign(ticket, team, best.UserId, "skill_based", $"Best match (score {scores.GetValueOrDefault(best.UserId):0.#})");
            }
        }
        return new RoutingDecision(team.Id, null, team.RoutingStrategy.ToString(), "No eligible agent");
    }

    private RoutingDecision Assign(Ticket ticket, Team team, Guid userId, string strategy, string reason)
    {
        ticket.TeamId = team.Id;
        ticket.AssigneeId = userId;
        return new RoutingDecision(team.Id, userId, strategy, reason);
    }

    private async Task<List<Candidate>> EligibleAsync(List<Guid> userIds, Guid departmentId, bool onlineOnly, DateTime now, CancellationToken ct)
    {
        if (userIds.Count == 0) return [];
        var agents = await db.Agents.AsNoTracking()
            .Where(a => userIds.Contains(a.UserId) && !a.IsLightAgent)
            .Join(db.Users.AsNoTracking().Where(u => u.IsActive && u.Kind != UserKind.LightAgent), a => a.UserId, u => u.Id, (a, u) => a)
            .Where(a => db.AgentDepartments.Any(d => d.UserId == a.UserId && d.DepartmentId == departmentId))
            .ToListAsync(ct);
        var onLeave = await db.AgentLeaves.AsNoTracking()
            .Where(l => userIds.Contains(l.UserId) && l.Status == LeaveStatus.Approved && l.StartsAt <= now && l.EndsAt > now).Select(l => l.UserId).ToListAsync(ct);
        var delegated = await db.AgentDelegations.AsNoTracking()
            .Where(d => userIds.Contains(d.UserId) && d.IsActive && d.StartsAt <= now && d.EndsAt > now).Select(d => d.UserId).ToListAsync(ct);
        var loads = await db.Tickets.AsNoTracking()
            .Where(t => t.AssigneeId != null && userIds.Contains(t.AssigneeId.Value) && t.StatusCategory < StatusCategory.Resolved)
            .GroupBy(t => t.AssigneeId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return agents
            .Where(a => !onLeave.Contains(a.UserId) && !delegated.Contains(a.UserId))
            .Where(a => !onlineOnly || a.Availability == AgentAvailability.Online)
            .Select(a => new Candidate(a.UserId, loads.GetValueOrDefault(a.UserId), a.MaxOpenTickets, a.Availability, a.LanguagesJson))
            .Where(c => c.Open < c.Capacity)
            .ToList();
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

    public async Task<Guid?> ResolveDelegateAsync(Guid userId, CancellationToken ct)
    {
        var now = db.Clock.UtcNow;
        return await db.AgentDelegations.AsNoTracking()
            .Where(d => d.UserId == userId && d.IsActive && d.StartsAt <= now && d.EndsAt > now)
            .OrderByDescending(d => d.StartsAt).Select(d => (Guid?)d.DelegateUserId).FirstOrDefaultAsync(ct);
    }
}
