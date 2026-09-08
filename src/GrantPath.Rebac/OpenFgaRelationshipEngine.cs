using OpenFga.Sdk.Client;
using OpenFga.Sdk.Client.Model;
using OpenFga.Sdk.Model;

namespace GrantPath.Rebac;

/// <summary>Where the relationship store lives and which store to use.</summary>
public sealed class RebacOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "GrantPath:Rebac";

    /// <summary>The OpenFGA HTTP endpoint.</summary>
    public string ApiUrl { get; init; } = "http://localhost:8080";

    /// <summary>The store name. One is created on first start if it does not exist.</summary>
    public string StoreName { get; init; } = "grantpath";

    /// <summary>How long to wait for a single call before giving up.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// The only class in this repository that knows what OpenFGA's SDK looks like.
/// </summary>
/// <remarks>
/// <para>
/// It resolves or creates the store, writes the authorization model, and pins every later call to that
/// model id. Pinning matters: without it, adding a relation would silently change the meaning of checks
/// that were already in flight, and a decision recorded in the audit trail could not say which version of
/// the rules produced it.
/// </para>
/// <para>
/// Nothing here caches a verdict. A revoked relationship has to stop granting access on the very next
/// call, and a cache in front of the check is the most natural way to break that promise without noticing.
/// The materialized projection that backs row-level security is a separate, deliberately asymmetric thing
/// and lives elsewhere.
/// </para>
/// </remarks>
public sealed class OpenFgaRelationshipEngine : IRelationshipEngine, IDisposable
{
    private readonly OpenFgaClient _client;

    private OpenFgaRelationshipEngine(OpenFgaClient client, string storeId, string modelId)
    {
        _client = client;
        StoreId = storeId;
        ModelId = modelId;
    }

    /// <inheritdoc />
    public string ModelId { get; }

    /// <summary>The store decisions are made against.</summary>
    public string StoreId { get; }

    /// <summary>
    /// Connects, finds or creates the store, and writes the model.
    /// </summary>
    /// <remarks>
    /// Writing the model on every start is safe and cheap: OpenFGA versions models rather than replacing
    /// them, so an unchanged model produces a new id pointing at identical rules, and a changed one is a
    /// deliberate act with a visible id.
    /// </remarks>
    /// <param name="options">Where to connect.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<OpenFgaRelationshipEngine> ConnectAsync(
        RebacOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var bootstrap = new OpenFgaClient(new ClientConfiguration { ApiUrl = options.ApiUrl });

        string storeId = await ResolveStoreAsync(bootstrap, options.StoreName, cancellationToken);

        var client = new OpenFgaClient(new ClientConfiguration
        {
            ApiUrl = options.ApiUrl,
            StoreId = storeId,
        });

        bootstrap.Dispose();

        WriteAuthorizationModelResponse model = await client.WriteAuthorizationModel(
            new ClientWriteAuthorizationModelRequest
            {
                SchemaVersion = "1.1",
                TypeDefinitions = AuthorizationModel.Build(),
            },
            cancellationToken: cancellationToken);

        client.AuthorizationModelId = model.AuthorizationModelId;

        return new OpenFgaRelationshipEngine(client, storeId, model.AuthorizationModelId);
    }

    /// <inheritdoc />
    public async Task<bool> CheckAsync(RelationshipTuple tuple, CancellationToken cancellationToken)
    {
        CheckResponse response = await _client.Check(
            new ClientCheckRequest
            {
                User = tuple.Subject,
                Relation = tuple.Relation,
                Object = tuple.Resource,
            },
            cancellationToken: cancellationToken);

        return response.Allowed ?? false;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RelationshipAnswer>> BatchCheckAsync(
        IReadOnlyList<RelationshipQuery> queries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queries);

        if (queries.Count is 0)
        {
            return [];
        }

        // One round trip for the whole batch. The alternative, a check per ancestor, turns explaining a
        // decision into four sequential network calls on a path with a millisecond budget.
        ClientBatchCheckResponse response = await _client.BatchCheck(
            new ClientBatchCheckRequest
            {
                Checks =
                [
                    .. queries.Select(query => new ClientBatchCheckItem
                    {
                        User = query.Tuple.Subject,
                        Relation = query.Tuple.Relation,
                        Object = query.Tuple.Resource,
                        CorrelationId = query.CorrelationId,
                    }),
                ],
            },
            cancellationToken: cancellationToken);

        return
        [
            .. response.Result.Select(item => new RelationshipAnswer(
                item.CorrelationId,
                item.Allowed)),
        ];
    }

    /// <inheritdoc />
    public async Task WriteAsync(
        IReadOnlyList<RelationshipTuple> writes,
        IReadOnlyList<RelationshipTuple> deletes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writes);
        ArgumentNullException.ThrowIfNull(deletes);

        if (writes.Count is 0 && deletes.Count is 0)
        {
            return;
        }

        await _client.Write(
            new ClientWriteRequest
            {
                Writes = [.. writes.Select(tuple => new ClientTupleKey
                {
                    User = tuple.Subject,
                    Relation = tuple.Relation,
                    Object = tuple.Resource,
                })],
                Deletes = [.. deletes.Select(tuple => new ClientTupleKeyWithoutCondition
                {
                    User = tuple.Subject,
                    Relation = tuple.Relation,
                    Object = tuple.Resource,
                })],
            },
            cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RelationshipTuple>> ReadAsync(
        string? subject,
        string? relation,
        string? resource,
        CancellationToken cancellationToken)
    {
        ReadResponse response = await _client.Read(
            new ClientReadRequest
            {
                User = subject,
                Relation = relation,
                Object = resource,
            },
            cancellationToken: cancellationToken);

        return
        [
            .. response.Tuples.Select(entry => new RelationshipTuple(
                entry.Key.User,
                entry.Key.Relation,
                entry.Key.Object)),
        ];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListResourcesAsync(
        string subject,
        string relation,
        string resourceType,
        CancellationToken cancellationToken)
    {
        ListObjectsResponse response = await _client.ListObjects(
            new ClientListObjectsRequest
            {
                User = subject,
                Relation = relation,
                Type = resourceType,
            },
            cancellationToken: cancellationToken);

        return response.Objects;
    }

    /// <inheritdoc />
    public async Task<RelationshipChangePage> ReadChangesAsync(
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        ReadChangesResponse response = await _client.ReadChanges(
            new ClientReadChangesRequest(),
            new ClientReadChangesOptions { ContinuationToken = continuationToken, PageSize = 100 },
            cancellationToken);

        List<RelationshipChange> changes =
        [
            .. (response.Changes ?? []).Select(change => new RelationshipChange(
                new RelationshipTuple(change.TupleKey.User, change.TupleKey.Relation, change.TupleKey.Object),
                change.Operation is TupleOperation.TUPLEOPERATIONDELETE
                    ? RelationshipChangeOperation.Delete
                    : RelationshipChangeOperation.Write,
                change.Timestamp)),
        ];

        return new RelationshipChangePage(changes, response.ContinuationToken);
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    private static async Task<string> ResolveStoreAsync(
        OpenFgaClient client,
        string name,
        CancellationToken cancellationToken)
    {
        ListStoresResponse stores = await client.ListStores(
            new ClientListStoresRequest(),
            cancellationToken: cancellationToken);

        foreach (Store store in stores.Stores)
        {
            if (string.Equals(store.Name, name, StringComparison.Ordinal))
            {
                return store.Id;
            }
        }

        CreateStoreResponse created = await client.CreateStore(
            new ClientCreateStoreRequest { Name = name },
            cancellationToken: cancellationToken);

        return created.Id;
    }
}
