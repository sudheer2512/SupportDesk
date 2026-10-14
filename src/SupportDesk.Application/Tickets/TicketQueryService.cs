using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Service;
using SupportDesk.Domain.Sla;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Tickets;

/// <summary>Read side for tickets: filtered keyset-paged lists, detail, counts and the workbench context panel.</summary>
public sealed class TicketQueryService(
    DeskDbContext db,
    RequestContext ctx,
    TicketLookups lookups,
    TicketService tickets,
    IEnumerable<ITicketContextContributor> contributors)
{
    public const int MaxLimit = 200;

    public IQueryable<Ticket> Scoped() => db.Tickets.AsNoTracking().VisibleTo(ctx, db);

    public async Task<PagedResult<TicketListItemDto>> ListAsync(TicketQuery q, CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsView);
        var query = await ApplyFiltersAsync(Scoped(), q, ct);
        var limit = Math.Clamp(q.Limit ?? 50, 1, MaxLimit);
        int? total = q.IncludeTotal == true ? await query.CountAsync(ct) : null;
        var desc = !string.Equals(q.Dir, "asc", StringComparison.OrdinalIgnoreCase);
        var sort = (q.Sort ?? "lastActivity").ToLowerInvariant();
        var cursor = DbExtensions.DecodeCursor(q.Cursor);

        IQueryable<Ticket> ordered;
        Func<Ticket, string> keyOf;
        switch (sort)
        {
            case "created":
            {
                if (cursor is not null)
                {
                    var v = DateTime.Parse(cursor.V, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                    query = desc ? query.Where(t => t.CreatedAt < v || (t.CreatedAt == v && t.Id.CompareTo(cursor.Id) < 0))
                                 : query.Where(t => t.CreatedAt > v || (t.CreatedAt == v && t.Id.CompareTo(cursor.Id) > 0));
                }
                ordered = desc ? query.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id) : query.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id);
                keyOf = t => t.CreatedAt.ToString("O", CultureInfo.InvariantCulture);
                break;
            }
            case "number":
            {
                if (cursor is not null)
                {
                    var v = long.Parse(cursor.V, CultureInfo.InvariantCulture);
                    query = desc ? query.Where(t => t.Number < v) : query.Where(t => t.Number > v);
                }
                ordered = desc ? query.OrderByDescending(t => t.Number) : query.OrderBy(t => t.Number);
                keyOf = t => t.Number.ToString(CultureInfo.InvariantCulture);
                break;
            }
            case "due":
            {
                // Tickets without a due date sort last; the cursor encodes the due date (or max value).
                var max = new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);
                if (cursor is not null)
                {
                    var v = DateTime.Parse(cursor.V, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                    query = query.Where(t => (t.DueAt ?? max) > v || ((t.DueAt ?? max) == v && t.Id.CompareTo(cursor.Id) > 0));
                }
                ordered = query.OrderBy(t => t.DueAt ?? max).ThenBy(t => t.Id);
                keyOf = t => (t.DueAt ?? max).ToString("O", CultureInfo.InvariantCulture);
                break;
            }
            case "priority":
            {
                // Priority (desc) then last activity (desc); cursor "priority|lastActivity".
                if (cursor is not null)
                {
                    var parts = cursor.V.Split('|');
                    var p = (TicketPriority)int.Parse(parts[0], CultureInfo.InvariantCulture);
                    var la = DateTime.Parse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                    query = query.Where(t => t.Priority < p || (t.Priority == p && (t.LastActivityAt < la || (t.LastActivityAt == la && t.Id.CompareTo(cursor.Id) < 0))));
                }
                ordered = query.OrderByDescending(t => t.Priority).ThenByDescending(t => t.LastActivityAt).ThenByDescending(t => t.Id);
                keyOf = t => $"{(int)t.Priority}|{t.LastActivityAt.ToString("O", CultureInfo.InvariantCulture)}";
                break;
            }
            default:
            {
                if (cursor is not null)
                {
                    var v = DateTime.Parse(cursor.V, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                    query = desc ? query.Where(t => t.LastActivityAt < v || (t.LastActivityAt == v && t.Id.CompareTo(cursor.Id) < 0))
                                 : query.Where(t => t.LastActivityAt > v || (t.LastActivityAt == v && t.Id.CompareTo(cursor.Id) > 0));
                }
                ordered = desc ? query.OrderByDescending(t => t.LastActivityAt).ThenByDescending(t => t.Id) : query.OrderBy(t => t.LastActivityAt).ThenBy(t => t.Id);
                keyOf = t => t.LastActivityAt.ToString("O", CultureInfo.InvariantCulture);
                break;
            }
        }

        var page = await ordered.Take(limit + 1).ToListAsync(ct);
        var hasMore = page.Count > limit;
        if (hasMore) page.RemoveAt(page.Count - 1);
        var items = await lookups.MapListAsync(page, ct);
        var next = hasMore ? DbExtensions.EncodeCursor(keyOf(page[^1]), page[^1].Id) : null;
        return new PagedResult<TicketListItemDto>(items, next, total);
    }

    /// <summary>Applies query-string filters plus a saved view's condition tree (same semantics everywhere).</summary>
    public async Task<IQueryable<Ticket>> ApplyFiltersAsync(IQueryable<Ticket> query, TicketQuery q, CancellationToken ct)
    {
        if (q.ViewId is { } viewId)
        {
            var view = await db.SavedViews.AsNoTracking().FirstOrDefaultAsync(v => v.Id == viewId && (v.Visibility == ViewVisibility.Shared || v.OwnerUserId == ctx.UserId), ct)
                       ?? throw new NotFoundException("View", viewId);
            query = ApplyViewFilter(query, view.FilterJson);
        }
        static List<string> Split(string? s) => string.IsNullOrWhiteSpace(s) ? [] : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        static List<Guid> Guids(string? s) => Split(s).Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
        static List<T> Enums<T>(string? s) where T : struct, Enum => Split(s).Select(x => Enum.TryParse<T>(x, true, out var v) ? (T?)v : null).Where(v => v.HasValue).Select(v => v!.Value).ToList();

        if (Guids(q.StatusId) is { Count: > 0 } statuses) query = query.Where(t => statuses.Contains(t.StatusId));
        // "unresolved" / "open" (lower-case) = every open category; "done" = resolved or closed; otherwise exact categories.
        if (q.StatusCategory is "open" or "unresolved") query = query.Where(t => t.StatusCategory < StatusCategory.Resolved);
        else if (q.StatusCategory is "done") query = query.Where(t => t.StatusCategory >= StatusCategory.Resolved);
        else if (Enums<StatusCategory>(q.StatusCategory) is { Count: > 0 } cats) query = query.Where(t => cats.Contains(t.StatusCategory));
        if (Enums<TicketPriority>(q.Priority) is { Count: > 0 } prios) query = query.Where(t => prios.Contains(t.Priority));
        if (Guids(q.AssigneeId) is { Count: > 0 } assignees) query = query.Where(t => t.AssigneeId != null && assignees.Contains(t.AssigneeId.Value));
        if (q.Unassigned == true) query = query.Where(t => t.AssigneeId == null);
        if (q.Mine == true) query = query.Where(t => t.AssigneeId == ctx.UserId);
        if (q.Watching == true) query = query.Where(t => db.TicketWatchers.Any(w => w.TicketId == t.Id && w.UserId == ctx.UserId));
        if (Guids(q.DepartmentId) is { Count: > 0 } depts) query = query.Where(t => depts.Contains(t.DepartmentId));
        if (Guids(q.TeamId) is { Count: > 0 } teams) query = query.Where(t => t.TeamId != null && teams.Contains(t.TeamId.Value));
        if (q.CustomerId is { } cust) query = query.Where(t => t.CustomerId == cust);
        if (q.ContactId is { } contact) query = query.Where(t => t.ContactId == contact);
        if (q.ProductId is { } product) query = query.Where(t => t.ProductId == product);
        if (Enums<Channel>(q.Channel) is { Count: > 0 } channels) query = query.Where(t => channels.Contains(t.Channel));
        if (Split(q.Tag) is { Count: > 0 } tags)
        {
            var normalized = tags.Select(TicketService.NormalizeTag).ToList();
            query = query.Where(t => t.Tags.Any(tt => normalized.Contains(tt.Tag!.Name)));
        }
        if (Enums<SlaState>(q.SlaState) is { Count: > 0 } sla) query = query.Where(t => sla.Contains(t.SlaState));
        if (q.Overdue == true)
        {
            var now = db.Clock.UtcNow;
            query = query.Where(t => t.StatusCategory < StatusCategory.Resolved && t.DueAt != null && t.DueAt < now);
        }
        if (q.Escalated == true) query = query.Where(t => t.IsEscalated);
        if (q.CreatedFrom is { } from) query = query.Where(t => t.CreatedAt >= from);
        if (q.CreatedTo is { } to) query = query.Where(t => t.CreatedAt < to);
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = q.Q.Trim();
            var number = term.TrimStart('#');
            if (long.TryParse(number, out var n))
                query = query.Where(t => t.Number == n || t.Subject.Contains(term));
            else
                query = query.Where(t => t.Subject.Contains(term) || (t.DescriptionText != null && t.DescriptionText.Contains(term)) ||
                                         db.Contacts.Any(c => c.Id == t.ContactId && (c.FullName.Contains(term) || (c.Email != null && c.Email.Contains(term)))) ||
                                         db.Customers.Any(c => c.Id == t.CustomerId && c.Name.Contains(term)));
        }
        return query;
    }

    public IQueryable<Ticket> ApplyViewFilter(IQueryable<Ticket> query, string filterJson)
    {
        var group = ConditionGroup.Parse(filterJson);
        // Views may reference the current user with the "me" token.
        foreach (var c in group.AllConditions().Where(c => c.Field.Equals("assigneeId", StringComparison.OrdinalIgnoreCase)))
            if (c.Value is { } v && v.ToString().Contains("me", StringComparison.OrdinalIgnoreCase))
                c.Value = Json.ToElement(v.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? (object)v.EnumerateArray().Select(x => x.GetString() == "me" ? ctx.UserId.ToString() : x.GetString()).ToList()
                    : ctx.UserId.ToString()!);
        return group.IsEmpty ? query : query.Where(TicketFilterTranslator.Translate(group, db.Clock.UtcNow));
    }

    public async Task<TicketDetailDto> GetDetailAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsView);
        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Ticket", id);
        await tickets.EnsureVisibleAsync(t, ct);
        return await MapDetailAsync(t, ct);
    }

    public async Task<TicketDetailDto> GetByNumberAsync(long number, CancellationToken ct = default)
    {
        var id = await db.Tickets.Where(t => t.Number == number).Select(t => t.Id).FirstOrDefaultAsync(ct);
        if (id == Guid.Empty) throw new NotFoundException("Ticket", number);
        return await GetDetailAsync(id, ct);
    }

    public async Task<TicketDetailDto> MapDetailAsync(Ticket t, CancellationToken ct)
    {
        await lookups.LoadAsync(ct);
        var customer = t.CustomerId is null ? null : await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(c => c.Id == t.CustomerId).Select(c => new { c.Name, c.Tier }).FirstOrDefaultAsync(ct);
        var contact = t.ContactId is null ? null : await db.Contacts.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(c => c.Id == t.ContactId).Select(c => new { c.FullName, c.Email, c.Phone, c.IsVip }).FirstOrDefaultAsync(ct);
        var location = t.LocationId is null ? null : await db.CustomerLocations.AsNoTracking().Where(l => l.Id == t.LocationId).Select(l => l.Name).FirstOrDefaultAsync(ct);
        var installation = t.InstallationId is null ? null : await db.Installations.AsNoTracking().Where(i => i.Id == t.InstallationId).Select(i => i.Name).FirstOrDefaultAsync(ct);
        var asset = t.AssetId is null ? null : await db.CustomerAssets.AsNoTracking().Where(a => a.Id == t.AssetId).Select(a => a.Name).FirstOrDefaultAsync(ct);
        var contract = t.ContractId is null ? null : await db.Contracts.AsNoTracking().Where(c => c.Id == t.ContractId).Select(c => c.Number + " · " + c.Name).FirstOrDefaultAsync(ct);
        var parentNumber = t.ParentTicketId is null ? null : await db.Tickets.AsNoTracking().Where(p => p.Id == t.ParentTicketId).Select(p => (long?)p.Number).FirstOrDefaultAsync(ct);
        var tags = await db.TicketTags.AsNoTracking().Where(x => x.TicketId == t.Id).Select(x => x.Tag!.Name).OrderBy(x => x).ToListAsync(ct);
        var watchers = await db.TicketWatchers.AsNoTracking().Where(w => w.TicketId == t.Id).OrderBy(w => w.AddedAt).ToListAsync(ct);
        var linksOut = await db.TicketLinks.AsNoTracking().Where(l => l.SourceTicketId == t.Id)
            .Join(db.Tickets.AsNoTracking(), l => l.TargetTicketId, o => o.Id, (l, o) => new { l, o.Id, o.Number, o.Subject, o.StatusId }).ToListAsync(ct);
        var linksIn = await db.TicketLinks.AsNoTracking().Where(l => l.TargetTicketId == t.Id)
            .Join(db.Tickets.AsNoTracking(), l => l.SourceTicketId, o => o.Id, (l, o) => new { l, o.Id, o.Number, o.Subject, o.StatusId }).ToListAsync(ct);
        var children = await db.Tickets.AsNoTracking().Where(c => c.ParentTicketId == t.Id).OrderBy(c => c.Number)
            .Select(c => new { c.Id, c.Number, c.Subject, c.StatusId, c.StatusCategory, c.AssigneeId }).ToListAsync(ct);
        var refs = await db.TicketExternalReferences.AsNoTracking().Where(r => r.TicketId == t.Id && r.System != "client-request").ToListAsync(ct);
        var lockInfo = await tickets.GetLockAsync(t.Id, ct);
        var canCosts = ctx.Has(Permissions.TicketsViewCosts);

        return new TicketDetailDto(
            t.Id, t.Number, lookups.Display(t.Number), t.Subject, t.DescriptionHtml, t.Channel.ToString(), t.BrandId, lookups.Brand(t.BrandId),
            t.CustomerId, customer?.Name, customer?.Tier, t.ContactId, contact?.FullName, contact?.Email, contact?.Phone, contact?.IsVip ?? false,
            t.LocationId, location, t.DepartmentId, lookups.Department(t.DepartmentId) ?? "", t.TeamId, lookups.Team(t.TeamId), t.AssigneeId, lookups.User(t.AssigneeId),
            t.ProductId, lookups.Product(t.ProductId), t.ModuleId, lookups.Module(t.ModuleId), t.VersionId, lookups.Version(t.VersionId), t.InstallationId, installation,
            t.AssetId, asset, t.CategoryId, lookups.Category(t.CategoryId), t.SubcategoryId, lookups.Category(t.SubcategoryId), t.IssueTypeId, lookups.IssueType(t.IssueTypeId),
            t.Priority.ToString(), t.Severity?.ToString(), t.StatusId, lookups.Status(t.StatusId), t.StatusCategory.ToString(), lookups.StatusColor(t.StatusId), t.Substatus,
            t.Language, t.CreatedAt, t.UpdatedAt, t.AssignedAt, t.FirstResponseAt, t.FirstResponseDueAt, t.NextResponseDueAt, t.DueAt, t.ResolvedAt, t.ClosedAt,
            t.LastActivityAt, t.SlaPolicyId, lookups.SlaPolicy(t.SlaPolicyId), t.SlaState.ToString(), t.EntitlementStatus.ToString(), t.ContractId, contract, t.EntitlementNote,
            t.ParentTicketId, parentNumber, t.MergedIntoTicketId, t.ResolutionCode, t.RootCause, t.ResolutionHtml, t.AwaitingCustomerConfirmation, t.CsatScore, t.CsatComment,
            t.Sentiment.ToString(), t.AiSummary, t.TimeSpentMinutes, t.BillableMinutes, canCosts ? t.SupportCost : null, t.ReopenCount, t.IsEscalated, t.EscalationLevel,
            t.LastThreadSeq, t.WorkflowId, t.WorkflowStateKey, Json.Element(t.CustomFieldsJson), tags,
            watchers.Select(w => new WatcherDto(w.UserId, lookups.User(w.UserId) ?? "", w.Kind.ToString(), w.AddedAt, w.ExpiresAt)).ToList(),
            linksOut.Select(l => new TicketLinkDto(l.l.Id, l.l.LinkType.ToString(), "outgoing", l.Id, l.Number, l.Subject, lookups.Status(l.StatusId)))
                .Concat(linksIn.Select(l => new TicketLinkDto(l.l.Id, l.l.LinkType.ToString(), "incoming", l.Id, l.Number, l.Subject, lookups.Status(l.StatusId)))).ToList(),
            children.Select(c => new ChildTicketDto(c.Id, c.Number, c.Subject, lookups.Status(c.StatusId), c.StatusCategory.ToString(), lookups.User(c.AssigneeId))).ToList(),
            refs.Select(MapRef).ToList(),
            lockInfo is null || lockInfo.Until < db.Clock.UtcNow ? null : new TicketLockDto(lockInfo.UserId, lockInfo.UserName, lockInfo.Until),
            Convert.ToBase64String(t.RowVersion), TicketLookups.PermissionsFor(t, ctx));
    }

    public static ExternalReferenceDto MapRef(TicketExternalReference r) =>
        new(r.Id, r.System, r.RecordType, r.RecordId, r.RecordNumber, r.Url, r.IsOrigin, Json.ElementOrNull(r.ContextJson));

    /// <summary>Right-hand panel: customer 360 summary, history, SLA, contracts, installations, transactions, suggestions.</summary>
    public async Task<TicketContextDto> ContextAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsView);
        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Ticket", id);
        await tickets.EnsureVisibleAsync(t, ct);
        await lookups.LoadAsync(ct);
        var now = db.Clock.UtcNow;
        var since90 = now.AddDays(-90);

        CustomerSummaryDto? customer = null;
        if (t.CustomerId is { } cid && ctx.Has(Permissions.CustomersView))
        {
            var c = await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().FirstAsync(x => x.Id == cid, ct);
            var stats = await db.Tickets.AsNoTracking().Where(x => x.CustomerId == cid)
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    Open = g.Count(x => x.StatusCategory < StatusCategory.Resolved),
                    Recent = g.Count(x => x.CreatedAt >= since90),
                    Breaches = g.Count(x => x.CreatedAt >= since90 && x.SlaState == SlaState.Breached),
                    Csat = g.Where(x => x.CsatScore != null).Average(x => (double?)x.CsatScore)
                }).SingleOrDefaultAsync(ct); // one group at most
            customer = new CustomerSummaryDto(c.Id, c.Name, c.Code, c.Tier, c.Industry, lookups.User(c.AccountManagerUserId), c.ServiceRegion,
                stats?.Open ?? 0, stats?.Recent ?? 0, stats?.Csat, stats?.Breaches ?? 0, c.Status.ToString());
        }

        ContactSummaryDto? contact = null;
        if (t.ContactId is { } kid)
        {
            var k = await db.Contacts.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().FirstAsync(x => x.Id == kid, ct);
            var loc = k.LocationId is null ? null : await db.CustomerLocations.AsNoTracking().Where(l => l.Id == k.LocationId).Select(l => l.Name).FirstOrDefaultAsync(ct);
            var open = await db.Tickets.CountAsync(x => x.ContactId == kid && x.StatusCategory < StatusCategory.Resolved, ct);
            contact = new ContactSummaryDto(k.Id, k.FullName, k.Email, k.Phone, k.JobTitle, k.IsVip, k.PreferredLanguage, k.PreferredChannel?.ToString(), loc, open);
        }

        var previousQuery = Scoped().Where(x => x.Id != t.Id && ((t.ContactId != null && x.ContactId == t.ContactId) || (t.CustomerId != null && x.CustomerId == t.CustomerId)));
        var previous = await lookups.MapListAsync(await previousQuery.OrderByDescending(x => x.CreatedAt).Take(10).ToListAsync(ct), ct);

        var sla = await SlaStatusAsync(t.Id, now, ct);

        var contracts = new List<ContractSummaryDto>();
        if (t.CustomerId is { } custId && ctx.Has(Permissions.ContractsView))
        {
            var rows = await db.Contracts.AsNoTracking().Where(c => c.CustomerId == custId).OrderByDescending(c => c.EndDate).Take(10).ToListAsync(ct);
            contracts = rows.Select(c => new ContractSummaryDto(c.Id, c.Number, c.Name, c.Type.ToString(), c.Status.ToString(), c.StartDate, c.EndDate, c.PrepaidHours,
                c.ConsumedHours, c.IsBillable, c.Id == t.ContractId)).ToList();
        }

        var installations = new List<InstallationSummaryDto>();
        if (t.CustomerId is { } custId2 && ctx.Has(Permissions.ProductsView))
        {
            var rows = await db.Installations.AsNoTracking().Where(i => i.CustomerId == custId2).OrderByDescending(i => i.Id == t.InstallationId).ThenBy(i => i.Name).Take(10).ToListAsync(ct);
            installations = rows.Select(i => new InstallationSummaryDto(i.Id, i.Name, lookups.Product(i.ProductId) ?? "", lookups.Version(i.VersionId), i.Environment.ToString(),
                i.DeploymentMode.ToString(), i.OperatingSystem, i.DatabaseVersion, i.Status.ToString())).ToList();
        }

        var builder = new TicketContextBuilder();
        var refs = await db.TicketExternalReferences.AsNoTracking().Where(r => r.TicketId == t.Id && r.System != "client-request").ToListAsync(ct);
        builder.RelatedTransactions.AddRange(refs.Select(MapRef));
        foreach (var c in contributors)
        {
            try { await c.ContributeAsync(t, builder, ct); }
            catch (DomainException) { /* a contributor (e.g. disabled AI) must never break the panel */ }
        }
        return new TicketContextDto(customer, contact, previous, sla, contracts, installations, builder.RelatedTransactions,
            builder.Articles.OrderByDescending(a => a.Score).Take(8).ToList(), builder.AiRecommendations, builder.EngineeringLinks);
    }

    public async Task<SlaStatusDto?> SlaStatusAsync(Guid ticketId, DateTime now, CancellationToken ct)
    {
        var instance = await db.SlaInstances.AsNoTracking().Include(i => i.Milestones).Where(i => i.TicketId == ticketId)
            .OrderByDescending(i => i.StartedAt).FirstOrDefaultAsync(ct);
        if (instance is null) return null;
        var policyName = lookups.SlaPolicy(instance.PolicyId) ?? "SLA";
        var calendar = Sla.SlaSnapshot.Calendar(instance.CalendarSnapshotJson);
        return new SlaStatusDto(instance.Id, policyName, instance.Status.ToString(), instance.Milestones.OrderBy(m => m.Kind).ThenBy(m => m.Cycle).Select(m =>
        {
            var elapsed = Sla.SlaClock.Elapsed(m, now, m.BusinessHours ? calendar : Domain.Rules.WorkingCalendar.AlwaysOpen);
            var remaining = Math.Max(0, m.TargetMinutes - elapsed);
            return new SlaMilestoneDto(m.Id, m.Kind.ToString(), m.Cycle, m.Status.ToString(), m.TargetMinutes, m.BusinessHours, m.StartedAt, m.DueAt, m.CompletedAt,
                m.BreachedAt, Math.Round(elapsed, 1), Math.Round(remaining, 1), m.TargetMinutes == 0 ? 100 : Math.Round(elapsed * 100.0 / m.TargetMinutes, 1), m.EscalationLevelReached);
        }).ToList());
    }

    /// <summary>Badge counts for the workbench's standard queues.</summary>
    public async Task<Dictionary<string, int>> QueueCountsAsync(CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsView);
        var now = db.Clock.UtcNow;
        var open = Scoped().Where(t => t.StatusCategory < StatusCategory.Resolved);
        return new Dictionary<string, int>
        {
            ["mine"] = await open.CountAsync(t => t.AssigneeId == ctx.UserId, ct),
            ["unassigned"] = await open.CountAsync(t => t.AssigneeId == null, ct),
            ["open"] = await open.CountAsync(ct),
            ["overdue"] = await open.CountAsync(t => t.DueAt != null && t.DueAt < now, ct),
            ["atRisk"] = await open.CountAsync(t => t.SlaState == SlaState.AtRisk, ct),
            ["escalated"] = await open.CountAsync(t => t.IsEscalated, ct),
            ["watching"] = await open.CountAsync(t => db.TicketWatchers.Any(w => w.TicketId == t.Id && w.UserId == ctx.UserId), ct),
            ["waiting"] = await open.CountAsync(t => t.StatusCategory == StatusCategory.Waiting, ct)
        };
    }
}
