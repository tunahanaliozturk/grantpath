using GrantPath.Api.Authorization;
using GrantPath.Data;
using GrantPath.Rebac;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GrantPath.Api.Projection;

/// <summary>
/// Maintains the flattened permission table that row-level security reads.
/// </summary>
/// <remarks>
/// <para>
/// The projection exists so that a query which never touches the facade, a reporting job or somebody with
/// a psql prompt, is still bounded by the same authorization decision. Application-layer discipline is not
/// a security boundary; a policy on the table is.
/// </para>
/// <para>
/// Its correctness rule is asymmetric and that asymmetry is the whole design. A newly granted permission
/// may take until the next poll to appear here, which is inconvenient. A revoked one is removed the moment
/// it is revoked, before any poll, because a database path that is briefly more permissive than the truth
/// is the one failure this table must never have. Rebuilding a subject always deletes first and inserts
/// second, inside one transaction, so there is no window in which the old rows and the new rows are both
/// visible.
/// </para>
/// </remarks>
/// <param name="engine">The relationship store.</param>
/// <param name="options">Which relation is materialised.</param>
/// <param name="timeProvider">Clock.</param>
public sealed class PermissionProjection(
    IRelationshipEngine engine,
    IOptions<ProjectionOptions> options,
    TimeProvider timeProvider)
{
    private readonly ProjectionOptions _options = options.Value;

    /// <summary>Rebuilds one subject's rows from the relationship store.</summary>
    /// <param name="dbContext">The database.</param>
    /// <param name="subject">The subject, as <c>user:alice</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many documents the subject can now reach.</returns>
    public async Task<int> RebuildSubjectAsync(
        GrantPathDbContext dbContext,
        string subject,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        IReadOnlyList<string> documents = await engine.ListResourcesAsync(
            subject,
            _options.Relation,
            AuthorizationModel.Types.Document,
            cancellationToken);

        DateTimeOffset now = timeProvider.GetUtcNow();

        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        await dbContext.DocumentPermissions
            .Where(entry => entry.Subject == subject && entry.Relation == _options.Relation)
            .ExecuteDeleteAsync(cancellationToken);

        foreach (string document in documents)
        {
            dbContext.DocumentPermissions.Add(new DocumentPermissionCacheEntry
            {
                DocumentId = document,
                Subject = subject,
                Relation = _options.Relation,
                ComputedAtUtc = now,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return documents.Count;
    }

    /// <summary>
    /// Removes a subject's rows immediately, without waiting for the poller.
    /// </summary>
    /// <remarks>
    /// Called by the admin endpoints the moment a tuple is deleted. It over-removes on purpose: everything
    /// for that subject goes, and the next poll puts back whatever the subject is still entitled to. Being
    /// briefly too strict costs a retry; being briefly too permissive costs a disclosure.
    /// </remarks>
    /// <param name="dbContext">The database.</param>
    /// <param name="subject">The subject whose access changed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task RevokeSubjectAsync(
        GrantPathDbContext dbContext,
        string subject,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        await dbContext.DocumentPermissions
            .Where(entry => entry.Subject == subject)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Removes every row, for a rebuild from scratch.</summary>
    /// <param name="dbContext">The database.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task ClearAsync(GrantPathDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        return dbContext.DocumentPermissions.ExecuteDeleteAsync(cancellationToken);
    }
}
