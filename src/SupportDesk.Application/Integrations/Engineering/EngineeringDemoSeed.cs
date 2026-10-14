using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Seed;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Engineering;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain.Integration;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Integrations.Engineering;

/// <summary>
/// Demo data for engineering escalation (wings tenant): the built-in tracker with releases 8.0.1 (released) and 8.1
/// (planned), four work items escalated from demo tickets through the real services (timeline entries, substatus,
/// events) — ENG-1 (Sales Register export, fixed in 8.0.1 and linked to two customers' tickets), the POS freeze, the
/// discount-scheme crash and the loyalty feature request — plus a "Fix released" reply template. Azure DevOps and GitHub
/// stay unconfigured (see docs/integrations/ENGINEERING.md).
/// </summary>
public sealed class EngineeringDemoSeed(ILogger<EngineeringDemoSeed> logger) : IDemoSeedContributor
{
    public int Order => 60;

    public async Task SeedAsync(DemoSeedContext c, CancellationToken ct)
    {
        Guid trackerId = default, release801 = default, release81 = default;
        c.At(c.DaysAgo(30));
        await c.AsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            if (await db.Integrations.AnyAsync(i => i.Type == IntegrationTypes.LocalEngineering, ct)) return;
            var tracker = new Integration
            {
                Type = IntegrationTypes.LocalEngineering, Name = "Wings engineering board", SettingsJson = Json.Serialize(TrackerSettings.Defaults(Domain.Tickets.EngineeringProvider.Local)),
                Status = IntegrationStatus.Connected
            };
            db.Integrations.Add(tracker);
            var r801 = new LocalRelease
            {
                IntegrationId = tracker.Id, Name = "8.0.1", ProductId = c.Products["erp"], PlannedDate = DateOnly.FromDateTime(c.Anchor.AddDays(-2)),
                Notes = "Wings ERP 8.0.1 patch: paged Excel export for large registers, GST rounding fixes."
            };
            var r81 = new LocalRelease
            {
                IntegrationId = tracker.Id, Name = "8.1", ProductId = c.Products["erp"], PlannedDate = DateOnly.FromDateTime(c.Anchor.AddDays(45)),
                Notes = "Wings ERP 8.1 / POS 3.3: loyalty redemption with partial payments, billing counter performance."
            };
            db.Set<LocalRelease>().AddRange(r801, r81);
            var template = new ReplyTemplate
            {
                Name = "Fix released", Category = "Engineering", Subject = "The fix for your issue is available",
                BodyHtml = "<p>Hi {{contact.name}},</p><p>The fix for ticket {{ticket.number}} (“{{ticket.subject}}”) is now available in version " +
                           "<strong>{{engineering.fixVersion}}</strong>. Please update and let us know if anything still looks wrong.</p><p>Regards,<br>{{agent.name}}</p>"
            };
            db.ReplyTemplates.Add(template);
            await db.SaveChangesAsync(ct);
            trackerId = tracker.Id;
            release801 = r801.Id;
            release81 = r81.Id;
            await sp.GetRequiredService<TenantSettingsService>().SetAsync(EngineeringSettings.Key, new EngineeringSettings
            {
                AddNoteOnResolved = true, AddNoteOnReleased = true, NotifyAssignee = true, NotifyCustomerOnRelease = false, ReleaseReplyTemplateId = template.Id,
                SetEngineeringSubstatus = true, EscalationStatusId = c.Statuses.GetValueOrDefault("Waiting on Third Party"), DefaultTrackerId = tracker.Id
            }, ct);
        });
        if (trackerId == default) return;

        try
        {
            // ENG-1: export timeout, escalated by Meera after reproducing it, fixed and released in 8.0.1.
            var (eng1, escalatedAt) = await EscalateAsync(c, "export", "meera", 23, trackerId, new EscalateToEngineeringRequest
            {
                TrackerId = trackerId, ItemType = "Bug", Title = "Sales Register export to Excel times out for large date ranges", Severity = "Major", Priority = "High",
                AssigneeUserId = c.Users["dev"], Tags = ["export", "performance"], AreaPath = "ERP\\Sales", IterationPath = "8.0.1"
            }, ct);
            // A second customer hits the same defect: linked to the same work item ("other tickets affected").
            var second = await CreateTicketAsync(c, ct);
            if (second is { } secondId && eng1 is not null)
            {
                c.At(Cap(c, c.DaysAgo(0.8, 12)));
                await c.AsAgentAsync("karthik", sp => sp.GetRequiredService<EngineeringTicketService>()
                    .LinkAsync(secondId, new LinkWorkItemRequest { TrackerId = trackerId, Reference = eng1.ExternalId, SetSubstatus = false }, ct));
            }
            if (eng1 is not null)
            {
                var fixedAt = Cap(c, escalatedAt.AddHours(6));
                await BoardAsync(c, "dev", fixedAt, async board =>
                {
                    var item = await board.GetAsync(await WorkItemIdAsync(c, trackerId, eng1.ExternalId, ct), ct);
                    var w = item.Item;
                    await board.UpdateAsync(w.Id, new SaveEngineeringWorkItemRequest
                    {
                        Title = w.Title, Type = w.Type, Severity = w.Severity, Priority = w.Priority, AssigneeUserId = w.AssigneeUserId, AreaPath = w.AreaPath,
                        IterationPath = w.IterationPath, Tags = [.. w.Tags], ReleaseId = release801, ProductId = c.Products["erp"],
                        DescriptionHtml = w.DescriptionHtml + "<p><strong>Fix:</strong> export now pages 20k rows at a time and streams to the workbook.</p>",
                        RowVersion = w.RowVersion
                    }, ct);
                    var moved = await board.GetAsync(w.Id, ct);
                    await board.MoveAsync(w.Id, new MoveEngineeringWorkItemRequest("Resolved", null, moved.Item.RowVersion), ct);
                });
                await BoardAsync(c, "lakshmi", Cap(c, fixedAt.AddHours(2)), board => board.PublishReleaseAsync(release801, new PublishEngineeringReleaseRequest(Deployed: true), ct));
            }
        }
        catch (Exception ex) when (ex is Domain.DomainException or InvalidOperationException or KeyNotFoundException)
        {
            logger.LogWarning(ex, "Engineering demo data incomplete (ENG-1)");
        }

        // ENG-2: the POS billing counter freeze (ticket already in "Escalated to Engineering").
        await TryAsync("POS freeze", () => EscalateAsync(c, "pharmacy", "meera", 5, trackerId, new EscalateToEngineeringRequest
        {
            TrackerId = trackerId, ItemType = "Bug", Title = "POS billing counter freezes when scanning more than 30 items", Severity = "Critical", Priority = "High",
            AssigneeUserId = c.Users["dev"], Tags = ["pos", "billing-counter"], SetSubstatus = false
        }, ct, activate: true, releaseId: release81));

        // ENG-3: discount scheme crash behind the VIP outage.
        await TryAsync("discount scheme crash", () => EscalateAsync(c, "vip", "meera", 3, trackerId, new EscalateToEngineeringRequest
        {
            TrackerId = trackerId, ItemType = "Bug", Title = "Invoice save fails with a null discount scheme for walk-in patients", Severity = "Critical",
            Priority = "Critical", AssigneeUserId = c.Users["dev"], Tags = ["billing", "regression"]
        }, ct, activate: true));

        // ENG-4: the loyalty feature request from the roadmap backlog.
        await TryAsync("loyalty feature", () => EscalateAsync(c, "feature", "lakshmi,vikram", 31, trackerId, new EscalateToEngineeringRequest
        {
            TrackerId = trackerId, ItemType = "Feature", Title = "Redeem loyalty points together with UPI for the balance", Priority = "Low", Tags = ["loyalty"],
            SetSubstatus = false
        }, ct, releaseId: release81));
    }

    private async Task TryAsync(string what, Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex) when (ex is Domain.DomainException or InvalidOperationException or KeyNotFoundException)
        {
            logger.LogWarning(ex, "Engineering demo data incomplete ({What})", what);
        }
    }

    private static async Task<(EngineeringLinkViewDto? Link, DateTime At)> EscalateAsync(DemoSeedContext c, string ticketKey, string agent, double afterHours, Guid trackerId,
        EscalateToEngineeringRequest request, CancellationToken ct, bool activate = false, Guid? releaseId = null)
    {
        if (!c.Tickets.TryGetValue(ticketKey, out var ticketId)) return (null, c.Anchor);
        DateTime created = default;
        await c.AsSystemAsync(async sp => created = await sp.GetRequiredService<DeskDbContext>().Tickets.AsNoTracking().Where(t => t.Id == ticketId).Select(t => t.CreatedAt).FirstAsync(ct));
        var at = created.AddHours(afterHours);
        if (at > c.Anchor.AddMinutes(-30)) at = c.Anchor.AddMinutes(-30);
        c.At(at);
        EngineeringLinkViewDto? link = null;
        // The first persona who can see the ticket escalates it (department scope depends on earlier demo steps).
        foreach (var persona in agent.Split(','))
        {
            try
            {
                await c.AsAgentAsync(persona, async sp =>
                {
                    var svc = sp.GetRequiredService<EngineeringTicketService>();
                    var prefill = await svc.PrefillAsync(ticketId, ct);
                    link = (await svc.EscalateAsync(ticketId, request with { ReproStepsHtml = request.ReproStepsHtml ?? prefill.ReproStepsHtml }, ct)).Link;
                });
                break;
            }
            catch (Domain.NotFoundException) when (persona != agent.Split(',')[^1])
            {
                // Out of this persona's scope: try the next one.
            }
        }
        if (link is null || (!activate && releaseId is null)) return (link, at);
        await BoardAsync(c, "dev", Cap(c, at.AddHours(0.5)), async board =>
        {
            var w = (await board.GetAsync(await WorkItemIdAsync(c, trackerId, link.ExternalId, ct), ct)).Item;
            if (releaseId is not null)
            {
                await board.UpdateAsync(w.Id, new SaveEngineeringWorkItemRequest
                {
                    Title = w.Title, Type = w.Type, Severity = w.Severity, Priority = w.Priority, AssigneeUserId = w.AssigneeUserId, AreaPath = w.AreaPath,
                    IterationPath = w.IterationPath, Tags = [.. w.Tags], ReleaseId = releaseId, ProductId = w.ProductId, DescriptionHtml = w.DescriptionHtml, RowVersion = w.RowVersion
                }, ct);
                w = (await board.GetAsync(w.Id, ct)).Item;
            }
            if (activate) await board.MoveAsync(w.Id, new MoveEngineeringWorkItemRequest("Active", null, w.RowVersion), ct);
        });
        return (link, at);
    }

    private static DateTime Cap(DemoSeedContext c, DateTime at) => at > c.Anchor.AddMinutes(-10) ? c.Anchor.AddMinutes(-10) : at;

    /// <summary>Runs board work as a persona at <paramref name="at"/> (clock pins only flow into awaited calls, so every step pins explicitly).</summary>
    private static async Task BoardAsync(DemoSeedContext c, string user, DateTime at, Func<EngineeringBoardService, Task> work)
    {
        c.At(at);
        await c.AsAgentAsync(user, sp => work(sp.GetRequiredService<EngineeringBoardService>()));
    }

    private static async Task<Guid> WorkItemIdAsync(DemoSeedContext c, Guid trackerId, string externalId, CancellationToken ct)
    {
        var number = int.Parse(externalId, System.Globalization.CultureInfo.InvariantCulture);
        var id = Guid.Empty;
        await c.AsSystemAsync(async sp => id = await sp.GetRequiredService<DeskDbContext>().Set<LocalWorkItem>().AsNoTracking()
            .Where(w => w.IntegrationId == trackerId && w.Number == number).Select(w => w.Id).FirstAsync(ct));
        return id;
    }

    private static async Task<Guid?> CreateTicketAsync(DemoSeedContext c, CancellationToken ct)
    {
        if (!c.Contacts.TryGetValue("ramesh", out var contact) || !c.Customers.TryGetValue("abc", out var customer)) return null;
        c.At(c.DaysAgo(0.8, 11));
        Guid id = default;
        await c.AsSystemAsync(async sp => id = (await sp.GetRequiredService<TicketService>().CreateAsync(new CreateTicketRequest
        {
            Subject = "Sales Register export to Excel hangs for the full financial year", Channel = "Email", ContactId = contact, CustomerId = customer,
            DescriptionHtml = "<p>Exporting the Sales Register for April–March to Excel never finishes. Quarter-wise exports work.</p>",
            ProductId = c.Products["erp"], ModuleId = c.Modules.TryGetValue("erp:Sales", out var module) ? module : null, Priority = "Medium",
            DepartmentId = c.Departments.TryGetValue("support", out var department) ? department : null
        }, new TicketCreationSource(CreateInitialThread: true, EnforceRequiredFields: false), ct)).Id);
        return id;
    }
}
