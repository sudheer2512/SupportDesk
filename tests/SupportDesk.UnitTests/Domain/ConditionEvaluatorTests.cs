using System.Text.Json;
using SupportDesk.Domain.Rules;

namespace SupportDesk.UnitTests.Domain;

public sealed class ConditionEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc);

    private static DictionaryConditionContext Ctx(IDictionary<string, object?>? previous = null) => new(new Dictionary<string, object?>
    {
        ["priority"] = "High",
        ["channel"] = "Email",
        ["subject"] = "GST total mismatch on invoice",
        ["tags"] = new List<string> { "gst", "invoice" },
        ["customer.tier"] = "Platinum",
        ["contact.isVip"] = true,
        ["reopenCount"] = 2,
        ["createdAt"] = Now.AddHours(-5),
        ["assigneeId"] = null,
        ["cf:affected_users"] = 40m
    }, Now, previous);

    private static ConditionGroup Parse(string json) => ConditionGroup.Parse(json);

    [Fact]
    public void Empty_group_matches_everything() => Assert.True(ConditionEvaluator.Evaluate(new ConditionGroup(), Ctx()));

    [Fact]
    public void And_or_nesting()
    {
        var g = Parse("""
            {"logic":"and","conditions":[{"field":"priority","op":"in","value":["High","Critical"]}],
             "groups":[{"logic":"or","conditions":[{"field":"customer.tier","op":"eq","value":"Gold"},{"field":"contact.isVip","op":"eq","value":true}]}]}
            """);
        Assert.True(ConditionEvaluator.Evaluate(g, Ctx()));
        g.Groups[0].Conditions[1].Value = JsonSerializer.SerializeToElement(false);
        Assert.False(ConditionEvaluator.Evaluate(g, Ctx()));
    }

    [Theory]
    [InlineData("subject", "contains", "\"invoice\"", true)]
    [InlineData("subject", "not_contains", "\"refund\"", true)]
    [InlineData("subject", "starts_with", "\"gst\"", true)]
    [InlineData("subject", "contains_any", "[\"refund\",\"GST\"]", true)]
    [InlineData("tags", "contains", "\"gst\"", true)]
    [InlineData("tags", "not_in", "[\"urgent\"]", true)]
    [InlineData("reopenCount", "gte", "2", true)]
    [InlineData("reopenCount", "gt", "2", false)]
    [InlineData("cf:affected_users", "between", "[10, 50]", true)]
    [InlineData("priority", "gte", "\"Medium\"", true)]
    [InlineData("priority", "gt", "\"High\"", false)]
    [InlineData("assigneeId", "is_empty", "null", true)]
    [InlineData("createdAt", "older_than_minutes", "240", true)]
    [InlineData("createdAt", "within_last_minutes", "60", false)]
    [InlineData("subject", "matches", "\"^GST\\\\s+total\"", true)]
    public void Operators(string field, string op, string valueJson, bool expected)
    {
        var c = new Condition { Field = field, Op = op, Value = JsonDocument.Parse(valueJson).RootElement.Clone() };
        Assert.Equal(expected, ConditionEvaluator.EvaluateOne(c, Ctx()));
    }

    [Fact]
    public void Change_operators_use_previous_values()
    {
        var ctx = Ctx(new Dictionary<string, object?> { ["priority"] = "Low" });
        Assert.True(ConditionEvaluator.EvaluateOne(new Condition { Field = "priority", Op = "changed" }, ctx));
        Assert.True(ConditionEvaluator.EvaluateOne(new Condition { Field = "priority", Op = "changed_to", Value = JsonSerializer.SerializeToElement("High") }, ctx));
        Assert.True(ConditionEvaluator.EvaluateOne(new Condition { Field = "priority", Op = "changed_from", Value = JsonSerializer.SerializeToElement("Low") }, ctx));
        Assert.False(ConditionEvaluator.EvaluateOne(new Condition { Field = "channel", Op = "changed" }, ctx));
    }

    [Fact]
    public void Unknown_operator_or_bad_input_evaluates_false_instead_of_throwing()
    {
        Assert.False(ConditionEvaluator.EvaluateOne(new Condition { Field = "priority", Op = "explode" }, Ctx()));
        Assert.False(ConditionEvaluator.EvaluateOne(new Condition { Field = "reopenCount", Op = "gt", Value = JsonSerializer.SerializeToElement("abc") }, Ctx()));
    }

    [Fact]
    public void Catastrophic_regex_is_bounded_by_timeout()
    {
        var ctx = new DictionaryConditionContext(new Dictionary<string, object?> { ["subject"] = new string('a', 5000) + "!" }, Now);
        var c = new Condition { Field = "subject", Op = "matches", Value = JsonSerializer.SerializeToElement("^(a+)+$") };
        Assert.False(ConditionEvaluator.EvaluateOne(c, ctx));
    }

    [Fact]
    public void Simulation_trace_records_every_condition()
    {
        var g = Parse("""{"logic":"or","conditions":[{"field":"priority","op":"eq","value":"High"},{"field":"channel","op":"eq","value":"Chat"}]}""");
        var trace = new List<ConditionTrace>();
        Assert.True(ConditionEvaluator.Evaluate(g, Ctx(), trace));
        Assert.Equal(2, trace.Count);
        Assert.False(trace[1].Result);
    }
}
