using System.Text.Json;
using SupportDesk.Application.Automation;
using SupportDesk.Domain.Automation;
using SupportDesk.Domain.Rules;

namespace SupportDesk.UnitTests.Automation;

public sealed class ConflictDetectorTests
{
    private static Condition C(string field, string op, object? value = null) =>
        new() { Field = field, Op = op, Value = value is null ? null : JsonSerializer.SerializeToElement(value) };

    private static RuleAction A(string type, object parameters) => new() { Type = type, Params = JsonSerializer.SerializeToElement(parameters) };

    private static RuleForAnalysis Rule(string name, int order, IEnumerable<Condition> conditions, IEnumerable<RuleAction> actions, bool stop = false,
        string trigger = RuleTriggers.TicketCreated, string logic = "and") =>
        new(Guid.NewGuid(), name, trigger, order, stop, new RuleDefinition
        {
            Conditions = new ConditionGroup { Logic = logic, Conditions = conditions.ToList() },
            Actions = actions.ToList()
        });

    [Fact]
    public void Rules_setting_the_same_field_to_different_values_conflict()
    {
        var a = Rule("High for VIP", 10, [C("contact.isVip", ConditionOps.Equals, true)], [A(ActionTypes.SetPriority, new { priority = "High" })]);
        var b = Rule("Low for email", 20, [C("channel", ConditionOps.Equals, "Email")], [A(ActionTypes.SetPriority, new { priority = "Low" })]);
        var conflicts = ConflictDetector.Detect([a, b]);
        var c = Assert.Single(conflicts);
        Assert.Equal(ConflictKinds.FieldConflict, c.Kind);
        Assert.Equal("priority", c.Field);
        Assert.Contains("'Low for email' runs later and wins", c.Explanation);
    }

    [Fact]
    public void Disjoint_conditions_do_not_conflict()
    {
        var a = Rule("Email high", 10, [C("channel", ConditionOps.Equals, "Email")], [A(ActionTypes.SetPriority, new { priority = "High" })]);
        var b = Rule("Phone low", 20, [C("channel", ConditionOps.In, new[] { "Phone", "Chat" })], [A(ActionTypes.SetPriority, new { priority = "Low" })]);
        Assert.Empty(ConflictDetector.Detect([a, b]));
    }

    [Fact]
    public void Equal_values_and_different_triggers_do_not_conflict()
    {
        var a = Rule("A", 10, [], [A(ActionTypes.SetPriority, new { priority = "High" })]);
        var b = Rule("B", 20, [], [A(ActionTypes.SetPriority, new { priority = "High" })]);
        var c = Rule("C", 30, [], [A(ActionTypes.SetPriority, new { priority = "Low" })], trigger: RuleTriggers.TicketUpdated);
        Assert.Empty(ConflictDetector.Detect([a, b, c]));
    }

    [Fact]
    public void Assigning_to_different_owners_conflicts()
    {
        var a = Rule("To Priya", 10, [C("priority", ConditionOps.GreaterOrEqual, "High")], [A(ActionTypes.Assign, new { userId = Guid.NewGuid() })]);
        var b = Rule("To Arjun", 20, [C("priority", ConditionOps.Equals, "Critical")], [A(ActionTypes.Assign, new { userId = Guid.NewGuid() })]);
        Assert.Contains(ConflictDetector.Detect([a, b]), c => c.Kind == ConflictKinds.AssignmentConflict);
    }

    [Fact]
    public void Priority_ranges_that_cannot_overlap_are_recognised()
    {
        var a = Rule("Urgent", 10, [C("priority", ConditionOps.GreaterOrEqual, "High")], [A(ActionTypes.Assign, new { userId = Guid.NewGuid() })]);
        var b = Rule("Routine", 20, [C("priority", ConditionOps.Equals, "Low")], [A(ActionTypes.Assign, new { userId = Guid.NewGuid() })]);
        Assert.Empty(ConflictDetector.Detect([a, b]));
    }

    [Fact]
    public void Stop_processing_hides_later_rules()
    {
        var a = Rule("Spam filter", 10, [C("subject", ConditionOps.Contains, "unsubscribe")], [A(ActionTypes.AddTags, new { tags = new[] { "spam" } })], stop: true);
        var b = Rule("Tag everything", 20, [], [A(ActionTypes.AddTags, new { tags = new[] { "new" } })]);
        var c = Assert.Single(ConflictDetector.Detect([a, b]));
        Assert.Equal(ConflictKinds.StopProcessing, c.Kind);
        Assert.Equal(a.Id, c.RuleId);
        Assert.Equal(b.Id, c.OtherRuleId);
    }

    [Fact]
    public void Adding_and_removing_the_same_tag_conflicts()
    {
        var a = Rule("Add urgent", 10, [], [A(ActionTypes.AddTags, new { tags = new[] { "urgent" } })]);
        var b = Rule("Remove urgent", 20, [], [A(ActionTypes.RemoveTags, new { tags = new[] { "URGENT" } })]);
        Assert.Contains(ConflictDetector.Detect([a, b]), c => c.Kind == ConflictKinds.TagConflict);
    }

    [Fact]
    public void Tag_presence_and_absence_are_disjoint_but_or_groups_are_treated_conservatively()
    {
        var a = Rule("Has vip", 10, [C("tags", ConditionOps.Contains, "vip")], [A(ActionTypes.SetPriority, new { priority = "High" })]);
        var b = Rule("No vip", 20, [C("tags", ConditionOps.NotContains, "vip")], [A(ActionTypes.SetPriority, new { priority = "Low" })]);
        Assert.Empty(ConflictDetector.Detect([a, b]));

        var orA = Rule("Email or phone", 10, [C("channel", ConditionOps.Equals, "Email"), C("channel", ConditionOps.Equals, "Phone")],
            [A(ActionTypes.SetPriority, new { priority = "High" })], logic: "or");
        var orB = Rule("Chat", 20, [C("channel", ConditionOps.Equals, "Chat")], [A(ActionTypes.SetPriority, new { priority = "Low" })]);
        Assert.Single(ConflictDetector.Detect([orA, orB])); // OR branches are not analysed → assumed to overlap
    }

    [Fact]
    public void Numeric_and_emptiness_constraints_are_compared()
    {
        var many = Rule("Many users", 10, [C("cf:affected_users", ConditionOps.GreaterThan, 100)], [A(ActionTypes.SetSeverity, new { severity = "Critical" })]);
        var few = Rule("Few users", 20, [C("cf:affected_users", ConditionOps.LessThan, 50)], [A(ActionTypes.SetSeverity, new { severity = "Minor" })]);
        Assert.Empty(ConflictDetector.Detect([many, few]));

        var unassigned = Rule("Unassigned", 10, [C("assigneeId", ConditionOps.IsEmpty)], [A(ActionTypes.SetPriority, new { priority = "High" })]);
        var assigned = Rule("Assigned", 20, [C("assigneeId", ConditionOps.IsNotEmpty)], [A(ActionTypes.SetPriority, new { priority = "Low" })]);
        Assert.Empty(ConflictDetector.Detect([unassigned, assigned]));
    }

    [Fact]
    public void Detect_for_a_single_rule_reports_only_its_conflicts()
    {
        var a = Rule("A", 10, [], [A(ActionTypes.SetPriority, new { priority = "High" })]);
        var b = Rule("B", 20, [], [A(ActionTypes.SetPriority, new { priority = "Low" })]);
        var c = Rule("C", 30, [], [A(ActionTypes.SetSeverity, new { severity = "Major" })]);
        var d = Rule("D", 40, [], [A(ActionTypes.SetSeverity, new { severity = "Minor" })]);
        var forC = ConflictDetector.DetectFor(c, [a, b, d]);
        var only = Assert.Single(forC);
        Assert.Equal("severity", only.Field);
    }
}
