using System.Net.Http.Json;
using System.Text.Json;

namespace GrantPath.TestSupport;

/// <summary>What the service answered.</summary>
/// <param name="RequestId">The handle for asking why.</param>
/// <param name="Allowed">The combined decision.</param>
/// <param name="RelationshipAllowed">What the relationship graph said alone.</param>
/// <param name="AttributeVerdict">What the attribute layer said alone.</param>
/// <param name="Reason">The recorded reasoning.</param>
/// <param name="LatencyMs">How long it took.</param>
/// <param name="ModelId">Which model decided.</param>
public sealed record CheckResult(
    Guid RequestId,
    bool Allowed,
    bool RelationshipAllowed,
    string AttributeVerdict,
    JsonElement Reason,
    double LatencyMs,
    string ModelId);

/// <summary>One rung of a recorded relationship path.</summary>
/// <param name="Resource">The level.</param>
/// <param name="Relation">The relation asked about there.</param>
/// <param name="Grants">Whether it granted.</param>
public sealed record PathStep(string Resource, string Relation, bool Grants);

/// <summary>
/// Talks to the service over HTTP, the way an application would.
/// </summary>
/// <remarks>
/// Deliberately over the wire rather than against the facade directly. Serialisation, routing and the
/// caller gate are part of what a consumer experiences, and a test that calls the class underneath them
/// proves less than it appears to.
/// </remarks>
/// <param name="fixture">The running service.</param>
public sealed class AuthzClient(GrantPathFixture fixture) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client = fixture.CreateClient();

    /// <summary>Asks one question.</summary>
    /// <param name="subject">The subject.</param>
    /// <param name="relation">The relation.</param>
    /// <param name="resource">The resource.</param>
    /// <param name="context">Attributes, if any.</param>
    public async Task<CheckResult> CheckAsync(
        string subject,
        string relation,
        string resource,
        object? context = null)
    {
        using HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/authz/check",
            new { subject, relation, resource, context },
            Json);

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<CheckResult>(Json))!;
    }

    /// <summary>Reads a decision back out of the trail.</summary>
    /// <param name="requestId">The handle.</param>
    public async Task<JsonElement?> WhyAsync(Guid requestId)
    {
        using HttpResponseMessage response = await _client.GetAsync($"/authz/why/{requestId}");

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    /// <summary>Writes and removes relationships through the admin API.</summary>
    /// <param name="administrator">The subject performing the change.</param>
    /// <param name="writes">Tuples to add.</param>
    /// <param name="deletes">Tuples to remove.</param>
    public async Task<HttpResponseMessage> WriteTuplesAsync(
        string administrator,
        IEnumerable<(string Subject, string Relation, string Resource)>? writes = null,
        IEnumerable<(string Subject, string Relation, string Resource)>? deletes = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/tuples")
        {
            Content = JsonContent.Create(
                new
                {
                    writes = writes?.Select(tuple => new
                    {
                        subject = tuple.Subject,
                        relation = tuple.Relation,
                        resource = tuple.Resource,
                    }),
                    deletes = deletes?.Select(tuple => new
                    {
                        subject = tuple.Subject,
                        relation = tuple.Relation,
                        resource = tuple.Resource,
                    }),
                },
                options: Json),
        };

        request.Headers.Add("X-GrantPath-Subject", administrator);

        return await _client.SendAsync(request);
    }

    /// <summary>Creates an attribute policy through the admin API.</summary>
    /// <param name="administrator">The subject performing the change.</param>
    /// <param name="name">Policy name.</param>
    /// <param name="resourceType">Which resource type.</param>
    /// <param name="effect">Deny or Allow.</param>
    /// <param name="priority">Priority.</param>
    /// <param name="conditionJson">The condition tree.</param>
    public async Task<HttpResponseMessage> CreatePolicyAsync(
        string administrator,
        string name,
        string resourceType,
        string effect,
        int priority,
        string conditionJson)
    {
        using JsonDocument condition = JsonDocument.Parse(conditionJson);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/policies")
        {
            Content = JsonContent.Create(
                new
                {
                    name,
                    resourceType,
                    effect,
                    priority,
                    condition = condition.RootElement,
                },
                options: Json),
        };

        request.Headers.Add("X-GrantPath-Subject", administrator);

        return await _client.SendAsync(request);
    }

    /// <summary>Deletes an attribute policy.</summary>
    /// <param name="administrator">The subject performing the change.</param>
    /// <param name="id">Policy identifier.</param>
    public async Task<HttpResponseMessage> DeletePolicyAsync(string administrator, Guid id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/admin/policies/{id}");
        request.Headers.Add("X-GrantPath-Subject", administrator);

        return await _client.SendAsync(request);
    }

    /// <summary>Reads the documents the database is willing to return for a subject.</summary>
    /// <param name="subject">The subject the query runs as.</param>
    public async Task<IReadOnlyList<string>> ReadVisibleDocumentsAsync(string subject)
    {
        using HttpResponseMessage response = await _client.GetAsync(
            $"/demo/documents?subject={Uri.EscapeDataString(subject)}");

        response.EnsureSuccessStatusCode();

        JsonElement documents = await response.Content.ReadFromJsonAsync<JsonElement>(Json);

        return [.. documents.EnumerateArray().Select(entry => entry.GetProperty("id").GetString()!)];
    }

    /// <summary>Reads the relationship path out of a decision's reasoning.</summary>
    /// <param name="reason">The reason document.</param>
    public static IReadOnlyList<PathStep> ReadPath(JsonElement reason) =>
    [
        .. reason.GetProperty("relationshipPath").EnumerateArray().Select(step => new PathStep(
            step.GetProperty("resource").GetString()!,
            step.GetProperty("relation").GetString()!,
            step.GetProperty("grants").GetBoolean())),
    ];

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();
}
