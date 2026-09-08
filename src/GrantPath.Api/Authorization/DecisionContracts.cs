using System.Text.Json;
using GrantPath.Abac;

namespace GrantPath.Api.Authorization;

/// <summary>One rung of the containment chain, and whether the subject holds the relation there.</summary>
/// <param name="Resource">The resource at this level, as <c>folder:policies</c>.</param>
/// <param name="Relation">The relation asked about at this level.</param>
/// <param name="Grants">Whether the subject holds it here.</param>
public sealed record RelationshipPathStep(string Resource, string Relation, bool Grants);

/// <summary>A policy, as it appears in an explanation.</summary>
/// <param name="Id">Policy identifier.</param>
/// <param name="Name">Policy name.</param>
/// <param name="Effect">Deny or Allow.</param>
/// <param name="Priority">Its priority, so a reader can see why it outranked the others.</param>
public sealed record PolicyReference(Guid Id, string Name, string Effect, int Priority);

/// <summary>
/// Why a decision came out the way it did, recorded at the moment it was made.
/// </summary>
/// <remarks>
/// Written once and never recomputed. The endpoint that explains a decision reads this back verbatim,
/// because a reconstruction produced from today's tuples and today's policies answers a different question
/// from the one an auditor is asking.
/// </remarks>
/// <param name="RelationshipPath">The containment chain and where the grant came from, deepest first.</param>
/// <param name="GrantedAt">The level that actually produced the grant, when one did.</param>
/// <param name="MatchedPolicies">Attribute policies whose conditions matched, highest priority first.</param>
/// <param name="DecidingPolicy">The policy that settled the attribute verdict, when one did.</param>
/// <param name="ConsideredPolicyCount">How many policies were evaluated for this resource type.</param>
/// <param name="Summary">A sentence a person can read without decoding the rest.</param>
public sealed record DecisionReason(
    IReadOnlyList<RelationshipPathStep> RelationshipPath,
    string? GrantedAt,
    IReadOnlyList<PolicyReference> MatchedPolicies,
    PolicyReference? DecidingPolicy,
    int ConsideredPolicyCount,
    string Summary);

/// <summary>The answer to one authorization question.</summary>
/// <param name="RequestId">The handle to ask about this decision later.</param>
/// <param name="Allowed">The combined answer.</param>
/// <param name="RelationshipAllowed">What the relationship graph said on its own.</param>
/// <param name="AttributeVerdict">What the attribute layer said on its own.</param>
/// <param name="Reason">The recorded reasoning.</param>
/// <param name="LatencyMs">How long the decision took.</param>
/// <param name="ModelId">Which authorization model version produced it.</param>
public sealed record AuthorizationOutcome(
    Guid RequestId,
    bool Allowed,
    bool RelationshipAllowed,
    AbacVerdict AttributeVerdict,
    DecisionReason Reason,
    double LatencyMs,
    string ModelId);

/// <summary>The attributes accompanying a question.</summary>
/// <param name="Subject">Attributes of the caller.</param>
/// <param name="Resource">Attributes of the resource.</param>
/// <param name="Environment">Attributes of the moment.</param>
public sealed record DecisionContext(
    IReadOnlyDictionary<string, JsonElement>? Subject = null,
    IReadOnlyDictionary<string, JsonElement>? Resource = null,
    IReadOnlyDictionary<string, JsonElement>? Environment = null)
{
    /// <summary>Turns the request's attributes into the form the evaluator reads.</summary>
    public AttributeSet ToAttributeSet() => new(Subject, Resource, Environment);
}
