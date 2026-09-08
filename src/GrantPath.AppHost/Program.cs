// One command brings up Postgres, OpenFGA, the authorization service and the telemetry dashboard, with
// the schema applied, the relationship model written and the demonstration domain seeded.
IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<PostgresServerResource> postgres = builder
    .AddPostgres("postgres")
    .WithDataVolume();

IResourceBuilder<PostgresDatabaseResource> database = postgres.AddDatabase("grantpath");

// OpenFGA keeps its own schema in the same database and refuses to start against an empty one, so the
// migration is a separate container that has to finish first.
IResourceBuilder<ContainerResource> migrate = builder
    .AddContainer("openfga-migrate", "openfga/openfga", "v1.19.0")
    .WithArgs("migrate")
    .WithEnvironment("OPENFGA_DATASTORE_ENGINE", "postgres")
    .WithEnvironment(
        "OPENFGA_DATASTORE_URI",
        "postgres://postgres:postgres@host.docker.internal:5432/grantpath?sslmode=disable")
    .WaitFor(database);

IResourceBuilder<ContainerResource> openfga = builder
    .AddContainer("openfga", "openfga/openfga", "v1.19.0")
    .WithArgs("run")
    .WithEnvironment("OPENFGA_DATASTORE_ENGINE", "postgres")
    .WithEnvironment(
        "OPENFGA_DATASTORE_URI",
        "postgres://postgres:postgres@host.docker.internal:5432/grantpath?sslmode=disable")
    .WithEnvironment("OPENFGA_PLAYGROUND_ENABLED", "false")
    .WithHttpEndpoint(port: 8080, targetPort: 8080, name: "http")
    .WaitForCompletion(migrate);

builder.AddProject<Projects.GrantPath_Api>("grantpath")
    .WithReference(database)
    .WaitFor(database)
    .WaitFor(openfga)
    .WithEnvironment("GrantPath__Rebac__ApiUrl", "http://localhost:8080")
    .WithEnvironment("GrantPath__Database__MigrateOnStartup", "true")
    .WithEnvironment("GrantPath__Database__SeedDemoData", "true")
    .WithEnvironment("GrantPath__Callers__AllowAnonymous", "true")
    .WithHttpHealthCheck("/health/ready");

await builder.Build().RunAsync();
