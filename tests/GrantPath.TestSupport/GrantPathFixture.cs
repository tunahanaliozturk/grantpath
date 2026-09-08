using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using GrantPath.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using Xunit;

namespace GrantPath.TestSupport;

/// <summary>
/// The real service, against a real Postgres and a real OpenFGA, listening on a real socket.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is faked. The behaviour under test is OpenFGA's relationship-graph evaluation and Postgres's
/// row-level security, and a substitute for either would be a substitute for the thing being proven. A
/// test that mocks <c>Check</c> proves that the mock returns what it was told to.
/// </para>
/// <para>
/// OpenFGA runs with its in-memory datastore here. That is its own storage concern and not what these
/// tests are about; the compose stack and the Aspire host both run it against Postgres, which is what a
/// deployment would do.
/// </para>
/// </remarks>
public sealed class GrantPathFixture : IAsyncLifetime
{
    /// <summary>The image both the tests and the compose stack pin.</summary>
    public const string OpenFgaImage = "openfga/openfga:v1.19.0";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("grantpath")
        .WithUsername("grantpath")
        .WithPassword("grantpath")
        .Build();

    private readonly IContainer _openFga = new ContainerBuilder(OpenFgaImage)
        .WithCommand("run")
        .WithEnvironment("OPENFGA_LOG_LEVEL", "error")
        .WithEnvironment("OPENFGA_PLAYGROUND_ENABLED", "false")
        .WithPortBinding(8080, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPort(8080).ForPath("/healthz")))
        .Build();

    private WebApplication? _app;

    /// <summary>Where the service is listening.</summary>
    public Uri BaseAddress { get; private set; } = new("http://localhost");

    /// <summary>Where OpenFGA is listening.</summary>
    public string OpenFgaUrl { get; private set; } = "http://localhost:8080";

    /// <summary>The running application's services.</summary>
    public IServiceProvider Services =>
        _app?.Services ?? throw new InvalidOperationException("The host has not been started.");

    /// <summary>Starts the containers and the service.</summary>
    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _openFga.StartAsync());

        OpenFgaUrl = $"http://{_openFga.Hostname}:{_openFga.GetMappedPublicPort(8080)}";

        _app = GrantPathHost.Build([], builder =>
        {
            builder.Environment.EnvironmentName = "Testing";
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:grantpath"] = _postgres.GetConnectionString(),
                ["GrantPath:Rebac:ApiUrl"] = OpenFgaUrl,
                ["GrantPath:Rebac:StoreName"] = "grantpath-tests",
                ["GrantPath:Database:MigrateOnStartup"] = "true",
                ["GrantPath:Database:SeedDemoData"] = "false",
                ["GrantPath:Callers:AllowAnonymous"] = "true",

                // Short enough that a convergence assertion finishes quickly, long enough that the poller
                // is not the thing under test.
                ["GrantPath:Projection:PollInterval"] = "00:00:00.200",
            });
        });

        await _app.StartAsync();

        BaseAddress = new Uri(_app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .First());
    }

    /// <summary>Stops everything.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        await Task.WhenAll(_openFga.DisposeAsync().AsTask(), _postgres.DisposeAsync().AsTask());
    }

    /// <summary>A client pointed at the service.</summary>
    public HttpClient CreateClient() => new() { BaseAddress = BaseAddress };
}
