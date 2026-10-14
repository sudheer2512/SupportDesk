using System.Text.Json;
using SupportDesk.Application.Automation;
using SupportDesk.Domain.Automation;
using SupportDesk.Domain.Rules;

namespace SupportDesk.UnitTests.Automation;

public sealed class ActionValidationTests
{
    private static RuleAction Action(string type, object? parameters = null) =>
        new() { Type = type, Params = parameters is null ? null : JsonSerializer.SerializeToElement(parameters, AutomationJson.Options) };

    private static List<ValidationIssue> Errors(RuleAction action, AutomationReferenceData? refs = null) =>
        AutomationValidator.ValidateAction(action, "actions[0]", refs).Where(i => !i.IsWarning).ToList();

    [Fact]
    public void Unknown_action_type_is_rejected()
    {
        var errors = Errors(Action("delete_ticket"));
        Assert.Contains(errors, e => e.Path == "actions[0].type");
    }

    [Theory]
    [InlineData("Critical", true)]
    [InlineData("high", true)]
    [InlineData("Urgent", false)]
    [InlineData("", false)]
    public void Set_priority_requires_a_known_priority(string priority, bool valid)
    {
        var errors = Errors(Action(ActionTypes.SetPriority, new { priority }));
        Assert.Equal(valid, errors.Count == 0);
    }

    [Fact]
    public void Assign_needs_an_agent_a_team_or_routing()
    {
        Assert.NotEmpty(Errors(Action(ActionTypes.Assign, new { })));
        Assert.Empty(Errors(Action(ActionTypes.Assign, new { route = true })));
        Assert.Contains(Errors(Action(ActionTypes.Assign, new { userId = "not-a-guid" })), e => e.Path.EndsWith("userId"));
        Assert.Empty(Errors(Action(ActionTypes.Assign, new { userId = Guid.NewGuid() })));
    }

    [Theory]
    [InlineData("https://erp.example.com/hooks/{{ticket.number}}", "POST", true)]
    [InlineData("http://localhost:5830/api", "PUT", true)]
    [InlineData("ftp://files.example.com/x", "POST", false)]
    [InlineData("https://user:pass@example.com/x", "POST", false)]
    [InlineData("/relative/path", "POST", false)]
    [InlineData("https://example.com/x", "TRACE", false)]
    public void Invoke_webhook_validates_url_and_method(string url, string method, bool valid)
    {
        var errors = Errors(Action(ActionTypes.InvokeWebhook, new { method, url }));
        Assert.Equal(valid, errors.Count == 0);
    }

    [Fact]
    public void Invoke_webhook_rejects_forbidden_headers_short_secrets_and_long_timeouts()
    {
        var errors = Errors(Action(ActionTypes.InvokeWebhook, new
        {
            method = "POST", url = "https://example.com/hook", headers = new Dictionary<string, string> { ["Host"] = "evil" }, secret = "short", timeoutSeconds = 120
        }));
        Assert.Contains(errors, e => e.Path.EndsWith("headers"));
        Assert.Contains(errors, e => e.Path.EndsWith("secret"));
        Assert.Contains(errors, e => e.Path.EndsWith("timeoutSeconds"));
    }

    [Fact]
    public void Send_message_by_email_needs_a_subject_and_a_known_channel()
    {
        Assert.Contains(Errors(Action(ActionTypes.SendMessage, new { channel = "Email", to = "contact", bodyHtml = "<p>Hi</p>" })), e => e.Path.EndsWith("subject"));
        Assert.Contains(Errors(Action(ActionTypes.SendMessage, new { channel = "Fax", to = "contact", bodyHtml = "x" })), e => e.Path.EndsWith("channel"));
        Assert.Empty(Errors(Action(ActionTypes.SendMessage, new { channel = "WhatsApp", to = "contact", bodyHtml = "Hello {{contact.name}}" })));
        Assert.Contains(Errors(Action(ActionTypes.SendMessage, new { channel = "Sms", to = "user", bodyHtml = "x" })), e => e.Path.EndsWith("userId"));
    }

    [Fact]
    public void Tags_actions_need_at_least_one_short_tag()
    {
        Assert.NotEmpty(Errors(Action(ActionTypes.AddTags, new { tags = Array.Empty<string>() })));
        Assert.NotEmpty(Errors(Action(ActionTypes.RemoveTags, new { tags = new[] { new string('x', 60) } })));
        Assert.Empty(Errors(Action(ActionTypes.AddTags, new { tags = new[] { "gst", "vip" } })));
    }

    [Fact]
    public void References_are_checked_against_tenant_configuration()
    {
        var status = Guid.NewGuid();
        var refs = new AutomationReferenceData { Statuses = new Dictionary<Guid, IReadOnlyList<string>> { [status] = ["Information requested"] } };
        Assert.Empty(Errors(Action(ActionTypes.SetStatus, new { statusId = status, substatus = "Information requested" }), refs));
        Assert.Contains(Errors(Action(ActionTypes.SetStatus, new { statusId = status, substatus = "Lunch break" }), refs), e => e.Path.EndsWith("substatus"));
        Assert.Contains(Errors(Action(ActionTypes.SetStatus, new { statusId = Guid.NewGuid() }), refs), e => e.Path.EndsWith("statusId"));
        Assert.Contains(Errors(Action(ActionTypes.SetCustomField, new { key = "unknown_field", value = "x" }), refs), e => e.Path.EndsWith("key"));
    }

    [Fact]
    public void Parameters_must_be_an_object()
    {
        var action = new RuleAction { Type = ActionTypes.AddNote, Params = JsonSerializer.SerializeToElement(new[] { 1, 2 }) };
        Assert.Contains(Errors(action), e => e.Path.EndsWith("params"));
    }

    [Fact]
    public void Rule_validation_rejects_change_operators_on_scheduled_rules_and_webhook_fields_on_event_rules()
    {
        var scheduled = new RuleDefinition
        {
            Conditions = new ConditionGroup { Conditions = [new Condition { Field = "priority", Op = ConditionOps.ChangedTo, Value = JsonSerializer.SerializeToElement("High") }] },
            Actions = [Action(ActionTypes.AddNote, new { bodyHtml = "x" })]
        };
        Assert.Contains(AutomationValidator.ValidateRule(RuleTriggers.Scheduled, scheduled, null), i => !i.IsWarning && i.Path.EndsWith(".op"));

        var webhookField = new RuleDefinition
        {
            Conditions = new ConditionGroup { Conditions = [new Condition { Field = "webhook.event", Op = ConditionOps.Equals, Value = JsonSerializer.SerializeToElement("paid") }] },
            Actions = [Action(ActionTypes.AddNote, new { bodyHtml = "x" })]
        };
        Assert.Contains(AutomationValidator.ValidateRule(RuleTriggers.TicketCreated, webhookField, null), i => !i.IsWarning);
        Assert.DoesNotContain(AutomationValidator.ValidateRule(RuleTriggers.Webhook, webhookField, null), i => !i.IsWarning);
    }

    [Fact]
    public void Rule_without_actions_or_with_unknown_fields_is_invalid()
    {
        var def = new RuleDefinition
        {
            Conditions = new ConditionGroup { Conditions = [new Condition { Field = "colour", Op = ConditionOps.Equals, Value = JsonSerializer.SerializeToElement("red") }] }
        };
        var issues = AutomationValidator.ValidateRule(RuleTriggers.TicketCreated, def, null);
        Assert.Contains(issues, i => i.Path == "actions");
        Assert.Contains(issues, i => i.Path.EndsWith(".field"));
    }

    [Fact]
    public void Blueprint_validation_requires_one_initial_state_reachability_and_no_dead_ends()
    {
        var def = BlueprintDefinition.Parse("""
            {"states":[{"key":"a","name":"A","isInitial":true},{"key":"b","name":"B"},{"key":"c","name":"C","isFinal":true},{"key":"orphan","name":"Orphan","isFinal":true}],
             "transitions":[{"key":"go","name":"Go","from":"a","to":"b"},{"key":"bad","name":"Bad","from":"c","to":"a"}]}
            """);
        var errors = AutomationValidator.ValidateBlueprint(def, null).Where(i => !i.IsWarning).Select(i => i.Message).ToList();
        Assert.Contains(errors, m => m.Contains("Orphan") && m.Contains("cannot be reached"));
        Assert.Contains(errors, m => m.Contains("'B'") && m.Contains("no outgoing transition"));
        Assert.Contains(errors, m => m.Contains("final state"));

        var twoInitial = BlueprintDefinition.Parse("""{"states":[{"key":"a","name":"A","isInitial":true},{"key":"b","name":"B","isInitial":true,"isFinal":true}],"transitions":[{"key":"t","name":"T","from":"a","to":"b"}]}""");
        Assert.Contains(AutomationValidator.ValidateBlueprint(twoInitial, null), i => i.Message.Contains("initial"));

        var good = BlueprintDefinition.Parse("""{"states":[{"key":"a","name":"A","isInitial":true},{"key":"b","name":"B","isFinal":true}],"transitions":[{"key":"t","name":"T","from":["a"],"to":"b","requiredFields":["severity","cf:x"]}]}""");
        Assert.DoesNotContain(AutomationValidator.ValidateBlueprint(good, null), i => !i.IsWarning);
        var refs = new AutomationReferenceData();
        Assert.Contains(AutomationValidator.ValidateBlueprint(good, refs), i => i.Message.Contains("Unknown custom field"));
    }
}
