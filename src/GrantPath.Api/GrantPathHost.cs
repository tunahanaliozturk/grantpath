using GrantPath.Api.Auditing;
using GrantPath.Api.Authorization;
using GrantPath.Api.Endpoints;
using GrantPath.Api.Observability;
using GrantPath.Api.Projection;
using GrantPath.Api.Security;
using GrantPath.Api.Seeding;
using GrantPath.Data;
using GrantPath.Rebac;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace GrantPath.Api;

/// <summary>Builds the authorization service.</summary>
/// <remarks>
/// Separated from the entry point so the test suites can start and stop it in process, against a database
/// and a relationship store they created, without the code under test knowing it is being tested.
/// </remarks>
public static class GrantPathHost
{
    /// <summary>Builds the application.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="configure">Applied to the builder before it is built.</param>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        configure?.Invoke(builder);

        AddOptions(builder);
        AddPersistence(builder);
        AddAuthorization(builder);
        AddTelemetry(builder);

        builder.Services.AddProblemDetails();
        builder.Services.AddOpenApi();

        WebApplication app = builder.Build();

        app.UseExceptionHandler();
        app.UseStatusCodePages();

        app.MapOpenApi();
        app.MapAuthzEndpoints();
        app.MapAdminEndpoints();
        app.MapDemoEndpoints();

        app.MapHealthChecks("/health/live", new()
        {
            Predicate = static registration => registration.Tags.Count == 0,
        });

        app.MapHealthChecks("/health/ready", new()
        {
            Predicate = static registration => registration.Tags.Contains("ready"),
        });

        return app;
    }

    private static void AddOptions(WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<DecisionOptions>()
            .Bind(builder.Configuration.GetSection(DecisionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddOptions<AuditOptions>()
            .Bind(builder.Configuration.GetSection(AuditOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddOptions<ProjectionOptions>()
            .Bind(builder.Configuration.GetSection(ProjectionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddOptions<CallerOptions>()
            .Bind(builder.Configuration.GetSection(CallerOptions.SectionName))
            .ValidateOnStart();

        builder.Services.AddOptions<DatabaseOptions>()
            .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateOnStart();

        builder.Services.AddOptions<RebacOptions>()
            .Bind(builder.Configuration.GetSection(RebacOptions.SectionName))
            .ValidateOnStart();
    }

    private static void AddPersistence(WebApplicationBuilder builder)
    {
        string connectionString = builder.Configuration.GetConnectionString("grantpath")
            ?? throw new InvalidOperationException("Connection string 'grantpath' is not configured.");

        builder.Services.AddDbContext<GrantPathDbContext>(options => options
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());

        builder.Services.AddHealthChecks()
            .AddDbContextCheck<GrantPathDbContext>("database", tags: ["ready"])
            .AddCheck<RelationshipStoreHealthCheck>("relationships", tags: ["ready"]);
    }

    private static void AddAuthorization(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<GrantPathMetrics>();
        builder.Services.AddSingleton<CallerGate>();
        builder.Services.AddSingleton<PolicyCache>();
        builder.Services.AddSingleton<ContainmentResolver>();
        builder.Services.AddSingleton<DecisionAuditWriter>();
        builder.Services.AddSingleton<PermissionProjection>();
        builder.Services.AddScoped<AuthorizationFacade>();
        builder.Services.AddScoped<CallerKeyFilter>();
        builder.Services.AddScoped<PlatformAdminFilter>();

        // The connection to the relationship store is established once, at startup, because it resolves
        // the store and writes the authorization model. Doing that per request would mean a model write
        // on every call, and a different model id in every audit record.
        builder.Services.AddSingleton<IRelationshipEngine>(provider =>
        {
            RebacOptions options = provider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<RebacOptions>>()
                .Value;

            return OpenFgaRelationshipEngine
                .ConnectAsync(options, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        });

        builder.Services.AddHostedService<DecisionAuditService>();
        builder.Services.AddHostedService<ProjectionService>();
        builder.Services.AddHostedService<DatabaseInitializer>();
    }

    private static void AddTelemetry(WebApplicationBuilder builder)
    {
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddMeter(GrantPathMetrics.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(tracing => tracing
                .AddSource(GrantPathMetrics.ActivitySourceName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation());

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }
    }
}
