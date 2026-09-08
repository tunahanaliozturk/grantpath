using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GrantPath.Data;

/// <summary>The application's own database, separate from the relationship store.</summary>
/// <param name="options">Context options.</param>
public sealed class GrantPathDbContext(DbContextOptions<GrantPathDbContext> options) : DbContext(options)
{
    /// <summary>The subject a database session is acting as, read by the row-level security policy.</summary>
    /// <remarks>
    /// A Postgres session setting rather than a query filter. A filter is application-layer discipline and
    /// disappears the moment somebody writes raw SQL; a policy on the table applies to every statement that
    /// reaches it.
    /// </remarks>
    public const string SubjectSetting = "grantpath.subject";

    /// <summary>
    /// The role a query runs as when it is meant to be bounded by row-level security.
    /// </summary>
    /// <remarks>
    /// Postgres exempts superusers and roles with BYPASSRLS from every policy, so a service that connects
    /// as one has protection that reads correctly and enforces nothing. Switching to this role for the
    /// duration of a transaction is what makes the policy apply, whatever the login user happens to be.
    /// </remarks>
    public const string ReaderRole = "grantpath_reader";

    /// <summary>Attribute policies.</summary>
    public DbSet<AbacPolicyRecord> AbacPolicies => Set<AbacPolicyRecord>();

    /// <summary>The decision trail.</summary>
    public DbSet<AuthorizationDecisionRecord> Decisions => Set<AuthorizationDecisionRecord>();

    /// <summary>The projection row-level security reads.</summary>
    public DbSet<DocumentPermissionCacheEntry> DocumentPermissions => Set<DocumentPermissionCacheEntry>();

    /// <summary>Where the changelog reader has got to.</summary>
    public DbSet<ChangelogCursor> ChangelogCursors => Set<ChangelogCursor>();

    /// <summary>The demonstration content table.</summary>
    public DbSet<Document> Documents => Set<Document>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<AbacPolicyRecord>(entity =>
        {
            entity.ToTable("abac_policies");
            entity.HasKey(policy => policy.Id);
            entity.Property(policy => policy.Name).HasMaxLength(200);
            entity.Property(policy => policy.ResourceType).HasMaxLength(64);
            entity.Property(policy => policy.ConditionJson).HasColumnType("jsonb");

            // Every decision loads the policies for one resource type, so that is the index that matters.
            entity.HasIndex(policy => policy.ResourceType);
        });

        modelBuilder.Entity<AuthorizationDecisionRecord>(entity =>
        {
            entity.ToTable("authz_decisions");
            entity.HasKey(record => record.RequestId);
            entity.Property(record => record.Subject).HasMaxLength(256);
            entity.Property(record => record.Resource).HasMaxLength(256);
            entity.Property(record => record.ResourceType).HasMaxLength(64);
            entity.Property(record => record.Relation).HasMaxLength(64);
            entity.Property(record => record.AttributeVerdict).HasMaxLength(32);
            entity.Property(record => record.ModelId).HasMaxLength(64);
            entity.Property(record => record.ReasonJson).HasColumnType("jsonb");

            // An auditor asks about one resource across its lifetime, or one subject across a window.
            // Both are answered by an index that leads with the thing and orders by time.
            entity.HasIndex(record => new { record.Resource, record.DecidedAtUtc });
            entity.HasIndex(record => new { record.Subject, record.DecidedAtUtc });
        });

        modelBuilder.Entity<DocumentPermissionCacheEntry>(entity =>
        {
            entity.ToTable("document_permission_cache");
            entity.HasKey(entry => new { entry.DocumentId, entry.Subject, entry.Relation });
            entity.Property(entry => entry.DocumentId).HasMaxLength(256);
            entity.Property(entry => entry.Subject).HasMaxLength(256);
            entity.Property(entry => entry.Relation).HasMaxLength(64);
        });

        modelBuilder.Entity<ChangelogCursor>(entity =>
        {
            entity.ToTable("changelog_cursor");
            entity.HasKey(cursor => cursor.Id);
            entity.Property(cursor => cursor.Id).ValueGeneratedNever();
            entity.Property(cursor => cursor.ContinuationToken).HasMaxLength(512);
        });

        modelBuilder.Entity<Document>(entity =>
        {
            entity.ToTable("documents");
            entity.HasKey(document => document.Id);
            entity.Property(document => document.Id).HasMaxLength(256);
            entity.Property(document => document.FolderId).HasMaxLength(256);
            entity.Property(document => document.Title).HasMaxLength(400);
            entity.Property(document => document.Classification).HasMaxLength(64);
            entity.HasIndex(document => document.FolderId);
        });
    }
}

/// <summary>Lets the EF tooling build a context without starting the application.</summary>
public sealed class GrantPathDbContextFactory : IDesignTimeDbContextFactory<GrantPathDbContext>
{
    /// <inheritdoc />
    public GrantPathDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<GrantPathDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=grantpath;Username=grantpath;Password=grantpath")
            .UseSnakeCaseNamingConvention()
            .Options);
}
