using System.Diagnostics;
using System.Text.Json;
using GrantPath.Abac;
using GrantPath.Api.Auditing;
using GrantPath.Api.Observability;
using GrantPath.Data;
using GrantPath.Rebac;
using Microsoft.Extensions.Options;

namespace GrantPath.Api.Authorization;

/// <summary>
/// The one entry point application code calls.
/// </summary>
/// <remarks>
/// <para>
/// Two engines answer independently and a fixed rule combines them: allow if and only if the relationship
/// graph allows and the attribute layer does not object. The rule is fixed because the alternative, a
/// combination that can be configured, is a second permission system nobody remembers to audit. An
/// attribute policy cannot manufacture access that no relationship justifies, and that is a property of
/// this method rather than a convention policy authors are trusted to follow.
/// </para>
/// <para>
/// Deny-by-default falls out of the same rule. A resource with no tuples produces a relationship deny, and
/// nothing downstream can turn that into an allow, so absence of data is never read as permission.
/// </para>
/// </remarks>
/// <param name="engine">The relationship store.</param>
/// <param name="policies">Compiled attribute policies.</param>
/// <param name="containment">Resolves the hierarchy for explanations.</param>
/// <param name="audit">Where decisions are recorded.</param>
/// <param name="metrics">Instruments.</param>
/// <param name="options">Decision behaviour.</param>
/// <param name="timeProvider">Clock.</param>
public sealed class AuthorizationFacade(
    IRelationshipEngine engine,
    PolicyCache policies,
    ContainmentResolver containment,
    DecisionAuditWriter audit,
    GrantPathMetrics metrics,
    IOptions<DecisionOptions> options,
    TimeProvider timeProvider)
{
    private readonly DecisionOptions _options = options.Value;

    /// <summary>Answers one authorization question and records the answer.</summary>
    /// <param name="subject">The already-authenticated subject, as <c>user:alice</c>.</param>
    /// <param name="relation">The relation being asked about.</param>
    /// <param name="resource">The resource, as <c>document:handbook</c>.</param>
    /// <param name="context">Attributes accompanying the question.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<AuthorizationOutcome> CheckAsync(
        string subject,
        string relation,
        string resource,
        DecisionContext? context,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(relation);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);

        DateTimeOffset now = timeProvider.GetUtcNow();
        Guid requestId = Guid.CreateVersion7(now);
        long started = Stopwatch.GetTimestamp();

        using Activity? activity = GrantPathMetrics.ActivitySource.StartActivity(
            "authz.check",
            ActivityKind.Server);

        activity?.SetTag("authz.subject", subject);
        activity?.SetTag("authz.relation", relation);
        activity?.SetTag("authz.resource", resource);

        string resourceType = TypeOf(resource);

        // The two engines are independent, so the relationship round trip runs while the attribute
        // policies are being evaluated in memory rather than after it.
        Task<bool> relationshipTask = engine.CheckAsync(
            new RelationshipTuple(subject, relation, resource),
            cancellationToken);

        IReadOnlyList<AbacPolicy> applicable = await policies.GetAsync(resourceType, cancellationToken);
        AbacDecision attributes = AbacEvaluator.Evaluate(
            applicable,
            (context ?? new DecisionContext()).ToAttributeSet());

        bool relationshipAllowed = await relationshipTask;
        bool allowed = relationshipAllowed && attributes.Verdict is not AbacVerdict.Deny;

        DecisionReason reason = await BuildReasonAsync(
            subject,
            relation,
            resource,
            relationshipAllowed,
            attributes,
            applicable,
            cancellationToken);

        double latencyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        activity?.SetTag("authz.decision", allowed ? "allow" : "deny");
        activity?.SetTag("authz.latency_ms", latencyMs);

        metrics.RecordDecision(relation, resourceType, allowed, latencyMs);

        bool synchronous = await audit.RecordAsync(
            new DecisionRecord
            {
                RequestId = requestId,
                Subject = subject,
                Resource = resource,
                ResourceType = resourceType,
                Relation = relation,
                Decision = allowed ? AuthorizationDecision.Allow : AuthorizationDecision.Deny,
                RelationshipAllowed = relationshipAllowed,
                AttributeVerdict = attributes.Verdict.ToString(),
                ReasonJson = JsonSerializer.Serialize(reason, DecisionJson.Options),
                ModelId = engine.ModelId,
                LatencyMs = latencyMs,
                DecidedAtUtc = now,
            },
            cancellationToken);

        if (synchronous)
        {
            metrics.RecordSynchronousAuditWrite();
        }

        return new AuthorizationOutcome(
            requestId,
            allowed,
            relationshipAllowed,
            attributes.Verdict,
            reason,
            latencyMs,
            engine.ModelId);
    }

    /// <summary>Extracts the type half of a resource identifier.</summary>
    /// <param name="resource">The resource, as <c>document:handbook</c>.</param>
    public static string TypeOf(string resource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);

        int separator = resource.IndexOf(':', StringComparison.Ordinal);

        return separator > 0 ? resource[..separator] : resource;
    }

    /// <summary>
    /// Works out which level of the hierarchy a relation is asked about.
    /// </summary>
    /// <remarks>
    /// The same permission is spelled differently at different levels: someone who may edit a document is
    /// an admin of the org that contains it, not an editor of it. Without this mapping an explanation
    /// would ask an org whether somebody is an editor, get a deny, and report that the grant came from
    /// nowhere.
    /// </remarks>
    /// <param name="resourceType">The level being asked about.</param>
    /// <param name="requestedRelation">The relation asked about on the leaf resource.</param>
    public static string RelationAtLevel(string resourceType, string requestedRelation) =>
        resourceType switch
        {
            AuthorizationModel.Types.Org or AuthorizationModel.Types.Workspace =>
                requestedRelation is AuthorizationModel.Relations.Viewer
                    ? AuthorizationModel.Relations.Viewer
                    : AuthorizationModel.Relations.Admin,

            _ => requestedRelation,
        };

    private async Task<DecisionReason> BuildReasonAsync(
        string subject,
        string relation,
        string resource,
        bool relationshipAllowed,
        AbacDecision attributes,
        IReadOnlyList<AbacPolicy> applicable,
        CancellationToken cancellationToken)
    {
        PolicyReference? deciding = attributes.DecidingPolicyId is { } decidingId
            ? Describe(applicable, decidingId)
            : null;

        List<PolicyReference> matched = [];

        foreach (Guid id in attributes.MatchedPolicyIds)
        {
            if (Describe(applicable, id) is { } reference)
            {
                matched.Add(reference);
            }
        }

        if (!_options.ResolveRelationshipPath)
        {
            return new DecisionReason(
                [],
                null,
                matched,
                deciding,
                attributes.ConsideredPolicyCount,
                Summarise(relationshipAllowed, null, resource, attributes, deciding, resolved: false));
        }

        IReadOnlyList<string> chain = await containment.GetChainAsync(resource, cancellationToken);

        List<RelationshipQuery> queries = [];

        for (int index = 0; index < chain.Count; index++)
        {
            string level = chain[index];

            queries.Add(new RelationshipQuery(
                new RelationshipTuple(subject, RelationAtLevel(TypeOf(level), relation), level),
                index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        // One round trip for the whole chain. Asking level by level would turn an explanation into four
        // sequential calls and put the cost of being auditable straight onto the latency budget.
        IReadOnlyList<RelationshipAnswer> answers = await engine.BatchCheckAsync(queries, cancellationToken);

        var granted = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (RelationshipAnswer answer in answers)
        {
            granted[answer.CorrelationId] = answer.Allowed;
        }

        List<RelationshipPathStep> steps = [];
        string? grantedAt = null;

        for (int index = 0; index < chain.Count; index++)
        {
            string key = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            bool grants = granted.TryGetValue(key, out bool value) && value;

            steps.Add(new RelationshipPathStep(chain[index], RelationAtLevel(TypeOf(chain[index]), relation), grants));

            // The outermost level that grants is where the permission originates; everything below it
            // inherits. Reporting the leaf instead would describe the symptom rather than the cause.
            if (grants)
            {
                grantedAt = chain[index];
            }
        }

        // Reported only when the relationship check actually allowed. An ancestor can grant while the
        // leaf refuses, which is exactly what an explicit block looks like, and naming that ancestor as
        // the origin of a permission the subject does not have would be worse than saying nothing.
        return new DecisionReason(
            steps,
            relationshipAllowed ? grantedAt : null,
            matched,
            deciding,
            attributes.ConsideredPolicyCount,
            Summarise(relationshipAllowed, grantedAt, resource, attributes, deciding, resolved: true));
    }

    private static PolicyReference? Describe(IReadOnlyList<AbacPolicy> applicable, Guid id)
    {
        for (int index = 0; index < applicable.Count; index++)
        {
            if (applicable[index].Id == id)
            {
                AbacPolicy policy = applicable[index];

                return new PolicyReference(policy.Id, policy.Name, policy.Effect.ToString(), policy.Priority);
            }
        }

        return null;
    }

    private static string Summarise(
        bool relationshipAllowed,
        string? grantedAt,
        string resource,
        AbacDecision attributes,
        PolicyReference? deciding,
        bool resolved)
    {
        if (!relationshipAllowed)
        {
            if (!resolved)
            {
                return "No relationship grants this.";
            }

            // An ancestor granting while the leaf refuses is the signature of an explicit exclusion.
            // Saying only that nothing grants it would be true of the outcome and useless for working out
            // why, which is the whole reason a decision carries an explanation.
            return grantedAt is null
                ? "No relationship grants this at any level of the hierarchy."
                : $"{grantedAt} grants this, but the grant does not reach {resource}. "
                    + "Something further down the hierarchy excludes it.";
        }

        string origin = grantedAt is null
            ? "a relationship grants this"
            : $"the grant comes from {grantedAt}";

        return attributes.Verdict is AbacVerdict.Deny
            ? $"Refused: {origin}, but policy '{deciding?.Name}' forbids it."
            : $"Allowed: {origin}, and no policy forbids it.";
    }
}

/// <summary>Serialisation settings shared by the decision record and the endpoints that read it back.</summary>
public static class DecisionJson
{
    /// <summary>Camel-cased, so a recorded reason and an API response read identically.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
