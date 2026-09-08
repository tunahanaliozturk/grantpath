using System.Threading.Channels;
using GrantPath.Api.Observability;
using GrantPath.Data;
using Microsoft.Extensions.Options;

namespace GrantPath.Api.Auditing;

/// <summary>One decision, on its way to the trail.</summary>
public sealed class DecisionRecord
{
    /// <summary>The request this decision belongs to.</summary>
    public required Guid RequestId { get; init; }

    /// <summary>Who asked.</summary>
    public required string Subject { get; init; }

    /// <summary>What was asked for.</summary>
    public required string Resource { get; init; }

    /// <summary>The type half of the resource.</summary>
    public required string ResourceType { get; init; }

    /// <summary>Which relation.</summary>
    public required string Relation { get; init; }

    /// <summary>The answer.</summary>
    public required AuthorizationDecision Decision { get; init; }

    /// <summary>What the relationship graph said alone.</summary>
    public required bool RelationshipAllowed { get; init; }

    /// <summary>What the attribute layer said alone.</summary>
    public required string AttributeVerdict { get; init; }

    /// <summary>The reasoning, serialised.</summary>
    public required string ReasonJson { get; init; }

    /// <summary>Which authorization model produced it.</summary>
    public required string ModelId { get; init; }

    /// <summary>How long it took.</summary>
    public required double LatencyMs { get; init; }

    /// <summary>When it happened.</summary>
    public required DateTimeOffset DecidedAtUtc { get; init; }
}

/// <summary>
/// Puts every decision in the trail without putting a database write on the decision path.
/// </summary>
/// <remarks>
/// <para>
/// A bounded queue drained by a background writer. Bounded matters: an unbounded queue does not remove
/// backpressure, it converts it into memory growth and then into a process that dies holding every record
/// it had not written yet.
/// </para>
/// <para>
/// When the queue is full the record is written inline, on the request thread, before the call returns.
/// That is slower and it is meant to be: the requirement is that no decision goes unrecorded, so under
/// sustained overload the system gets slower rather than quieter. Dropping would make the trail
/// unreliable exactly when something unusual is happening, which is the only time anyone reads it.
/// </para>
/// </remarks>
/// <param name="scopeFactory">Opens a scope per write, since this outlives any request.</param>
/// <param name="metrics">Where the queue depth is published.</param>
/// <param name="options">Queue and batching settings.</param>
public sealed class DecisionAuditWriter(
    IServiceScopeFactory scopeFactory,
    GrantPathMetrics metrics,
    IOptions<AuditOptions> options)
{
    private readonly AuditOptions _options = options.Value;

    private readonly Channel<DecisionRecord> _queue = Channel.CreateBounded<DecisionRecord>(
        new BoundedChannelOptions(options.Value.QueueCapacity)
        {
            // Wait, not DropWrite. DropWrite would make TryWrite report success and throw the record
            // away, which is precisely the silent loss the synchronous fallback exists to prevent. In
            // this mode TryWrite refuses a full queue without blocking, and the caller writes inline.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>How many records are waiting.</summary>
    public int QueueDepth => _queue.Reader.Count;

    /// <summary>The queue the background writer drains.</summary>
    internal ChannelReader<DecisionRecord> Reader => _queue.Reader;

    /// <summary>Records a decision, inline if the queue is full.</summary>
    /// <param name="record">The decision.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the record had to be written on the calling thread.</returns>
    public async Task<bool> RecordAsync(DecisionRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (_queue.Writer.TryWrite(record))
        {
            metrics.SetQueueDepth(_queue.Reader.Count);
            return false;
        }

        await WriteAsync([record], synchronous: true, cancellationToken);

        return true;
    }

    /// <summary>Persists a batch.</summary>
    /// <param name="records">The decisions.</param>
    /// <param name="synchronous">Whether these are being written on a request thread.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal async Task WriteAsync(
        IReadOnlyList<DecisionRecord> records,
        bool synchronous,
        CancellationToken cancellationToken)
    {
        if (records.Count is 0)
        {
            return;
        }

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        GrantPathDbContext dbContext = scope.ServiceProvider.GetRequiredService<GrantPathDbContext>();

        foreach (DecisionRecord record in records)
        {
            dbContext.Decisions.Add(new AuthorizationDecisionRecord
            {
                RequestId = record.RequestId,
                Subject = record.Subject,
                Resource = record.Resource,
                ResourceType = record.ResourceType,
                Relation = record.Relation,
                Decision = record.Decision,
                RelationshipAllowed = record.RelationshipAllowed,
                AttributeVerdict = record.AttributeVerdict,
                ReasonJson = record.ReasonJson,
                ModelId = record.ModelId,
                LatencyMs = record.LatencyMs,
                WrittenSynchronously = synchronous,
                DecidedAtUtc = record.DecidedAtUtc,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        metrics.SetQueueDepth(_queue.Reader.Count);
    }

    /// <summary>Batching settings, for the background writer.</summary>
    internal AuditOptions Options => _options;
}
