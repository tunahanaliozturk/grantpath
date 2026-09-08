using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GrantPath.Api;
using GrantPath.Api.Auditing;
using GrantPath.Api.Observability;
using GrantPath.Data;
using GrantPath.Rebac;
using GrantPath.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GrantPath.IntegrationTests;

/// <summary>
/// The trail, which is the difference between a system that decides and a system that can be reviewed.
/// </summary>
/// <remarks>
/// Two properties matter. The recorded reasoning has to match what was actually evaluated, not be a
/// plausible reconstruction; and no decision may go unrecorded, including under load, because the trail is
/// read precisely when something unusual has happened.
/// </remarks>
/// <param name="fixture">The running service.</param>
[Collection(GrantPathTestGroup.Name)]
public sealed class AuditTrailTests(GrantPathFixture fixture)
{
    [Fact]
    public async Task The_recorded_reasoning_matches_what_was_evaluated()
    {
        Domain domain = await Domain.CreateAsync(fixture);
        using var client = new AuthzClient(fixture);

        await Domain.GrantAsync(
            fixture,
            new RelationshipTuple(domain.Member, AuthorizationModel.Relations.Member, domain.Org));

        CheckResult result = await client.CheckAsync(domain.Member, "viewer", domain.Document);
        result.Allowed.ShouldBeTrue();

        JsonElement recorded = await WaitForRecordAsync(client, result.RequestId);

        recorded.GetProperty("subject").GetString().ShouldBe(domain.Member);
        recorded.GetProperty("resource").GetString().ShouldBe(domain.Document);
        recorded.GetProperty("relation").GetString().ShouldBe("viewer");
        recorded.GetProperty("decision").GetString().ShouldBe("Allow");
        recorded.GetProperty("modelId").GetString().ShouldBe(result.ModelId);

        // The path in the trail is the path the decision used, level for level, not a fresh evaluation
        // that happens to agree today.
        JsonElement reason = recorded.GetProperty("reason");

        AuthzClient.ReadPath(reason).ShouldBe(AuthzClient.ReadPath(result.Reason));
        reason.GetProperty("grantedAt").GetString().ShouldBe(domain.Org);
    }

    [Fact]
    public async Task A_denial_records_why_it_denied()
    {
        Domain domain = await Domain.CreateAsync(fixture);
        using var client = new AuthzClient(fixture);

        CheckResult result = await client.CheckAsync(domain.Stranger, "viewer", domain.Document);
        result.Allowed.ShouldBeFalse();

        JsonElement recorded = await WaitForRecordAsync(client, result.RequestId);

        recorded.GetProperty("decision").GetString().ShouldBe("Deny");
        recorded.GetProperty("relationshipAllowed").GetBoolean().ShouldBeFalse();

        // Every level, and none of them granting. A trail that recorded only the verdict would leave an
        // auditor unable to tell "nobody granted this" from "the check never ran".
        IReadOnlyList<PathStep> path = AuthzClient.ReadPath(recorded.GetProperty("reason"));

        path.Count.ShouldBe(4);
        path.ShouldAllBe(step => !step.Grants);
    }

    [Fact]
    public async Task Asking_about_a_decision_nobody_made_returns_nothing()
    {
        using var client = new AuthzClient(fixture);

        (await client.WhyAsync(Guid.NewGuid())).ShouldBeNull();
    }

    [Fact]
    public async Task Every_decision_under_a_burst_reaches_the_trail()
    {
        Domain domain = await Domain.CreateAsync(fixture);
        using var client = new AuthzClient(fixture);

        await Domain.GrantAsync(
            fixture,
            new RelationshipTuple(domain.Member, AuthorizationModel.Relations.Member, domain.Org));

        const int Requests = 400;

        Guid[] ids = new Guid[Requests];
        int next = -1;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, 16),
            new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (_, _) =>
            {
                while (true)
                {
                    int index = Interlocked.Increment(ref next);

                    if (index >= Requests)
                    {
                        return;
                    }

                    ids[index] = (await client.CheckAsync(domain.Member, "viewer", domain.Document)).RequestId;
                }
            });

        await using AsyncServiceScope scope = fixture.Services.CreateAsyncScope();
        GrantPathDbContext dbContext = scope.ServiceProvider.GetRequiredService<GrantPathDbContext>();

        long started = Stopwatch.GetTimestamp();
        int found = 0;

        // The writer batches, so the last few records land shortly after the last request returns. The
        // assertion is completeness, not immediacy.
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(15))
        {
            found = await dbContext.Decisions.CountAsync(
                record => ids.Contains(record.RequestId),
                TestContext.Current.CancellationToken);

            if (found == Requests)
            {
                break;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        found.ShouldBe(Requests);
    }

    [Fact]
    public async Task A_full_queue_writes_the_record_inline_rather_than_dropping_it()
    {
        // A writer with room for one record and nothing draining it. The second decision has nowhere to
        // go, which is exactly the condition the fallback exists for.
        var writer = new DecisionAuditWriter(
            fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            fixture.Services.GetRequiredService<GrantPathMetrics>(),
            Options.Create(new AuditOptions { QueueCapacity = 1, BatchSize = 1 }));

        DecisionRecord first = Record();
        DecisionRecord second = Record();

        (await writer.RecordAsync(first, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await writer.RecordAsync(second, TestContext.Current.CancellationToken)).ShouldBeTrue();

        await using AsyncServiceScope scope = fixture.Services.CreateAsyncScope();
        GrantPathDbContext dbContext = scope.ServiceProvider.GetRequiredService<GrantPathDbContext>();

        AuthorizationDecisionRecord? written = await dbContext.Decisions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                record => record.RequestId == second.RequestId,
                TestContext.Current.CancellationToken);

        // Slower, and recorded as such, but present. Dropping would make the trail unreliable exactly
        // when the system is under pressure, which is when it is worth reading.
        written.ShouldNotBeNull();
        written.WrittenSynchronously.ShouldBeTrue();

        // The first one is still sitting in the queue, unwritten, because nothing is draining it. That is
        // the queue doing its job rather than a lost record.
        writer.QueueDepth.ShouldBe(1);
    }

    private static DecisionRecord Record() => new()
    {
        RequestId = Guid.CreateVersion7(),
        Subject = "user:queue-test",
        Resource = "document:queue-test",
        ResourceType = "document",
        Relation = "viewer",
        Decision = AuthorizationDecision.Deny,
        RelationshipAllowed = false,
        AttributeVerdict = "NoOpinion",
        ReasonJson = """{"relationshipPath":[],"summary":"queue test"}""",
        ModelId = "model-under-test",
        LatencyMs = 1,
        DecidedAtUtc = DateTimeOffset.UtcNow,
    };

    private static async Task<JsonElement> WaitForRecordAsync(AuthzClient client, Guid requestId)
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

        throw new TimeoutException($"Decision {requestId} never reached the trail.");
    }
}
