using System.Collections.Frozen;
using GrantPath.Abac;
using GrantPath.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GrantPath.Api.Authorization;

/// <summary>
/// Keeps the compiled attribute policies in memory.
/// </summary>
/// <remarks>
/// <para>
/// Policies are authored as JSON and parsed into a tree. Doing that per request would put a JSON parse on
/// a path with a five millisecond budget, for documents that change a few times a month. They are compiled
/// once, grouped by resource type, and swapped in as a whole.
/// </para>
/// <para>
/// The swap is a single reference assignment, so a request either sees the previous set or the next one
/// and never a half-built map. A refresh is triggered by the admin endpoints the moment a policy changes,
/// and the interval is only a backstop for an instance that did not serve the write.
/// </para>
/// </remarks>
/// <param name="scopeFactory">Used to open a scope, since this outlives any request.</param>
/// <param name="options">Cache lifetime.</param>
/// <param name="timeProvider">Clock.</param>
public sealed class PolicyCache(
    IServiceScopeFactory scopeFactory,
    IOptions<DecisionOptions> options,
    TimeProvider timeProvider) : IDisposable
{
    private static readonly FrozenDictionary<string, AbacPolicy[]> EmptySet =
        FrozenDictionary<string, AbacPolicy[]>.Empty;

    private readonly TimeSpan _lifetime = options.Value.PolicyCacheLifetime;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private FrozenDictionary<string, AbacPolicy[]> _byResourceType = EmptySet;
    private DateTimeOffset _loadedAtUtc = DateTimeOffset.MinValue;

    /// <summary>Returns the policies for a resource type, reloading first if the set has gone stale.</summary>
    /// <param name="resourceType">The resource type, as <c>document</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async ValueTask<IReadOnlyList<AbacPolicy>> GetAsync(
        string resourceType,
        CancellationToken cancellationToken)
    {
        if (timeProvider.GetUtcNow() - _loadedAtUtc > _lifetime)
        {
            await RefreshAsync(cancellationToken);
        }

        return _byResourceType.TryGetValue(resourceType, out AbacPolicy[]? policies) ? policies : [];
    }

    /// <summary>Reloads every policy from the database.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);

        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            GrantPathDbContext dbContext = scope.ServiceProvider.GetRequiredService<GrantPathDbContext>();

            List<AbacPolicyRecord> records = await dbContext.AbacPolicies
                .AsNoTracking()
                .OrderByDescending(policy => policy.Priority)
                .ToListAsync(cancellationToken);

            var grouped = new Dictionary<string, List<AbacPolicy>>(StringComparer.Ordinal);

            foreach (AbacPolicyRecord record in records)
            {
                AbacPolicy compiled = AbacPolicy.Compile(
                    record.Id,
                    record.Name,
                    record.ResourceType,
                    record.Effect is StoredPolicyEffect.Deny ? PolicyEffect.Deny : PolicyEffect.Allow,
                    record.Priority,
                    record.ConditionJson);

                if (!grouped.TryGetValue(record.ResourceType, out List<AbacPolicy>? bucket))
                {
                    bucket = [];
                    grouped[record.ResourceType] = bucket;
                }

                bucket.Add(compiled);
            }

            _byResourceType = grouped.ToFrozenDictionary(
                pair => pair.Key,
                pair => pair.Value.ToArray(),
                StringComparer.Ordinal);

            _loadedAtUtc = timeProvider.GetUtcNow();
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _refreshLock.Dispose();
}
