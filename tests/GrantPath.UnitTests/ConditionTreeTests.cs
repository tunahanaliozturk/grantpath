using System.Text.Json;
using GrantPath.Abac;

namespace GrantPath.UnitTests;

/// <summary>
/// The condition evaluator, which is the only part of a decision this service computes itself.
/// </summary>
/// <remarks>
/// Every case here is a pure function of a policy document and a bag of attributes, which is why they
/// belong in a unit test: no container, no network, and a failure points at one comparison rather than at
/// a system.
/// </remarks>
public sealed class ConditionTreeTests
{
    private static readonly AttributeSet Alice = Attributes(
        subject: """{ "clearance": "secret", "department": "engineering", "years": 7, "tags": ["oncall"] }""",
        resource: """{ "classification": "confidential", "owner": "user:alice" }""",
        environment: """{ "hour": 14 }""");

    [Theory]
    [InlineData("""{ "attribute": "subject.clearance", "operator": "equals", "value": "secret" }""", true)]
    [InlineData("""{ "attribute": "subject.clearance", "operator": "equals", "value": "public" }""", false)]
    [InlineData("""{ "attribute": "subject.clearance", "operator": "notEquals", "value": "public" }""", true)]
    [InlineData("""{ "attribute": "resource.classification", "operator": "in", "value": ["confidential", "secret"] }""", true)]
    [InlineData("""{ "attribute": "resource.classification", "operator": "notIn", "value": ["public"] }""", true)]
    [InlineData("""{ "attribute": "subject.years", "operator": "greaterThan", "value": 5 }""", true)]
    [InlineData("""{ "attribute": "subject.years", "operator": "greaterThanOrEqual", "value": 7 }""", true)]
    [InlineData("""{ "attribute": "subject.years", "operator": "lessThan", "value": 7 }""", false)]
    [InlineData("""{ "attribute": "environment.hour", "operator": "lessThanOrEqual", "value": 14 }""", true)]
    [InlineData("""{ "attribute": "subject.tags", "operator": "contains", "value": "oncall" }""", true)]
    [InlineData("""{ "attribute": "subject.tags", "operator": "contains", "value": "manager" }""", false)]
    [InlineData("""{ "attribute": "subject.department", "operator": "exists" }""", true)]
    public void Comparisons_evaluate_as_written(string condition, bool expected) =>
        Parse(condition).Matches(Alice).ShouldBe(expected);

    [Theory]
    [InlineData("""{ "attribute": "subject.missing", "operator": "equals", "value": "anything" }""")]
    [InlineData("""{ "attribute": "subject.missing", "operator": "notEquals", "value": "anything" }""")]
    [InlineData("""{ "attribute": "subject.missing", "operator": "exists" }""")]
    [InlineData("""{ "attribute": "subject.missing", "operator": "greaterThan", "value": 1 }""")]
    public void An_absent_attribute_satisfies_nothing_in_either_direction(string condition)
    {
        // Including notEquals, which is the surprising one. A deny policy reading "clearance is not
        // secret" would otherwise fire for every subject whose clearance attribute was simply misspelled
        // by whoever wrote the policy, and it would look like the policy working.
        Parse(condition).Matches(Alice).ShouldBeFalse();
    }

    [Fact]
    public void Comparing_across_types_does_not_match()
    {
        // A string seven and a numeric seven are different things, and quietly treating them as equal is
        // how a policy written against one system starts silently passing against another.
        Parse("""{ "attribute": "subject.years", "operator": "equals", "value": "7" }""")
            .Matches(Alice)
            .ShouldBeFalse();
    }

    [Fact]
    public void AllOf_requires_every_child()
    {
        Parse("""
            {
              "allOf": [
                { "attribute": "subject.clearance", "operator": "equals", "value": "secret" },
                { "attribute": "subject.department", "operator": "equals", "value": "engineering" }
              ]
            }
            """).Matches(Alice).ShouldBeTrue();

        Parse("""
            {
              "allOf": [
                { "attribute": "subject.clearance", "operator": "equals", "value": "secret" },
                { "attribute": "subject.department", "operator": "equals", "value": "finance" }
              ]
            }
            """).Matches(Alice).ShouldBeFalse();
    }

    [Fact]
    public void AnyOf_requires_one_child_and_an_empty_list_matches_nothing()
    {
        Parse("""
            {
              "anyOf": [
                { "attribute": "subject.clearance", "operator": "equals", "value": "public" },
                { "attribute": "subject.department", "operator": "equals", "value": "engineering" }
              ]
            }
            """).Matches(Alice).ShouldBeTrue();

        Parse("""{ "anyOf": [] }""").Matches(Alice).ShouldBeFalse();
    }

    [Fact]
    public void Not_inverts_its_child()
    {
        Parse("""
            { "not": { "attribute": "subject.clearance", "operator": "equals", "value": "public" } }
            """).Matches(Alice).ShouldBeTrue();
    }

    [Fact]
    public void Nested_combinators_evaluate_to_the_bottom()
    {
        Parse("""
            {
              "allOf": [
                { "attribute": "resource.classification", "operator": "equals", "value": "confidential" },
                {
                  "not": {
                    "anyOf": [
                      { "attribute": "subject.clearance", "operator": "equals", "value": "secret" },
                      { "attribute": "subject.clearance", "operator": "equals", "value": "top-secret" }
                    ]
                  }
                }
              ]
            }
            """).Matches(Alice).ShouldBeFalse();
    }

    [Theory]
    [InlineData("""{ "operator": "equals", "value": "x" }""")]
    [InlineData("""{ "attribute": "clearance", "operator": "equals", "value": "x" }""")]
    [InlineData("""{ "attribute": "nowhere.clearance", "operator": "equals", "value": "x" }""")]
    [InlineData("""{ "attribute": "subject.clearance", "operator": "sortOf", "value": "x" }""")]
    [InlineData("""{ "attribute": "subject.clearance" }""")]
    [InlineData("""{ "attribute": "subject.clearance", "operator": "equals" }""")]
    [InlineData("""{ "allOf": { "attribute": "subject.clearance", "operator": "exists" } }""")]
    public void A_malformed_condition_is_refused_at_parse_time(string condition)
    {
        // Refused when the policy is written rather than when a decision needs it. A policy that fails to
        // compile at request time takes the whole policy set for that resource type down with it.
        Should.Throw<FormatException>(() => Parse(condition));
    }

    private static ConditionNode Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        return ConditionNode.Parse(document.RootElement);
    }

    private static AttributeSet Attributes(string subject, string resource, string environment) =>
        new(Read(subject), Read(resource), Read(environment));

    private static Dictionary<string, JsonElement> Read(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        return document.RootElement
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
    }
}
