namespace GrantPath.Rebac;

/// <summary>One relationship, in the form the rest of this codebase speaks.</summary>
/// <param name="Subject">The subject, as <c>user:alice</c>.</param>
/// <param name="Relation">The relation, as <c>viewer</c>.</param>
/// <param name="Resource">The resource, as <c>document:handbook</c>.</param>
/// <remarks>
/// OpenFGA calls these three parts user, relation and object. They are named subject and resource
/// here because "object" is a keyword in most languages that would consume this, and because subject
/// and resource are what the rest of the system, and every audit record, already calls them.
/// </remarks>
public readonly record struct RelationshipTuple(string Subject, string Relation, string Resource)
{
    /// <summary>Renders the tuple the way the audit trail writes it.</summary>
    public override string ToString() => $"{Subject}#{Relation}@{Resource}";
}

/// <summary>Whether a change added or removed a relationship.</summary>
public enum RelationshipChangeOperation
{
    /// <summary>The tuple was written.</summary>
    Write = 0,

    /// <summary>The tuple was deleted.</summary>
    Delete = 1,
}

/// <summary>One entry from the relationship changelog.</summary>
/// <param name="Tuple">What changed.</param>
/// <param name="Operation">How.</param>
/// <param name="OccurredAtUtc">When the store recorded it.</param>
public sealed record RelationshipChange(
    RelationshipTuple Tuple,
    RelationshipChangeOperation Operation,
    DateTimeOffset OccurredAtUtc);

/// <summary>A page of the changelog.</summary>
/// <param name="Changes">The entries.</param>
/// <param name="ContinuationToken">Where to resume, or null when the log has been drained.</param>
public sealed record RelationshipChangePage(
    IReadOnlyList<RelationshipChange> Changes,
    string? ContinuationToken);

/// <summary>One question in a batch.</summary>
/// <param name="Tuple">The relationship being asked about.</param>
/// <param name="CorrelationId">How the caller recognises the answer.</param>
public sealed record RelationshipQuery(RelationshipTuple Tuple, string CorrelationId);

/// <summary>One answer from a batch.</summary>
/// <param name="CorrelationId">Matches the query.</param>
/// <param name="Allowed">Whether the relationship holds.</param>
public sealed record RelationshipAnswer(string CorrelationId, bool Allowed);

/// <summary>
/// The relationship store, as this codebase needs it.
/// </summary>
/// <remarks>
/// <para>
/// Everything OpenFGA-shaped stops here. The SDK is pre-1.0 and its request and response types change
/// between releases; keeping them behind this interface means an upgrade is a change to one implementation
/// rather than a change to every call site, and it means the rest of the system can be reasoned about
/// without knowing what a <c>ClientBatchCheckItem</c> is.
/// </para>
/// <para>
/// It is a narrow interface on purpose. Only what the facade genuinely needs appears here, so the seam
/// stays a seam rather than becoming a second copy of the SDK.
/// </para>
/// </remarks>
public interface IRelationshipEngine
{
    /// <summary>The authorization model decisions are being made against.</summary>
    string ModelId { get; }

    /// <summary>Asks whether one relationship holds, directly or through the graph.</summary>
    /// <param name="tuple">The relationship in question.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> CheckAsync(RelationshipTuple tuple, CancellationToken cancellationToken);

    /// <summary>Asks several questions in one round trip.</summary>
    /// <param name="queries">The questions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<RelationshipAnswer>> BatchCheckAsync(
        IReadOnlyList<RelationshipQuery> queries,
        CancellationToken cancellationToken);

    /// <summary>Writes and deletes tuples in one transaction.</summary>
    /// <param name="writes">Tuples to add.</param>
    /// <param name="deletes">Tuples to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WriteAsync(
        IReadOnlyList<RelationshipTuple> writes,
        IReadOnlyList<RelationshipTuple> deletes,
        CancellationToken cancellationToken);

    /// <summary>Reads stored tuples matching a partial pattern.</summary>
    /// <param name="subject">Subject filter, or null.</param>
    /// <param name="relation">Relation filter, or null.</param>
    /// <param name="resource">Resource filter, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<RelationshipTuple>> ReadAsync(
        string? subject,
        string? relation,
        string? resource,
        CancellationToken cancellationToken);

    /// <summary>Lists every resource of a type on which the subject holds a relation.</summary>
    /// <remarks>
    /// Used to rebuild the flattened projection rather than on the decision path. One call answers what a
    /// subject can reach, which is a different and much cheaper question than asking about each resource
    /// in turn.
    /// </remarks>
    /// <param name="subject">The subject, as <c>user:alice</c>.</param>
    /// <param name="relation">The relation.</param>
    /// <param name="resourceType">The type, as <c>document</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<string>> ListResourcesAsync(
        string subject,
        string relation,
        string resourceType,
        CancellationToken cancellationToken);

    /// <summary>Reads the changelog from a position.</summary>
    /// <param name="continuationToken">Where to resume, or null to start from the beginning.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<RelationshipChangePage> ReadChangesAsync(
        string? continuationToken,
        CancellationToken cancellationToken);
}
