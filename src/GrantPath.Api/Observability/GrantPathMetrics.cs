using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace GrantPath.Api.Observability;

/// <summary>
/// The instruments this service publishes.
/// </summary>
/// <remarks>
/// The one worth an alert is the audit queue depth. A sustained nonzero depth means the writer is falling
/// behind, and the fallback that keeps records from being lost does so by writing them on the request
/// thread, which shows up as latency rather than as an error. Watching the depth catches that before the
/// latency does.
/// </remarks>
public sealed class GrantPathMetrics : IDisposable
{
    /// <summary>The meter to subscribe to.</summary>
    public const string MeterName = "GrantPath";

    /// <summary>The activity source for spans this service starts.</summary>
    public const string ActivitySourceName = "GrantPath";

    private readonly Meter _meter;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _decisions;
    private readonly Counter<long> _synchronousWrites;
    private readonly Counter<long> _projectionChanges;

    private int _queueDepth;
    private long _projectionLagTicks;

    /// <summary>Creates the meter and its instruments.</summary>
    /// <param name="factory">Meter factory, so the meter is disposed with the container.</param>
    public GrantPathMetrics(IMeterFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _meter = factory.Create(MeterName);

        _duration = _meter.CreateHistogram<double>(
            "grantpath.check.duration",
            unit: "ms",
            description: "How long an authorization decision took.");

        _decisions = _meter.CreateCounter<long>(
            "grantpath.decision.total",
            description: "Authorization decisions, by outcome.");

        _synchronousWrites = _meter.CreateCounter<long>(
            "grantpath.audit.synchronous_writes",
            description: "Decisions whose audit record had to be written on the request thread.");

        _projectionChanges = _meter.CreateCounter<long>(
            "grantpath.projection.changes",
            description: "Relationship changes applied to the row-level security projection.");

        _meter.CreateObservableGauge(
            "grantpath.audit.queue_depth",
            () => Volatile.Read(ref _queueDepth),
            description: "Decisions waiting to be written to the audit trail.");

        _meter.CreateObservableGauge(
            "grantpath.projection.lag",
            () => TimeSpan.FromTicks(Interlocked.Read(ref _projectionLagTicks)).TotalSeconds,
            unit: "s",
            description: "Age of the oldest relationship change the projection has not yet applied.");
    }

    /// <summary>The source spans are started from.</summary>
    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName);

    /// <summary>Records a decision.</summary>
    /// <param name="relation">Which relation was asked about.</param>
    /// <param name="resourceType">Which kind of resource.</param>
    /// <param name="allowed">The outcome.</param>
    /// <param name="latencyMs">How long it took.</param>
    public void RecordDecision(string relation, string resourceType, bool allowed, double latencyMs)
    {
        var relationTag = new KeyValuePair<string, object?>("relation", relation);
        var typeTag = new KeyValuePair<string, object?>("resource_type", resourceType);
        var decisionTag = new KeyValuePair<string, object?>("decision", allowed ? "allow" : "deny");

        _duration.Record(latencyMs, relationTag, typeTag, decisionTag);
        _decisions.Add(1, relationTag, typeTag, decisionTag);
    }

    /// <summary>Records that the audit queue was full and a record was written inline.</summary>
    public void RecordSynchronousAuditWrite() => _synchronousWrites.Add(1);

    /// <summary>Records how many relationship changes the projection applied.</summary>
    /// <param name="count">How many.</param>
    public void RecordProjectionChanges(int count) => _projectionChanges.Add(count);

    /// <summary>Publishes the current audit queue depth.</summary>
    /// <param name="depth">How many records are waiting.</param>
    public void SetQueueDepth(int depth) => Volatile.Write(ref _queueDepth, depth);

    /// <summary>Publishes how far behind the projection is.</summary>
    /// <param name="lag">The age of the oldest unapplied change.</param>
    public void SetProjectionLag(TimeSpan lag) => Interlocked.Exchange(ref _projectionLagTicks, lag.Ticks);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
