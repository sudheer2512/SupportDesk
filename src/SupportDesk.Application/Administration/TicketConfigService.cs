using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Administration;
using SupportDesk.Contracts.Common;
using SupportDesk.Domain;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Administration;

/// <summary>Display labels and colours of the fixed priority and severity values (tenant setting "tickets.display").</summary>
public sealed record TicketDisplaySettings
{
    public List<DisplayOptionDto> Priorities { get; init; } =
    [
        new("Low", "Low", "#64748b"), new("Medium", "Medium", "#2563eb"), new("High", "High", "#d97706"), new("Critical", "Critical", "#dc2626")
    ];

    public List<DisplayOptionDto> Severities { get; init; } =
    [
        new("Cosmetic", "Cosmetic", "#64748b"), new("Minor", "Minor", "#2563eb"), new("Major", "Major", "#d97706"), new("Critical", "Critical", "#dc2626")
    ];
}

/// <summary>
/// Ticket configuration (admin.ticket_config): statuses (category mapping, colour, order, default per category, allowed
/// transitions, substatuses), categories/subcategories, issue types, priority/severity display, numbering and lifecycle
/// rules. Ticket screens read this configuration live (no server cache), so changes apply to the next request; a
/// "configuration.changed" event tells open desks to refresh their cached form metadata.
/// </summary>
public sealed class TicketConfigService(DeskDbContext db, RequestContext ctx, IAuditWriter audit, AdminSettingsStore store, AdminChangeNotifier changes)
{
    private static readonly StatusCategory[] RequiredCategories = Enum.GetValues<StatusCategory>();

    // ───────────────────────────── Statuses ─────────────────────────────

    public async Task<List<AdminStatusDto>> ListStatusesAsync(CancellationToken ct)
    {
        ctx.Require(Permissions.AdminTicketConfig);
        var statuses = await db.TicketStatuses.AsNoTracking().OrderBy(s => s.SortOrder).ThenBy(s => s.Name).ToListAsync(ct);
        var counts = await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking()
            .GroupBy(t => t.StatusId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return statuses.Select(s => MapStatus(s, counts.GetValueOrDefault(s.Id))).ToList();
    }

    public async Task<AdminStatusDto> CreateStatusAsync(SaveStatusRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminTicketConfig);
        var all = await db.TicketStatuses.ToListAsync(ct);
        var s = new TicketStatus { SortOrder = all.Count == 0 ? 1 : all.Max(x => x.SortOrder) + 1 };
        Apply(s, r, all, isNew: true, ticketCount: 0);
        db.TicketStatuses.Add(s);
        EnsureEveryCategoryCovered([.. all, s]);
        audit.Write(AdminAuditActions.TicketConfigChanged, "TicketStatus", s.Id, $"Status '{s.Name}' created ({s.Category})", new { r.Category, r.AllowedNext, r.Substatuses });
        changes.Changed("statuses", s.Id);
        await db.SaveChangesAsync(ct);
        return MapStatus(s, 0);
    }

    public async Task<AdminStatusDto> UpdateStatusAsync(Guid id, SaveStatusRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminTicketConfig);
        var all = await db.TicketStatuses.ToListAsync(ct);
        var s = all.FirstOrDefault(x => x.Id == id) ?? throw new NotFoundException("Status", id);
        AdminRules.ApplyRowVersion(db, s, r.RowVersion, "status");
        var count = await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).CountAsync(t => t.StatusId == id, ct);
        var before = new { s.Name, Category = s.Category.ToString(), s.Color, s.IsDefault, s.PausesSla, s.IsActive, s.CustomerLabel, AllowedNext = Json.GuidList(s.AllowedNextJson), Substatuses = Json.StringList(s.SubstatusesJson) };
        Apply(s, r, all, isNew: false, ticketCount: count);
        EnsureEveryCategoryCovered(all);
        audit.Write(AdminAuditActions.TicketConfigChanged, "TicketStatus", s.Id, $"Status '{s.Name}' updated", new { from = before, to = r });
        changes.Changed("statuses", s.Id);
        await db.SaveChangesAsync(ct);
        return MapStatus(s, count);
    }

    public async Task DeleteStatusAsync(Guid id, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminTicketConfig);
        var all = await db.TicketStatuses.ToListAsync(ct);
        var s = all.FirstOrDefault(x => x.Id == id) ?? throw new NotFoundException("Status", id);
        if (s.IsSystem) throw new ConflictException("system_status", "Built-in statuses cannot be deleted. Deactivate or rename them instead.");
        if (await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AnyAsync(t => t.StatusId == id, ct))
            throw new ConflictException("status_in_use", $"Tickets use '{s.Name}'. Move them to another status or deactivate it instead.");
        EnsureEveryCategoryCovered(all.Where(x => x.Id != id).ToList());
        foreach (var other in all.Where(x => x.Id != id))
        {
            var next = Json.GuidList(other.AllowedNextJson);
            if (next.Remove(id)) other.AllowedNextJson = Json.Serialize(next);
        }
        foreach (var f in await db.CustomFields.Where(f => f.RequiredForStatusIdsJson.Contains(id.ToString())).ToListAsync(ct))
            f.RequiredForStatusIdsJson = Json.Serialize(Json.GuidList(f.RequiredForStatusIdsJson).Where(x => x != id));
        db.TicketStatuses.Remove(s);
        audit.Write(AdminAuditActions.TicketConfigChanged, "TicketStatus", s.Id, $"Status '{s.Name}' deleted");
        changes.Changed("statuses", s.Id);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<AdminStatusDto>> ReorderStatusesAsync(ReorderRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminTicketConfig);
        var all = await db.TicketStatuses.ToListAsync(ct);
        ApplyOrder(all, r.Ids, s => s.Id, (s, i) => s.SortOrder = i);
        audit.Write(AdminAuditActions.TicketConfigChanged, "TicketStatus", null, "Statuses reordered", new { order = all.OrderBy(s => s.SortOrder).Select(s => s.Name) });
        changes.Changed("statuses");
        await db.SaveChangesAsync(ct);
        return await ListStatusesAsync(ct);
    }

    private void Apply(TicketStatus s, SaveStatusRequest r, List<TicketStatus> all, bool isNew, int ticketCount)
    {
        var errors = new Dictionary<string, string>();
        var name = r.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 100) errors["name"] = "Enter a name of up to 100 characters.";
        else if (all.Any(x => x.Id != s.Id && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) errors["name"] = "Another status already has this name.";
        var category = AdminRules.ParseEnum(r.Category, "category", errors, StatusCategory.Open);
        if (!isNew && category != s.Category)
        {
            if (s.IsSystem) errors["category"] = "The category of a built-in status cannot change.";
            else if (ticketCount > 0) errors["category"] = $"{ticketCount} ticket(s) use this status; its category cannot change.";
        }
        if (!AdminRules.HexColor().IsMatch(r.Color ?? "")) errors["color"] = "Use a hex colour such as #2563eb.";
        var next = (r.AllowedNext ?? []).Distinct().Where(x => x != s.Id).ToList();
        if (next.Any(x => all.All(o => o.Id != x))) errors["allowedNext"] = "One or more target statuses do not exist.";
        var subs = (r.Substatuses ?? []).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (subs.Count > 20 || subs.Any(x => x.Length > 60)) errors["substatuses"] = "Up to 20 substatuses of at most 60 characters.";
        var label = AdminRules.Clean(r.CustomerLabel, 100);
        if (s.IsSystem && !r.IsActive) errors["isActive"] = "Built-in statuses cannot be deactivated.";
        if (r.IsDefault && !r.IsActive) errors["isDefault"] = "An inactive status cannot be the default.";
        if (errors.Count > 0) throw new ValidationException(errors);

        s.Name = name;
        s.Category = category;
        s.Color = r.Color!.ToLowerInvariant();
        s.PausesSla = r.PausesSla;
        s.IsActive = r.IsActive;
        s.CustomerLabel = label;
        s.AllowedNextJson = Json.Serialize(next);
        s.SubstatusesJson = Json.Serialize(subs);
        if (r.IsDefault)
            foreach (var other in all.Where(o => o.Id != s.Id && o.Category == category && o.IsDefault)) other.IsDefault = false;
        s.IsDefault = r.IsDefault;
    }

    /// <summary>Ticket lifecycle code needs an active status in every category (open, assigned, in progress, waiting, resolved, closed).</summary>
    private static void EnsureEveryCategoryCovered(IReadOnlyCollection<TicketStatus> statuses)
    {
        var missing = RequiredCategories.Where(c => !statuses.Any(s => s.Category == c && s.IsActive)).ToList();
        if (missing.Count > 0)
            throw new ConflictException("status_category_uncovered", $"Every category needs at least one active status. Missing: {string.Join(", ", missing)}.");
    }

    private static AdminStatusDto MapStatus(TicketStatus s, int count) => new(s.Id, s.Name, s.Category.ToString(), s.Color, s.SortOrder, s.IsDefault, s.PausesSla,
        s.IsSystem, s.IsActive, s.CustomerLabel, Json.GuidList(s.AllowedNextJson), Json.StringList(s.SubstatusesJson), count, AdminRules.Version(s.RowVersion));

    // ───────────────────────────── Categories ─────────────────────────────

    public async Task<List<AdminCategoryDto>> ListCategoriesAsync(CancellationToken ct)
    {
        ctx.Require(Permissions.AdminTicketConfig);
        var rows = await db.TicketCategories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
        var tickets = db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking();
        var byCat = await tickets.Where(t => t.CategoryId != null).GroupBy(t => t.CategoryId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var bySub = await tickets.Where(t => t.SubcategoryId != null).GroupBy(t => t.SubcategoryId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return rows.Select(c => new AdminCategoryDto(c.Id, c.Name, c.ParentId, c.DepartmentId, c.VisibleInPortal, c.IsActive, c.SortOrder,
            byCat.GetValueOrDefault(c.Id) + bySub.GetValueOrDefault(c.Id))).ToList();
    }

    public async Task<AdminCategoryDto> SaveCategoryAsync(Guid? id, SaveCategoryRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminTicketConfig);
        var all = await db.TicketCategories.ToListAsync(ct);
        TicketCategory c;
        if (id is { } existing) c = all.FirstOrDefault(x => x.Id == existing) ?? throw new NotFoundException("Category", existing);
        else
        {
            c = new TicketCategory { SortOrder = all.Where(x => x.ParentId == r.ParentId).Select(x => x.SortOrder).DefaultIfEmpty(0).Max() + 1 };
            db.TicketCategories.Add(c);
        }
        var errors = new Dictionary<string, string>();
        var name = r.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 100) errors["name"] = "Enter a name of up to 100 characters.";
        else if (all.Any(x => x.Id != c.Id && x.ParentId == r.ParentId && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            errors["name"] = "Another category at this level has this name.";
        if (r.ParentId is { } parentId)
        {
            var parent = all.FirstOrDefault(x => x.Id == parentId);
            if (parent is null || parentId == c.Id) errors["parentId"] = "Choose an existing top-level category.";
            else if (parent.ParentId is not null) errors["parentId"] = "Subcategories can only be one level deep.";
            else if (id is not null && all.Any(x => x.ParentId == c.Id)) errors["parentId"] = "A category with subcategories cannot become a subcategory.";
        }
        if (r.DepartmentId is { } d && !await db.Departments.AnyAsync(x => x.Id == d, ct)) errors["departmentId"] = "Unknown department.";
        if (errors.Count > 0) throw new ValidationException(errors);
        c.Name = name;
        c.ParentId = r.ParentId;
        c.DepartmentId = r.DepartmentId;
        c.VisibleInPortal = r.VisibleInPortal;
        c.IsActive = r.IsActive;
        audit.Write(AdminAuditActions.TicketConfigChanged, "TicketCategory", c.Id, $"Category '{name}' {(id is null ? "created" : "updated")}", r);
        changes.Changed("categories", c.Id);
        await db.SaveChangesAsync(ct);
        return (await ListCategoriesAsync(ct)).First(x => x.Id == c.Id);
    }

    public async Task DeleteCategoryAsync(Guid id, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminTicketConfig);
        var c = await db.TicketCategories.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Category", id);
        if (await db.TicketCategories.AnyAsync(x => x.ParentId == id, ct))
            throw new ConflictException("category_has_children", "Delete or move the subcategories first.");
        if (await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AnyAsync(t => t.CategoryId == id || t.SubcategoryId == id, ct))
            throw new ConflictException("category_in_use", $"Tickets use '{c.Name}'. Deactivate it instead.");
        db.TicketCategories.Remove(c);
        audit.Write(AdminAuditActions.TicketConfigChanged, "TicketCategory", c.Id, $"Category '{c.Name}' deleted");
        changes.Changed("categories", c.Id);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<AdminCategoryDto>> ReorderCategoriesAsync(ReorderRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminTicketConfig);
        var ids = r.Ids.ToHashSet();
        var rows = await db.TicketCategories.Where(c => ids.Contains(c.Id)).ToListAsync(ct);
        if (rows.Select(c => c.ParentId).Distinct().Count() > 1) throw new DomainException(ErrorCodes.Validation, "Reorder categories within one level at a time.");
        ApplyOrder(rows, r.Ids, c => c.Id, (c, i) => c.SortOrder = i);
        changes.Changed("categories");
        await db.SaveChangesAsync(ct);
        return await ListCategoriesAsync(ct);
    }

    // ───────────────────────────── Issue types ─────────────────────────────

    public async Task<List<AdminIssueTypeDto>> ListIssueTypesAsync(CancellationToken ct)
    {
        ctx.Require(Permissions.AdminTicketConfig);
        var rows = await db.IssueTypes.AsNoTracking().OrderBy(i => i.SortOrder).ThenBy(i => i.Name).ToListAsync(ct);
        var counts = await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(t => t.IssueTypeId != null)
            .GroupBy(t => t.IssueTypeId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return rows.Select(i => new AdminIssueTypeDto(i.Id, i.Name, i.Description, i.IsActive, i.SortOrder, counts.GetValueOrDefault(i.Id))).ToList();
    }

    public async Task<AdminIssueTypeDto> SaveIssueTypeAsync(Guid? id, SaveIssueTypeRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminTicketConfig);
        var all = await db.IssueTypes.ToListAsync(ct);
        IssueType it;
        if (id is { } existing) it = all.FirstOrDefault(x => x.Id == existing) ?? throw new NotFoundException("Issue type", existing);
        else
        {
            it = new IssueType { SortOrder = all.Select(x => x.SortOrder).DefaultIfEmpty(0).Max() + 1 };
            db.IssueTypes.Add(it);
        }
        var name = r.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 100) throw new ValidationException(new Dictionary<string, string> { ["name"] = "Enter a name of up to 100 characters." });
        if (all.Any(x => x.Id != it.Id && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ValidationException(new Dictionary<string, string> { ["name"] = "Another issue type has this name." });
        it.Name = name;
        it.Description = AdminRules.Clean(r.Description, 500);
        it.IsActive = r.IsActive;
        audit.Write(AdminAuditActions.TicketConfigChanged, "IssueType", it.Id, $"Issue type '{name}' {(id is null ? "created" : "updated")}", r);
        changes.Changed("issue-types", it.Id);
        await db.SaveChangesAsync(ct);
        return (await ListIssueTypesAsync(ct)).First(x => x.Id == it.Id);
    }

    public async Task DeleteIssueTypeAsync(Guid id, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminTicketConfig);
        var it = await db.IssueTypes.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Issue type", id);
        if (await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AnyAsync(t => t.IssueTypeId == id, ct))
            throw new ConflictException("issue_type_in_use", $"Tickets use '{it.Name}'. Deactivate it instead.");
        db.IssueTypes.Remove(it);
        audit.Write(AdminAuditActions.TicketConfigChanged, "IssueType", it.Id, $"Issue type '{it.Name}' deleted");
        changes.Changed("issue-types", it.Id);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<AdminIssueTypeDto>> ReorderIssueTypesAsync(ReorderRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminTicketConfig);
        var rows = await db.IssueTypes.ToListAsync(ct);
        ApplyOrder(rows, r.Ids, i => i.Id, (i, n) => i.SortOrder = n);
        changes.Changed("issue-types");
        await db.SaveChangesAsync(ct);
        return await ListIssueTypesAsync(ct);
    }

    // ───────────────────────────── Priorities & severities display ─────────────────────────────

    /// <summary>Readable by every agent (ticket lists render the labels); editable with admin.ticket_config.</summary>
    public async Task<TicketDisplayDto> GetDisplayAsync(CancellationToken ct)
    {
        ctx.RequireAgent();
        var (s, _) = await store.GetAsync<TicketDisplaySettings>(AdminSettingKeys.TicketDisplay, ct);
        return new TicketDisplayDto(Normalize<TicketPriority>(s.Priorities, new TicketDisplaySettings().Priorities),
            Normalize<TicketSeverity>(s.Severities, new TicketDisplaySettings().Severities));
    }

    public async Task<TicketDisplayDto> SaveDisplayAsync(TicketDisplayDto r, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminTicketConfig);
        var errors = new Dictionary<string, string>();
        Check<TicketPriority>(r.Priorities, "priorities", errors);
        Check<TicketSeverity>(r.Severities, "severities", errors);
        if (errors.Count > 0) throw new ValidationException(errors);
        var value = new TicketDisplaySettings
        {
            Priorities = Normalize<TicketPriority>(r.Priorities, new TicketDisplaySettings().Priorities),
            Severities = Normalize<TicketSeverity>(r.Severities, new TicketDisplaySettings().Severities)
        };
        if (await store.StageAsync(AdminSettingKeys.TicketDisplay, value, "Priority and severity display changed", ct))
        {
            changes.Changed("display");
            await db.SaveChangesAsync(ct);
        }
        return await GetDisplayAsync(ct);
    }

    private static void Check<TEnum>(IReadOnlyList<DisplayOptionDto>? options, string field, Dictionary<string, string> errors) where TEnum : struct, Enum
    {
        foreach (var o in options ?? [])
        {
            if (!Enum.TryParse<TEnum>(o.Value, true, out _)) errors[field] = $"Unknown value '{o.Value}'.";
            else if (string.IsNullOrWhiteSpace(o.Label) || o.Label.Trim().Length > 40) errors[field] = "Labels must be 1–40 characters.";
            else if (!AdminRules.HexColor().IsMatch(o.Color ?? "")) errors[field] = "Use hex colours such as #2563eb.";
        }
    }

    /// <summary>One entry per enum value in enum order; missing values fall back to defaults.</summary>
    private static List<DisplayOptionDto> Normalize<TEnum>(IReadOnlyList<DisplayOptionDto>? options, List<DisplayOptionDto> defaults) where TEnum : struct, Enum =>
        Enum.GetNames<TEnum>().Select(name =>
        {
            var o = options?.FirstOrDefault(x => string.Equals(x.Value, name, StringComparison.OrdinalIgnoreCase));
            var d = defaults.First(x => x.Value == name);
            return o is null ? d : new DisplayOptionDto(name, o.Label.Trim(), o.Color.ToLowerInvariant());
        }).ToList();

    // ───────────────────────────── Numbering & lifecycle ─────────────────────────────

    public async Task<TicketLifecycleDto> GetLifecycleAsync(CancellationToken ct)
    {
        ctx.Require(Permissions.AdminTicketConfig);
        var (s, version) = await store.GetAsync<TicketSettings>(SettingKeys.Tickets, ct);
        var counter = await db.TenantCounters.AsNoTracking().Where(c => c.Name == "ticket").Select(c => (long?)c.NextValue).FirstOrDefaultAsync(ct);
        return new TicketLifecycleDto(s.NumberPrefix, counter ?? s.NumberStart, s.ReopenWindowDays, s.AllowCustomerReopen, s.AutoCloseResolvedAfterDays,
            s.RequireCustomerConfirmation, s.RequireResolutionCode, s.SoftLockMinutes, s.AllowMergeAcrossCustomers, s.AllowMergeAcrossDepartments, s.MaxMergeSources,
            s.AllowSplit, s.CsatEnabled, s.CsatExpiryDays, s.AutoAcknowledge, version);
    }

    public async Task<TicketLifecycleDto> SaveLifecycleAsync(SaveTicketLifecycleRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminTicketConfig);
        var errors = new Dictionary<string, string>();
        var prefix = (r.NumberPrefix ?? "").Trim();
        if (prefix.Length > 10 || prefix.Any(ch => char.IsWhiteSpace(ch) || ch is '<' or '>' or '"' or '\'' or '&')) errors["numberPrefix"] = "Up to 10 characters, no spaces or HTML characters.";
        if (r.ReopenWindowDays is < 0 or > 365) errors["reopenWindowDays"] = "Between 0 and 365 days.";
        if (r.AutoCloseResolvedAfterDays is < 0 or > 365) errors["autoCloseResolvedAfterDays"] = "Between 0 (never) and 365 days.";
        if (r.SoftLockMinutes is < 1 or > 240) errors["softLockMinutes"] = "Between 1 and 240 minutes.";
        if (r.MaxMergeSources is < 1 or > 50) errors["maxMergeSources"] = "Between 1 and 50 tickets.";
        if (r.CsatExpiryDays is < 1 or > 90) errors["csatExpiryDays"] = "Between 1 and 90 days.";
        if (errors.Count > 0) throw new ValidationException(errors);
        var (current, _) = await store.GetAsync<TicketSettings>(SettingKeys.Tickets, ct);
        var next = current with
        {
            NumberPrefix = prefix, ReopenWindowDays = r.ReopenWindowDays, AllowCustomerReopen = r.AllowCustomerReopen, AutoCloseResolvedAfterDays = r.AutoCloseResolvedAfterDays,
            RequireCustomerConfirmation = r.RequireCustomerConfirmation, RequireResolutionCode = r.RequireResolutionCode, SoftLockMinutes = r.SoftLockMinutes,
            AllowMergeAcrossCustomers = r.AllowMergeAcrossCustomers, AllowMergeAcrossDepartments = r.AllowMergeAcrossDepartments, MaxMergeSources = r.MaxMergeSources,
            AllowSplit = r.AllowSplit, CsatEnabled = r.CsatEnabled, CsatExpiryDays = r.CsatExpiryDays, AutoAcknowledge = r.AutoAcknowledge
        };
        if (await store.StageAsync(SettingKeys.Tickets, next, "Ticket numbering and lifecycle settings changed", ct))
        {
            changes.Changed("lifecycle");
            await db.SaveChangesAsync(ct);
        }
        return await GetLifecycleAsync(ct);
    }

    // ───────────────────────────── Helpers ─────────────────────────────

    /// <summary>Assigns 1..n in the given order; rows not listed keep their relative order after the listed ones.</summary>
    internal static void ApplyOrder<T>(List<T> rows, IReadOnlyList<Guid> ids, Func<T, Guid> key, Action<T, int> set)
    {
        if (ids.Count == 0 || ids.Distinct().Count() != ids.Count || ids.Any(id => rows.All(r => key(r) != id)))
            throw new DomainException(ErrorCodes.Validation, "The order must list existing items exactly once.");
        var i = 0;
        foreach (var id in ids) set(rows.First(r => key(r) == id), ++i);
        foreach (var r in rows.Where(r => !ids.Contains(key(r)))) set(r, ++i);
    }
}
