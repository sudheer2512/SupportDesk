using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Common;
using SupportDesk.Contracts.ConfigTransfer;
using SupportDesk.Domain;
using SupportDesk.Domain.Configuration;
using SupportDesk.Domain.Sla;
using SupportDesk.Domain.Tickets;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.Persistence;

namespace SupportDesk.IntegrationTests.Subscriptions;

[Collection("api")]
public sealed class ConfigTransferTests(DeskApiFactory factory)
{
    private static readonly JsonSerializerOptions J = SaasTestKit.J;

    /// <summary>Adds a status, a two-level category, a custom field and an SLA policy that references the status and the calendar.</summary>
    private Task SeedConfigAsync(Guid tenantId) => factory.InTenantAsync(tenantId, async sp =>
    {
        var db = sp.GetRequiredService<DeskDbContext>();
        var status = new TicketStatus { Name = "Awaiting Parts", Category = StatusCategory.Waiting, Color = "#a16207", SortOrder = 8, PausesSla = true };
        db.TicketStatuses.Add(status);
        var hardware = new TicketCategory { Name = "Hardware" };
        db.TicketCategories.AddRange(hardware, new TicketCategory { Name = "Printers", ParentId = hardware.Id });
        db.CustomFields.Add(new CustomFieldDefinition { Entity = "Ticket", Key = "serial_number", Label = "Serial number", FieldType = CustomFieldType.Text });
        var calendar = await db.BusinessCalendars.FirstAsync(c => c.IsDefault);
        var sla = new SlaPolicy
        {
            Name = "Hardware SLA", CalendarId = calendar.Id, EvaluationOrder = 10, PauseStatusIdsJson = Json.Serialize(new[] { status.Id })
        };
        sla.Targets.Add(new SlaTarget { PolicyId = sla.Id, Priority = TicketPriority.High, FirstResponseMinutes = 30, ResolutionMinutes = 600 });
        db.SlaPolicies.Add(sla);
        await db.SaveChangesAsync();
    });

    [Fact]
    public async Task Exported_configuration_imports_into_another_tenant_with_ids_remapped()
    {
        var source = await factory.CreateTenantAsync();
        var target = await factory.CreateTenantAsync();
        await SeedConfigAsync(source.Id);
        var sourceAdmin = await source.AdminClientAsync();
        var targetAdmin = await target.AdminClientAsync();

        var export = await sourceAdmin.GetAsync("/api/v1/admin/config/export");
        var bundle = JsonDocument.Parse(await export.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("supportdesk.config", bundle.GetProperty("format").GetString());
        Assert.DoesNotContain(source.Id.ToString(), bundle.GetRawText());

        // A local tweak in the target makes "Open" a conflict; keep the target's version.
        await factory.InTenantAsync(target.Id, sp => sp.GetRequiredService<DeskDbContext>().TicketStatuses.Where(s => s.Name == "Open")
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Color, "#000000")));
        var draft = await (await targetAdmin.PostAsJsonAsync("/api/v1/admin/config/imports", new ImportConfigRequest(bundle, "From source"), J)).ReadAsync<ConfigDraftDto>();
        var items = draft.Diff!.Sections.SelectMany(s => s.Items).ToList();
        Assert.Equal("new", items.Single(i => i.Key == "statuses/Awaiting Parts").Status);
        Assert.Equal("new", items.Single(i => i.Key == "categories/Hardware/Printers").Status);
        var open = items.Single(i => i.Key == "statuses/Open");
        Assert.Equal("changed", open.Status);
        Assert.Contains(open.Changes, c => c.Field == "Color");
        Assert.Equal("unchanged", items.Single(i => i.Key == "sla/Standard SLA").Status);

        draft = await (await targetAdmin.PutAsJsonAsync($"/api/v1/admin/config/imports/{draft.Id}/resolutions",
            new SaveResolutionsRequest([new ConfigResolutionDto("statuses/Open", "skip"), new ConfigResolutionDto("fields/Ticket:serial_number", "rename", "asset_serial")]), J))
            .ReadAsync<ConfigDraftDto>();
        Assert.Equal("skip", draft.Diff!.Sections.SelectMany(s => s.Items).Single(i => i.Key == "statuses/Open").Resolution);

        var published = await (await targetAdmin.PostAsync($"/api/v1/admin/config/imports/{draft.Id}/publish", null)).ReadAsync<ConfigPublishResultDto>();
        Assert.True(published.Result.Created >= 5, string.Join("; ", published.Result.Warnings));
        Assert.Equal("Import", published.Snapshot.Kind);

        await factory.InTenantAsync(target.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var status = await db.TicketStatuses.SingleAsync(s => s.Name == "Awaiting Parts");
            Assert.Equal("#000000", (await db.TicketStatuses.SingleAsync(s => s.Name == "Open")).Color);
            var parent = await db.TicketCategories.SingleAsync(c => c.Name == "Hardware");
            var child = await db.TicketCategories.SingleAsync(c => c.Name == "Printers");
            Assert.Equal(parent.Id, child.ParentId);
            Assert.True(await db.CustomFields.AnyAsync(f => f.Key == "asset_serial"));
            var sla = await db.SlaPolicies.Include(p => p.Targets).SingleAsync(p => p.Name == "Hardware SLA");
            Assert.Equal([status.Id], Json.GuidList(sla.PauseStatusIdsJson));
            Assert.Equal((await db.BusinessCalendars.FirstAsync(c => c.IsDefault)).Id, sla.CalendarId);
            Assert.Single(sla.Targets);
            Assert.Equal(1, await db.TicketStatuses.CountAsync(s => s.IsDefault));
        });
    }

    [Fact]
    public async Task Rollback_restores_a_snapshot_and_is_itself_recorded_as_a_snapshot()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var v1 = await (await admin.PostAsJsonAsync("/api/v1/admin/config/snapshots", new CreateSnapshotRequest("Before changes"), J)).ReadAsync<ConfigSnapshotDto>();

        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            db.TicketStatuses.Add(new TicketStatus { Name = "Temporary", Category = StatusCategory.InProgress, SortOrder = 20 });
            (await db.TicketStatuses.SingleAsync(s => s.Name == "Open")).Color = "#ff00ff";
            await db.SaveChangesAsync();
        });

        var result = await (await admin.PostAsJsonAsync($"/api/v1/admin/config/snapshots/{v1.Id}/rollback", new RollbackRequest(null), J)).ReadAsync<ConfigPublishResultDto>();
        Assert.True(result.Result.Deactivated >= 1);
        Assert.True(result.Result.Updated >= 1);
        Assert.Equal("Rollback", result.Snapshot.Kind);

        var snapshots = await (await admin.GetAsync("/api/v1/admin/config/snapshots")).ReadAsync<List<ConfigSnapshotDto>>();
        Assert.Equal(["Rollback", "PreRollback", "Manual"], snapshots.Select(s => s.Kind).ToList());
        Assert.Equal(v1.Version + 2, snapshots[0].Version);
        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            Assert.Equal("#2563eb", (await db.TicketStatuses.SingleAsync(s => s.Name == "Open")).Color);
            Assert.False((await db.TicketStatuses.SingleAsync(s => s.Name == "Temporary")).IsActive);
        });
    }
}
