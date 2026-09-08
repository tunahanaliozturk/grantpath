using GrantPath.Api.Security;
using GrantPath.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GrantPath.Api.Endpoints;

/// <summary>A document as the database was willing to return it.</summary>
/// <param name="Id">The document.</param>
/// <param name="FolderId">Which folder holds it.</param>
/// <param name="Title">Title.</param>
/// <param name="Classification">Classification, which the attribute policies read.</param>
public sealed record DocumentView(string Id, string FolderId, string Title, string Classification);

/// <summary>
/// A content endpoint that goes nowhere near the decision path.
/// </summary>
/// <remarks>
/// <para>
/// This exists to prove a point rather than to be a product. It reads the documents table directly, with
/// no check, no facade and no filtering in application code. What bounds the result is a row-level
/// security policy on the table itself, reading the flattened projection.
/// </para>
/// <para>
/// That is the defence-in-depth argument made concrete: a reporting job, an ad hoc query, or a bug that
/// forgets to call the authorization service still cannot read what the subject is not entitled to. The
/// only thing this endpoint does is tell the database which subject it is acting as.
/// </para>
/// </remarks>
public static class DemoEndpoints
{
    /// <summary>Maps the demonstration endpoints.</summary>
    /// <param name="routes">Route builder.</param>
    public static IEndpointRouteBuilder MapDemoEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/demo/documents", ListAsync)
            .WithName("ListDocuments")
            .WithTags("Demonstration")
            .AddEndpointFilter<CallerKeyFilter>();

        return routes;
    }

    private static async Task<IResult> ListAsync(
        string subject,
        GrantPathDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["subject"] = ["Name the subject this query is running as."],
            });
        }

        // set_config with the local flag scopes the setting to this transaction, so one request cannot
        // leak its identity into the next one that happens to reuse the pooled connection.
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Row-level security does not apply to a superuser, and a service that connects as one has
        // policies that look right and enforce nothing. Switching to a role with neither superuser nor
        // BYPASSRLS for the duration of the transaction is what makes the policy real. The role is
        // created by the migration that creates the policy.
        await dbContext.Database.ExecuteSqlRawAsync(
            $"SET LOCAL ROLE {GrantPathDbContext.ReaderRole}",
            cancellationToken);

        await dbContext.Database.ExecuteSqlAsync(
            $"SELECT set_config({GrantPathDbContext.SubjectSetting}, {subject}, true)",
            cancellationToken);

        List<DocumentView> documents = await dbContext.Documents
            .AsNoTracking()
            .OrderBy(document => document.Id)
            .Select(document => new DocumentView(
                document.Id,
                document.FolderId,
                document.Title,
                document.Classification))
            .ToListAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return Results.Ok(documents);
    }
}
