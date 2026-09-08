using System.Diagnostics;
using System.Net;
using GrantPath.Rebac;
using GrantPath.TestSupport;

namespace GrantPath.IntegrationTests;

/// <summary>
/// What happens when access is taken away, at both layers.
/// </summary>
/// <remarks>
/// <para>
/// The relationship store is the source of truth and has no cache in front of it, so a revocation applies
/// to the very next decision. The database-side projection is a copy, and a copy is allowed to lag, but
/// only in the safe direction: it may be a moment behind on a grant and must never be behind on a
/// revocation.
/// </para>
/// <para>
/// That asymmetry is the interesting property here, and it is why the revoke path removes rows in the
/// same request rather than leaving it to the poller.
/// </para>
/// </remarks>
/// <param name="fixture">The running service.</param>
[Collection(GrantPathTestGroup.Name)]
public sealed class RevocationTests(GrantPathFixture fixture)
{
    private static readonly TimeSpan ConvergenceBound = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task A_revoked_relationship_is_refused_on_the_very_next_check()
    {
        Domain domain = await Domain.CreateAsync(fixture);
        using var client = new AuthzClient(fixture);

        var membership = new RelationshipTuple(
            domain.Member,
            AuthorizationModel.Relations.Member,
            domain.Org);

        await Domain.GrantAsync(fixture, membership);
        (await client.CheckAsync(domain.Member, "viewer", domain.Document)).Allowed.ShouldBeTrue();

        await Domain.RevokeAsync(fixture, membership);

        // No sleep, no retry, no polling. The decision path holds no cache of its own precisely so that
        // this assertion can be written without one.
        (await client.CheckAsync(domain.Member, "viewer", domain.Document)).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task The_database_path_sees_a_grant_within_the_convergence_bound()
    {
        Domain domain = await Domain.CreateAsync(fixture);
        using var client = new AuthzClient(fixture);

        (await client.ReadVisibleDocumentsAsync(domain.Member)).ShouldBeEmpty();

        await client.WriteTuplesAsync(
            domain.Administrator,
            writes: [(domain.Member, AuthorizationModel.Relations.Member, domain.Org)]);

        IReadOnlyList<string> visible = await WaitForAsync(
            client,
            domain.Member,
            documents => documents.Count is 2);

        // Both documents, reached with no direct tuple on either, through a query that never called the
        // authorization service.
        visible.ShouldBe([domain.ConfidentialDocument, domain.Document], ignoreOrder: true);
    }

    [Fact]
    public async Task The_database_path_loses_access_the_moment_it_is_revoked()
    {
        Domain domain = await Domain.CreateAsync(fixture);
        using var client = new AuthzClient(fixture);

        await client.WriteTuplesAsync(
            domain.Administrator,
            writes: [(domain.Member, AuthorizationModel.Relations.Member, domain.Org)]);

        await WaitForAsync(client, domain.Member, documents => documents.Count is 2);

        using HttpResponseMessage revoke = await client.WriteTuplesAsync(
            domain.Administrator,
            deletes: [(domain.Member, AuthorizationModel.Relations.Member, domain.Org)]);

        revoke.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Immediately, with no wait. The revoke removed the projection rows in the same request, which is
        // the one direction this copy is not allowed to lag in.
        (await client.ReadVisibleDocumentsAsync(domain.Member)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_subject_the_projection_never_heard_of_sees_nothing()
    {
        Domain domain = await Domain.CreateAsync(fixture);
        using var client = new AuthzClient(fixture);

        // Deny by default, enforced by the database rather than by the application. The policy reads a
        // session setting that names no rows, so the query returns none.
        (await client.ReadVisibleDocumentsAsync(domain.Stranger)).ShouldBeEmpty();
    }

    private static async Task<IReadOnlyList<string>> WaitForAsync(
        AuthzClient client,
        string subject,
        Func<IReadOnlyList<string>, bool> condition)
    {
        long started = Stopwatch.GetTimestamp();

        while (Stopwatch.GetElapsedTime(started) < ConvergenceBound)
        {
            IReadOnlyList<string> documents = await client.ReadVisibleDocumentsAsync(subject);

            if (condition(documents))
            {
                return documents;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException(
            $"The projection did not converge for {subject} within {ConvergenceBound.TotalSeconds} seconds.");
    }
}
