using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Meta;
using SupportDesk.Domain;
using SupportDesk.Domain.Configuration;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Organisation;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Tickets;

/// <summary>Configuration needed by ticket forms, filters and the workbench in a single round trip.</summary>
public sealed class MetaService(DeskDbContext db, TenantSettingsService settings)
{
    public async Task<TicketFormMetaDto> TicketFormAsync(CancellationToken ct = default)
    {
        var statuses = await db.TicketStatuses.AsNoTracking().OrderBy(s => s.SortOrder).ToListAsync(ct);
        var departments = await db.Departments.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.SortOrder).ThenBy(d => d.Name)
            .Select(d => new DepartmentRefDto(d.Id, d.Name, d.Code, d.VisibleInPortal)).ToListAsync(ct);
        var teams = await db.Teams.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.Name).Select(t => new TeamRefDto(t.Id, t.Name, t.DepartmentId)).ToListAsync(ct);
        var agents = await db.Agents.AsNoTracking()
            .Join(db.Users.AsNoTracking().Where(u => u.IsActive), a => a.UserId, u => u.Id, (a, u) => new { a, u })
            .OrderBy(x => x.u.DisplayName).ToListAsync(ct);
        var memberships = await db.AgentDepartments.AsNoTracking().Select(d => new { d.UserId, d.DepartmentId }).ToListAsync(ct);
        var teamMemberships = await db.TeamMembers.AsNoTracking().Select(d => new { d.UserId, d.TeamId }).ToListAsync(ct);
        var loads = await db.Tickets.AsNoTracking().Where(t => t.AssigneeId != null && t.StatusCategory < StatusCategory.Resolved)
            .GroupBy(t => t.AssigneeId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var categories = await db.TicketCategories.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new CategoryRefDto(c.Id, c.Name, c.ParentId, c.DepartmentId, c.VisibleInPortal)).ToListAsync(ct);
        var issueTypes = await db.IssueTypes.AsNoTracking().Where(i => i.IsActive).OrderBy(i => i.SortOrder).Select(i => new IdName(i.Id, i.Name)).ToListAsync(ct);
        var products = await db.Products.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Name).ToListAsync(ct);
        var modules = await db.ProductModules.AsNoTracking().Where(m => m.IsActive).OrderBy(m => m.Name).ToListAsync(ct);
        var versions = await db.ProductVersions.AsNoTracking().OrderByDescending(v => v.ReleaseDate).ToListAsync(ct);
        var fields = await db.CustomFields.AsNoTracking().Where(f => f.IsActive).OrderBy(f => f.Entity).ThenBy(f => f.SortOrder).ToListAsync(ct);
        var layouts = await db.FormLayouts.AsNoTracking().Where(l => l.IsActive).ToListAsync(ct);
        var lookups = await db.LookupValues.AsNoTracking().Where(l => l.IsActive).OrderBy(l => l.Type).ThenBy(l => l.SortOrder)
            .Select(l => new LookupDto(l.Type, l.Value, l.Label)).ToListAsync(ct);
        var brands = await db.Brands.AsNoTracking().OrderByDescending(b => b.IsDefault).ThenBy(b => b.Name).Select(b => new IdName(b.Id, b.Name)).ToListAsync(ct);
        var tags = await db.Tags.AsNoTracking().OrderBy(t => t.Name).Select(t => t.Name).Take(500).ToListAsync(ct);
        var prefix = (await settings.TicketsAsync(ct)).NumberPrefix;

        return new TicketFormMetaDto(
            statuses.Select(MapStatus).ToList(),
            Enum.GetNames<TicketPriority>(), Enum.GetNames<TicketSeverity>(), Enum.GetNames<Channel>(),
            departments, teams,
            agents.Select(x => new AgentRefDto(x.u.Id, x.u.DisplayName, x.u.Email, x.u.AvatarUrl, x.a.Availability.ToString(), x.a.IsLightAgent || x.u.Kind == UserKind.LightAgent,
                memberships.Where(m => m.UserId == x.u.Id).Select(m => m.DepartmentId).ToList(), teamMemberships.Where(m => m.UserId == x.u.Id).Select(m => m.TeamId).ToList(),
                loads.GetValueOrDefault(x.u.Id), x.a.MaxOpenTickets)).ToList(),
            categories, issueTypes,
            products.Select(p => new ProductRefDto(p.Id, p.Name, p.Code, modules.Where(m => m.ProductId == p.Id).Select(m => new IdName(m.Id, m.Name)).ToList(),
                versions.Where(v => v.ProductId == p.Id).Select(v => new IdName(v.Id, v.Version)).ToList())).ToList(),
            fields.Select(MapField).ToList(),
            layouts.Select(l => new LayoutDto(l.Id, l.Entity, l.Name, l.Kind.ToString(), l.DepartmentId, l.BrandId, Json.Element(l.SectionsJson, "[]"), l.IsDefault)).ToList(),
            lookups, brands, tags, prefix);
    }

    public static StatusDto MapStatus(TicketStatus s) => new(s.Id, s.Name, s.Category.ToString(), s.Color, s.SortOrder, s.IsDefault, s.PausesSla, s.IsActive,
        s.CustomerLabel, Json.GuidList(s.AllowedNextJson), Json.StringList(s.SubstatusesJson));

    public static CustomFieldDto MapField(CustomFieldDefinition f) => new(f.Id, f.Entity, f.Key, f.Label, f.FieldType.ToString(), Json.Element(f.OptionsJson, "[]"),
        f.IsRequired, f.DefaultValue, f.HelpText, f.ValidationRegex, f.VisibleInPortal, f.EditableInPortal, Json.GuidList(f.DepartmentIdsJson), f.SortOrder, f.IsActive);
}
