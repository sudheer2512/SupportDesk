using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Seed;
using SupportDesk.Contracts.Administration;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Administration;

/// <summary>
/// Demo administration data for tenant "wings", created through the real services (validation + audit): a custom
/// "Escalation Desk" role, a ticket field required before "Waiting on Third Party", a branded acknowledgement e-mail and
/// the default brand's e-mail footer.
/// </summary>
public sealed class AdministrationDemoSeed : IDemoSeedContributor
{
    public int Order => 80;

    public async Task SeedAsync(DemoSeedContext c, CancellationToken ct)
    {
        c.At(c.Anchor.AddHours(-1));
        await c.AsAgentAsync("admin", async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var roles = sp.GetRequiredService<RoleAdminService>();
            if (!await db.Roles.AnyAsync(r => r.Name == "Escalation Desk", ct))
                await roles.CreateAsync(new SaveRoleRequest("Escalation Desk", "Senior agents handling escalations across every department (custom role).",
                    nameof(TicketScope.All), nameof(CustomerScope.All), false,
                    [.. Permissions.Templates.Agent, Permissions.TicketsTransfer, Permissions.TicketsBulk, Permissions.ApprovalsDecide, Permissions.SupervisorMonitor]), ct);

            if (c.Statuses.TryGetValue("Waiting on Third Party", out var thirdParty) && !await db.CustomFields.AnyAsync(f => f.Entity == "Ticket" && f.Key == "vendor_reference", ct))
                await sp.GetRequiredService<FieldLayoutService>().CreateFieldAsync(new SaveCustomFieldRequest("Ticket", "vendor_reference", "Vendor reference", "Text", null,
                    false, null, "The vendor's or partner's case number — required while the ticket waits on a third party.", @"^[A-Za-z0-9\-/#]{3,40}$", null, null, 40,
                    false, false, null, [thirdParty], true), ct);

            var notifications = sp.GetRequiredService<NotificationAdminService>();
            var ack = await notifications.GetTemplateAsync(EmailTemplateCatalog.CustomerAcknowledgement, ct);
            if (!ack.IsCustomized)
                await notifications.SaveTemplateAsync(EmailTemplateCatalog.CustomerAcknowledgement, new SaveNotificationTemplateRequest("We received your request {{ticket.number}}",
                    "<p>Hello {{contact.name}},</p><p>Thank you for writing to <strong>{{brand.name}}</strong>. Your request is logged as <strong>{{ticket.number}}</strong> — " +
                    "<em>{{ticket.subject}}</em>.</p><p>A Wings support engineer will respond within your contract's response time. Reply to this e-mail to add details, " +
                    "or follow progress in the help center: <a href=\"{{portal.url}}\">{{portal.url}}</a>.</p>", true), ct);

            var brand = await db.Brands.FirstOrDefaultAsync(b => b.IsDefault, ct);
            if (brand is not null && string.IsNullOrEmpty(brand.EmailFooterHtml))
            {
                brand.EmailFooterHtml = "<p style=\"color:#64748b\">Wings Software Support · Mon–Sat 9:00–19:00 IST · support@wings.demo</p>";
                await db.SaveChangesAsync(ct);
            }
        });
    }
}
