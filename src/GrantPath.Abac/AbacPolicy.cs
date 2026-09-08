using System.Text.Json;

namespace GrantPath.Abac;

/// <summary>What a matching policy says.</summary>
public enum PolicyEffect
{
    /// <summary>Forbid, whatever the relationship graph said.</summary>
    Deny = 0,

    /// <summary>Raise no objection. This is not a grant. See <see cref="AbacEvaluator"/>.</summary>
    Allow = 1,
}

/// <summary>
/// One attribute policy, parsed and ready to evaluate.
/// </summary>
/// <remarks>
/// A policy is scoped to a resource type, carries a priority, and holds a condition tree. It is compiled
/// once when it is loaded and reused for every decision after that.
/// </remarks>
/// <param name="Id">Identifier, recorded in the audit trail when the policy decides an outcome.</param>
/// <param name="Name">A name a human can recognise in a decision explanation.</param>
/// <param name="ResourceType">Which resource type this applies to, matching the relationship model.</param>
/// <param name="Effect">What it says when it matches.</param>
/// <param name="Priority">Higher wins. Ties go to deny.</param>
/// <param name="Condition">When it applies.</param>
public sealed record AbacPolicy(
    Guid Id,
    string Name,
    string ResourceType,
    PolicyEffect Effect,
    int Priority,
    ConditionNode Condition)
{
    /// <summary>Compiles a policy from its stored form.</summary>
    /// <param name="id">Identifier.</param>
    /// <param name="name">Display name.</param>
    /// <param name="resourceType">Resource type.</param>
    /// <param name="effect">Effect.</param>
    /// <param name="priority">Priority.</param>
    /// <param name="conditionJson">The condition, as stored.</param>
    /// <exception cref="FormatException">The condition is not valid.</exception>
    public static AbacPolicy Compile(
        Guid id,
        string name,
        string resourceType,
        PolicyEffect effect,
        int priority,
        string conditionJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conditionJson);

        using JsonDocument document = JsonDocument.Parse(conditionJson);

        return new AbacPolicy(
            id,
            name,
            resourceType,
            effect,
            priority,
            ConditionNode.Parse(document.RootElement));
    }
}
