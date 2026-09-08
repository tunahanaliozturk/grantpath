using System.Security.Cryptography;
using System.Text;
using GrantPath.Api.Authorization;
using GrantPath.Rebac;
using Microsoft.Extensions.Options;

namespace GrantPath.Api.Security;

/// <summary>
/// Decides whether a caller may talk to this service at all.
/// </summary>
/// <remarks>
/// <para>
/// This service authorizes; it does not authenticate people. A subject arrives already authenticated from
/// an identity provider, and what has to be established here is that the caller is a service entitled to
/// ask questions on that subject's behalf. Without that, anyone who can reach the port can ask whether
/// any subject may read any resource, which is a permission map with a friendly API in front of it.
/// </para>
/// <para>
/// Keys are compared in fixed time. A comparison that returns early on the first differing byte leaks the
/// key one byte at a time to anyone patient enough to measure.
/// </para>
/// </remarks>
/// <param name="options">Configured keys.</param>
public sealed class CallerGate(IOptions<CallerOptions> options)
{
    private readonly CallerOptions _options = options.Value;

    /// <summary>Whether a request carries an acceptable key.</summary>
    /// <param name="context">The request.</param>
    public bool IsRecognised(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_options.AllowAnonymous)
        {
            return true;
        }

        string? presented = context.Request.Headers[CallerOptions.ApiKeyHeader];

        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        byte[] candidate = Encoding.UTF8.GetBytes(presented);
        bool matched = false;

        // Every configured key is compared, and the loop does not stop at the first match. Stopping early
        // would make the number of comparisons depend on which key was presented.
        foreach (string key in _options.ApiKeys)
        {
            matched |= CryptographicOperations.FixedTimeEquals(candidate, Encoding.UTF8.GetBytes(key));
        }

        return matched;
    }

    /// <summary>Reads the subject an administrative call is being made on behalf of.</summary>
    /// <param name="context">The request.</param>
    public static string? ReadSubject(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string? subject = context.Request.Headers[CallerOptions.SubjectHeader];

        return string.IsNullOrWhiteSpace(subject) ? null : subject;
    }
}

/// <summary>Rejects callers this service does not recognise.</summary>
/// <param name="gate">The key check.</param>
public sealed class CallerKeyFilter(CallerGate gate) : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        return gate.IsRecognised(context.HttpContext)
            ? await next(context)
            : Results.Problem(
                title: "Unrecognised caller",
                detail: $"Send a recognised key in the {CallerOptions.ApiKeyHeader} header.",
                statusCode: StatusCodes.Status401Unauthorized);
    }
}

/// <summary>
/// Gates the administrative endpoints through the same decision path they administer.
/// </summary>
/// <remarks>
/// The service authorizes access to its own administration. That is not symmetry for its own sake: it
/// means granting somebody the right to change relationships is itself a relationship, recorded in the
/// same store, visible in the same audit trail, and revocable the same way. An admin surface with its own
/// private notion of who is an admin is a second permission system, and the second one never gets audited.
/// </remarks>
/// <param name="facade">The decision path.</param>
public sealed class PlatformAdminFilter(AuthorizationFacade facade) : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (CallerGate.ReadSubject(context.HttpContext) is not { } subject)
        {
            return Results.Problem(
                title: "No subject",
                detail: $"Name the administering subject in the {CallerOptions.SubjectHeader} header.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        AuthorizationOutcome outcome = await facade.CheckAsync(
            subject,
            AuthorizationModel.Relations.Administrator,
            AuthorizationModel.PlatformObject,
            context: null,
            context.HttpContext.RequestAborted);

        if (!outcome.Allowed)
        {
            return Results.Problem(
                title: "Not an administrator",
                detail: $"{subject} does not hold {AuthorizationModel.Relations.Administrator} on "
                    + AuthorizationModel.PlatformObject,
                statusCode: StatusCodes.Status403Forbidden,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["requestId"] = outcome.RequestId,
                });
        }

        context.HttpContext.Items[SubjectKey] = subject;

        return await next(context);
    }

    /// <summary>Where the verified subject is stashed for the endpoint that follows.</summary>
    public const string SubjectKey = "grantpath.subject";
}
