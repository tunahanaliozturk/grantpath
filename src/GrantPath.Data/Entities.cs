namespace GrantPath.Data;

/// <summary>What a stored policy says when it matches.</summary>
public enum StoredPolicyEffect
{
    /// <summary>Forbid.</summary>
    Deny = 0,

    /// <summary>Raise no objection.</summary>
    Allow = 1,
}

/// <summary>The outcome of one authorization decision.</summary>
public enum AuthorizationDecision
{
    /// <summary>Refused.</summary>
    Deny = 0,

    /// <summary>Permitted.</summary>
    Allow = 1,
}

/// <summary>
/// An attribute policy as stored.
/// </summary>
/// <remarks>
/// The condition is kept as text rather than shredded into columns. It is a tree, it is authored as JSON,
/// and it is compiled once on load; storing it any other way would mean reassembling the tree on every
/// read for no benefit.
/// </remarks>
public sealed class AbacPolicyRecord
{
    /// <summary>Identifier, quoted in every decision this policy produced.</summary>
    public Guid Id { get; set; }

    /// <summary>A name a person can recognise in an explanation.</summary>
    public required string Name { get; set; }

    /// <summary>Which resource type it applies to.</summary>
    public required string ResourceType { get; set; }

    /// <summary>What it says when it matches.</summary>
    public StoredPolicyEffect Effect { get; set; }

    /// <summary>Higher wins. Ties go to deny.</summary>
    public int Priority { get; set; }

    /// <summary>The condition tree, as authored.</summary>
    public required string ConditionJson { get; set; }

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>
/// One decision, recorded so it can be explained later.
/// </summary>
/// <remarks>
/// <para>
/// Append-only and never pruned. The question this table answers, "why did the system let that person read
/// that document three weeks ago", is always asked about a date in the past, and re-running today's rules
/// against today's data is not an answer to it.
/// </para>
/// <para>
/// The reasoning is stored as written, including which authorization model version produced it. A model id
/// makes an old decision legible after the rules have moved on.
/// </para>
/// </remarks>
public sealed class AuthorizationDecisionRecord
{
    /// <summary>The request this decision belongs to.</summary>
    public Guid RequestId { get; set; }

    /// <summary>Who asked, as <c>user:alice</c>.</summary>
    public required string Subject { get; set; }

    /// <summary>What was asked for, as <c>document:handbook</c>.</summary>
    public required string Resource { get; set; }

    /// <summary>The type half of the resource, kept separately so the trail can be filtered by it.</summary>
    public required string ResourceType { get; set; }

    /// <summary>Which relation was asked about.</summary>
    public required string Relation { get; set; }

    /// <summary>The answer.</summary>
    public AuthorizationDecision Decision { get; set; }

    /// <summary>What the relationship graph said on its own.</summary>
    public bool RelationshipAllowed { get; set; }

    /// <summary>What the attribute layer said on its own.</summary>
    public required string AttributeVerdict { get; set; }

    /// <summary>The relationship path, as resolved at the moment of the decision.</summary>
    public required string ReasonJson { get; set; }

    /// <summary>The authorization model the relationship check ran against.</summary>
    public required string ModelId { get; set; }

    /// <summary>How long the decision took, in milliseconds.</summary>
    public double LatencyMs { get; set; }

    /// <summary>Whether the record had to be written on the request thread because the queue was full.</summary>
    public bool WrittenSynchronously { get; set; }

    /// <summary>When the decision was made.</summary>
    public DateTimeOffset DecidedAtUtc { get; set; }
}

/// <summary>
/// A flattened projection of who may read which document, for row-level security to consult.
/// </summary>
/// <remarks>
/// <para>
/// Derived, disposable and rebuildable from the relationship store at any time. It exists so that a query
/// which bypasses the facade entirely, a reporting job or an ad hoc console, is still bounded by the same
/// decision, a second or two behind.
/// </para>
/// <para>
/// The lag is deliberately asymmetric. A new grant may take until the next poll to appear here, which is
/// merely inconvenient. A revocation is removed the moment it is requested, before the poller runs, because
/// a database path that is briefly more permissive than the truth is the one failure mode this table must
/// not have.
/// </para>
/// </remarks>
public sealed class DocumentPermissionCacheEntry
{
    /// <summary>The document, as <c>document:handbook</c>.</summary>
    public required string DocumentId { get; set; }

    /// <summary>The subject, as <c>user:alice</c>.</summary>
    public required string Subject { get; set; }

    /// <summary>Which relation the subject holds.</summary>
    public required string Relation { get; set; }

    /// <summary>When the projection last confirmed this row.</summary>
    public DateTimeOffset ComputedAtUtc { get; set; }
}

/// <summary>Where the changelog reader has got to.</summary>
/// <remarks>
/// A single row. Kept in the database rather than in memory so a restart resumes rather than replaying the
/// entire relationship history, which on a busy store would take longer than the outage did.
/// </remarks>
public sealed class ChangelogCursor
{
    /// <summary>Constant identifier for the single row.</summary>
    public int Id { get; set; }

    /// <summary>The store's continuation token.</summary>
    public string? ContinuationToken { get; set; }

    /// <summary>When the reader last made progress.</summary>
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>
/// A document in the demonstration domain.
/// </summary>
/// <remarks>
/// The content table exists so row-level security has something real to protect. Its classification is
/// also what the attribute policies read, which is the point: the same row is both the thing being guarded
/// and the source of the attribute that narrows access to it.
/// </remarks>
public sealed class Document
{
    /// <summary>The document, as <c>document:handbook</c>.</summary>
    public required string Id { get; set; }

    /// <summary>The folder that contains it, as <c>folder:policies</c>.</summary>
    public required string FolderId { get; set; }

    /// <summary>Title.</summary>
    public required string Title { get; set; }

    /// <summary>Classification, read by attribute policies.</summary>
    public required string Classification { get; set; }

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }
}
