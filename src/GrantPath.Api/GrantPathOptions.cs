using System.ComponentModel.DataAnnotations;

namespace GrantPath.Api;

/// <summary>How the decision path behaves.</summary>
public sealed class DecisionOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "GrantPath:Decision";

    /// <summary>
    /// Whether an allowed decision also resolves the relationship path that produced it.
    /// </summary>
    /// <remarks>
    /// On by default, because a decision nobody can explain is most of the way to a decision nobody can
    /// defend. It is a switch rather than a constant because it is not free: resolving the path costs one
    /// batched round trip to the relationship store on top of the check itself. The README publishes the
    /// measured cost of both settings rather than asserting it is negligible.
    /// </remarks>
    public bool ResolveRelationshipPath { get; init; } = true;

    /// <summary>How long a resolved containment chain stays cached.</summary>
    /// <remarks>
    /// Containment changes rarely: documents move between folders far less often than they are read. The
    /// changelog reader evicts an entry the moment a parent relation actually changes, so this is a
    /// backstop rather than the mechanism.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan ContainmentCacheLifetime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long attribute policies stay cached before being reloaded.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan PolicyCacheLifetime { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>How the decision trail is written.</summary>
public sealed class AuditOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "GrantPath:Audit";

    /// <summary>
    /// How many decisions may be waiting to be written before the queue is full.
    /// </summary>
    /// <remarks>
    /// Bounded on purpose. An unbounded queue does not remove the backpressure, it converts it into memory
    /// growth and then into a process that dies holding every record it had not written yet.
    /// </remarks>
    [Range(16, 1_000_000)]
    public int QueueCapacity { get; init; } = 8192;

    /// <summary>How many records the writer batches into one insert.</summary>
    [Range(1, 10_000)]
    public int BatchSize { get; init; } = 200;

    /// <summary>How long the writer waits for a batch to fill before writing what it has.</summary>
    [Range(typeof(TimeSpan), "00:00:00.010", "00:00:10")]
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>How the row-level security projection is maintained.</summary>
public sealed class ProjectionOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "GrantPath:Projection";

    /// <summary>Whether the changelog reader runs.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How often the changelog is read.</summary>
    /// <remarks>
    /// This is the bound on how long a new grant may take to appear in the projection. It is not the bound
    /// on a revocation: a revoke removes the row before this poller runs, so the database path is never
    /// more permissive than the relationship store.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:05:00")]
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Which relation on a document the projection materialises.</summary>
    public string Relation { get; init; } = "viewer";
}

/// <summary>Who is allowed to call this service, and how it recognises them.</summary>
public sealed class CallerOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "GrantPath:Callers";

    /// <summary>The header carrying the calling service's key.</summary>
    public const string ApiKeyHeader = "X-GrantPath-Key";

    /// <summary>The header naming the person on whose behalf an administrative call is made.</summary>
    public const string SubjectHeader = "X-GrantPath-Subject";

    /// <summary>
    /// Keys that identify a calling service.
    /// </summary>
    /// <remarks>
    /// This service authorizes; it does not authenticate people. The subject arrives already
    /// authenticated from an identity provider, and what has to be established here is that the caller is
    /// a service entitled to ask on that subject's behalf. A shared key is the smallest thing that does
    /// that honestly. In a real deployment it would be a token from the same provider that authenticated
    /// the subject.
    /// </remarks>
    public IReadOnlyList<string> ApiKeys { get; init; } = [];

    /// <summary>
    /// Whether callers without a key are accepted.
    /// </summary>
    /// <remarks>
    /// False anywhere real. It exists so the demo stack and the test suites can talk to the service
    /// without a key ceremony that would prove nothing about the behaviour under test.
    /// </remarks>
    public bool AllowAnonymous { get; init; }
}

/// <summary>What the service does to the schema on startup.</summary>
public sealed class DatabaseOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "GrantPath:Database";

    /// <summary>Whether pending migrations are applied when the service starts.</summary>
    /// <remarks>
    /// Off by default. A rolling deploy runs two versions side by side for a minute or two, and a
    /// migration racing itself across instances is a poor way to find that out.
    /// </remarks>
    public bool MigrateOnStartup { get; init; }

    /// <summary>Whether the demonstration org, workspace, folder, documents and accounts are created.</summary>
    public bool SeedDemoData { get; init; }
}
