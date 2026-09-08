using GrantPath.Api.Authorization;
using GrantPath.Api.Observability;
using GrantPath.Data;
using GrantPath.Rebac;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GrantPath.Api.Projection;

/// <summary>
/// Reads the relationship changelog and keeps the projection in step with it.
/// </summary>
/// <remarks>
/// <para>
/// The cursor lives in the database rather than in memory, so a restart resumes where the reader left off.
/// Replaying an entire relationship history after every deploy would take longer than the deploy did, and
/// would grow worse the longer the system ran.
/// </para>
/// <para>
/// A change that mentions a subject rebuilds that subject. A structural change, one that moves a resource
/// between parents, rebuilds every subject the projection already knows about, because inheritance means
/// the blast radius of moving a folder is everyone who could see anything inside it. That is a coarse
/// answer and it is stated as a limitation rather than dressed up: a system with a large user base would
/// resolve the affected set from the moved subtree instead.
/// </para>
/// </remarks>
/// <param name="scopeFactory">Opens a scope per pass.</param>
/// <param name="engine">The relationship store.</param>
/// <param name="containment">Cached containment chains, invalidated when a parent moves.</param>
/// <param name="metrics">Instruments.</param>
/// <param name="options">Poll interval and relation.</param>
/// <param name="timeProvider">Clock.</param>
/// <param name="logger">Logger.</param>
public sealed partial class ProjectionService(
    IServiceScopeFactory scopeFactory,
    IRelationshipEngine engine,
    ContainmentResolver containment,
    GrantPathMetrics metrics,
    IOptions<ProjectionOptions> options,
    TimeProvider timeProvider,
    ILogger<ProjectionService> logger) : BackgroundService
{
    private const int CursorId = 1;

    private readonly ProjectionOptions _options = options.Value;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        using PeriodicTimer timer = new(_options.PollInterval, timeProvider);

        do
        {
            try
            {
                await PollAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A failed pass is not fatal: the cursor is only advanced after a successful apply, so the
                // next pass repeats the same changes.
                LogPassFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Runs one pass. Exposed so a test can drive it deterministically.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many changes were applied.</returns>
    public async Task<int> PollAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        GrantPathDbContext dbContext = scope.ServiceProvider.GetRequiredService<GrantPathDbContext>();
        PermissionProjection projection = scope.ServiceProvider.GetRequiredService<PermissionProjection>();

        ChangelogCursor cursor = await dbContext.ChangelogCursors
            .FirstOrDefaultAsync(entry => entry.Id == CursorId, cancellationToken)
            ?? new ChangelogCursor { Id = CursorId };

        RelationshipChangePage page = await engine.ReadChangesAsync(cursor.ContinuationToken, cancellationToken);

        if (page.Changes.Count is 0)
        {
            metrics.SetProjectionLag(TimeSpan.Zero);
            await SaveCursorAsync(dbContext, cursor, page.ContinuationToken, cancellationToken);
            return 0;
        }

        var subjects = new HashSet<string>(StringComparer.Ordinal);
        bool structural = false;

        foreach (RelationshipChange change in page.Changes)
        {
            if (change.Tuple.Relation is AuthorizationModel.Relations.Parent)
            {
                structural = true;
                containment.Invalidate(change.Tuple.Resource);
                continue;
            }

            if (change.Tuple.Subject.StartsWith("user:", StringComparison.Ordinal))
            {
                subjects.Add(change.Tuple.Subject);
            }
        }

        if (structural)
        {
            List<string> known = await dbContext.DocumentPermissions
                .Select(entry => entry.Subject)
                .Distinct()
                .ToListAsync(cancellationToken);

            subjects.UnionWith(known);
        }

        foreach (string subject in subjects)
        {
            await projection.RebuildSubjectAsync(dbContext, subject, cancellationToken);
        }

        // The cursor advances only now, after every affected subject has been rebuilt. A crash halfway
        // through repeats the batch, and rebuilding a subject twice produces the same rows.
        await SaveCursorAsync(dbContext, cursor, page.ContinuationToken, cancellationToken);

        metrics.RecordProjectionChanges(page.Changes.Count);
        metrics.SetProjectionLag(timeProvider.GetUtcNow() - page.Changes[^1].OccurredAtUtc);

        LogApplied(logger, page.Changes.Count, subjects.Count);

        return page.Changes.Count;
    }

    private async Task SaveCursorAsync(
        GrantPathDbContext dbContext,
        ChangelogCursor cursor,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        cursor.ContinuationToken = continuationToken;
        cursor.UpdatedAtUtc = timeProvider.GetUtcNow();

        if (dbContext.Entry(cursor).State is EntityState.Detached)
        {
            dbContext.ChangelogCursors.Add(cursor);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    [LoggerMessage(
        EventId = 5100,
        Level = LogLevel.Debug,
        Message = "Applied {ChangeCount} relationship changes, rebuilding {SubjectCount} subjects.")]
    private static partial void LogApplied(ILogger logger, int changeCount, int subjectCount);

    [LoggerMessage(
        EventId = 5101,
        Level = LogLevel.Warning,
        Message = "A projection pass failed. The cursor did not advance, so the next pass repeats it.")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
