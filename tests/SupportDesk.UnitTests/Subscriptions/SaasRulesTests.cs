using System.Text.Json;
using System.Text.Json.Nodes;
using SupportDesk.Application.Administration.Compliance;
using SupportDesk.Application.Administration.ConfigTransfer;
using SupportDesk.Application.Subscriptions;
using SupportDesk.Contracts.ConfigTransfer;
using SupportDesk.Domain;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Tickets;

namespace SupportDesk.UnitTests.Subscriptions;

public sealed class BillingMathTests
{
    private static readonly Plan Pro = new() { Code = "pro", MonthlyPricePerAgent = 35m, AnnualPricePerAgent = 350m, SortOrder = 2 };
    private static readonly Plan Ent = new() { Code = "ent", MonthlyPricePerAgent = 60m, AnnualPricePerAgent = 600m, SortOrder = 3 };

    [Fact]
    public void Period_amount_is_seat_price_times_seats()
    {
        Assert.Equal(350m, BillingMath.PeriodAmount(Pro, BillingCycle.Monthly, 10));
        Assert.Equal(3500m, BillingMath.PeriodAmount(Pro, BillingCycle.Annual, 10));
    }

    [Fact]
    public void Upgrade_halfway_through_the_period_charges_half_the_difference()
    {
        var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddDays(30);
        var charge = BillingMath.ProratedCharge(Pro, 10, Ent, 10, BillingCycle.Monthly, start, end, start.AddDays(15));
        Assert.Equal(125m, charge); // (600 − 350) × 0.5
        Assert.Equal(0m, BillingMath.ProratedCharge(Ent, 10, Pro, 10, BillingCycle.Monthly, start, end, start.AddDays(15)));
        Assert.Equal(0m, BillingMath.ProratedCharge(Pro, 10, Ent, 10, BillingCycle.Monthly, start, end, end.AddDays(1)));
    }

    [Fact]
    public void Direction_compares_period_amounts_then_plan_order()
    {
        Assert.Equal("upgrade", BillingMath.Direction(Pro, 10, Ent, 10, BillingCycle.Monthly));
        Assert.Equal("downgrade", BillingMath.Direction(Ent, 10, Pro, 10, BillingCycle.Monthly));
        Assert.Equal("upgrade", BillingMath.Direction(Pro, 10, Pro, 12, BillingCycle.Monthly));
        Assert.Equal("downgrade", BillingMath.Direction(Pro, 10, Pro, 8, BillingCycle.Monthly));
        Assert.Equal("same", BillingMath.Direction(Pro, 10, Pro, 10, BillingCycle.Monthly));
    }

    [Fact]
    public void Meters_report_warning_and_exceeded_states()
    {
        Assert.Equal("ok", PlanCatalog.Meter(UsageMetrics.Agents, 5, 10).State);
        Assert.Equal("warning", PlanCatalog.Meter(UsageMetrics.Agents, 8, 10).State);
        Assert.Equal("exceeded", PlanCatalog.Meter(UsageMetrics.Agents, 11, 10).State);
        Assert.Equal("unlimited", PlanCatalog.Meter(UsageMetrics.Agents, 11, null).State);
    }

    [Fact]
    public void Limit_overrides_replace_plan_limits()
    {
        var sub = new Subscription { LimitOverridesJson = "{\"MaxAgents\":80,\"StorageMb\":10}" };
        var plan = new Plan { MaxAgents = 10, StorageMb = 1 };
        Assert.Equal(80, PlanCatalog.EffectiveLimit(plan, sub, UsageMetrics.Agents));
        Assert.Equal(10L * 1024 * 1024, PlanCatalog.EffectiveLimit(plan, sub, UsageMetrics.StorageBytes));
        Assert.Null(PlanCatalog.EffectiveLimit(plan, null, UsageMetrics.Attachments));
    }
}

public sealed class AuditDiffTests
{
    [Fact]
    public void Before_after_objects_are_flattened_into_changes()
    {
        var diff = AuditDiff.Compute(JsonDocument.Parse("""{"before":{"name":"A","limits":{"agents":5},"tags":["x"]},"after":{"name":"B","limits":{"agents":5},"extra":true}}""").RootElement);
        Assert.Contains(diff, d => d is { Path: "name", Before: "A", After: "B", Kind: "changed" });
        Assert.Contains(diff, d => d is { Path: "tags[0]", Kind: "removed" });
        Assert.Contains(diff, d => d is { Path: "extra", After: "true", Kind: "added" });
        Assert.DoesNotContain(diff, d => d.Path == "limits.agents");
    }

    [Fact]
    public void Field_change_lists_and_key_from_to_shapes_are_understood()
    {
        var list = AuditDiff.Compute(JsonDocument.Parse("""[{"field":"priority","from":"Medium","to":"High"}]""").RootElement);
        Assert.Equal(new AuditDiffDtoShape("priority", "Medium", "High", "changed"), Shape(list[0]));
        var keyed = AuditDiff.Compute(JsonDocument.Parse("""{"key":"security","from":{"a":1},"to":{"a":2}}""").RootElement);
        Assert.Contains(keyed, d => d.Path == "a" && d.Before == "1" && d.After == "2");
    }

    private sealed record AuditDiffDtoShape(string Path, string? Before, string? After, string Kind);
    private static AuditDiffDtoShape Shape(Contracts.Compliance.AuditDiffDto d) => new(d.Path, d.Before, d.After, d.Kind);

    [Fact]
    public void Csv_cells_are_quoted_and_formulas_neutralised()
    {
        Assert.Equal("\"=1+1\"".Replace("\"=", "\"'="), AuditQueryService.Csv("=1+1"));
        Assert.Equal("\"say \"\"hi\"\"\"", AuditQueryService.Csv("say \"hi\""));
        Assert.Equal("", AuditQueryService.Csv(null));
    }
}

public sealed class ConfigCodecTests
{
    [Fact]
    public void Rename_replaces_the_last_key_segment()
    {
        Assert.Equal("Hardware/Scanners", ConfigSections.RenameKey("Hardware/Printers", "Scanners"));
        Assert.Equal("Ticket:asset_serial", ConfigSections.RenameKey("Ticket:serial_number", "asset_serial"));
        Assert.Equal("New", ConfigSections.RenameKey("Old", "New"));
    }

    [Fact]
    public void Guid_references_round_trip_through_natural_keys()
    {
        var sourceStatus = Guid.NewGuid();
        var refs = new ConfigRefs();
        refs.Add(sourceStatus, "statuses", "Waiting on Customer");
        var status = new TicketStatus { Name = "Resolved", AllowedNextJson = $"[\"{sourceStatus}\"]", Category = StatusCategory.Resolved };
        var exported = ConfigCodec.ExportObject(status, refs, null);
        Assert.Equal("@ref:statuses/Waiting on Customer", exported["AllowedNextJson"]![0]!.GetValue<string>());
        Assert.False(exported.ContainsKey("Id"));
        Assert.False(exported.ContainsKey("TenantId"));

        var targetStatus = Guid.NewGuid();
        var ctx = new ConfigApplyContext(DateTime.UtcNow, null, "test");
        ctx.Resolver["statuses/Waiting on Customer"] = targetStatus;
        var imported = new TicketStatus();
        ConfigCodec.ApplyObject(imported, exported, ctx, null, refsOnly: null);
        Assert.Equal("Resolved", imported.Name);
        Assert.Equal(StatusCategory.Resolved, imported.Category);
        Assert.Equal($"[\"{targetStatus}\"]", imported.AllowedNextJson);
    }

    [Fact]
    public void Unresolved_references_are_dropped_with_a_warning()
    {
        var ctx = new ConfigApplyContext(DateTime.UtcNow, null, "test");
        var json = ConfigCodec.ImportJson(JsonNode.Parse("""{"ids":["@ref:departments/Gone","keep"],"one":"@ref:users/x@y"}"""), ctx);
        Assert.Equal("""{"ids":["keep"],"one":null}""", json);
        Assert.NotEmpty(ctx.Warnings);
    }

    [Fact]
    public void Resolution_defaults_follow_the_item_status()
    {
        var section = ConfigSections.Find("statuses")!;
        Assert.Equal("create", ConfigTransferEngine.Resolve(section, "new", null).Action);
        Assert.Equal("overwrite", ConfigTransferEngine.Resolve(section, "changed", null).Action);
        Assert.Equal("skip", ConfigTransferEngine.Resolve(section, "unchanged", null).Action);
        Assert.Equal(("rename", "Copy"), ConfigTransferEngine.Resolve(section, "changed", new ConfigResolutionDto("statuses/X", "rename", "Copy")));
        Assert.Equal("create", ConfigTransferEngine.Resolve(section, "new", new ConfigResolutionDto("statuses/X", "overwrite")).Action);
        Assert.Equal("overwrite", ConfigTransferEngine.Resolve(ConfigSections.Find("prompts")!, "changed", new ConfigResolutionDto("prompts/x", "rename", "y")).Action);
    }
}
