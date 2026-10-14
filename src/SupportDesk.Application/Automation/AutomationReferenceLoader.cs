using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Domain.Identity;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Automation;

/// <summary>Loads the tenant's configuration ids used to validate definitions at publish time (small tables only).</summary>
public sealed class AutomationReferenceLoader(DeskDbContext db)
{
    private AutomationReferenceData? _cached;

    public async Task<AutomationReferenceData> LoadAsync(CancellationToken ct)
    {
        if (_cached is not null) return _cached;
        var users = await db.Users.AsNoTracking().Where(u => u.IsActive).Select(u => new { u.Id, u.Kind }).ToListAsync(ct);
        var fullAgents = await db.Agents.AsNoTracking().Where(a => !a.IsLightAgent).Select(a => a.UserId).ToListAsync(ct);
        var statuses = await db.TicketStatuses.AsNoTracking().Where(s => s.IsActive).Select(s => new { s.Id, s.SubstatusesJson }).ToListAsync(ct);
        _cached = new AutomationReferenceData
        {
            Users = users.Select(u => u.Id).ToHashSet(),
            AssignableUsers = users.Where(u => u.Kind != UserKind.LightAgent && fullAgents.Contains(u.Id)).Select(u => u.Id).ToHashSet(),
            Teams = (await db.Teams.AsNoTracking().Where(t => t.IsActive).Select(t => t.Id).ToListAsync(ct)).ToHashSet(),
            Departments = (await db.Departments.AsNoTracking().Where(d => d.IsActive).Select(d => d.Id).ToListAsync(ct)).ToHashSet(),
            Statuses = statuses.ToDictionary(s => s.Id, s => (IReadOnlyList<string>)Json.StringList(s.SubstatusesJson)),
            Categories = await db.TicketCategories.AsNoTracking().Where(c => c.IsActive).ToDictionaryAsync(c => c.Id, c => c.ParentId, ct),
            IssueTypes = (await db.IssueTypes.AsNoTracking().Where(i => i.IsActive).Select(i => i.Id).ToListAsync(ct)).ToHashSet(),
            Roles = (await db.Roles.AsNoTracking().Select(r => r.Id).ToListAsync(ct)).ToHashSet(),
            ReplyTemplates = (await db.ReplyTemplates.AsNoTracking().Where(t => t.IsActive).Select(t => t.Id).ToListAsync(ct)).ToHashSet(),
            CustomFieldKeys = new HashSet<string>(await db.CustomFields.AsNoTracking().Where(f => f.IsActive && f.Entity == "Ticket").Select(f => f.Key).ToListAsync(ct),
                StringComparer.OrdinalIgnoreCase)
        };
        return _cached;
    }
}
