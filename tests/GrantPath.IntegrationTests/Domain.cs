using GrantPath.Data;
using GrantPath.Rebac;
using GrantPath.TestSupport;

namespace GrantPath.IntegrationTests;

/// <summary>
/// A four-level hierarchy with identifiers nothing else uses.
/// </summary>
/// <remarks>
/// The suite shares one relationship store, because starting OpenFGA per test would dominate the run
/// time. Isolation therefore comes from naming: every test builds its own org, workspace, folder and
/// documents under a unique suffix, so no test can see, or disturb, another test's tuples.
/// </remarks>
public sealed class Domain
{
    private Domain(string suffix)
    {
        Org = $"org:acme-{suffix}";
        Workspace = $"workspace:engineering-{suffix}";
        Folder = $"folder:handbooks-{suffix}";
        Document = $"document:onboarding-{suffix}";
        ConfidentialDocument = $"document:incident-{suffix}";
        Member = $"user:alice-{suffix}";
        Blocked = $"user:bob-{suffix}";
        Stranger = $"user:mallory-{suffix}";
        Administrator = $"user:root-{suffix}";
    }

    /// <summary>The top of the hierarchy.</summary>
    public string Org { get; }

    /// <summary>Inside the org.</summary>
    public string Workspace { get; }

    /// <summary>Inside the workspace.</summary>
    public string Folder { get; }

    /// <summary>Inside the folder, four levels below the org.</summary>
    public string Document { get; }

    /// <summary>A second document, classified.</summary>
    public string ConfidentialDocument { get; }

    /// <summary>Holds one org-level tuple and nothing else.</summary>
    public string Member { get; }

    /// <summary>Inherits access and is then explicitly excluded from one document.</summary>
    public string Blocked { get; }

    /// <summary>Related to nothing at all.</summary>
    public string Stranger { get; }

    /// <summary>May use the admin API.</summary>
    public string Administrator { get; }

    /// <summary>Builds the containment chain, with no membership tuples at all.</summary>
    /// <param name="fixture">The running service.</param>
    public static async Task<Domain> CreateAsync(GrantPathFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var domain = new Domain(Guid.NewGuid().ToString("N")[..8]);
        IRelationshipEngine engine = fixture.Services.GetRequiredService<IRelationshipEngine>();

        // Containment only. Nobody is a member of anything yet, which is what makes the deny-by-default
        // assertions mean something: the resources exist and are related to each other, and to no one.
        await engine.WriteAsync(
            [
                new RelationshipTuple(domain.Org, AuthorizationModel.Relations.Parent, domain.Workspace),
                new RelationshipTuple(domain.Workspace, AuthorizationModel.Relations.Parent, domain.Folder),
                new RelationshipTuple(domain.Folder, AuthorizationModel.Relations.Parent, domain.Document),
                new RelationshipTuple(
                    domain.Folder,
                    AuthorizationModel.Relations.Parent,
                    domain.ConfidentialDocument),
                new RelationshipTuple(
                    domain.Administrator,
                    AuthorizationModel.Relations.Administrator,
                    AuthorizationModel.PlatformObject),
            ],
            [],
            TestContext.Current.CancellationToken);

        // The content rows exist so the row-level security path has something real to protect. They are
        // written directly, with no check, which is the point: nothing about inserting a document consults
        // the authorization service.
        await using AsyncServiceScope scope = fixture.Services.CreateAsyncScope();
        GrantPathDbContext dbContext = scope.ServiceProvider.GetRequiredService<GrantPathDbContext>();

        dbContext.Documents.AddRange(
            new Document
            {
                Id = domain.Document,
                FolderId = domain.Folder,
                Title = "Onboarding handbook",
                Classification = "internal",
                CreatedAtUtc = DateTimeOffset.UtcNow,
            },
            new Document
            {
                Id = domain.ConfidentialDocument,
                FolderId = domain.Folder,
                Title = "Incident report",
                Classification = "confidential",
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });

        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        return domain;
    }

    /// <summary>Grants a relationship directly, bypassing the admin API.</summary>
    /// <param name="fixture">The running service.</param>
    /// <param name="tuples">What to write.</param>
    public static Task GrantAsync(GrantPathFixture fixture, params RelationshipTuple[] tuples)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        return fixture.Services
            .GetRequiredService<IRelationshipEngine>()
            .WriteAsync(tuples, [], TestContext.Current.CancellationToken);
    }

    /// <summary>Removes a relationship directly, bypassing the admin API.</summary>
    /// <param name="fixture">The running service.</param>
    /// <param name="tuples">What to remove.</param>
    public static Task RevokeAsync(GrantPathFixture fixture, params RelationshipTuple[] tuples)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        return fixture.Services
            .GetRequiredService<IRelationshipEngine>()
            .WriteAsync([], tuples, TestContext.Current.CancellationToken);
    }
}
