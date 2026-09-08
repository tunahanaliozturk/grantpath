using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GrantPath.Rebac;
using GrantPath.TestSupport;

namespace GrantPath.IntegrationTests;

/// <summary>
/// The rule that keeps two engines from becoming two permission systems.
/// </summary>
/// <remarks>
/// <para>
/// Relationships grant; attributes only ever take away. The four cases below are the whole contract, and
/// the one that matters most is the third: an attribute policy that says allow, against a subject with no
/// relationship at all, still denies. Without that property an attribute policy would be a second and
/// much quieter way to grant access, and the audit story would have to cover both.
/// </para>
/// </remarks>
/// <param name="fixture">The running service.</param>
[Collection(GrantPathTestGroup.Name)]
public sealed class NarrowingTests(GrantPathFixture fixture)
{
    private const string ConfidentialAndUncleared = """
        {
          "allOf": [
            { "attribute": "resource.classification", "operator": "equals", "value": "confidential" },
            { "not": { "attribute": "subject.clearance", "operator": "in", "value": ["secret", "top-secret"] } }
          ]
        }
        """;

    [Fact]
    public async Task A_relationship_and_no_objection_allows()
    {
        (Domain domain, AuthzClient client, Guid policyId) = await ArrangeAsync(grantMembership: true);

        try
        {
            CheckResult result = await client.CheckAsync(
                domain.Member,
                "viewer",
                domain.ConfidentialDocument,
                Context("confidential", "secret"));

            result.RelationshipAllowed.ShouldBeTrue();
            result.AttributeVerdict.ShouldBe("NoOpinion");
            result.Allowed.ShouldBeTrue();
        }
        finally
        {
            await CleanUpAsync(client, domain, policyId);
        }
    }

    [Fact]
    public async Task A_relationship_that_a_policy_objects_to_denies()
    {
        (Domain domain, AuthzClient client, Guid policyId) = await ArrangeAsync(grantMembership: true);

        try
        {
            CheckResult result = await client.CheckAsync(
                domain.Member,
                "viewer",
                domain.ConfidentialDocument,
                Context("confidential", "public"));

            result.RelationshipAllowed.ShouldBeTrue();
            result.AttributeVerdict.ShouldBe("Deny");
            result.Allowed.ShouldBeFalse();

            result.Reason.GetProperty("decidingPolicy").GetProperty("id").GetGuid().ShouldBe(policyId);
        }
        finally
        {
            await CleanUpAsync(client, domain, policyId);
        }
    }

    [Fact]
    public async Task A_policy_that_raises_no_objection_cannot_manufacture_access()
    {
        (Domain domain, AuthzClient client, Guid policyId) = await ArrangeAsync(grantMembership: false);

        try
        {
            using HttpResponseMessage created = await client.CreatePolicyAsync(
                domain.Administrator,
                "Anything goes",
                "document",
                "Allow",
                priority: 1000,
                """{ "attribute": "resource.classification", "operator": "exists" }""");

            created.StatusCode.ShouldBe(HttpStatusCode.Created);
            Guid permissiveId = await ReadIdAsync(created);

            try
            {
                CheckResult result = await client.CheckAsync(
                    domain.Stranger,
                    "viewer",
                    domain.ConfidentialDocument,
                    Context("confidential", "top-secret"));

                // The attribute layer said allow, loudly and at the highest priority. It changes nothing,
                // because there is no relationship for it to narrow.
                result.AttributeVerdict.ShouldBe("Allow");
                result.RelationshipAllowed.ShouldBeFalse();
                result.Allowed.ShouldBeFalse();
            }
            finally
            {
                await client.DeletePolicyAsync(domain.Administrator, permissiveId);
            }
        }
        finally
        {
            await CleanUpAsync(client, domain, policyId);
        }
    }

    [Fact]
    public async Task No_relationship_and_an_objection_denies()
    {
        (Domain domain, AuthzClient client, Guid policyId) = await ArrangeAsync(grantMembership: false);

        try
        {
            CheckResult result = await client.CheckAsync(
                domain.Stranger,
                "viewer",
                domain.ConfidentialDocument,
                Context("confidential", "public"));

            result.RelationshipAllowed.ShouldBeFalse();
            result.AttributeVerdict.ShouldBe("Deny");
            result.Allowed.ShouldBeFalse();
        }
        finally
        {
            await CleanUpAsync(client, domain, policyId);
        }
    }

    [Fact]
    public async Task A_policy_whose_condition_cannot_be_parsed_is_refused_when_it_is_written()
    {
        Domain domain = await Domain.CreateAsync(fixture);
        using var client = new AuthzClient(fixture);

        using HttpResponseMessage response = await client.CreatePolicyAsync(
            domain.Administrator,
            "Nonsense",
            "document",
            "Deny",
            priority: 1,
            """{ "attribute": "classification", "operator": "equals", "value": "x" }""");

        // Caught at write time. A policy that only fails when a decision loads it takes every policy for
        // that resource type down with it, at the worst possible moment.
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static object Context(string classification, string clearance) => new
    {
        subject = new { clearance },
        resource = new { classification },
    };

    private async Task<(Domain Domain, AuthzClient Client, Guid PolicyId)> ArrangeAsync(bool grantMembership)
    {
        Domain domain = await Domain.CreateAsync(fixture);
        var client = new AuthzClient(fixture);

        if (grantMembership)
        {
            await Domain.GrantAsync(
                fixture,
                new RelationshipTuple(domain.Member, AuthorizationModel.Relations.Member, domain.Org));
        }

        using HttpResponseMessage created = await client.CreatePolicyAsync(
            domain.Administrator,
            "Confidential documents need clearance",
            "document",
            "Deny",
            priority: 100,
            ConfidentialAndUncleared);

        created.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (domain, client, await ReadIdAsync(created));
    }

    private static async Task<Guid> ReadIdAsync(HttpResponseMessage response)
    {
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        return body.GetProperty("id").GetGuid();
    }

    private static async Task CleanUpAsync(AuthzClient client, Domain domain, Guid policyId)
    {
        // Policies are global to a resource type, so a test that leaves one behind changes the answer for
        // every test that runs after it. Unlike tuples, they cannot be isolated by naming.
        using HttpResponseMessage response = await client.DeletePolicyAsync(domain.Administrator, policyId);
        client.Dispose();
    }
}
