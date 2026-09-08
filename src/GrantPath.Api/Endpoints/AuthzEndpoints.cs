using System.Text.Json;
using GrantPath.Api.Authorization;
using GrantPath.Api.Security;
using GrantPath.Data;
using Microsoft.EntityFrameworkCore;

namespace GrantPath.Api.Endpoints;

/// <summary>One authorization question.</summary>
/// <param name="Subject">The already-authenticated subject, as <c>user:alice</c>.</param>
/// <param name="Relation">What is being asked, as <c>viewer</c>.</param>
/// <param name="Resource">What it is being asked about, as <c>document:handbook</c>.</param>
/// <param name="Context">Attributes accompanying the question.</param>
public sealed record CheckRequest(
    string Subject,
    string Relation,
    string Resource,
    DecisionContext? Context = null);

/// <summary>The answer, plus the handle to ask about it later.</summary>
/// <param name="RequestId">Quote this to <c>/authz/why</c>.</param>
/// <param name="Allowed">The combined decision.</param>
/// <param name="RelationshipAllowed">What the relationship graph said alone.</param>
/// <param name="AttributeVerdict">What the attribute layer said alone.</param>
/// <param name="Reason">Why.</param>
/// <param name="LatencyMs">How long it took.</param>
/// <param name="ModelId">Which model version decided.</param>
public sealed record CheckResponse(
    Guid RequestId,
    bool Allowed,
    bool RelationshipAllowed,
    string AttributeVerdict,
    DecisionReason Reason,
    double LatencyMs,
    string ModelId);

/// <summary>A decision as the trail recorded it.</summary>
/// <param name="RequestId">The handle.</param>
/// <param name="Subject">Who asked.</param>
/// <param name="Resource">What about.</param>
/// <param name="Relation">Which relation.</param>
/// <param name="Decision">The answer.</param>
/// <param name="RelationshipAllowed">What the relationship graph said alone.</param>
/// <param name="AttributeVerdict">What the attribute layer said alone.</param>
/// <param name="Reason">The reasoning, exactly as recorded at the time.</param>
/// <param name="ModelId">Which model version decided.</param>
/// <param name="LatencyMs">How long it took.</param>
/// <param name="WrittenSynchronously">Whether the queue was full and the record was written inline.</param>
/// <param name="DecidedAtUtc">When.</param>
public sealed record DecisionView(
    Guid RequestId,
    string Subject,
    string Resource,
    string Relation,
    string Decision,
    bool RelationshipAllowed,
    string AttributeVerdict,
    JsonElement Reason,
    string ModelId,
    double LatencyMs,
    bool WrittenSynchronously,
    DateTimeOffset DecidedAtUtc);

/// <summary>The decision endpoints.</summary>
public static class AuthzEndpoints
{
    private const int MaxPageSize = 200;

    /// <summary>Maps the decision endpoints.</summary>
    /// <param name="routes">Route builder.</param>
    public static IEndpointRouteBuilder MapAuthzEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        RouteGroupBuilder group = routes.MapGroup("/authz")
            .WithTags("Authorization")
            .AddEndpointFilter<CallerKeyFilter>();

        group.MapPost("/check", CheckAsync).WithName("Check");
        group.MapGet("/why/{requestId:guid}", WhyAsync).WithName("Why");
        group.MapGet("/decisions", ListAsync).WithName("ListDecisions");

        return routes;
    }

    /// <summary>Answers one question and records the answer.</summary>
    private static async Task<IResult> CheckAsync(
        CheckRequest request,
        AuthorizationFacade facade,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Subject)
            || string.IsNullOrWhiteSpace(request.Relation)
            || string.IsNullOrWhiteSpace(request.Resource))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["request"] = ["subject, relation and resource are all required."],
            });
        }

        AuthorizationOutcome outcome = await facade.CheckAsync(
            request.Subject,
            request.Relation,
            request.Resource,
            request.Context,
            cancellationToken);

        return Results.Ok(new CheckResponse(
            outcome.RequestId,
            outcome.Allowed,
            outcome.RelationshipAllowed,
            outcome.AttributeVerdict.ToString(),
            outcome.Reason,
            outcome.LatencyMs,
            outcome.ModelId));
    }

    /// <summary>
    /// Reads back why a past decision came out the way it did.
    /// </summary>
    /// <remarks>
    /// It reads the record and nothing else. Re-running the rules would answer a different question, one
    /// about today's tuples and today's policies, and it would answer it confidently while being wrong
    /// about the past in exactly the cases somebody is asking.
    /// </remarks>
    private static async Task<IResult> WhyAsync(
        Guid requestId,
        GrantPathDbContext dbContext,
        CancellationToken cancellationToken)
    {
        AuthorizationDecisionRecord? record = await dbContext.Decisions
            .AsNoTracking()
            .FirstOrDefaultAsync(entry => entry.RequestId == requestId, cancellationToken);

        return record is null ? Results.NotFound() : Results.Ok(ToView(record));
    }

    /// <summary>The trail, newest first, filtered the way an auditor asks for it.</summary>
    private static async Task<IResult> ListAsync(
        GrantPathDbContext dbContext,
        CancellationToken cancellationToken,
        string? subject = null,
        string? resource = null,
        string? decision = null,
        DateTimeOffset? before = null,
        int limit = 50)
    {
        IQueryable<AuthorizationDecisionRecord> query = dbContext.Decisions.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(subject))
        {
            query = query.Where(entry => entry.Subject == subject);
        }

        if (!string.IsNullOrWhiteSpace(resource))
        {
            query = query.Where(entry => entry.Resource == resource);
        }

        if (Enum.TryParse(decision, ignoreCase: true, out AuthorizationDecision parsed))
        {
            query = query.Where(entry => entry.Decision == parsed);
        }

        if (before is { } cursor)
        {
            // Paged by timestamp rather than offset. An audit table only grows, and OFFSET 40000 makes the
            // database walk forty thousand rows to throw them away.
            query = query.Where(entry => entry.DecidedAtUtc < cursor);
        }

        List<AuthorizationDecisionRecord> records = await query
            .OrderByDescending(entry => entry.DecidedAtUtc)
            .Take(Math.Clamp(limit, 1, MaxPageSize))
            .ToListAsync(cancellationToken);

        return Results.Ok(records.Select(ToView));
    }

    private static DecisionView ToView(AuthorizationDecisionRecord record) =>
        new(
            record.RequestId,
            record.Subject,
            record.Resource,
            record.Relation,
            record.Decision.ToString(),
            record.RelationshipAllowed,
            record.AttributeVerdict,
            JsonDocument.Parse(record.ReasonJson).RootElement.Clone(),
            record.ModelId,
            record.LatencyMs,
            record.WrittenSynchronously,
            record.DecidedAtUtc);
}
