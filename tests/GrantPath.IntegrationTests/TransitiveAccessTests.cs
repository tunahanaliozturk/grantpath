using System.Diagnostics;
using GrantPath.Rebac;
using GrantPath.TestSupport;

namespace GrantPath.IntegrationTests;

/// <summary>
/// The two claims the whole design rests on: nothing is permitted by default, and inheritance works at
/// real depth.
/// </summary>
/// <remarks>
/// These run against a real OpenFGA. Substituting it would mean asserting that a stand-in returns what it
/// was told to, which is a test of the test.
/// </remarks>
/// <param name="fixture">The running service.</param>
[Collection(GrantPathTestGroup.Name)]
public sealed class TransitiveAccessTests(GrantPathFixture fixture)
{
    [Theory]
    [InlineData("viewer")]
    [InlineData("editor")]
    [InlineData("owner")]
    public async Task A_resource_with_no_relationships_denies_every_relation(string relation)
    {
        Domain domain = await Domain.CreateAsync(fixture);
        using var client = new AuthzClient(fixture);

        // The resource exists and sits inside a real hierarchy. What it does not have is anybody related
        // to it, and that has to be enough to refuse, without a policy saying so.
        CheckResult result = await client.CheckAsync(domain.Stranger, relation, domain.Document);

        result.Allowed.ShouldBeFalse();
        result.RelationshipAllowed.ShouldBeFalse();
        result.AttributeVerdict.ShouldBe("NoOpinion");
    }

    [Fact]
    public async Task A_resource_nobody_has_ever_mentioned_denies()
    {
        using var client = new AuthzClient(fixture);

        CheckResult result = await client.CheckAsync(
            "user:nobody",
            "viewer",
            $"document:never-created-{Guid.NewGuid():N}");

        result.Allowed.ShouldBeFalse();
        result.RelationshipAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task One_org_level_tuple_reaches_a_document_four_levels_down()
    {
        Domain domain = await Domain.CreateAsync(fixture);
        using var client = new AuthzClient(fixture);

        (await client.CheckAsync(domain.Member, "viewer", domain.Document)).Allowed.ShouldBeFalse();

        // A single membership tuple at the top. Nothing is written anywhere near the document, and no
        // tuple mentions the workspace, the folder or the document in connection with this subject.
        await Domain.GrantAsync(
            fixture,
            new RelationshipTuple(domain.Member, AuthorizationModel.Relations.Member, domain.Org));

        CheckResult result = await client.CheckAsync(domain.Member, "viewer", domain.Document);

        result.Allowed.ShouldBeTrue();
        result.RelationshipAllowed.ShouldBeTrue();

        // The explanation has to name where the grant came from, otherwise the allow is unfalsifiable.
        IReadOnlyList<PathStep> path = AuthzClient.ReadPath(result.Reason);

        path.Select(step => step.Resource).ShouldBe(
            [domain.Document, domain.Folder, domain.Workspace, domain.Org]);

        path.Single(step => step.Resource == domain.Org).Grants.ShouldBeTrue();
        result.Reason.GetProperty("grantedAt").GetString().ShouldBe(domain.Org);
    }

    [Fact]
    public async Task Inheritance_carries_the_role_rather_than_flattening_it()
    {
        Domain domain = await Domain.CreateAsync(fixture);
        using var client = new AuthzClient(fixture);

        // A plain member of the org may read, and that is all. Being able to read a folder has never
        // implied being able to change what is in it.
        await Domain.GrantAsync(
            fixture,
            new RelationshipTuple(domain.Member, AuthorizationModel.Relations.Member, domain.Org));

        (await client.CheckAsync(domain.Member, "viewer", domain.Document)).Allowed.ShouldBeTrue();
        (await client.CheckAsync(domain.Member, "editor", domain.Document)).Allowed.ShouldBeFalse();
        (await client.CheckAsync(domain.Member, "owner", domain.Document)).Allowed.ShouldBeFalse();

        await Domain.GrantAsync(
            fixture,
            new RelationshipTuple(domain.Blocked, AuthorizationModel.Relations.Admin, domain.Org));

        (await client.CheckAsync(domain.Blocked, "editor", domain.Document)).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task An_explicit_block_beats_everything_it_inherited()
    {
        Domain domain = await Domain.CreateAsync(fixture);
        using var client = new AuthzClient(fixture);

        await Domain.GrantAsync(
            fixture,
            new RelationshipTuple(domain.Blocked, AuthorizationModel.Relations.Member, domain.Org));

        (await client.CheckAsync(domain.Blocked, "viewer", domain.ConfidentialDocument))
            .Allowed.ShouldBeTrue();

        // A relationship graph only ever grants, so the domain's one narrowing rule is a subtraction in
        // the model. Without it, "this person specifically may not see this document" would be
        // inexpressible except by removing their access to everything above it.
        await Domain.GrantAsync(
            fixture,
            new RelationshipTuple(
                domain.Blocked,
                AuthorizationModel.Relations.Blocked,
                domain.ConfidentialDocument));

        (await client.CheckAsync(domain.Blocked, "viewer", domain.ConfidentialDocument))
            .Allowed.ShouldBeFalse();

        // And only that document. The block is not a demotion.
        (await client.CheckAsync(domain.Blocked, "viewer", domain.Document)).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task A_blocked_decision_explains_that_the_grant_did_not_reach_the_document()
    {
        Domain domain = await Domain.CreateAsync(fixture);
        using var client = new AuthzClient(fixture);

        await Domain.GrantAsync(
            fixture,
            new RelationshipTuple(domain.Blocked, AuthorizationModel.Relations.Member, domain.Org),
            new RelationshipTuple(
                domain.Blocked,
                AuthorizationModel.Relations.Blocked,
                domain.ConfidentialDocument));

        CheckResult result = await client.CheckAsync(domain.Blocked, "viewer", domain.ConfidentialDocument);

        result.Allowed.ShouldBeFalse();

        // The org does grant, and naming it as the origin of a permission this subject does not have
        // would read as though something allowed the request. It reports no origin, and says instead
        // that the grant was cut off before it reached the document, which is what actually happened.
        result.Reason.GetProperty("grantedAt").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);

        string summary = result.Reason.GetProperty("summary").GetString()!;
        summary.ShouldContain(domain.Org);
        summary.ShouldContain("does not reach");

        // The per-level detail still shows exactly where it was cut, which is the useful half.
        IReadOnlyList<PathStep> path = AuthzClient.ReadPath(result.Reason);

        path.Single(step => step.Resource == domain.Org).Grants.ShouldBeTrue();
        path.Single(step => step.Resource == domain.ConfidentialDocument).Grants.ShouldBeFalse();
    }

    [Fact]
    public async Task A_full_depth_check_stays_inside_a_sane_latency_budget()
    {
        Domain domain = await Domain.CreateAsync(fixture);
        using var client = new AuthzClient(fixture);

        await Domain.GrantAsync(
            fixture,
            new RelationshipTuple(domain.Member, AuthorizationModel.Relations.Member, domain.Org));

        double[] samples = new double[60];

        for (int index = 0; index < samples.Length; index++)
        {
            long started = Stopwatch.GetTimestamp();
            await client.CheckAsync(domain.Member, "viewer", domain.Document);
            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        Array.Sort(samples);

        // A shared build runner cannot back the tight bound the README publishes, and a test that tried
        // would fail for reasons that have nothing to do with this code. What it can prove is that a
        // four-level resolution has not quietly become a hundred-millisecond operation. The real numbers
        // come from load/GrantPath.LoadTests on known hardware.
        double p95 = samples[(int)(samples.Length * 0.95) - 1];

        p95.ShouldBeLessThan(250);
    }
}
