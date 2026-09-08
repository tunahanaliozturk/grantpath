using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GrantPath.Rebac;
using GrantPath.TestSupport;

namespace GrantPath.IntegrationTests;

/// <summary>
/// The service authorizes access to its own administration through its own decision path.
/// </summary>
/// <remarks>
/// Not symmetry for its own sake. It means the right to change relationships is itself a relationship:
/// stored in the same place, visible in the same audit trail, and revocable the same way as everything
/// else. An admin surface with a private notion of who counts as an admin is a second permission system,
/// and the second one is never the one anybody audits.
/// </remarks>
/// <param name="fixture">The running service.</param>
[Collection(GrantPathTestGroup.Name)]
public sealed class SelfAdministrationTests(GrantPathFixture fixture)
{
    [Fact]
    public async Task A_caller_who_names_no_subject_is_refused()
    {
        using HttpClient client = fixture.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/admin/tuples",
            new { writes = Array.Empty<object>() },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_subject_without_the_platform_relation_cannot_write_tuples()
    {
        Domain domain = await Domain.CreateAsync(fixture);
        using var client = new AuthzClient(fixture);

        using HttpResponseMessage response = await client.WriteTuplesAsync(
            domain.Stranger,
            writes: [(domain.Stranger, AuthorizationModel.Relations.Admin, domain.Org)]);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // The refusal is itself a decision, with a request id, so the attempt is in the trail alongside
        // everything else rather than only in a log line.
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        Guid requestId = problem.GetProperty("requestId").GetGuid();

        // The trail is written off the request path, so the record lands a moment after the refusal.
        (await WaitForRecordAsync(client, requestId)).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_subject_holding_the_platform_relation_can()
    {
        Domain domain = await Domain.CreateAsync(fixture);
        using var client = new AuthzClient(fixture);

        using HttpResponseMessage response = await client.WriteTuplesAsync(
            domain.Administrator,
            writes: [(domain.Member, AuthorizationModel.Relations.Member, domain.Org)]);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await client.CheckAsync(domain.Member, "viewer", domain.Document)).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Taking_the_platform_relation_away_takes_the_admin_API_with_it()
    {
        Domain domain = await Domain.CreateAsync(fixture);
        using var client = new AuthzClient(fixture);

        (await client.WriteTuplesAsync(
            domain.Administrator,
            writes: [(domain.Member, AuthorizationModel.Relations.Member, domain.Org)]))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        await Domain.RevokeAsync(
            fixture,
            new RelationshipTuple(
                domain.Administrator,
                AuthorizationModel.Relations.Administrator,
                AuthorizationModel.PlatformObject));

        using HttpResponseMessage afterwards = await client.WriteTuplesAsync(
            domain.Administrator,
            writes: [(domain.Blocked, AuthorizationModel.Relations.Member, domain.Org)]);

        // Revoking administration is a tuple delete like any other, and it takes effect on the next call
        // because the admin gate goes through the same uncached decision path as everything else.
        afterwards.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_empty_change_set_is_refused_rather_than_silently_accepted()
    {
        Domain domain = await Domain.CreateAsync(fixture);
        using var client = new AuthzClient(fixture);

        using HttpResponseMessage response = await client.WriteTuplesAsync(domain.Administrator);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static async Task<JsonElement?> WaitForRecordAsync(AuthzClient client, Guid requestId)
    {
        long started = Stopwatch.GetTimestamp();

        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
        {
            if (await client.WhyAsync(requestId) is { } record)
            {
                return record;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return null;
    }
}
