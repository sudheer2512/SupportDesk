using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Integrations.Erp;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Products;
using SupportDesk.Application.Service;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Customers;
using SupportDesk.Contracts.Products;
using SupportDesk.Domain;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Service;
using SupportDesk.Domain.Sla;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Customers;

/// <summary>
/// Customer 360: account KPIs (open/overdue/escalated tickets, 90-day volume, SLA breaches, CSAT, resolution time),
/// open tickets by priority, 12-month CSAT and ticket trends, outstanding complaints and escalations, recent
/// conversations, contracts/entitlements with consumption, installations, assets, licences, upcoming service
/// activities, key contacts and locations. Aggregates cover all of the customer's tickets; ticket content (subjects,
/// excerpts) is limited to tickets the agent may see. Sections are included only with the matching view permission.
/// </summary>
public sealed class Customer360Service(
    DeskDbContext db,
    RequestContext ctx,
    CustomerService customers,
    ContactService contacts,
    ContractService contracts,
    InstallationService installations,
    ServiceActivityService activities,
    TenantSettingsService settings,
    ErpIntegrationService erp)
{
    public async Task<Customer360Dto> GetAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.CustomersView);
        var customer = await customers.GetAsync(id, ct);
        var now = db.Clock.UtcNow;
        var today = DateOnly.FromDateTime(now);
        var since90 = now.AddDays(-90);
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-11);
        var prefix = (await settings.TicketsAsync(ct)).NumberPrefix;

        var all = db.Tickets.AsNoTracking().Where(t => t.CustomerId == id);
        var stats = await all.GroupBy(_ => 1).Select(g => new
        {
            Open = g.Count(t => t.StatusCategory < StatusCategory.Resolved),
            Overdue = g.Count(t => t.StatusCategory < StatusCategory.Resolved && t.DueAt != null && t.DueAt < now),
            Escalated = g.Count(t => t.StatusCategory < StatusCategory.Resolved && t.IsEscalated),
            Recent = g.Count(t => t.CreatedAt >= since90),
            Resolved = g.Count(t => t.ResolvedAt != null && t.ResolvedAt >= since90),
            Breaches = g.Count(t => t.CreatedAt >= since90 && t.SlaState == SlaState.Breached),
            BreachedOpen = g.Count(t => t.StatusCategory < StatusCategory.Resolved && t.SlaState == SlaState.Breached),
            Last = g.Max(t => (DateTime?)t.CreatedAt)
        }).FirstOrDefaultAsync(ct);
        var resolvedDurations = await all.Where(t => t.ResolvedAt != null && t.ResolvedAt >= since90).Select(t => new { t.CreatedAt, t.ResolvedAt }).Take(2000).ToListAsync(ct);
        double? avgResolution = resolvedDurations.Count == 0 ? null : Math.Round(resolvedDurations.Average(t => (t.ResolvedAt!.Value - t.CreatedAt).TotalHours), 1);

        var csatRows = await db.SatisfactionResponses.AsNoTracking()
            .Where(s => s.Rating != null && s.RespondedAt != null && s.RespondedAt >= monthStart && db.Tickets.Any(t => t.Id == s.TicketId && t.CustomerId == id))
            .Select(s => new { s.RespondedAt, s.Rating }).ToListAsync(ct);
        var csat90 = csatRows.Where(r => r.RespondedAt >= since90).ToList();
        var months = Enumerable.Range(0, 12).Select(i => monthStart.AddMonths(i)).ToList();
        string Key(DateTime d) => d.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var csatTrend = months.Select(m =>
        {
            var set = csatRows.Where(r => Key(r.RespondedAt!.Value) == Key(m)).ToList();
            return new CsatPointDto(Key(m), set.Count == 0 ? null : Math.Round(set.Average(r => r.Rating!.Value), 2), set.Count,
                set.Count(r => r.Rating >= 4), set.Count(r => r.Rating <= 2));
        }).ToList();

        var created = await all.Where(t => t.CreatedAt >= monthStart).Select(t => t.CreatedAt).ToListAsync(ct);
        var resolved = await all.Where(t => t.ResolvedAt != null && t.ResolvedAt >= monthStart).Select(t => t.ResolvedAt!.Value).ToListAsync(ct);
        var ticketTrend = months.Select(m => new TicketVolumePointDto(Key(m), created.Count(c => Key(c) == Key(m)), resolved.Count(r => Key(r) == Key(m)))).ToList();

        var byPriority = await all.Where(t => t.StatusCategory < StatusCategory.Resolved).GroupBy(t => t.Priority)
            .Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var openByPriority = Enum.GetValues<TicketPriority>().OrderByDescending(p => p)
            .Select(p => new CountDto(p.ToString(), p.ToString(), byPriority.FirstOrDefault(x => x.Key == p)?.Count ?? 0)).ToList();

        // Ticket content is limited to tickets the agent may see.
        var visible = db.Tickets.AsNoTracking().VisibleTo(ctx, db).Where(t => t.CustomerId == id);
        var complaintCategories = db.TicketCategories.Where(c => c.Name.Contains("Complaint") || c.Name.Contains("Escalation")).Select(c => c.Id);
        var outstanding = await visible.Where(t => t.StatusCategory < StatusCategory.Resolved &&
                                                   (t.IsEscalated || t.SlaState == SlaState.Breached || t.Priority == TicketPriority.Critical ||
                                                    t.Sentiment == Sentiment.Negative || t.Sentiment == Sentiment.Frustrated ||
                                                    (t.CategoryId != null && complaintCategories.Contains(t.CategoryId.Value)) ||
                                                    (t.SubcategoryId != null && complaintCategories.Contains(t.SubcategoryId.Value))))
            .OrderByDescending(t => t.IsEscalated).ThenByDescending(t => t.Priority).ThenBy(t => t.CreatedAt).Take(20).ToListAsync(ct);
        var statusRows = await db.TicketStatuses.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var assigneeNames = await customers.UserNamesAsync(outstanding.Select(t => t.AssigneeId), ct);
        var complaintIds = (await complaintCategories.ToListAsync(ct)).ToHashSet();
        var complaints = outstanding.Select(t =>
        {
            var reasons = new List<string>();
            if (t.IsEscalated) reasons.Add($"Escalated (level {Math.Max(1, t.EscalationLevel)})");
            if (t.SlaState == SlaState.Breached) reasons.Add("SLA breached");
            if (t.Priority == TicketPriority.Critical) reasons.Add("Critical priority");
            if (t.Sentiment is Sentiment.Negative or Sentiment.Frustrated) reasons.Add($"{t.Sentiment} sentiment");
            if ((t.CategoryId is { } c && complaintIds.Contains(c)) || (t.SubcategoryId is { } s && complaintIds.Contains(s))) reasons.Add("Complaint");
            return new Customer360TicketDto(t.Id, t.Number, $"{prefix}{t.Number}", t.Subject, t.Priority.ToString(), statusRows.GetValueOrDefault(t.StatusId) ?? "",
                t.StatusCategory.ToString(), t.IsEscalated, t.SlaState.ToString(), t.Sentiment.ToString(),
                t.AssigneeId is { } a ? assigneeNames.GetValueOrDefault(a) : null, t.CreatedAt, reasons);
        }).ToList();

        var visibleIds = visible.Select(t => t.Id);
        var threads = await db.TicketThreads.AsNoTracking()
            .Where(th => visibleIds.Contains(th.TicketId) && th.Visibility == Visibility.Public &&
                         (th.Kind == ThreadKind.CustomerMessage || th.Kind == ThreadKind.AgentReply || th.Kind == ThreadKind.ChatTranscript || th.Kind == ThreadKind.CallLog))
            .OrderByDescending(th => th.CreatedAt).Take(12)
            .Select(th => new { th.Id, th.TicketId, th.Kind, th.Channel, th.Direction, th.AuthorName, th.BodyText, th.CreatedAt }).ToListAsync(ct);
        var threadTicketIds = threads.Select(t => t.TicketId).Distinct().ToList();
        var threadTickets = await db.Tickets.AsNoTracking().Where(t => threadTicketIds.Contains(t.Id)).Select(t => new { t.Id, t.Number, t.Subject }).ToDictionaryAsync(t => t.Id, ct);
        var conversations = threads.Select(th =>
        {
            var t = threadTickets.GetValueOrDefault(th.TicketId);
            return new ConversationExcerptDto(th.TicketId, t is null ? "" : $"{prefix}{t.Number}", t?.Subject ?? "", th.Id, th.Kind.ToString(), th.Channel.ToString(),
                th.Direction.ToString(), th.AuthorName, TextNormalization.Truncate(th.BodyText, 280), th.CreatedAt);
        }).ToList();

        List<ContractListItemDto> contractList = [];
        List<EntitlementDto> entitlementList = [];
        if (ctx.Has(Permissions.ContractsView))
        {
            contractList = await contracts.ForCustomerAsync(id, ct);
            entitlementList = await contracts.EntitlementsForCustomerAsync(id, ct);
        }
        List<InstallationListItemDto> installationList = [];
        List<AssetDto> assetList = [];
        List<LicenceDto> licenceList = [];
        if (ctx.Has(Permissions.ProductsView))
        {
            installationList = await installations.ForCustomerAsync(id, ct);
            assetList = await installations.AssetsAsync(new AssetQuery { CustomerId = id, Limit = 100 }, ct: ct);
            licenceList = await installations.LicencesAsync(new LicenceQuery { CustomerId = id, Limit = 100 }, ct: ct);
        }
        var upcoming = await activities.ListAsync(new ServiceActivityQuery { CustomerId = id, From = now.AddDays(-1), To = now.AddDays(120), Limit = 20 }, ct);
        var keyContactRows = await db.Contacts.AsNoTracking().Where(k => k.CustomerId == id && k.Status == ContactStatus.Active)
            .OrderByDescending(k => k.IsPrimary).ThenByDescending(k => k.IsVip).ThenBy(k => k.FullName).Take(12).ToListAsync(ct);
        var keyContacts = await contacts.MapListAsync(keyContactRows, ct);
        var locations = await customers.LocationsAsync(id, ct);
        var activeContracts = await db.Contracts.CountAsync(c => c.CustomerId == id && c.Status == ContractStatus.Active && c.StartDate <= today && c.EndDate >= today, ct);
        var expiringSoon = await db.Contracts.CountAsync(c => c.CustomerId == id && c.Status == ContractStatus.Active && c.EndDate >= today && c.EndDate <= today.AddDays(60), ct);
        var installationCount = await db.Installations.CountAsync(i => i.CustomerId == id && i.Status != Domain.Products.InstallationStatus.Decommissioned, ct);
        var contactCount = await db.Contacts.CountAsync(k => k.CustomerId == id && k.Status == ContactStatus.Active, ct);

        var kpis = new Customer360KpisDto(stats?.Open ?? 0, stats?.Overdue ?? 0, stats?.Escalated ?? 0, stats?.Recent ?? 0, stats?.Resolved ?? 0, stats?.Breaches ?? 0,
            stats?.BreachedOpen ?? 0, csat90.Count == 0 ? null : Math.Round(csat90.Average(r => r.Rating!.Value), 2), csat90.Count, avgResolution, activeContracts,
            expiringSoon, installationCount, contactCount, upcoming.Count(a => a.ScheduledStart >= now && a.Status == nameof(ServiceActivityStatus.Scheduled)), stats?.Last);
        var erpConnected = customer.ErpCustomerId is not null && await erp.IsActiveAsync(Contracts.Erp.ErpSystems.WingsErp, ct);
        return new Customer360Dto(customer, kpis, openByPriority, csatTrend, ticketTrend, complaints, conversations, contractList, entitlementList, installationList,
            assetList, licenceList, upcoming, keyContacts, locations, erpConnected);
    }
}
