namespace GrantPath.Abac;

/// <summary>What the attribute layer concluded.</summary>
public enum AbacVerdict
{
    /// <summary>No policy matched. The attribute layer has nothing to say about this request.</summary>
    NoOpinion = 0,

    /// <summary>A policy matched and raised no objection.</summary>
    Allow = 1,

    /// <summary>A policy matched and forbids this, whatever the relationship graph said.</summary>
    Deny = 2,
}

/// <summary>
/// The attribute layer's answer, with enough detail to explain itself afterwards.
/// </summary>
/// <param name="Verdict">The conclusion.</param>
/// <param name="DecidingPolicyId">The policy that produced it, when one did.</param>
/// <param name="DecidingPolicyName">Its name, so an explanation reads like a sentence.</param>
/// <param name="MatchedPolicyIds">Every policy whose condition matched, highest priority first.</param>
/// <param name="ConsideredPolicyCount">How many policies were evaluated for this resource type.</param>
public sealed record AbacDecision(
    AbacVerdict Verdict,
    Guid? DecidingPolicyId,
    string? DecidingPolicyName,
    IReadOnlyList<Guid> MatchedPolicyIds,
    int ConsideredPolicyCount)
{
    /// <summary>The answer when no policy applies.</summary>
    public static AbacDecision NoOpinion { get; } = new(AbacVerdict.NoOpinion, null, null, [], 0);
}

/// <summary>
/// Evaluates attribute policies against a request.
/// </summary>
/// <remarks>
/// <para>
/// This layer narrows and never widens. A policy with <see cref="PolicyEffect.Allow"/> is not a grant; it
/// is a statement that this policy set raises no objection, which matters only because it can outrank a
/// lower-priority deny. Access itself comes from the relationship graph, and the facade combines the two
/// with a fixed rule that cannot turn a relationship-level deny into an allow. That constraint is the
/// entire reason the two engines are separate: an attribute policy that could grant access would be a
/// second, quieter permission system, and nobody would remember to audit it.
/// </para>
/// <para>
/// When several policies match, the highest priority wins and a tie goes to deny. Tie-breaking has to go
/// somewhere, and the safe direction is the one where a mistake produces a support ticket rather than a
/// disclosure.
/// </para>
/// </remarks>
public static class AbacEvaluator
{
    /// <summary>Evaluates a policy set.</summary>
    /// <param name="policies">Policies for the resource type in question.</param>
    /// <param name="attributes">The request's attributes.</param>
    public static AbacDecision Evaluate(IReadOnlyList<AbacPolicy> policies, AttributeSet attributes)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(attributes);

        if (policies.Count is 0)
        {
            return AbacDecision.NoOpinion;
        }

        AbacPolicy? deciding = null;
        List<(int Priority, Guid Id)>? matched = null;

        for (int index = 0; index < policies.Count; index++)
        {
            AbacPolicy policy = policies[index];

            if (!policy.Condition.Matches(attributes))
            {
                continue;
            }

            // The priority travels with the identifier. Looking it back up during the sort turns an
            // ordering job into a quadratic scan, which a benchmark over sixty-four matching policies
            // showed costing more than the condition evaluation it was ordering.
            (matched ??= []).Add((policy.Priority, policy.Id));

            if (deciding is null || Outranks(policy, deciding))
            {
                deciding = policy;
            }
        }

        if (deciding is null)
        {
            return new AbacDecision(AbacVerdict.NoOpinion, null, null, [], policies.Count);
        }

        // Reported highest priority first, because that is the order somebody reading an explanation
        // wants: the policy that decided, then the ones it outranked.
        matched!.Sort(static (left, right) => right.Priority.CompareTo(left.Priority));

        Guid[] ordered = new Guid[matched.Count];

        for (int index = 0; index < matched.Count; index++)
        {
            ordered[index] = matched[index].Id;
        }

        return new AbacDecision(
            deciding.Effect is PolicyEffect.Deny ? AbacVerdict.Deny : AbacVerdict.Allow,
            deciding.Id,
            deciding.Name,
            ordered,
            policies.Count);
    }

    private static bool Outranks(AbacPolicy candidate, AbacPolicy incumbent) =>
        candidate.Priority > incumbent.Priority
        || (candidate.Priority == incumbent.Priority
            && candidate.Effect is PolicyEffect.Deny
            && incumbent.Effect is PolicyEffect.Allow);
}
