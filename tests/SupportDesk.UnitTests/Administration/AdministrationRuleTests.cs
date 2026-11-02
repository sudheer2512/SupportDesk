using System.Text.Json;
using SupportDesk.Application.Administration;
using SupportDesk.Application.Platform;
using SupportDesk.Contracts.Administration;
using SupportDesk.Domain.Configuration;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Rules;

namespace SupportDesk.UnitTests.Administration;

public sealed class AdministrationRuleTests
{
    [Theory]
    [InlineData("10.0.0.7", true)]
    [InlineData("10.0.1.7", false)]
    [InlineData("::ffff:10.0.0.9", true)] // IPv4-mapped IPv6 (Kestrel dual-stack) matches IPv4 rules
    [InlineData("203.0.113.5", true)]
    [InlineData("203.0.113.6", false)]
    [InlineData("2001:db8:1::5", true)]
    [InlineData("2001:db9::1", false)]
    [InlineData(null, false)]
    [InlineData("not-an-ip", false)]
    public void Ip_ranges_match_addresses_and_cidr_blocks(string? address, bool expected) =>
        Assert.Equal(expected, IpRanges.Matches(address, ["10.0.0.0/24", "203.0.113.5", "2001:db8::/32"]));

    [Theory]
    [InlineData("10.0.0.0/8", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("fe80::/10", true)]
    [InlineData("10.0.0.0/33", false)]
    [InlineData("example.com", false)]
    [InlineData("", false)]
    public void Ip_range_syntax_is_validated(string text, bool valid) => Assert.Equal(valid, IpRanges.TryParse(text, out _));

    [Fact]
    public void Granting_permissions_or_scope_beyond_ones_own_is_an_escalation()
    {
        var actor = new HashSet<string> { Permissions.TicketsView, Permissions.AdminRoles };
        Assert.Empty(RoleRules.NotHeld(actor, [Permissions.TicketsView]));
        Assert.Equal([Permissions.TicketsDelete], RoleRules.NotHeld(actor, [Permissions.TicketsView, Permissions.TicketsDelete, Permissions.TicketsDelete]));
        Assert.True(RoleRules.ScopeWithin(TicketScope.Departments, CustomerScope.All, TicketScope.Team, CustomerScope.AccountManaged));
        Assert.False(RoleRules.ScopeWithin(TicketScope.Departments, CustomerScope.All, TicketScope.All, CustomerScope.All));
        Assert.False(RoleRules.ScopeWithin(TicketScope.All, CustomerScope.AccountManaged, TicketScope.All, CustomerScope.All));
    }

    [Fact]
    public void Administrators_are_users_holding_an_administrative_role()
    {
        Guid adminRole = Guid.NewGuid(), agentRole = Guid.NewGuid(), u1 = Guid.NewGuid(), u2 = Guid.NewGuid();
        var users = new Dictionary<Guid, IReadOnlyCollection<Guid>> { [u1] = [adminRole, agentRole], [u2] = [agentRole] };
        Assert.Equal(1, RoleRules.CountAdministrators(users, new HashSet<Guid> { adminRole }));
        users[u1] = [agentRole];
        Assert.Equal(0, RoleRules.CountAdministrators(users, new HashSet<Guid> { adminRole }));
        Assert.Equal(2, RoleRules.CountAdministrators(users, new HashSet<Guid> { agentRole }));
    }

    [Fact]
    public void Fields_required_for_a_status_are_reported_when_empty()
    {
        var resolved = Guid.NewGuid();
        var support = Guid.NewGuid();
        var billing = Guid.NewGuid();
        CustomFieldDefinition Field(string key, string label, Guid[]? depts = null, bool active = true) => new()
        {
            Entity = "Ticket", Key = key, Label = label, IsActive = active, RequiredForStatusIdsJson = JsonSerializer.Serialize(new[] { resolved }),
            DepartmentIdsJson = JsonSerializer.Serialize(depts ?? [])
        };
        var defs = new[]
        {
            Field("root_cause", "Root cause"), Field("billing_ref", "Billing reference", [billing]), Field("old", "Retired", active: false),
            new CustomFieldDefinition { Entity = "Ticket", Key = "notes", Label = "Notes", IsActive = true }
        };
        Assert.Equal(["Root cause"], CustomFieldRules.MissingForStatus(defs, "{}", support, resolved));
        Assert.Equal(["Root cause", "Billing reference"], CustomFieldRules.MissingForStatus(defs, "{\"root_cause\":\"  \",\"billing_ref\":[]}", billing, resolved));
        Assert.Empty(CustomFieldRules.MissingForStatus(defs, "{\"root_cause\":\"defect\"}", support, resolved));
        Assert.Empty(CustomFieldRules.MissingForStatus(defs, "{}", support, Guid.NewGuid()));
    }

    [Fact]
    public void Layout_sections_are_validated_and_normalised()
    {
        IReadOnlyList<LayoutFieldOptionDto> catalog =
        [
            new("subject", "Subject", "Text", false, true, false, true), new("priority", "Priority", "Picklist", false, true, true, false),
            new("cf:root_cause", "Root cause", "Picklist", true, true, false, false)
        ];
        var errors = new Dictionary<string, string>();
        var json = FieldLayoutService.NormalizeSections(JsonSerializer.SerializeToElement(new object[]
        {
            new { title = " Basics ", columns = 9, fields = new object[] { new { key = "subject" }, "priority" } },
            new { title = "Analysis", fields = new object[] { new { key = "cf:root_cause", required = true } } }
        }), LayoutKind.AgentForm, catalog, errors);
        Assert.Empty(errors);
        var sections = JsonDocument.Parse(json).RootElement;
        Assert.Equal("Basics", sections[0].GetProperty("title").GetString());
        Assert.Equal(3, sections[0].GetProperty("columns").GetInt32());
        Assert.True(sections[0].GetProperty("fields")[0].GetProperty("required").GetBoolean()); // subject is always required
        Assert.True(sections[1].GetProperty("fields")[0].GetProperty("required").GetBoolean());

        // Portal forms only take portal fields; duplicates and unknown keys are rejected.
        errors.Clear();
        FieldLayoutService.NormalizeSections(JsonSerializer.SerializeToElement(new[] { new { title = "x", fields = new[] { "cf:root_cause" } } }), LayoutKind.PortalForm, catalog, errors);
        Assert.True(errors.ContainsKey("sections"));
        errors.Clear();
        FieldLayoutService.NormalizeSections(JsonSerializer.SerializeToElement(new[] { new { title = "x", fields = new[] { "priority", "priority" } } }), LayoutKind.AgentForm, catalog, errors);
        Assert.Contains("more than once", errors["sections"]);
        errors.Clear();
        FieldLayoutService.NormalizeSections(JsonSerializer.SerializeToElement(new[] { new { title = "x", fields = new[] { "cf:nope" } } }), LayoutKind.AgentForm, catalog, errors);
        Assert.Contains("Unknown field", errors["sections"]);
    }

    [Fact]
    public void Templates_encode_values_sanitise_markup_and_keep_generated_html()
    {
        var html = new HtmlSanitization();
        var body = EmailTemplateRenderer.RenderBody("<p>Hi {{contact.name}}</p><script>alert(1)</script><p><a href=\"{{link}}\">open</a></p>{{buttons}}",
            new Dictionary<string, string?> { ["contact.name"] = "<b>Eve</b>", ["link"] = "https://help.example.com/x?a=1&b=2", ["buttons"] = "<a style=\"display:inline-block\" href=\"#\">5 ★</a>" },
            new HashSet<string> { "buttons" }, html);
        Assert.Contains("Hi &lt;b&gt;Eve&lt;/b&gt;", body);
        Assert.DoesNotContain("<script", body);
        Assert.Contains("https://help.example.com/x?a=1&amp;b=2", body);
        Assert.Contains("<a style=\"display:inline-block\" href=\"#\">5 ★</a>", body); // raw placeholder inserted after sanitising
        Assert.Equal("Re: Printer <down>", EmailTemplateRenderer.RenderSubject("Re: {{subject}}\n", new Dictionary<string, string?> { ["subject"] = "Printer &lt;down&gt;" }));
        Assert.Equal(["nope"], EmailTemplateRenderer.Unknown("{{ ticket.number }} {{nope}}", ["ticket.number"]));
    }

    [Fact]
    public void Notification_policy_falls_back_to_built_in_defaults()
    {
        var policy = new NotificationPolicySettings
        {
            Events = new() { ["ticket.assigned"] = new NotificationChannelDefaults(false, false, true, true) }, DigestFrequency = "Hourly", QuietHoursStart = "22:00", QuietHoursEnd = "07:00"
        };
        Assert.Equal((false, false, true, false, true), policy.For("ticket.assigned"));
        Assert.True(policy.For("sla.breached").Email); // not configured → built-in default
        Assert.Equal(SupportDesk.Domain.Communication.DigestMode.Hourly, policy.DigestMode);
        var defaults = policy.AsUserDefaults(Guid.NewGuid());
        Assert.Equal(new TimeOnly(22, 0), defaults.QuietHoursStart);
        Assert.Equal(new TimeOnly(7, 0), defaults.QuietHoursEnd);
    }

    [Fact]
    public void Every_notification_type_has_a_label_for_the_delivery_defaults()
    {
        var missing = SupportDesk.Application.Notifications.NotificationTypes.All.Where(t => !NotificationPolicySettings.EventLabels.ContainsKey(t));
        Assert.Empty(missing);
    }
}
