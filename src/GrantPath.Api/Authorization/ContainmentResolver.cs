using System.Collections.Concurrent;
using GrantPath.Rebac;
using Microsoft.Extensions.Options;

namespace GrantPath.Api.Authorization;

/// <summary>
/// Works out what contains what, so a decision can be explained in terms of the hierarchy.
/// </summary>
/// <remarks>
/// <para>
/// The chain from a document up to its org is read from the relationship store rather than kept in a
/// second table, because the store is where containment is actually defined. Keeping a copy would mean
/// two truths and an unglamorous class of bug where they disagree.
/// </para>
/// <para>
/// Reading it costs one round trip per level, which is why it is cached. Containment barely changes:
/// documents are read constantly and moved between folders rarely. The changelog reader evicts an entry
/// as soon as a parent relation actually changes, so the time-based expiry is a backstop rather than the
/// mechanism.
/// </para>
/// </remarks>
/// <param name="engine">The relationship store.</param>
/// <param name="options">Cache lifetime.</param>
/// <param name="timeProvider">Clock.</param>
public sealed class ContainmentResolver(
    IRelationshipEngine engine,
    IOptions<DecisionOptions> options,
    TimeProvider timeProvider)
{
    private const int MaxDepth = 8;

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly TimeSpan _lifetime = options.Value.ContainmentCacheLifetime;

    /// <summary>Returns the chain from a resource up to its outermost container, the resource first.</summary>
    /// <param name="resource">The resource, as <c>document:handbook</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<string>> GetChainAsync(
        string resource,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);

        DateTimeOffset now = timeProvider.GetUtcNow();

        if (_cache.TryGetValue(resource, out CacheEntry cached) && cached.ExpiresAtUtc > now)
        {
            return cached.Chain;
        }

        List<string> chain = [resource];
        string current = resource;

        for (int depth = 0; depth < MaxDepth; depth++)
        {
            IReadOnlyList<RelationshipTuple> parents = await engine.ReadAsync(
                subject: null,
                relation: AuthorizationModel.Relations.Parent,
                resource: current,
                cancellationToken);

            if (parents.Count is 0)
            {
                break;
            }

            // A resource with more than one parent is not something this domain models. Taking the first
            // keeps the explanation deterministic instead of ordering-dependent.
            current = parents[0].Subject;
            chain.Add(current);
        }

        _cache[resource] = new CacheEntry(chain, now + _lifetime);

        return chain;
    }

    /// <summary>Forgets a cached chain, because the relationship behind it changed.</summary>
    /// <param name="resource">The resource whose parent changed.</param>
    public void Invalidate(string resource) => _cache.TryRemove(resource, out _);

    /// <summary>Forgets everything, for a test or an operator who wants a clean slate.</summary>
    public void Clear() => _cache.Clear();

    private readonly record struct CacheEntry(IReadOnlyList<string> Chain, DateTimeOffset ExpiresAtUtc);
}
