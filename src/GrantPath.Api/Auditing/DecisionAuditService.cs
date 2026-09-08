using System.Threading.Channels;

namespace GrantPath.Api.Auditing;

/// <summary>
/// Drains the decision queue into the trail.
/// </summary>
/// <remarks>
/// <para>
/// Batches on two conditions, whichever comes first: a full batch, or a short interval elapsing. Batching
/// only on count would leave the last few decisions of a quiet minute unwritten until the next busy one,
/// which is the wrong way round for a trail whose value is that it is complete.
/// </para>
/// <para>
/// A failed write is retried once with the same batch and then logged and dropped, because the alternative
/// is a writer that stops draining and forces every subsequent decision onto the synchronous path. That is
/// a deliberate trade, and it is why the failure is logged at error rather than swallowed.
/// </para>
/// </remarks>
/// <param name="writer">The queue and its persistence.</param>
/// <param name="logger">Logger.</param>
public sealed partial class DecisionAuditService(
    DecisionAuditWriter writer,
    ILogger<DecisionAuditService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ChannelReader<DecisionRecord> reader = writer.Reader;
        List<DecisionRecord> batch = new(writer.Options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await reader.WaitToReadAsync(stoppingToken))
                {
                    break;
                }

                using var window = new CancellationTokenSource(writer.Options.FlushInterval);
                using CancellationTokenSource linked =
                    CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, window.Token);

                batch.Clear();

                try
                {
                    while (batch.Count < writer.Options.BatchSize
                        && await reader.WaitToReadAsync(linked.Token))
                    {
                        while (batch.Count < writer.Options.BatchSize && reader.TryRead(out DecisionRecord? record))
                        {
                            batch.Add(record);
                        }
                    }
                }
                catch (OperationCanceledException) when (window.IsCancellationRequested)
                {
                    // The flush interval elapsed. Whatever arrived in that window is what gets written.
                }

                await FlushAsync(batch, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        // On the way out, take whatever is still queued. A restart should not lose decisions that were
        // already made.
        batch.Clear();

        while (reader.TryRead(out DecisionRecord? remaining))
        {
            batch.Add(remaining);
        }

        await FlushAsync(batch, CancellationToken.None);
    }

    private async Task FlushAsync(List<DecisionRecord> batch, CancellationToken cancellationToken)
    {
        if (batch.Count is 0)
        {
            return;
        }

        try
        {
            await writer.WriteAsync(batch, synchronous: false, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRetry(logger, batch.Count, exception);

            try
            {
                await writer.WriteAsync(batch, synchronous: false, cancellationToken);
            }
            catch (Exception retry) when (retry is not OperationCanceledException)
            {
                LogLost(logger, batch.Count, retry);
            }
        }
    }

    [LoggerMessage(
        EventId = 5000,
        Level = LogLevel.Warning,
        Message = "Writing {Count} audit records failed. Retrying once.")]
    private static partial void LogRetry(ILogger logger, int count, Exception exception);

    [LoggerMessage(
        EventId = 5001,
        Level = LogLevel.Error,
        Message = "Writing {Count} audit records failed twice. Those decisions are not in the trail.")]
    private static partial void LogLost(ILogger logger, int count, Exception exception);
}
