using GrantPath.Rebac;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GrantPath.Api.Observability;

/// <summary>
/// Reports whether the relationship store is answering.
/// </summary>
/// <remarks>
/// A readiness check that only looked at the database would report a healthy instance that cannot make a
/// single decision, because every decision needs the relationship store. It asks a question whose answer
/// does not matter, only that an answer came back.
/// </remarks>
/// <param name="engine">The relationship store.</param>
public sealed class RelationshipStoreHealthCheck(IRelationshipEngine engine) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await engine.ReadAsync(
                subject: null,
                relation: null,
                resource: AuthorizationModel.PlatformObject,
                cancellationToken);

            return HealthCheckResult.Healthy($"Model {engine.ModelId}.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("The relationship store did not answer.", exception);
        }
    }
}
