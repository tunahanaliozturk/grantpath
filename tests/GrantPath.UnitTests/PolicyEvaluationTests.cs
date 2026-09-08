using System.Text.Json;
using GrantPath.Abac;

namespace GrantPath.UnitTests;

/// <summary>
/// How a policy set resolves when several policies have something to say.
/// </summary>
/// <remarks>
/// The rules are: highest priority wins, a tie goes to deny, and nothing here ever grants anything. The
/// last one is enforced by the facade rather than by this class, but the vocabulary matters: an Allow
/// effect means this policy set raises no objection, not that access is permitted.
/// </remarks>
public sealed class PolicyEvaluationTests
{
    private const string MatchesAnything = """{ "attribute": "resource.classification", "operator": "exists" }""";

    private static readonly AttributeSet Confidential = new(
        resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["classification"] = Value("\"confidential\""),
        });

    [Fact]
    public void An_empty_policy_set_has_no_opinion()
    {
        AbacDecision decision = AbacEvaluator.Evaluate([], Confidential);

        decision.Verdict.ShouldBe(AbacVerdict.NoOpinion);
        decision.DecidingPolicyId.ShouldBeNull();
        decision.MatchedPolicyIds.ShouldBeEmpty();
    }

    [Fact]
    public void A_policy_whose_condition_does_not_match_has_no_opinion()
    {
        AbacPolicy policy = Policy(
            "irrelevant",
            PolicyEffect.Deny,
            priority: 10,
            """{ "attribute": "resource.classification", "operator": "equals", "value": "public" }""");

        AbacDecision decision = AbacEvaluator.Evaluate([policy], Confidential);

        decision.Verdict.ShouldBe(AbacVerdict.NoOpinion);
        decision.ConsideredPolicyCount.ShouldBe(1);
    }

    [Fact]
    public void The_highest_priority_matching_policy_decides()
    {
        AbacPolicy low = Policy("low deny", PolicyEffect.Deny, priority: 1, MatchesAnything);
        AbacPolicy high = Policy("high allow", PolicyEffect.Allow, priority: 50, MatchesAnything);

        AbacDecision decision = AbacEvaluator.Evaluate([low, high], Confidential);

        decision.Verdict.ShouldBe(AbacVerdict.Allow);
        decision.DecidingPolicyId.ShouldBe(high.Id);
        decision.DecidingPolicyName.ShouldBe("high allow");
    }

    [Fact]
    public void A_tie_goes_to_deny()
    {
        AbacPolicy allow = Policy("allow", PolicyEffect.Allow, priority: 10, MatchesAnything);
        AbacPolicy deny = Policy("deny", PolicyEffect.Deny, priority: 10, MatchesAnything);

        // Tie-breaking has to go somewhere, and the safe direction is the one where a mistake produces a
        // support ticket rather than a disclosure. Asserted in both orders so the answer does not depend
        // on which policy the database happened to return first.
        AbacEvaluator.Evaluate([allow, deny], Confidential).Verdict.ShouldBe(AbacVerdict.Deny);
        AbacEvaluator.Evaluate([deny, allow], Confidential).Verdict.ShouldBe(AbacVerdict.Deny);
    }

    [Fact]
    public void Every_matching_policy_is_reported_highest_priority_first()
    {
        AbacPolicy low = Policy("low", PolicyEffect.Deny, priority: 1, MatchesAnything);
        AbacPolicy middle = Policy("middle", PolicyEffect.Deny, priority: 5, MatchesAnything);
        AbacPolicy high = Policy("high", PolicyEffect.Allow, priority: 9, MatchesAnything);

        AbacDecision decision = AbacEvaluator.Evaluate([low, high, middle], Confidential);

        // The explanation has to say what outranked what, otherwise "policy X decided" is unfalsifiable.
        decision.MatchedPolicyIds.ShouldBe([high.Id, middle.Id, low.Id]);
    }

    private static AbacPolicy Policy(string name, PolicyEffect effect, int priority, string condition) =>
        AbacPolicy.Compile(Guid.CreateVersion7(), name, "document", effect, priority, condition);

    private static JsonElement Value(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }
}
