using System.Text.Json;
using GrantPath.Abac;
using GrantPath.Api.Authorization;
using GrantPath.Api.Projection;
using GrantPath.Api.Security;
using GrantPath.Data;
using GrantPath.Rebac;
using Microsoft.EntityFrameworkCore;

namespace GrantPath.Api.Endpoints;

/// <summary>A relationship to write or remove.</summary>
/// <param name="Subject">Who, as <c>user:alice</c>.</param>
/// <param name="Relation">What, as <c>member</c>.</param>
/// <param name="Resource">Where, as <c>org:acme</c>.</param>
public sealed record TupleRequest(string Subject, string Relation, string Resource);

/// <summary>Several relationships in one transaction.</summary>
/// <param name="Writes">Relationships to add.</param>
/// <param name="Deletes">Relationships to remove.</param>
public sealed record TupleBatchRequest(
    IReadOnlyList<TupleRequest>? Writes = null,
    IReadOnlyList<TupleRequest>? Deletes = null);

/// <summary>An attribute policy as submitted.</summary>
/// <param name="Name">A name a person can recognise.</param>
/// <param name="ResourceType">Which resource type it applies to.</param>
/// <param name="Effect">Deny or Allow.</param>
/// <param name="Priority">Higher wins; ties go to deny.</param>
/// <param name="Condition">The condition tree.</param>
public sealed record PolicyRequest(
    string Name,
    string ResourceType,
    string Effect,
    int Priority,
    JsonElement Condition);

/// <summary>A stored policy as returned.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="Name">Name.</param>
/// <param name="ResourceType">Resource type.</param>
/// <param name="Effect">Deny or Allow.</param>
/// <param name="Priority">Priority.</param>
/// <param name="Condition">The condition tree.</param>
/// <param name="CreatedAtUtc">When it was created.</param>
/// <param name="UpdatedAtUtc">When it last changed.</param>
public sealed record PolicyView(
    Guid Id,
    string Name,
    string ResourceType,
    string Effect,
    int Priority,
    JsonElement Condition,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Relationship and policy administration.
/// </summary>
/// <remarks>
/// Every route here is behind the service's own decision path: the caller must hold
/// <c>administrator</c> on the platform object, which is itself a relationship in the same store. Changing
/// who can administer the system is therefore a tuple write like any other, recorded in the same audit
/// trail and revocable the same way.
/// </remarks>
public static class AdminEndpoints
{
    /// <summary>Maps the administrative endpoints.</summary>
    /// <param name="routes">Route builder.</param>
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        RouteGroupBuilder group = routes.MapGroup("/admin")
            .WithTags("Administration")
            .AddEndpointFilter<CallerKeyFilter>()
            .AddEndpointFilter<PlatformAdminFilter>();

        group.MapGet("/tuples", ReadTuplesAsync).WithName("ReadTuples");
        group.MapPost("/tuples", WriteTuplesAsync).WithName("WriteTuples");

        group.MapGet("/policies", ListPoliciesAsync).WithName("ListPolicies");
        group.MapPost("/policies", CreatePolicyAsync).WithName("CreatePolicy");
        group.MapPut("/policies/{id:guid}", UpdatePolicyAsync).WithName("UpdatePolicy");
        group.MapDelete("/policies/{id:guid}", DeletePolicyAsync).WithName("DeletePolicy");

        return routes;
    }

    private static async Task<IResult> ReadTuplesAsync(
        IRelationshipEngine engine,
        CancellationToken cancellationToken,
        string? subject = null,
        string? relation = null,
        string? resource = null)
    {
        IReadOnlyList<RelationshipTuple> tuples = await engine.ReadAsync(
            subject,
            relation,
            resource,
            cancellationToken);

        return Results.Ok(tuples.Select(tuple => new TupleRequest(
            tuple.Subject,
            tuple.Relation,
            tuple.Resource)));
    }

    /// <summary>
    /// Writes and removes relationships.
    /// </summary>
    /// <remarks>
    /// The projection rows for every subject named in a delete are removed here, in the same request,
    /// rather than waiting for the changelog reader. That is the asymmetry the design depends on: a grant
    /// may take a moment to show up in the database-side projection, and a revocation may not.
    /// </remarks>
    private static async Task<IResult> WriteTuplesAsync(
        TupleBatchRequest request,
        IRelationshipEngine engine,
        GrantPathDbContext dbContext,
        PermissionProjection projection,
        ContainmentResolver containment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<TupleRequest> writes = request.Writes ?? [];
        IReadOnlyList<TupleRequest> deletes = request.Deletes ?? [];

        if (writes.Count is 0 && deletes.Count is 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["request"] = ["Send at least one write or one delete."],
            });
        }

        await engine.WriteAsync(
            [.. writes.Select(Convert)],
            [.. deletes.Select(Convert)],
            cancellationToken);

        foreach (TupleRequest tuple in deletes)
        {
            if (tuple.Relation is AuthorizationModel.Relations.Parent)
            {
                containment.Invalidate(tuple.Resource);
            }

            if (tuple.Subject.StartsWith("user:", StringComparison.Ordinal))
            {
                await projection.RevokeSubjectAsync(dbContext, tuple.Subject, cancellationToken);
            }
        }

        foreach (TupleRequest tuple in writes)
        {
            if (tuple.Relation is AuthorizationModel.Relations.Parent)
            {
                containment.Invalidate(tuple.Resource);
            }
        }

        return Results.Ok(new { written = writes.Count, deleted = deletes.Count });
    }

    private static async Task<IResult> ListPoliciesAsync(
        GrantPathDbContext dbContext,
        CancellationToken cancellationToken,
        string? resourceType = null)
    {
        IQueryable<AbacPolicyRecord> query = dbContext.AbacPolicies.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(resourceType))
        {
            query = query.Where(policy => policy.ResourceType == resourceType);
        }

        List<AbacPolicyRecord> records = await query
            .OrderByDescending(policy => policy.Priority)
            .ToListAsync(cancellationToken);

        return Results.Ok(records.Select(ToView));
    }

    private static async Task<IResult> CreatePolicyAsync(
        PolicyRequest request,
        GrantPathDbContext dbContext,
        PolicyCache cache,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Validate(request) is { } problem)
        {
            return problem;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        var record = new AbacPolicyRecord
        {
            Id = Guid.CreateVersion7(now),
            Name = request.Name,
            ResourceType = request.ResourceType,
            Effect = ParseEffect(request.Effect),
            Priority = request.Priority,
            ConditionJson = request.Condition.GetRawText(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        dbContext.AbacPolicies.Add(record);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Refreshed here rather than left to the interval, so the instance that accepted the write is
        // never the one still deciding against the previous policy set.
        await cache.RefreshAsync(cancellationToken);

        return Results.Created($"/admin/policies/{record.Id}", ToView(record));
    }

    private static async Task<IResult> UpdatePolicyAsync(
        Guid id,
        PolicyRequest request,
        GrantPathDbContext dbContext,
        PolicyCache cache,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Validate(request) is { } problem)
        {
            return problem;
        }

        AbacPolicyRecord? record = await dbContext.AbacPolicies
            .FirstOrDefaultAsync(policy => policy.Id == id, cancellationToken);

        if (record is null)
        {
            return Results.NotFound();
        }

        record.Name = request.Name;
        record.ResourceType = request.ResourceType;
        record.Effect = ParseEffect(request.Effect);
        record.Priority = request.Priority;
        record.ConditionJson = request.Condition.GetRawText();
        record.UpdatedAtUtc = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);
        await cache.RefreshAsync(cancellationToken);

        return Results.Ok(ToView(record));
    }

    private static async Task<IResult> DeletePolicyAsync(
        Guid id,
        GrantPathDbContext dbContext,
        PolicyCache cache,
        CancellationToken cancellationToken)
    {
        int removed = await dbContext.AbacPolicies
            .Where(policy => policy.Id == id)
            .ExecuteDeleteAsync(cancellationToken);

        if (removed is 0)
        {
            return Results.NotFound();
        }

        await cache.RefreshAsync(cancellationToken);

        return Results.NoContent();
    }

    private static IResult? Validate(PolicyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.ResourceType))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["policy"] = ["name and resourceType are required."],
            });
        }

        if (!Enum.TryParse(request.Effect, ignoreCase: true, out StoredPolicyEffect _))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["effect"] = ["Effect must be Allow or Deny."],
            });
        }

        try
        {
            // Compiled before it is stored. A policy that cannot be parsed would otherwise sit in the
            // table until the next decision tried to load it, and take the whole policy set down with it.
            _ = ConditionNode.Parse(request.Condition);
        }
        catch (FormatException exception)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["condition"] = [exception.Message],
            });
        }

        return null;
    }

    private static StoredPolicyEffect ParseEffect(string effect) =>
        Enum.Parse<StoredPolicyEffect>(effect, ignoreCase: true);

    private static RelationshipTuple Convert(TupleRequest tuple) =>
        new(tuple.Subject, tuple.Relation, tuple.Resource);

    private static PolicyView ToView(AbacPolicyRecord record) =>
        new(
            record.Id,
            record.Name,
            record.ResourceType,
            record.Effect.ToString(),
            record.Priority,
            JsonDocument.Parse(record.ConditionJson).RootElement.Clone(),
            record.CreatedAtUtc,
            record.UpdatedAtUtc);
}
