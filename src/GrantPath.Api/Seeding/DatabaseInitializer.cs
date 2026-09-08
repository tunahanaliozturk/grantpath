using GrantPath.Api.Authorization;
using GrantPath.Api.Projection;
using GrantPath.Data;
using GrantPath.Rebac;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GrantPath.Api.Seeding;

/// <summary>
/// Brings the schema up to date and, when asked, creates the demonstration domain.
/// </summary>
/// <remarks>
/// <para>
/// Both halves are off unless configuration turns them on. Migrating during startup races itself across
/// instances during a rolling deploy; seeding creates relationships and accounts that nobody chose.
/// </para>
/// <para>
/// The demonstration domain is the one the tests and the README both use: one org, one workspace, one
/// folder, two documents, and four people whose access differs in exactly the ways worth showing.
/// </para>
/// </remarks>
/// <param name="scopeFactory">Opens a scope, since this runs before the request pipeline exists.</param>
/// <param name="engine">The relationship store.</param>
/// <param name="options">Whether to migrate and seed.</param>
/// <param name="timeProvider">Clock.</param>
/// <param name="logger">Logger.</param>
public sealed partial class DatabaseInitializer(
    IServiceScopeFactory scopeFactory,
    IRelationshipEngine engine,
    IOptions<DatabaseOptions> options,
    TimeProvider timeProvider,
    ILogger<DatabaseInitializer> logger) : IHostedService
{
    /// <summary>The demonstration org.</summary>
    public const string DemoOrg = "org:acme";

    /// <summary>The demonstration workspace, inside the org.</summary>
    public const string DemoWorkspace = "workspace:engineering";

    /// <summary>The demonstration folder, inside the workspace.</summary>
    public const string DemoFolder = "folder:handbooks";

    /// <summary>A document four levels below the org.</summary>
    public const string DemoDocument = "document:onboarding";

    /// <summary>A second document, classified, so an attribute policy has something to narrow.</summary>
    public const string DemoConfidentialDocument = "document:incident-report";

    /// <summary>Holds administrator on the platform, so it can use the admin API.</summary>
    public const string DemoAdmin = "user:root";

    /// <summary>A member of the org and nothing else. Reaches the leaf purely by inheritance.</summary>
    public const string DemoMember = "user:alice";

    /// <summary>Blocked on one document despite inheriting access to it.</summary>
    public const string DemoBlocked = "user:bob";

    /// <summary>Related to nothing. Every check for this account must deny.</summary>
    public const string DemoStranger = "user:mallory";

    private readonly DatabaseOptions _options = options.Value;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.MigrateOnStartup && !_options.SeedDemoData)
        {
            return;
        }

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        GrantPathDbContext dbContext = scope.ServiceProvider.GetRequiredService<GrantPathDbContext>();

        if (_options.MigrateOnStartup)
        {
            LogMigrating(logger);
            await dbContext.Database.MigrateAsync(cancellationToken);
        }

        if (!_options.SeedDemoData)
        {
            return;
        }

        LogSeeding(logger);

        await SeedRelationshipsAsync(cancellationToken);
        await SeedDocumentsAsync(dbContext, cancellationToken);

        PermissionProjection projection = scope.ServiceProvider.GetRequiredService<PermissionProjection>();

        // The projection is built once here rather than left to the first poll, so the demonstration
        // stack answers correctly from the first request instead of from the second.
        foreach (string subject in new[] { DemoAdmin, DemoMember, DemoBlocked, DemoStranger })
        {
            await projection.RebuildSubjectAsync(dbContext, subject, cancellationToken);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SeedRelationshipsAsync(CancellationToken cancellationToken)
    {
        // Writing a tuple that already exists is an error in OpenFGA, so the existing set is read first
        // and the seeder writes only the difference. That is what makes a second start a no-op.
        IReadOnlyList<RelationshipTuple> existing = await engine.ReadAsync(null, null, null, cancellationToken);
        var present = new HashSet<string>(existing.Select(tuple => tuple.ToString()), StringComparer.Ordinal);

        RelationshipTuple[] desired =
        [
            new(DemoAdmin, AuthorizationModel.Relations.Administrator, AuthorizationModel.PlatformObject),

            new(DemoOrg, AuthorizationModel.Relations.Parent, DemoWorkspace),
            new(DemoWorkspace, AuthorizationModel.Relations.Parent, DemoFolder),
            new(DemoFolder, AuthorizationModel.Relations.Parent, DemoDocument),
            new(DemoFolder, AuthorizationModel.Relations.Parent, DemoConfidentialDocument),

            // The whole demonstration rests on this one tuple: membership of the org, and nothing else.
            new(DemoMember, AuthorizationModel.Relations.Member, DemoOrg),
            new(DemoBlocked, AuthorizationModel.Relations.Member, DemoOrg),

            // An explicit exclusion, which the relationship model subtracts from whatever was inherited.
            new(DemoBlocked, AuthorizationModel.Relations.Blocked, DemoConfidentialDocument),
        ];

        List<RelationshipTuple> missing = [.. desired.Where(tuple => !present.Contains(tuple.ToString()))];

        if (missing.Count is 0)
        {
            return;
        }

        await engine.WriteAsync(missing, [], cancellationToken);

        LogTuples(logger, missing.Count);
    }

    private async Task SeedDocumentsAsync(GrantPathDbContext dbContext, CancellationToken cancellationToken)
    {
        if (await dbContext.Documents.AnyAsync(cancellationToken))
        {
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        dbContext.Documents.AddRange(
            new Document
            {
                Id = DemoDocument,
                FolderId = DemoFolder,
                Title = "Onboarding handbook",
                Classification = "internal",
                CreatedAtUtc = now,
            },
            new Document
            {
                Id = DemoConfidentialDocument,
                FolderId = DemoFolder,
                Title = "Incident report",
                Classification = "confidential",
                CreatedAtUtc = now,
            });

        if (!await dbContext.AbacPolicies.AnyAsync(cancellationToken))
        {
            // One policy, and it only ever narrows: a confidential document is refused to anyone whose
            // clearance is not high enough, however good their relationship to it is.
            dbContext.AbacPolicies.Add(new AbacPolicyRecord
            {
                Id = Guid.CreateVersion7(now),
                Name = "Confidential documents need clearance",
                ResourceType = AuthorizationModel.Types.Document,
                Effect = StoredPolicyEffect.Deny,
                Priority = 100,
                ConditionJson = """
                    {
                      "allOf": [
                        { "attribute": "resource.classification", "operator": "equals", "value": "confidential" },
                        { "not": { "attribute": "subject.clearance", "operator": "in", "value": ["secret", "top-secret"] } }
                      ]
                    }
                    """,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    [LoggerMessage(EventId = 5200, Level = LogLevel.Information, Message = "Applying database migrations.")]
    private static partial void LogMigrating(ILogger logger);

    [LoggerMessage(
        EventId = 5201,
        Level = LogLevel.Warning,
        Message = "Seeding the demonstration domain. This must never run in production.")]
    private static partial void LogSeeding(ILogger logger);

    [LoggerMessage(EventId = 5202, Level = LogLevel.Information, Message = "Wrote {Count} relationship tuples.")]
    private static partial void LogTuples(ILogger logger, int count);
}
